using MgtAiAuthen.Api.Contracts;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// One implementation per <see cref="DataSourceTypes"/>. Adding a fifth source type is a new
/// class plus one DI line — the same shape as <c>IAiProvider</c> for the AI vendors.
///
/// Every tester here does a real check — none fabricate success. A real external credential
/// problem (a fake secret, a Fabric admin setting not yet enabled) still surfaces as a genuine
/// failure with the provider's own error message, never a silent pretend-success.
/// </summary>
public interface IDataSourceConnectionTester
{
    /// <summary>Must match a value from <see cref="DataSourceTypes"/>.</summary>
    string SourceType { get; }

    Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct);
}

/// <summary>
/// Checks a folder path (local disk or a UNC share) exists and is readable from the server.
///
/// This does not distinguish "on the local disk" from "a mapped network share" — both are just
/// paths from .NET's point of view, and a UNC path (\\server\share\...) covers the on-prem SMB
/// case some sites still use instead of SharePoint.
/// </summary>
public class LocalFolderConnectionTester : IDataSourceConnectionTester
{
    public string SourceType => DataSourceTypes.LocalFolder;

    public Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        string path = config.GetValueOrDefault("path", "").Trim();

        if (path.Length == 0)
        {
            return Task.FromResult(new DataSourceTestResult(false, "No path is configured.", DateTime.Now));
        }

        if (!Directory.Exists(path))
        {
            // Distinguishing "does not exist" from "exists but the app has no permission" matters:
            // an admin acting on the first message would delete a folder they meant to keep, and
            // acting on the second would grant permissions to a path that is actually fine once
            // the app's service account can reach it.
            return Task.FromResult(new DataSourceTestResult(false,
                $"The folder does not exist, or the server this app runs on cannot see it: {path}",
                DateTime.Now));
        }

        try
        {
            int count = Directory.EnumerateFileSystemEntries(path).Take(1000).Count();
            return Task.FromResult(new DataSourceTestResult(true,
                $"Folder is reachable — {count}{(count == 1000 ? "+" : "")} item(s) at the top level.",
                DateTime.Now));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new DataSourceTestResult(false,
                "The folder exists, but the account this app runs as does not have permission to " +
                "list it. Grant read access to that account.", DateTime.Now));
        }
        catch (IOException ex)
        {
            return Task.FromResult(new DataSourceTestResult(false,
                $"The folder could not be read: {ex.Message}", DateTime.Now));
        }
    }
}

/// <summary>
/// Sends one request to the configured base URL with the configured auth applied, and reports
/// what came back. Reaching the server at all — even a 404 — is treated as success for the
/// "is this configured correctly" question; only a network failure or a rejected credential
/// (401/403) is reported as a failure, because this app cannot know whether a given endpoint
/// existing is the right test for every API someone registers here.
/// </summary>
public class ApiConnectionTester(IHttpClientFactory httpClientFactory) : IDataSourceConnectionTester
{
    public const string HttpClientName = "datasource-api";

    public string SourceType => DataSourceTypes.Api;

