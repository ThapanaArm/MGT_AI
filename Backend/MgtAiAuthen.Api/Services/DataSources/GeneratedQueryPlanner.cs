using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>The user's question as the planner needs it: the text, who answers it, and a little earlier context.</summary>
public record DataSourceQuestion(string Text, string Model, string Provider, string? RecentContext = null);

/// <summary>Outcome of turning a question into a query: a query to run, "no data needed", or a reason it failed.</summary>
/// <param name="Draft">The query text the AI wrote when it was rejected by the safety check - kept so the user can still see it.</param>
public record QueryPlan(bool Success, string? Query, bool NoDataNeeded, string Message, string? Draft = null);

/// <summary>
/// Tokens spent on helper AI calls made while preparing an answer (writing the query, choosing a
/// query). Scoped per request so ChatService can add them to the message's recorded tokens and cost -
/// otherwise those calls would be billed by the provider but missing from the cost report.
/// </summary>
public class ExtraAiUsage
{
    public int Input { get; private set; }
    public int Output { get; private set; }
    public int CacheWrite { get; private set; }
    public int CacheRead { get; private set; }

    public void Add(AiReply reply)
    {
        Input += reply.InputTokens;
        Output += reply.OutputTokens;
        CacheWrite += reply.CacheWriteTokens;
        CacheRead += reply.CacheReadTokens;
    }
}

/// <summary>
/// "Generated" query mode: instead of running the SQL the admin typed (which usually returns a whole
/// table), the AI reads the user's question and a compact description of the tables, and writes one
/// read-only query that returns just what is needed. That is what keeps input tokens low - the model
/// sees a handful of rows or an aggregate rather than thousands of rows.
/// </summary>
public interface IQueryPlanner
{
    /// <summary>True when this source is set to queryMode = Generated and its type/mode supports it.</summary>
    bool IsGenerated(string sourceType, Dictionary<string, string> config);

    Task<QueryPlan> PlanAsync(
        DataSource source, Dictionary<string, string> config, string? secret,
        DataSourceQuestion question, CancellationToken ct);

    /// <summary>One retry after the database rejected a generated query - the error text is shown to the model.</summary>
    Task<QueryPlan> RepairAsync(
        DataSource source, Dictionary<string, string> config, string? secret,
        DataSourceQuestion question, string failedQuery, string error, CancellationToken ct);
}

