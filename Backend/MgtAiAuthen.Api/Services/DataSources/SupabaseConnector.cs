using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using Npgsql;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// A Supabase project is reachable two ways, picked per source with the "connectionMode" config key:
/// <list type="bullet">
/// <item><b>Sql</b> (default) - straight to the project's Postgres database with a SQL query (or several
/// SELECTs separated by semicolons). Needs the database host/port/user and its password.</item>
/// <item><b>Rest</b> - the project's REST API (PostgREST): one request per line such as
/// <c>customers?select=id,name&amp;status=eq.active</c>, authenticated with the project's API key.</item>
/// </list>
/// Both are read-only by construction: SQL runs inside a READ ONLY transaction and REST only issues GETs.
/// </summary>
internal static class SupabaseMode
{
    public const string Sql = "Sql";
    public const string Rest = "Rest";

    public static string Resolve(Dictionary<string, string> config)
        => string.Equals(config.GetValueOrDefault("connectionMode", "").Trim(), Rest, StringComparison.OrdinalIgnoreCase)
            ? Rest
            : Sql;
}

internal static class SupabaseQuery
{
    private const int RestPageSize = 1000;

    public static Task<FabricWarehouseQueryResult> ExecuteAsync(
        HttpClient http, Dictionary<string, string> config, string? secret, string? queryOverride, CancellationToken ct)
        => SupabaseMode.Resolve(config) == SupabaseMode.Rest
            ? ExecuteRestAsync(http, config, secret, queryOverride, ct)
            : ExecuteSqlAsync(config, secret, queryOverride, ct);

    // ------------------------------------------------------------------ SQL (Postgres)