    public async Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        string baseUrl = config.GetValueOrDefault("baseUrl", "").Trim();
        string authType = config.GetValueOrDefault("authType", "None").Trim();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return new DataSourceTestResult(false, $"\"{baseUrl}\" is not a valid absolute URL.", DateTime.Now);
        }

        HttpClient http = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(ResolveMethod(config), uri);
        ApplyAuth(request, authType, config, secret);
        request.Content = BuildBody(config);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResolveTimeout(config));

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, timeoutCts.Token);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return new DataSourceTestResult(false,
                    $"Reached {uri.Host}, but the server rejected the credentials (HTTP " +
                    $"{(int)response.StatusCode}). Check the auth type and secret.", DateTime.Now);
            }

            return new DataSourceTestResult(true,
                $"Reached {uri.Host} — HTTP {(int)response.StatusCode}. " +
                "This confirms the server is reachable and the credentials were not rejected; it " +
                "does not confirm any specific endpoint works.", DateTime.Now);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DataSourceTestResult(false,
                $"Timed out waiting for {uri.Host} (after {ResolveTimeout(config).TotalMinutes:0.#} minute(s) — " +
                "raise \"Timeout (minutes)\" if this endpoint is just slow).", DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceTestResult(false, $"Could not reach {uri.Host}: {ex.Message}", DateTime.Now);
        }
    }

    /// <summary>
    /// Blank/missing "timeoutMinutes" = 2 minutes. A per-request cancellation (rather than the
    /// HttpClient's own Timeout, which is fixed per named client) so every source can set its own
    /// value — a slow internal report endpoint needs longer than a quick health check does.
    /// </summary>
    internal static TimeSpan ResolveTimeout(Dictionary<string, string> config)
    {
        string raw = config.GetValueOrDefault("timeoutMinutes", "").Trim();
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(2);
    }

    /// <summary>Blank/missing "method" = GET, so every source registered before this field existed is unaffected.</summary>
    internal static HttpMethod ResolveMethod(Dictionary<string, string> config)
        => string.Equals(config.GetValueOrDefault("method", ""), "POST", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;

    /// <summary>
    /// "requestBody" is only meaningful for POST — sent as-is (the admin types raw JSON, or
    /// whatever the target endpoint expects) with an application/json content type. A GET never
    /// carries a body, even if one was typed in and the method was switched back afterwards.
    ///
    /// A POST always gets a body, even when the admin left "requestBody" blank — an empty
    /// StringContent still sets the Content-Type header, whereas a null HttpContent sends the
    /// request with no Content-Type at all. Several ASP.NET-style APIs (including this app's own
    /// backend) reject a POST with no Content-Type as 415 Unsupported Media Type before even
    /// looking at the body, so a bare "{}" here is the difference between reaching the endpoint
    /// and never getting past its model binder.
    /// </summary>
    internal static HttpContent? BuildBody(Dictionary<string, string> config)
    {
        if (ResolveMethod(config) != HttpMethod.Post) return null;

        string body = config.GetValueOrDefault("requestBody", "").Trim();
        return new StringContent(body.Length == 0 ? "{}" : body, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Internal (not private) so <see cref="DataSourceFetchers.ApiFetcher"/> applies the exact same
    /// auth logic when actually pulling data — one place decides how a secret becomes a header.
    /// </summary>
    internal static void ApplyAuth(
        HttpRequestMessage request, string authType, Dictionary<string, string> config, string? secret)
    {
        switch (authType.ToLowerInvariant())
        {
            case "apikey":
                string header = config.GetValueOrDefault("apiKeyHeader", "X-API-Key");
                request.Headers.TryAddWithoutValidation(header, secret ?? "");
                break;

            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? "");
                break;

            case "basic":
                string username = config.GetValueOrDefault("username", "");
                string raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
                break;

            // "none" — no header.
        }
    }
}

/// <summary>
/// A real SharePoint Online test: client-credentials OAuth2 against Microsoft Entra ID, then one
/// Microsoft Graph call for the configured site. Written as plain HTTP (matching the Gemini and
/// OpenAI clients elsewhere in this app) rather than pulling in the Graph SDK or MSAL for what is,
/// here, two REST calls.
/// </summary>
public class SharePointConnectionTester(IHttpClientFactory httpClientFactory) : IDataSourceConnectionTester
{
    public const string HttpClientName = "datasource-sharepoint";

    public string SourceType => DataSourceTypes.SharePoint;

    public async Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        string tenantId = config.GetValueOrDefault("tenantId", "").Trim();
        string clientId = config.GetValueOrDefault("clientId", "").Trim();
        string siteUrl = config.GetValueOrDefault("siteUrl", "").Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            return new DataSourceTestResult(false,
                "No client secret is configured for this Entra ID app registration.", DateTime.Now);
        }

        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out Uri? site))
        {
            return new DataSourceTestResult(false, $"\"{siteUrl}\" is not a valid SharePoint site URL.", DateTime.Now);
        }

        HttpClient http = httpClientFactory.CreateClient(HttpClientName);

        (string? token, string? tokenError) = await EntraAuth.AcquireTokenAsync(http, tenantId, clientId, secret, ct);
        if (token is null)
        {
            return new DataSourceTestResult(false, tokenError!, DateTime.Now);
        }

        // A full site URL (https://contoso.sharepoint.com/sites/Sales) becomes the Graph path
        // "contoso.sharepoint.com:/sites/Sales" — Graph's documented way to address a site by URL.
        string graphPath = EntraAuth.GraphSitePath(site);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://graph.microsoft.com/v1.0/sites/{graphPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                string name = EntraAuth.ReadJsonField(body, "displayName") ?? site.AbsolutePath;
                return new DataSourceTestResult(true,
                    $"Connected to SharePoint site \"{name}\".", DateTime.Now);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new DataSourceTestResult(false,
                    $"Signed in, but no site was found at {siteUrl}. Check the site URL.", DateTime.Now);
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return new DataSourceTestResult(false,
                    "Signed in, but the app registration has no permission to read this site. " +
                    "Grant it Sites.Read.All (or a narrower Sites.Selected grant) in Entra ID.",
                    DateTime.Now);
            }

            return new DataSourceTestResult(false,
                $"Microsoft Graph returned HTTP {(int)response.StatusCode}: {EntraAuth.Truncate(body, 150)}",
                DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceTestResult(false,
                $"Could not reach Microsoft Graph: {ex.Message}", DateTime.Now);
        }
    }
}