public partial class GeneratedQueryPlanner(
    IAiClient ai, IHttpClientFactory httpClientFactory, ExtraAiUsage usage, ILogger<GeneratedQueryPlanner> logger)
    : IQueryPlanner
{
    private const int MaxQuestionContextChars = 700;

    public bool IsGenerated(string sourceType, Dictionary<string, string> config)
    {
        if (!string.Equals(config.GetValueOrDefault("queryMode", "").Trim(), "Generated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(sourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase)
               || (string.Equals(sourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase)
                   && DataLakeMode.Resolve(config) == DataLakeMode.Warehouse);
    }

    public Task<QueryPlan> PlanAsync(
        DataSource source, Dictionary<string, string> config, string? secret,
        DataSourceQuestion question, CancellationToken ct)
        => BuildAsync(source, config, secret, question, failedQuery: null, error: null, ct);

    public Task<QueryPlan> RepairAsync(
        DataSource source, Dictionary<string, string> config, string? secret,
        DataSourceQuestion question, string failedQuery, string error, CancellationToken ct)
        => BuildAsync(source, config, secret, question, failedQuery, error, ct);

    private async Task<QueryPlan> BuildAsync(
        DataSource source, Dictionary<string, string> config, string? secret,
        DataSourceQuestion question, string? failedQuery, string? error, CancellationToken ct)
    {
        Dialect dialect = ResolveDialect(source.SourceType, config);
        FetchCaps caps = DataSourceFetchLimits.Resolve(config);

        QuerySchemaResult schema = await QuerySchema.LoadAsync(httpClientFactory, source.SourceId, config, secret, dialect, ct);
        if (!schema.Success)
        {
            return new QueryPlan(false, null, false, schema.Message);
        }

        string system = BuildSystemPrompt(
            dialect, caps.MaxRows, schema.Text, config.GetValueOrDefault("schemaHint", "").Trim(),
            config.GetValueOrDefault(dialect == Dialect.PostgREST ? "restQuery" : "sqlQuery", "").Trim());

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(question.RecentContext))
        {
            user.Append("Earlier questions in this chat (oldest first):\n").Append(question.RecentContext).Append("\n\n");
        }

        user.Append("Question: ").Append(question.Text.Trim());

        if (failedQuery is not null)
        {
            user.Append("\n\nYour previous query failed.\nQuery: ").Append(failedQuery)
                .Append("\nError: ").Append(error is { Length: > 400 } ? error[..400] : error)
                .Append("\nWrite a corrected query.");
        }

        AiReply reply;
        try
        {
            reply = await ai.CompleteAsync(system, [new ChatTurn(MessageRoles.User, user.ToString())], question.Model, question.Provider, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Query-writing call failed for data source {SourceId}", source.SourceId);
            return new QueryPlan(false, null, false, "The AI could not write a query for this question (the AI call failed).");
        }

        usage.Add(reply);

        string text = CleanReply(reply.Text);
        if (text.Length == 0 || text.Equals("NO_QUERY", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("NO_QUERY", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryPlan(true, null, true, "No data query was needed for this question.");
        }

        string allowedRaw = config.GetValueOrDefault("allowedTables", "");
        HashSet<string> allowed = QuerySchema.ParseAllowed(allowedRaw);

        (string? validated, string? problem) = dialect == Dialect.PostgREST
            ? QueryValidator.ValidateRest(text, allowed, schema.TableNames, caps.MaxRows)
            : QueryValidator.ValidateSql(text, allowed);

        if (validated is null)
        {
            logger.LogWarning("Rejected a generated query for data source {SourceId}: {Problem}", source.SourceId, problem);
            return new QueryPlan(false, null, false, $"The generated query was not run: {problem}", text);
        }

        return new QueryPlan(true, validated, false, "OK");
    }

    // ------------------------------------------------------------------ prompt

    internal enum Dialect { Postgres, PostgREST, TSql }

    private static Dialect ResolveDialect(string sourceType, Dictionary<string, string> config)
        => string.Equals(sourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase)
            ? Dialect.TSql
            : SupabaseMode.Resolve(config) == SupabaseMode.Rest ? Dialect.PostgREST : Dialect.Postgres;

    private static string BuildSystemPrompt(Dialect dialect, int maxRows, string schema, string hint, string adminQueries)
    {
        string common =
            "Reply with ONLY the query - no explanation, no markdown fences, no comments.\n" +
            "- Select only the columns the question needs - for a plain \"show me the data\" request pick the 6 to 8 most useful columns; avoid SELECT * unless the user asks for every column.\n" +
            $"- Never return more than {maxRows} rows.\n" +
            "- When the user gives a name (product, customer, employee) match it loosely, never with exact equality: " +
            "case-insensitive and partial, so \"citric acid\" still finds \"Citric Acid Monohydrate\". Match on the name " +
            "column and, if several columns could hold it, on each of them with OR.\n" +
            "- If the question cannot be answered from this data (greeting, general knowledge, thanks, a " +
            "question about an earlier answer), reply exactly: NO_QUERY\n";

        string rules = dialect switch
        {
            Dialect.Postgres =>
                "You translate a user's question into ONE read-only PostgreSQL SELECT query over the tables below.\n" + common +
                "- Use PostgreSQL syntax. Double-quote any table or column name that has capital letters or spaces (\"Sales Demo\"). Use ILIKE '%text%' for name matching.\n" +
                $"- For totals, counts, averages, rankings or trends use aggregation (COUNT, SUM, AVG, GROUP BY, ORDER BY) so only a small result comes back; for listings add LIMIT {maxRows} or less.\n" +
                "- One statement only; no semicolons inside; read-only.\n",
            Dialect.TSql =>
                "You translate a user's question into ONE read-only T-SQL SELECT query over the tables below.\n" + common +
                "- Use T-SQL syntax (TOP, not LIMIT). Put [brackets] around names with spaces or capitals. Use LIKE N'%text%' for name matching.\n" +
                $"- For totals, counts, averages, rankings or trends use aggregation (COUNT, SUM, AVG, GROUP BY, ORDER BY) so only a small result comes back; for listings use TOP {maxRows} or less.\n" +
                "- One statement only; read-only.\n",
            _ =>
                "You translate a user's question into one to three Supabase REST (PostgREST) requests over the tables below.\n" + common +
                "- Write one request per line as: table?select=col1,col2&column=op.value&order=column.desc&limit=N\n" +
                "- Operators: eq, neq, gt, gte, lt, lte, like, ilike (use * as the wildcard, e.g. name=ilike.*citric*), in.(a,b), is.null. Combine filters with &. Use ilike.*text* for name matching.\n" +
                $"- Always end with &limit=N (N at most {maxRows}). Aggregate functions are usually NOT available, so filter precisely and select few columns.\n" +
                "- A table name with spaces is written as is (Sales Demo?select=*&limit=20). Only GET reads; no rpc/ calls.\n",
        };

        var sb = new StringBuilder(rules).Append("\nTables and columns:\n").Append(schema);
        if (hint.Length > 0)
        {
            sb.Append("\n\nAbout this data:\n").Append(hint.Length > 4000 ? hint[..4000] : hint);
        }

        if (adminQueries.Length > 0)
        {
            // Queries the administrator wrote for this source: they show the real column names and what the
            // figures mean (which column is the sales amount, how dates are grouped). Examples only.
            sb.Append("\n\nExample queries written by the data owner (use them as a guide to the columns and meaning; ")
                .Append("adapt them, add the filters this question needs, and keep the result small):\n")
                .Append(adminQueries.Length > 9000 ? adminQueries[..9000] : adminQueries);
        }

        return sb.ToString();
    }

    private static string CleanReply(string raw)
    {
        string text = raw.Trim();

        Match fence = FencedBlock().Match(text);
        if (fence.Success) text = fence.Groups[1].Value.Trim();

        return text.Trim().Trim('`').Trim();
    }

    [GeneratedRegex(@"```[a-zA-Z]*\s*([\s\S]*?)```")]
    private static partial Regex FencedBlock();
}

internal readonly record struct FetchCaps(int MaxRows, int MaxChars, bool SkipText = false);

internal record QuerySchemaResult(bool Success, string Text, string Message, IReadOnlySet<string> TableNames);

/// <summary>Reads the table/column list of a source and shrinks it to a compact, cached text block for the prompt.</summary>
internal static partial class QuerySchema
{
    private const int MaxSchemaChars = 12_000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, (DateTime At, QuerySchemaResult Result)> Cache = new();

    private const string PostgresSchemaSql =
        "SELECT table_schema AS s, table_name AS t, column_name AS c, data_type AS d " +
        "FROM information_schema.columns " +
        "WHERE table_schema NOT IN ('pg_catalog','information_schema','auth','storage','realtime','extensions'," +
        "'graphql','graphql_public','pgsodium','pgsodium_masks','vault','supabase_functions','supabase_migrations'," +
        "'_realtime','net','pgbouncer','cron','_analytics') " +
        "ORDER BY table_schema, table_name, ordinal_position";

    private const string TSqlSchemaSql =
        "SELECT TABLE_SCHEMA AS s, TABLE_NAME AS t, COLUMN_NAME AS c, DATA_TYPE AS d " +
        "FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

    public static HashSet<string> ParseAllowed(string raw)
        => raw.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('"', '[', ']', '`').ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToHashSet();

    public static async Task<QuerySchemaResult> LoadAsync(
        IHttpClientFactory httpClientFactory, int sourceId, Dictionary<string, string> config, string? secret,
        GeneratedQueryPlanner.Dialect dialect, CancellationToken ct)
    {
        string key = $"{sourceId}:{string.Join('|', config.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}:{secret?.GetHashCode()}";
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Result;
        }

        // Introspection must see the whole schema, so it runs with the normal (large) limits, not the small generated-query caps.
        var fixedConfig = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase) { ["queryMode"] = "Fixed" };

        QuerySchemaResult result = dialect == GeneratedQueryPlanner.Dialect.PostgREST
            ? await LoadRestAsync(httpClientFactory, fixedConfig, secret, ct)
            : await LoadSqlAsync(httpClientFactory, fixedConfig, secret, dialect, ct);

        if (result.Success)
        {
            result = Shrink(result, config);
            Cache[key] = (DateTime.UtcNow, result);
        }

        return result;
    }

    private static async Task<QuerySchemaResult> LoadSqlAsync(
        IHttpClientFactory httpClientFactory, Dictionary<string, string> config, string? secret,
        GeneratedQueryPlanner.Dialect dialect, CancellationToken ct)
    {
        FabricWarehouseQueryResult result;

        if (dialect == GeneratedQueryPlanner.Dialect.TSql)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                return Failed("No client secret is configured for this Entra ID app registration.");
            }

            HttpClient http = httpClientFactory.CreateClient(PowerBiConnectionTester.HttpClientName);
            result = await FabricWarehouseQuery.ExecuteAsync(
                http, config.GetValueOrDefault("tenantId", "").Trim(), config.GetValueOrDefault("clientId", "").Trim(),
                secret, config, TSqlSchemaSql, ct);
        }
        else
        {
            HttpClient http = httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName);
            result = await SupabaseQuery.ExecuteAsync(http, config, secret, PostgresSchemaSql, ct);
        }

        if (!result.Success)
        {
            return Failed(result.Message);
        }

        var columns = new List<(string Schema, string Table, string Column, string Type)>();
        foreach (string line in (result.FormattedText ?? "").Split('\n').Skip(2))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("| ") || !trimmed.EndsWith(" |")) continue;

            string[] cells = trimmed[2..^2].Split(" | ");
            if (cells.Length >= 4) columns.Add((cells[0], cells[1], cells[2], cells[3]));
        }

        if (columns.Count == 0)
        {
            return Failed("The database reports no tables this user can see - check the database user's permissions.");
        }

        return Build(columns.GroupBy(c => (c.Schema, c.Table))
            .Select(g => (g.Key.Schema, g.Key.Table, g.Select(c => (c.Column, c.Type)).ToList())).ToList());
    }

    private static async Task<QuerySchemaResult> LoadRestAsync(
        IHttpClientFactory httpClientFactory, Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        string projectUrl = SupabaseQuery.NormalizeProjectUrl(config.GetValueOrDefault("projectUrl", ""));
        string hint = config.GetValueOrDefault("schemaHint", "").Trim();
        HashSet<string> allowed = ParseAllowed(config.GetValueOrDefault("allowedTables", ""));

        if (string.IsNullOrWhiteSpace(secret)) return Failed("No API key is configured (put the anon or service_role key in Secret).");
        if (!Uri.TryCreate(projectUrl, UriKind.Absolute, out Uri? baseUri) || !SupabaseQuery.IsAllowedScheme(baseUri))
        {
            return Failed("The project URL must be an https:// address.");
        }

        try
        {
            HttpClient http = httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/rest/v1/"));
            request.Headers.TryAddWithoutValidation("apikey", secret);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            request.Headers.TryAddWithoutValidation("Accept", "application/openapi+json, application/json");
            string schema = config.GetValueOrDefault("schema", "").Trim();
            if (schema.Length > 0) request.Headers.TryAddWithoutValidation("Accept-Profile", schema);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("definitions", out JsonElement definitions)
                    && definitions.ValueKind == JsonValueKind.Object)
                {
                    var tables = new List<(string Schema, string Table, List<(string Column, string Type)> Columns)>();
                    foreach (JsonProperty table in definitions.EnumerateObject())
                    {
                        var cols = new List<(string, string)>();
                        if (table.Value.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty col in props.EnumerateObject())
                            {
                                string type = col.Value.TryGetProperty("format", out JsonElement f) ? f.GetString() ?? ""
                                    : col.Value.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                                cols.Add((col.Name, type));
                            }
                        }

                        tables.Add(("public", table.Name, cols));
                    }

                    if (tables.Count > 0) return Build(tables);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Fall through to the admin-provided description below.
        }

        // Newer Supabase projects only expose the table list to the service_role key. The admin can still
        // describe the data by hand: the table names and/or a short description of the columns.
        if (allowed.Count > 0 || hint.Length > 0)
        {
            string names = allowed.Count > 0
                ? string.Join("\n", allowed.Select(t => t))
                : "(see the description below)";
            return new QuerySchemaResult(true, names, "OK", allowed);
        }

        return Failed(
            "Could not read the table list from the REST API (it needs the service_role key). Add the table " +
            "names to \"Allowed tables\" or describe the data in \"Schema hint\" so the AI knows what to query.");
    }

    private static QuerySchemaResult Build(List<(string Schema, string Table, List<(string Column, string Type)> Columns)> tables)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach ((string schema, string table, List<(string Column, string Type)> columns) in tables)
        {
            string name = schema is "public" or "dbo" ? table : $"{schema}.{table}";
            names.Add(name);
            sb.Append(name).Append('(')
                .Append(string.Join(", ", columns.Select(c => c.Type.Length > 0 ? $"{c.Column} {Short(c.Type)}" : c.Column)))
                .Append(")\n");
        }

        return new QuerySchemaResult(true, sb.ToString().TrimEnd(), "OK", names);
    }

    /// <summary>Applies the admin's "Allowed tables" list and the size cap so the schema block stays small.</summary>
    private static QuerySchemaResult Shrink(QuerySchemaResult result, Dictionary<string, string> config)
    {
        HashSet<string> allowed = ParseAllowed(config.GetValueOrDefault("allowedTables", ""));
        string[] lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (allowed.Count > 0)
        {
            lines = lines.Where(l =>
            {
                string name = l[..Math.Max(0, l.IndexOf('('))].ToLowerInvariant();
                string bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                return allowed.Contains(name) || allowed.Contains(bare);
            }).ToArray();

            if (lines.Length == 0)
            {
                return Failed("None of the \"Allowed tables\" exist in this database - check the names.");
            }
        }

        var kept = new StringBuilder();
        int shown = 0;
        foreach (string line in lines)
        {
            if (kept.Length + line.Length + 1 > MaxSchemaChars) break;
            kept.Append(line).Append('\n');
            shown++;
        }

        if (shown < lines.Length)
        {
            kept.Append($"(+{lines.Length - shown} more tables not shown - set \"Allowed tables\" to choose which ones the AI may use)");
        }

        IReadOnlySet<string> names = lines
            .Select(l => l[..Math.Max(0, l.IndexOf('('))])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new QuerySchemaResult(true, kept.ToString().TrimEnd(), "OK", names);
    }

    private static string Short(string type) => type.ToLowerInvariant() switch
    {
        "character varying" or "varchar" or "nvarchar" => "text",
        "timestamp with time zone" => "timestamptz",
        "timestamp without time zone" => "timestamp",
        "double precision" => "float8",
        "integer" => "int",
        "bigint" => "int8",
        "smallint" => "int2",
        "boolean" => "bool",
        "numeric" => "num",
        var other => other,
    };

    private static QuerySchemaResult Failed(string message)
        => new(false, "", message, new HashSet<string>());

    /// <summary>Used by the connection testers: connects, reads the schema and reports how many tables the AI would see.</summary>
    public static async Task<DataSourceTestResult> TestAsync(
        IHttpClientFactory httpClientFactory, Dictionary<string, string> config, string? secret,
        GeneratedQueryPlanner.Dialect dialect, CancellationToken ct)
    {
        QuerySchemaResult schema = await LoadAsync(httpClientFactory, 0, config, secret, dialect, ct);
        return new DataSourceTestResult(
            schema.Success,
            schema.Success
                ? $"Connected - the AI can see {schema.TableNames.Count} table(s) and will write a query for each question."
                : schema.Message,
            DateTime.Now);
    }
}