    private static async Task<FabricWarehouseQueryResult> ExecuteSqlAsync(
        Dictionary<string, string> config, string? secret, string? sqlOverride, CancellationToken ct)
    {
        string host = config.GetValueOrDefault("host", "").Trim();
        string database = config.GetValueOrDefault("database", "").Trim();
        string username = config.GetValueOrDefault("username", "").Trim();
        string sql = string.IsNullOrWhiteSpace(sqlOverride)
            ? config.GetValueOrDefault("sqlQuery", "").Trim()
            : sqlOverride.Trim();

        if (host.Length == 0) return Fail("No database host is configured.");
        if (sql.Length == 0) return Fail("No SQL query is configured.");
        if (string.IsNullOrWhiteSpace(secret)) return Fail("No database password is configured (put it in Secret).");

        int port = int.TryParse(config.GetValueOrDefault("port", "").Trim(), out int p) && p is > 0 and < 65536 ? p : 5432;
        int timeoutSeconds = (int)Math.Max(1, ApiConnectionTester.ResolveTimeout(config).TotalSeconds);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database.Length > 0 ? database : "postgres",
            Username = username.Length > 0 ? username : "postgres",
            Password = secret,
            // Supabase only accepts TLS. Its certificate chain is not in the OS trust store on every server,
            // so the certificate is accepted (the connection is still encrypted).
            SslMode = SslMode.Require,
            TrustServerCertificate = true,
            // Supabase's pooler (pgbouncer) hands connections around between sessions - a short-lived
            // connection per query keeps session state from leaking or going missing.
            Pooling = false,
            Timeout = 15,
            CommandTimeout = timeoutSeconds,
            ApplicationName = "MGT_AI_Authen",
        };

        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);

            // Read-only at the database level: even a query that was edited into something harmful can only read.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(ct);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds };
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
            return await FabricWarehouseQuery.FormatAllResultSetsAsync(reader, ct, DataSourceFetchLimits.Resolve(config));
        }
        catch (PostgresException ex)
        {
            string hint = ex.SqlState switch
            {
                "28P01" or "28000" => " Check the database user and password (Project Settings > Database). With the pooler the user is \"postgres.<project-ref>\".",
                "42501" => " The database user is not allowed to read that table - grant SELECT, or use a role that can.",
                "25006" => " The query tried to change data - this connection is read-only.",
                _ => "",
            };
            return Fail($"Supabase rejected the query: {ex.MessageText}{hint}");
        }
        catch (NpgsqlException ex) when (ex.InnerException is not OperationCanceledException)
        {
            return Fail(
                $"Could not connect to the Supabase database at {host}:{port}: {ex.Message}. " +
                "The direct host (db.<ref>.supabase.co) is IPv6-only on many plans - if this server has no " +
                "IPv6, use the Session pooler host from Project Settings > Database > Connection string.");
        }
    }

    // ------------------------------------------------------------------ REST (PostgREST)

    private static async Task<FabricWarehouseQueryResult> ExecuteRestAsync(
        HttpClient http, Dictionary<string, string> config, string? secret, string? queryOverride, CancellationToken ct)
    {
        string projectUrl = NormalizeProjectUrl(config.GetValueOrDefault("projectUrl", ""));
        string schema = config.GetValueOrDefault("schema", "").Trim();
        string[] queries = (string.IsNullOrWhiteSpace(queryOverride) ? config.GetValueOrDefault("restQuery", "") : queryOverride)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeRestPath)
            .Where(q => q.Length > 0)
            .ToArray();

        if (projectUrl.Length == 0) return Fail("No project URL is configured (https://<project-ref>.supabase.co).");
        if (!Uri.TryCreate(projectUrl, UriKind.Absolute, out Uri? baseUri) || !IsAllowedScheme(baseUri))
        {
            return Fail("The project URL must be an https:// address, e.g. https://abcdefgh.supabase.co");
        }

        if (queries.Length == 0) return Fail("No REST query is configured (one per line, e.g. customers?select=*&limit=100).");
        if (string.IsNullOrWhiteSpace(secret)) return Fail("No API key is configured (put the anon or service_role key in Secret).");

        FetchCaps caps = DataSourceFetchLimits.Resolve(config);
        var sb = new StringBuilder();
        var tables = new List<QueryTable>();
        int totalRows = 0;
        bool truncated = false;

        for (int i = 0; i < queries.Length && !truncated; i++)
        {
            if (queries.Length > 1)
            {
                sb.Append($"### Query {i + 1}: {queries[i]}\n\n");
            }

            (bool ok, string message, List<JsonElement> rows, string? rawText, bool more) =
                await FetchRestAsync(http, baseUri, queries[i], schema, secret, caps, ct);

            if (!ok) return Fail(message);

            if (rawText is not null)
            {
                tables.Add(new QueryTable(["result"], [[rawText]]));
                if (!caps.SkipText) sb.Append(rawText).Append('\n');
            }
            else if (caps.SkipText)
            {
                tables.Add(ToTable(rows));
                totalRows += rows.Count;
                truncated = more;
            }
            else
            {
                (int written, bool cut) = AppendTable(sb, rows, caps.MaxChars);
                totalRows += written;
                truncated = cut || more;
                tables.Add(ToTable(rows));
            }

            if (queries.Length > 1) sb.Append('\n');
            if (sb.Length > caps.MaxChars) truncated = true;
        }

        string text = sb.Length > caps.MaxChars ? sb.ToString(0, caps.MaxChars) : sb.ToString();
        return new FabricWarehouseQueryResult(true, "OK", text, totalRows, truncated, tables);
    }

    /// <summary>Supabase is https-only; plain http is accepted solely for this machine, so a local PostgREST can be used for development.</summary>
    internal static bool IsAllowedScheme(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>"https://abc.supabase.co/rest/v1/Sales" or "https://abc.supabase.co/" both mean the project https://abc.supabase.co.</summary>
    internal static string NormalizeProjectUrl(string raw)
    {
        string url = raw.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : url.TrimEnd('/');
    }

    /// <summary>
    /// Accepts "customers?select=*", "/customers?...", "rest/v1/customers?..." or a whole pasted URL
    /// ("https://abc.supabase.co/rest/v1/customers?...") - the base path is added here. A table name
    /// with a space or other special characters ("Sales Demo") is percent-encoded.
    /// </summary>
    internal static string NormalizeRestPath(string raw)
    {
        string path = raw.Trim();

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            int marker = path.IndexOf("/rest/v1/", StringComparison.OrdinalIgnoreCase);
            path = marker >= 0 ? path[(marker + "/rest/v1/".Length)..] : string.Empty;
        }

        path = path.TrimStart('/');
        if (path.StartsWith("rest/v1/", StringComparison.OrdinalIgnoreCase)) path = path["rest/v1/".Length..];

        int question = path.IndexOf('?');
        string table = question >= 0 ? path[..question] : path;
        string query = question >= 0 ? path[question..] : string.Empty;

        // Already-encoded names (contain %) are left alone; otherwise encode each path segment.
        if (!table.Contains('%'))
        {
            table = string.Join('/', table.Split('/').Select(Uri.EscapeDataString));
        }

        // A space in the filter part would break the request line as well.
        return table + query.Replace(" ", "%20");
    }

    /// <summary>
    /// One REST query. PostgREST caps a single response (1,000 rows by default on Supabase), so unless the
    /// query sets its own "limit" this pages through with Range headers until the table - or the row cap -
    /// is reached; that is what lets a 6,000-row table come back whole.
    /// </summary>
    private static async Task<(bool Ok, string Message, List<JsonElement> Rows, string? RawText, bool More)> FetchRestAsync(
        HttpClient http, Uri baseUri, string path, string schema, string apiKey, FetchCaps caps, CancellationToken ct)
    {
        bool hasOwnLimit = path.Contains("limit=", StringComparison.OrdinalIgnoreCase);
        var rows = new List<JsonElement>();
        long? total = null;
        int offset = 0;
        bool more = false;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/rest/v1/" + path));
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (schema.Length > 0) request.Headers.TryAddWithoutValidation("Accept-Profile", schema);

            if (!hasOwnLimit)
            {
                request.Headers.TryAddWithoutValidation("Range-Unit", "items");
                request.Headers.TryAddWithoutValidation("Range", $"{offset}-{offset + RestPageSize - 1}");
                request.Headers.TryAddWithoutValidation("Prefer", "count=exact");
            }

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, DescribeRestError(response.StatusCode, body, path), rows, null, false);
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException)
            {
                return (true, "OK", rows, body, false);
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                // A single object or scalar (e.g. an rpc/ function result) - hand it over as it came.
                return (true, "OK", rows, root.GetRawText(), false);
            }

            int before = rows.Count;
            foreach (JsonElement item in root.EnumerateArray())
            {
                if (rows.Count >= caps.MaxRows)
                {
                    more = true;
                    break;
                }

                rows.Add(item);
            }

            int got = rows.Count - before;
            if (hasOwnLimit || more || got == 0) break;

            if (total is null
                && response.Content.Headers.TryGetValues("Content-Range", out IEnumerable<string>? ranges))
            {
                string? range = ranges.FirstOrDefault();
                int slash = range?.LastIndexOf('/') ?? -1;
                if (slash >= 0 && long.TryParse(range![(slash + 1)..], out long parsed)) total = parsed;
            }

            offset += got;
            bool finished = total is { } t ? offset >= t : got < RestPageSize;
            if (finished) break;
        }

        return (true, "OK", rows, null, more);
    }

    private static string DescribeRestError(HttpStatusCode status, string body, string path)
    {
        string detail = body;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string? message = doc.RootElement.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
            string? hint = doc.RootElement.TryGetProperty("hint", out JsonElement h) ? h.GetString() : null;
            if (message is not null) detail = hint is null ? message : $"{message} (hint: {hint})";
        }
        catch (JsonException)
        {
            // Not JSON - keep the raw text.
        }

        if (detail.Length > 400) detail = detail[..400] + "...";

        string advice = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                " Check the API key. An anon key can only read tables whose Row Level Security allows it - use the service_role key for full access.",
            HttpStatusCode.NotFound => " Check the table name (and the schema, if it is not public).",
            _ => "",
        };

        return $"Supabase REST rejected \"{path}\" (HTTP {(int)status}): {detail}{advice}";
    }

    /// <summary>Writes a JSON array of rows as one Markdown table; columns are the union of every row's keys, in first-seen order.</summary>
    private static (int Written, bool Truncated) AppendTable(StringBuilder sb, List<JsonElement> rows, int maxChars)
    {
        if (rows.Count == 0)
        {
            sb.Append("(no rows)\n");
            return (0, false);
        }

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            foreach (JsonProperty property in row.EnumerateObject())
            {
                if (seen.Add(property.Name)) columns.Add(property.Name);
            }
        }

        bool scalarRows = columns.Count == 0;
        if (scalarRows) columns.Add("value");

        sb.Append("| ").Append(string.Join(" | ", columns)).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).Append(" |\n");

        int written = 0;
        foreach (JsonElement row in rows)
        {
            IEnumerable<string> cells = scalarRows
                ? [CellText(row)]
                : columns.Select(c => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(c, out JsonElement v) ? CellText(v) : "");

            string line = "| " + string.Join(" | ", cells) + " |\n";
            if (sb.Length + line.Length > maxChars) return (written, true);

            sb.Append(line);
            written++;
        }

        return (written, false);
    }

    /// <summary>The same rows as <see cref="AppendTable"/>, but with real numbers / booleans kept for an Excel export.</summary>
    private static QueryTable ToTable(List<JsonElement> rows)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            foreach (JsonProperty property in row.EnumerateObject())
            {
                if (seen.Add(property.Name)) columns.Add(property.Name);
            }
        }

        bool scalarRows = columns.Count == 0;
        if (scalarRows) columns.Add("value");

        var data = new List<object?[]>(rows.Count);
        foreach (JsonElement row in rows)
        {
            data.Add(scalarRows
                ? [Typed(row)]
                : columns.Select(c => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(c, out JsonElement v) ? Typed(v) : null).ToArray());
        }

        return new QueryTable(columns.ToArray(), data);
    }

    private static object? Typed(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.GetRawText(),
    };

    private static string CellText(JsonElement value)
    {
        string text = value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };

        return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private static FabricWarehouseQueryResult Fail(string message) => new(false, message, null, null);
}