/// <summary>
/// The "Data Lake / Lakehouse" type covers two different ways of reading from Microsoft Fabric,
/// chosen per source via the "connectionMode" config key (see <see cref="DataLakeMode"/>):
/// a Power BI / Fabric "Semantic model" queried with DAX through the Power BI REST API, or a
/// Fabric Warehouse/Lakehouse SQL analytics endpoint queried with plain T-SQL. Both authenticate
/// the same way — client-credentials OAuth2 against Entra ID (same flow as SharePoint, different
/// scope/token audience) — so this tester acquires the token once and hands off to whichever
/// query engine the mode calls for.
///
/// Two prerequisites this app cannot satisfy from code, for either mode: the Entra ID app
/// registration needs the right API permission (Power BI Service API, e.g. Dataset.Read.All, for
/// PowerBi mode — SQL-level access granted to the service principal for Warehouse mode), and a
/// Fabric admin must enable "Allow service principals to use Fabric APIs" (or a security-group-
/// scoped version of it) in the Fabric admin portal — Fabric rejects every service-principal call
/// with no such error message until that switch is on, so a persistent 401/403 here usually means
/// that setting, not a wrong secret.
/// </summary>
public class PowerBiConnectionTester(IHttpClientFactory httpClientFactory) : IDataSourceConnectionTester
{
    public const string HttpClientName = "datasource-powerbi";

    public string SourceType => DataSourceTypes.DataLake;

    public async Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
    {
        string tenantId = config.GetValueOrDefault("tenantId", "").Trim();
        string clientId = config.GetValueOrDefault("clientId", "").Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            return new DataSourceTestResult(false, "No client secret is configured for this Entra ID app registration.", DateTime.Now);
        }

        HttpClient http = httpClientFactory.CreateClient(HttpClientName);

        if (DataLakeMode.Resolve(config) == DataLakeMode.Warehouse)
        {
            FabricWarehouseQueryResult wh = await FabricWarehouseQuery.ExecuteAsync(
                http, tenantId, clientId, secret, config, sqlOverride: null, ct);
            return new DataSourceTestResult(wh.Success,
                wh.Success ? $"Connected — the query returned {wh.RowCount} row(s)." : wh.Message,
                DateTime.Now);
        }

        string datasetId = config.GetValueOrDefault("datasetId", "").Trim();
        string daxQuery = config.GetValueOrDefault("daxQuery", "").Trim();

        (string? token, string? tokenError) = await EntraAuth.AcquireTokenAsync(
            http, tenantId, clientId, secret, ct, EntraAuth.PowerBiScope);
        if (token is null)
        {
            return new DataSourceTestResult(false, tokenError!, DateTime.Now);
        }

        try
        {
            PowerBiQueryResult result = await PowerBiQuery.ExecuteAsync(http, token, datasetId, daxQuery, ct);
            return new DataSourceTestResult(result.Success,
                result.Success ? $"Connected — the DAX query returned {result.RowCount} row(s)." : result.Message,
                DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceTestResult(false, $"Could not reach the Power BI API: {ex.Message}", DateTime.Now);
        }
    }
}

/// <summary>Which of the two Fabric query engines a DataLake source uses. Blank/missing = PowerBi, so every source registered before this field existed keeps behaving exactly as it did.</summary>
internal static class DataLakeMode
{
    public const string PowerBi = "PowerBi";
    public const string Warehouse = "Warehouse";

    public static string Resolve(Dictionary<string, string> config)
        => string.Equals(config.GetValueOrDefault("connectionMode", ""), Warehouse, StringComparison.OrdinalIgnoreCase)
            ? Warehouse
            : PowerBi;
}