/// <summary>Defence in depth for AI-written queries: read-only, single statement, allowed tables only, bounded size.</summary>
internal static partial class QueryValidator
{
    public static (string? Query, string? Problem) ValidateSql(string raw, HashSet<string> allowed)
    {
        string sql = raw.Trim().TrimEnd(';').Trim();

        if (sql.Length == 0) return (null, "the query was empty.");
        if (sql.Length > 4000) return (null, "the query was unreasonably long.");
        if (sql.Contains(';')) return (null, "only a single statement is allowed.");
        if (sql.Contains("--") || sql.Contains("/*")) return (null, "comments are not allowed in generated queries.");
        if (!StartsWithSelect().IsMatch(sql)) return (null, "only SELECT queries are allowed.");

        Match bad = Forbidden().Match(sql);
        if (bad.Success) return (null, $"\"{bad.Value}\" is not allowed in a read-only query.");

        if (allowed.Count > 0)
        {
            var ctes = CteNames().Matches(sql).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();

            string scanned = NeutraliseInnerFrom(sql);

            foreach (Match table in FromOrJoin().Matches(scanned))
            {
                string name = table.Groups[1].Value.Trim('"', '[', ']', '`').ToLowerInvariant();
                string bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].Trim('"', '[', ']', '`') : name;

                if (!allowed.Contains(name) && !allowed.Contains(bare) && !ctes.Contains(bare))
                {
                    return (null, $"the table \"{name}\" is not in this source's allowed tables.");
                }
            }
        }