/// <summary>"Test connection" runs the same query a real fetch would and reports how many rows came back.</summary>
public class SupabaseConnectionTester(IHttpClientFactory httpClientFactory) : IDataSourceConnectionTester
{
    public string SourceType => DataSourceTypes.Supabase;

    public async Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        HttpClient http = httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ApiConnectionTester.ResolveTimeout(config));

        try
        {
            if (string.Equals(config.GetValueOrDefault("queryMode", "").Trim(), "Generated", StringComparison.OrdinalIgnoreCase))
            {
                return await QuerySchema.TestAsync(
                    httpClientFactory, config, secret,
                    SupabaseMode.Resolve(config) == SupabaseMode.Rest
                        ? GeneratedQueryPlanner.Dialect.PostgREST
                        : GeneratedQueryPlanner.Dialect.Postgres,
                    timeoutCts.Token);
            }

            FabricWarehouseQueryResult result = await SupabaseQuery.ExecuteAsync(http, config, secret, null, timeoutCts.Token);

            return new DataSourceTestResult(
                result.Success,
                result.Success
                    ? $"Connected to Supabase ({SupabaseMode.Resolve(config)} mode) - the query returned {result.RowCount} row(s)" +
                      (result.Truncated ? " (more rows exist than the size limit allows)." : ".")
                    : result.Message,
                DateTime.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DataSourceTestResult(false,
                $"Timed out after {ApiConnectionTester.ResolveTimeout(config).TotalMinutes:0.#} minute(s) - raise \"Timeout (minutes)\" if the query is just slow.",
                DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceTestResult(false, $"Could not reach Supabase: {ex.Message}", DateTime.Now);
        }
    }
}