/// <summary>
/// Client-credentials OAuth2 + small Graph/PBI JSON helpers shared between
/// <see cref="SharePointConnectionTester"/>/<see cref="PowerBiConnectionTester"/> (which only check
/// the sign-in works) and <see cref="DataSourceFetchers.SharePointFetcher"/>/<see cref="DataSourceFetchers.PowerBiFetcher"/>
/// (which actually pull data) — one place decides how a client secret becomes an access token,
/// for whichever Microsoft API the caller passes as <paramref name="scope"/>.
/// </summary>
internal static class EntraAuth
{
    public const string GraphScope = "https://graph.microsoft.com/.default";
    public const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";
    // Fabric Warehouse/Lakehouse SQL endpoints sit on the same engine surface as Azure SQL, so they
    // accept the standard Azure SQL Database AAD audience — there is no separate Fabric-specific one.
    public const string SqlScope = "https://database.windows.net/.default";

    public static async Task<(string? Token, string? Error)> AcquireTokenAsync(
        HttpClient http, string tenantId, string clientId, string secret, CancellationToken ct,
        string scope = GraphScope)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = scope,
                ["grant_type"] = "client_credentials",
            });

            using HttpResponseMessage tokenResponse = await http.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
                form, ct);

            string tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                string detail = ReadJsonField(tokenBody, "error_description") ?? tokenBody;
                return (null, $"Entra ID rejected the sign-in (HTTP {(int)tokenResponse.StatusCode}): " +
                               $"{Truncate(detail, 200)}");
            }

            string? token = ReadJsonField(tokenBody, "access_token");
            return token is null ? (null, "Entra ID returned no access token.") : (token, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach Microsoft Entra ID: {ex.Message}");
        }
    }

    /// <summary>https://contoso.sharepoint.com/sites/Sales → "contoso.sharepoint.com:/sites/Sales"</summary>
    public static string GraphSitePath(Uri site) => $"{site.Host}:{site.AbsolutePath}";

    public static string? ReadJsonField(string json, string field)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out JsonElement value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

internal record PowerBiQueryResult(bool Success, string Message, string? RawJson, int? RowCount);

/// <summary>
/// Runs one DAX query against a Power BI / Fabric semantic model via the "Execute Queries" REST
/// endpoint. Shared by <see cref="PowerBiConnectionTester"/> (reports row count) and
/// <see cref="DataSourceFetchers.PowerBiFetcher"/> (hands the raw JSON to the AI) so "Test
/// connection" runs the exact same query the real fetch would.
/// </summary>
internal static class PowerBiQuery
{
    public static async Task<PowerBiQueryResult> ExecuteAsync(
        HttpClient http, string token, string datasetId, string daxQuery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
        {
            return new PowerBiQueryResult(false, "No dataset (semantic model) ID is configured.", null, null);
        }

        if (string.IsNullOrWhiteSpace(daxQuery))
        {
            return new PowerBiQueryResult(false, "No DAX query is configured.", null, null);
        }

        var payload = new
        {
            queries = new[] { new { query = daxQuery } },
            serializerSettings = new { includeNulls = true },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.powerbi.com/v1.0/myorg/datasets/{Uri.EscapeDataString(datasetId)}/executeQueries");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string detail = EntraAuth.ReadJsonField(body, "message") ?? EntraAuth.Truncate(body, 300);
            string hint = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? " This usually means the Entra app registration lacks the Power BI Service API " +
                  "permission (e.g. Dataset.Read.All), or a Fabric admin has not enabled \"Allow " +
                  "service principals to use Fabric APIs\" for it."
                : "";
            return new PowerBiQueryResult(false, $"Power BI returned HTTP {(int)response.StatusCode}: {detail}{hint}", body, null);
        }

        return new PowerBiQueryResult(true, "OK", body, CountFirstTableRows(body));
    }