        return (sql, null);
    }

    /// <summary>
    /// For the admin "preview a query" tool: one SELECT / WITH statement. Unlike AI-written queries this may
    /// contain comments and read the catalog (an admin looks at table structure), but it can never write.
    /// </summary>
    public static string? ValidateAdminSql(string raw)
    {
        string sql = raw.Trim().TrimEnd(';').Trim();

        if (sql.Length == 0) return "the query is empty.";
        if (sql.Contains(';')) return "preview runs one statement at a time.";
        if (!StartsWithSelect().IsMatch(StripLeadingComments(sql))) return "only SELECT queries can be previewed.";

        Match bad = AdminForbidden().Match(sql);
        return bad.Success ? $"\"{bad.Value}\" is not allowed in a read-only query." : null;
    }

    private static string StripLeadingComments(string sql)
    {
        string text = sql.TrimStart();
        while (text.StartsWith("--"))
        {
            int end = text.IndexOf('\n');
            text = end < 0 ? "" : text[(end + 1)..].TrimStart();
        }

        return text;
    }

    [GeneratedRegex(
        @"\b(insert|update|delete|drop|alter|create|truncate|grant|revoke|copy|merge|call|execute|exec|vacuum|reset|listen|notify|into|pg_sleep|waitfor|xp_\w+|dblink|lo_import|lo_export)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AdminForbidden();

    public static (string? Query, string? Problem) ValidateRest(
        string raw, HashSet<string> allowed, IReadOnlySet<string> knownTables, int maxRows)
    {
        var lines = new List<string>();

        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3))
        {
            string path = SupabaseQuery.NormalizeRestPath(line);
            if (path.Length == 0) continue;

            string decoded = Uri.UnescapeDataString(path);
            int question = decoded.IndexOf('?');
            string table = (question >= 0 ? decoded[..question] : decoded).Trim();
            string query = question >= 0 ? decoded[question..] : "";

            if (table.Contains('/') || table.StartsWith("rpc", StringComparison.OrdinalIgnoreCase) || decoded.Contains(".."))
            {
                return (null, "only plain table reads are allowed (no rpc/ calls).");
            }

            bool known = knownTables.Count > 0 && knownTables.Contains(table);
            bool listed = allowed.Contains(table.ToLowerInvariant());
            if ((knownTables.Count > 0 || allowed.Count > 0) && !known && !listed)
            {
                return (null, $"the table \"{table}\" is not available to this source.");
            }

            if (allowed.Count > 0 && !listed)
            {
                return (null, $"the table \"{table}\" is not in this source's allowed tables.");
            }

            // Always bounded: add a limit when missing, clamp one that is too high.
            Match limit = LimitParam().Match(query);
            if (!limit.Success)
            {
                query += (query.Length == 0 ? "?" : "&") + $"limit={maxRows}";
            }
            else if (int.TryParse(limit.Groups[1].Value, out int n) && n > maxRows)
            {
                query = LimitParam().Replace(query, $"limit={maxRows}");
            }

            lines.Add(SupabaseQuery.NormalizeRestPath(table + query));
        }

        return lines.Count == 0 ? (null, "no request was produced.") : (string.Join('\n', lines), null);
    }

    /// <summary>
    /// "EXTRACT(YEAR FROM \"Sales Date\")", "SUBSTRING(x FROM 1 FOR 3)", "TRIM(BOTH ' ' FROM x)" and
    /// "a IS DISTINCT FROM b" contain the word FROM without naming a table. Blank those words out (same
    /// length) so only real FROM / JOIN clauses are checked against the allowed tables.
    /// </summary>
    private static string NeutraliseInnerFrom(string sql)
    {
        char[] chars = sql.ToCharArray();

        foreach (Match function in FunctionWithFrom().Matches(sql))
        {
            int open = function.Index + function.Length - 1;
            int depth = 0;

            for (int i = open; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')' && --depth == 0)
                {
                    foreach (Match from in WordFrom().Matches(sql[open..i]))
                    {
                        for (int k = 0; k < from.Length; k++) chars[open + from.Index + k] = ' ';
                    }

                    break;
                }
            }
        }

        return DistinctFrom().Replace(new string(chars), m => new string(' ', m.Length));
    }

    [GeneratedRegex(@"\b(?:extract|substring|trim|overlay|position)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionWithFrom();

    [GeneratedRegex(@"\bfrom\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordFrom();

    [GeneratedRegex(@"\bdistinct\s+from\b", RegexOptions.IgnoreCase)]
    private static partial Regex DistinctFrom();

    [GeneratedRegex(@"^\s*(select|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithSelect();

    [GeneratedRegex(
        @"\b(insert|update|delete|drop|alter|create|truncate|grant|revoke|copy|merge|call|execute|exec|vacuum|reset|listen|notify|into|pg_sleep|waitfor|xp_\w+|openrowset|opendatasource|dblink|lo_import|lo_export)\b|\bpg_\w+\s*\(|\b(auth|storage|vault|pg_catalog|information_schema|sys)\.",
        RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"\b(?:from|join)\s+(""[^""]+""(?:\.""[^""]+"")?|\[[^\]]+\](?:\.\[[^\]]+\])?|[\w.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromOrJoin();

    [GeneratedRegex(@"\b(\w+)\s+as\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CteNames();

    [GeneratedRegex(@"(?<![\w])limit=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LimitParam();
}