/// <summary>
/// Pulls the configured query's result for the AI. A grant's scope filter, or the per-message
/// query choice made in ChatService, REPLACES the configured query (SQL text in Sql mode, REST
/// lines in Rest mode) - neither language has a generic way to append a filter to an arbitrary query.
/// </summary>
public class SupabaseFetcher(IHttpClientFactory httpClientFactory) : IDataSourceFetcher
{
    public string SourceType => DataSourceTypes.Supabase;

    public async Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
    {
        HttpClient http = httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ApiConnectionTester.ResolveTimeout(config));

        FabricWarehouseQueryResult result;
        try
        {
            result = await SupabaseQuery.ExecuteAsync(http, config, secret, scopeFilter, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DataSourceFetchResult(false,
                $"Timed out waiting for Supabase (after {ApiConnectionTester.ResolveTimeout(config).TotalMinutes:0.#} minute(s) - " +
                "raise \"Timeout (minutes)\" if this query is just slow).", null, 0, false);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceFetchResult(false, $"Could not reach Supabase: {ex.Message}", null, 0, false);
        }

        if (!result.Success)
        {
            return new DataSourceFetchResult(false, result.Message, null, 0, false);
        }

        string text = result.FormattedText ?? "";
        return new DataSourceFetchResult(true,
            $"Fetched {result.RowCount} row(s) from Supabase{(result.Truncated ? " (truncated)" : "")}.",
            text, text.Length, result.Truncated);
    }
}
