using MgtAiAuthen.Api.Contracts;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// One implementation per <see cref="DataSourceTypes"/>. Adding a fifth source type is a new
/// class plus one DI line — the same shape as <c>IAiProvider</c> for the AI vendors.
///
/// Every tester here does a real check. None of them fabricate success: a type nobody has
/// implemented a connector for (Data Lake, until a platform is chosen) says so plainly instead of
/// pretending the test passed.
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

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);

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
            return new DataSourceTestResult(false, $"Timed out waiting for {uri.Host}.", DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceTestResult(false, $"Could not reach {uri.Host}: {ex.Message}", DateTime.Now);
        }
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

        (string? token, string? tokenError) = await SharePointAuth.AcquireTokenAsync(http, tenantId, clientId, secret, ct);
        if (token is null)
        {
            return new DataSourceTestResult(false, tokenError!, DateTime.Now);
        }

        // A full site URL (https://contoso.sharepoint.com/sites/Sales) becomes the Graph path
        // "contoso.sharepoint.com:/sites/Sales" — Graph's documented way to address a site by URL.
        string graphPath = SharePointAuth.GraphSitePath(site);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://graph.microsoft.com/v1.0/sites/{graphPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                string name = SharePointAuth.ReadJsonField(body, "displayName") ?? site.AbsolutePath;
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
                $"Microsoft Graph returned HTTP {(int)response.StatusCode}: {SharePointAuth.Truncate(body, 150)}",
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
/// Data Lake / Lakehouse has no connector yet — the platform was left undecided on purpose
/// ("ยังไม่มี/ยังไม่แน่ใจ — ทำโครงรอไว้"). This tester exists so the registry can hold a row for
/// planning without the UI having a dead button; it always says plainly that nothing has been
/// built rather than reporting a fake success or silently doing nothing.
/// </summary>
public class DataLakeConnectionTester : IDataSourceConnectionTester
{
    public string SourceType => DataSourceTypes.DataLake;

    public Task<DataSourceTestResult> TestAsync(
        Dictionary<string, string> config, string? secret, CancellationToken ct)
        => Task.FromResult(new DataSourceTestResult(false,
            "No Data Lake / Lakehouse connector is implemented yet — this entry is a placeholder " +
            "for planning. When a platform is chosen (e.g. Microsoft Fabric / OneLake, Databricks), " +
            "a connector needs to be built for it before this source can be tested or used.",
            DateTime.Now));
}

/// <summary>
/// Client-credentials OAuth2 + small Graph JSON helpers shared between
/// <see cref="SharePointConnectionTester"/> (which only checks the sign-in works) and
/// <see cref="DataSourceFetchers.SharePointFetcher"/> (which actually reads files) — one place
/// decides how a SharePoint secret becomes a Graph access token.
/// </summary>
internal static class SharePointAuth
{
    public static async Task<(string? Token, string? Error)> AcquireTokenAsync(
        HttpClient http, string tenantId, string clientId, string secret, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = "https://graph.microsoft.com/.default",
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