    private static int? CountFirstTableRows(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("results")[0]
                .GetProperty("tables")[0]
                .GetProperty("rows")
                .GetArrayLength();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal record FabricWarehouseQueryResult(bool Success, string Message, string? FormattedText, int? RowCount, bool Truncated = false);

/// <summary>
/// Runs one T-SQL query against a Fabric Warehouse/Lakehouse SQL analytics endpoint and formats
/// the result as a Markdown table (cheap to read, and the same shape <c>MessageContent.jsx</c>
/// already renders nicely if it ever reaches the user verbatim). Shared by
/// <see cref="PowerBiConnectionTester"/> (reports row count) and
/// <see cref="DataSourceFetchers.PowerBiFetcher"/> (hands the table to the AI) so "Test connection"
/// runs the exact same query the real fetch would.
///
/// Authenticates the same way as <see cref="PowerBiQuery"/> — client-credentials OAuth2 against
/// Entra ID — but for the SQL scope, then hands the raw access token to <see cref="SqlConnection"/>
/// directly (its <c>AccessToken</c> property) rather than building an AAD-mode connection string,
/// so this needs no tenant-specific connection-string syntax to get right.
/// </summary>
internal static class FabricWarehouseQuery
{
    public static async Task<FabricWarehouseQueryResult> ExecuteAsync(
        HttpClient http, string tenantId, string clientId, string secret,
        Dictionary<string, string> config, string? sqlOverride, CancellationToken ct)
    {
        string sqlEndpoint = NormalizeEndpoint(config.GetValueOrDefault("sqlEndpoint", ""));
        string database = config.GetValueOrDefault("database", "").Trim();
        string sqlQuery = string.IsNullOrWhiteSpace(sqlOverride)
            ? config.GetValueOrDefault("sqlQuery", "").Trim()
            : sqlOverride.Trim();

        if (sqlEndpoint.Length == 0)
        {
            return new FabricWarehouseQueryResult(false, "No SQL endpoint is configured.", null, null);
        }

        if (database.Length == 0)
        {
            return new FabricWarehouseQueryResult(false, "No database (warehouse/lakehouse) name is configured.", null, null);
        }

        if (sqlQuery.Length == 0)
        {
            return new FabricWarehouseQueryResult(false, "No SQL query is configured.", null, null);
        }

        (string? token, string? tokenError) = await EntraAuth.AcquireTokenAsync(
            http, tenantId, clientId, secret, ct, EntraAuth.SqlScope);
        if (token is null)
        {
            return new FabricWarehouseQueryResult(false, tokenError!, null, null);
        }

        string connectionString =
            $"Server=tcp:{sqlEndpoint},1433;Initial Catalog={database};Encrypt=True;TrustServerCertificate=False;";

        try
        {
            await using var connection = new SqlConnection(connectionString) { AccessToken = token };
            await connection.OpenAsync(ct);

            await using var command = new SqlCommand(sqlQuery, connection)
            {
                CommandTimeout = (int)Math.Max(1, ApiConnectionTester.ResolveTimeout(config).TotalSeconds),
            };

            await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
            return FormatAsMarkdownTable(reader, await ReadAllAsync(reader, ct));
        }
        catch (SqlException ex)
        {
            string hint = ex.Number is 18456 or 4060 or 40615
                ? " This usually means the service principal has not been granted SQL access to " +
                  "this warehouse/database (a Fabric admin or the item owner grants it), or a " +
                  "Fabric admin has not enabled \"Allow service principals to use Fabric APIs\"."
                : "";
            return new FabricWarehouseQueryResult(false,
                $"The Fabric Warehouse SQL endpoint rejected the query: {ex.Message}{hint}", null, null);
        }
    }

    /// <summary>Strips a stray "tcp:" prefix or ",1433" port someone pasted along with the hostname — the connection string below adds both itself.</summary>
    private static string NormalizeEndpoint(string raw)
    {
        string endpoint = raw.Trim().TrimStart(' ').Replace("tcp:", "", StringComparison.OrdinalIgnoreCase);
        int comma = endpoint.IndexOf(',');
        return (comma >= 0 ? endpoint[..comma] : endpoint).Trim();
    }

    private static async Task<List<object?[]>> ReadAllAsync(SqlDataReader reader, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        while (rows.Count < DataSourceFetchLimits.MaxWarehouseRows && await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }
        return rows;
    }

    private static FabricWarehouseQueryResult FormatAsMarkdownTable(SqlDataReader reader, List<object?[]> rows)
    {
        int columnCount = reader.FieldCount;
        string[] columns = Enumerable.Range(0, columnCount).Select(reader.GetName).ToArray();

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", columns)).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).Append(" |\n");

        int written = 0;
        bool truncated = false;
        foreach (object?[] row in rows)
        {
            string line = "| " + string.Join(" | ", row.Select(FormatCell)) + " |\n";
            if (sb.Length + line.Length > DataSourceFetchLimits.MaxChars)
            {
                truncated = true;
                break;
            }
            sb.Append(line);
            written++;
        }

        return new FabricWarehouseQueryResult(true, "OK", sb.ToString(), written, truncated);
    }

    private static string FormatCell(object? value)
        => value is null or DBNull
            ? ""
            : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                .Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
