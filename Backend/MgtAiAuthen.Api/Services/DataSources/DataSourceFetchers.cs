using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// Result of actually pulling data from a source — distinct from <see cref="DataSourceTestResult"/>,
/// which only checks that a connection works. <see cref="ContentText"/> is what gets sent to the AI
/// as context; it is null whenever nothing readable came back (a real failure, or a 0-file scope).
/// </summary>
public record DataSourceFetchResult(
    bool Success, string Message, string? ContentText, int CharCount, bool Truncated);

/// <summary>
/// One implementation per <see cref="DataSourceTypes"/> — mirrors <see cref="IDataSourceConnectionTester"/>
/// one level further: instead of "can I reach this", "what does this actually contain, respecting
/// the caller's grant". <see cref="DataSourceScopeTypes"/> beyond Full does not get any automatic
/// interpretation (e.g. no built-in "Division = Users.Department" matching) — the one lever every
/// fetcher honours is <paramref name="scopeFilter"/> verbatim, exactly as the admin typed it into
/// the grant's Custom scope filter (see 12_datasource_chat.sql).
/// </summary>
public interface IDataSourceFetcher
{
    /// <summary>Must match a value from <see cref="DataSourceTypes"/>.</summary>
    string SourceType { get; }

    Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct);
}

/// <summary>Caps shared by every fetcher so one huge folder/response can't blow up a chat context.</summary>
internal static class DataSourceFetchLimits
{
    public const int MaxChars = 120_000;
    public const int MaxFiles = 30;
    public const long MaxFileBytes = 5 * 1024 * 1024;
}

/// <summary>
/// Reads text-extractable files under the configured folder (optionally narrowed to a sub-path
/// named by the grant's scope filter) and concatenates them, same extraction rules as a chat
/// attachment (plain text decoded as UTF-8, workbooks converted to a text table).
/// </summary>
public class LocalFolderFetcher(ISpreadsheetTextExtractor spreadsheets) : IDataSourceFetcher
{
    private static readonly string[] TextExtensions = [".txt", ".csv", ".md", ".json", ".log"];
    private static readonly string[] SpreadsheetExtensions = [".xlsx", ".xls"];

    public string SourceType => DataSourceTypes.LocalFolder;

    public Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
    {
        string root = config.GetValueOrDefault("path", "").Trim();
        if (root.Length == 0)
        {
            return Task.FromResult(new DataSourceFetchResult(false, "No path is configured.", null, 0, false));
        }

        string effectivePath = root;
        if (!string.IsNullOrWhiteSpace(scopeFilter))
        {
            string rootFull = Path.GetFullPath(root);
            string candidate = Path.GetFullPath(Path.Combine(root, scopeFilter.Trim().TrimStart('/', '\\')));

            // Path-traversal guard: a scope filter like "..\..\Windows" must not escape the
            // configured root just because it is syntactically a valid relative path.
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new DataSourceFetchResult(false,
                    $"The scope filter \"{scopeFilter}\" resolves outside the configured folder — refusing to read it.",
                    null, 0, false));
            }

            effectivePath = candidate;
        }

        if (!Directory.Exists(effectivePath))
        {
            return Task.FromResult(new DataSourceFetchResult(false,
                $"Folder not found: {effectivePath}", null, 0, false));
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(effectivePath, "*", SearchOption.AllDirectories)
                .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                            || SpreadsheetExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(DataSourceFetchLimits.MaxFiles)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new DataSourceFetchResult(false,
                "The folder exists, but the account this app runs as cannot list it.", null, 0, false));
        }
        catch (IOException ex)
        {
            return Task.FromResult(new DataSourceFetchResult(false, $"Could not read the folder: {ex.Message}", null, 0, false));
        }

        if (files.Count == 0)
        {
            return Task.FromResult(new DataSourceFetchResult(true,
                $"No readable files (.txt/.csv/.md/.json/.log/.xlsx/.xls) found under {effectivePath}.", null, 0, false));
        }

        var sb = new StringBuilder();
        bool truncated = false;
        int skipped = 0;

        foreach (string file in files)
        {
            if (sb.Length >= DataSourceFetchLimits.MaxChars) { truncated = true; break; }

            string? text = ReadOneFile(file, spreadsheets, out bool ok);
            if (!ok) { skipped++; continue; }

            string relative = Path.GetRelativePath(root, file);
            string block = $"== {relative} ==\n{text}\n\n";

            if (sb.Length + block.Length > DataSourceFetchLimits.MaxChars)
            {
                block = block[..Math.Max(0, DataSourceFetchLimits.MaxChars - sb.Length)];
                truncated = true;
            }

            sb.Append(block);
        }

        string content = sb.ToString();
        string message = $"Read {files.Count - skipped} of {files.Count} file(s) under {effectivePath}" +
            (skipped > 0 ? $" ({skipped} skipped — unreadable or not text)" : "") +
            (truncated ? " — content truncated to fit the size limit." : ".");

        return Task.FromResult(new DataSourceFetchResult(true, message, content.Length > 0 ? content : null, content.Length, truncated));
    }

    private static string? ReadOneFile(string path, ISpreadsheetTextExtractor spreadsheets, out bool ok)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength > DataSourceFetchLimits.MaxFileBytes) { ok = false; return null; }

            string extension = Path.GetExtension(path);
            ok = true;

            if (SpreadsheetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return spreadsheets.Extract(bytes, Path.GetFileName(path));
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(bytes).TrimStart('﻿');
        }
        catch
        {
            ok = false;
            return null;
        }
    }
}

/// <summary>
/// GETs the configured endpoint (auth applied exactly as <see cref="ApiConnectionTester"/> tests it)
/// and hands the raw response body to the model as text. The grant's scope filter, when present, is
/// appended as a raw query string — the natural way to encode "who reads which slice of this API"
/// for an admin who already knows that API's own query parameters (e.g. "division=Sales").
/// </summary>
public class ApiFetcher(IHttpClientFactory httpClientFactory) : IDataSourceFetcher
{
    public string SourceType => DataSourceTypes.Api;

    public async Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
    {
        string baseUrl = config.GetValueOrDefault("baseUrl", "").Trim();
        string authType = config.GetValueOrDefault("authType", "None").Trim();

        if (!Uri.TryCreate(ApplyScopeFilter(baseUrl, scopeFilter), UriKind.Absolute, out Uri? uri))
        {
            return new DataSourceFetchResult(false, $"\"{baseUrl}\" is not a valid absolute URL.", null, 0, false);
        }

        HttpClient http = httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName);
        using var request = new HttpRequestMessage(ApiConnectionTester.ResolveMethod(config), uri);
        ApiConnectionTester.ApplyAuth(request, authType, config, secret);
        request.Content = ApiConnectionTester.BuildBody(config);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new DataSourceFetchResult(false,
                    $"{uri.Host} returned HTTP {(int)response.StatusCode}: {SharePointAuth.Truncate(body, 300)}",
                    null, 0, false);
            }

            bool truncated = body.Length > DataSourceFetchLimits.MaxChars;
            string content = truncated ? body[..DataSourceFetchLimits.MaxChars] : body;

            return new DataSourceFetchResult(true,
                $"Fetched {content.Length:N0} character(s) from {uri.Host}{(truncated ? " (truncated)" : "")}.",
                content, content.Length, truncated);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DataSourceFetchResult(false, $"Timed out waiting for {uri.Host}.", null, 0, false);
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceFetchResult(false, $"Could not reach {uri.Host}: {ex.Message}", null, 0, false);
        }
    }

    private static string ApplyScopeFilter(string baseUrl, string? scopeFilter)
    {
        if (string.IsNullOrWhiteSpace(scopeFilter)) return baseUrl;

        string filter = scopeFilter.Trim().TrimStart('?', '&');
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + filter;
    }
}

/// <summary>
/// Lists and reads files from the configured SharePoint site's default document library via
/// Microsoft Graph, using the same client-credentials sign-in <see cref="SharePointConnectionTester"/>
/// verifies. The grant's scope filter, when present, is treated as a folder path relative to the
/// library root (e.g. "Shared Documents/Sales").
/// </summary>
public class SharePointFetcher(
    IHttpClientFactory httpClientFactory, ISpreadsheetTextExtractor spreadsheets) : IDataSourceFetcher
{
    private static readonly string[] ReadableExtensions = [".txt", ".csv", ".md", ".json", ".log", ".xlsx", ".xls"];

    public string SourceType => DataSourceTypes.SharePoint;

    public async Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
    {
        string tenantId = config.GetValueOrDefault("tenantId", "").Trim();
        string clientId = config.GetValueOrDefault("clientId", "").Trim();
        string siteUrl = config.GetValueOrDefault("siteUrl", "").Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            return new DataSourceFetchResult(false, "No client secret is configured.", null, 0, false);
        }

        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out Uri? site))
        {
            return new DataSourceFetchResult(false, $"\"{siteUrl}\" is not a valid SharePoint site URL.", null, 0, false);
        }

        HttpClient http = httpClientFactory.CreateClient(SharePointConnectionTester.HttpClientName);

        (string? token, string? tokenError) = await SharePointAuth.AcquireTokenAsync(http, tenantId, clientId, secret, ct);
        if (token is null)
        {
            return new DataSourceFetchResult(false, tokenError!, null, 0, false);
        }

        string graphPath = SharePointAuth.GraphSitePath(site);
        string childrenUrl = string.IsNullOrWhiteSpace(scopeFilter)
            ? $"https://graph.microsoft.com/v1.0/sites/{graphPath}/drive/root/children"
            : $"https://graph.microsoft.com/v1.0/sites/{graphPath}/drive/root:/{Uri.EscapeDataString(scopeFilter.Trim().Trim('/'))}:/children";

        JsonElement listing;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, childrenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new DataSourceFetchResult(false,
                    response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? $"No folder found at \"{scopeFilter}\" in this site's document library."
                        : $"Microsoft Graph returned HTTP {(int)response.StatusCode}: {SharePointAuth.Truncate(body, 200)}",
                    null, 0, false);
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            listing = doc.RootElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            return new DataSourceFetchResult(false, $"Could not reach Microsoft Graph: {ex.Message}", null, 0, false);
        }
        catch (JsonException)
        {
            return new DataSourceFetchResult(false, "Microsoft Graph returned an unexpected response.", null, 0, false);
        }

        if (!listing.TryGetProperty("value", out JsonElement items))
        {
            return new DataSourceFetchResult(true, "No files found at this location.", null, 0, false);
        }

        var sb = new StringBuilder();
        int fetched = 0, skipped = 0;
        bool truncated = false;

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (fetched >= DataSourceFetchLimits.MaxFiles || sb.Length >= DataSourceFetchLimits.MaxChars) { truncated = true; break; }
            if (item.TryGetProperty("folder", out _)) continue; // skip subfolders — one level only

            string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            string extension = Path.GetExtension(name);
            if (!ReadableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            long size = item.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;
            if (size > DataSourceFetchLimits.MaxFileBytes) { skipped++; continue; }

            if (!item.TryGetProperty("id", out JsonElement idProp)) continue;
            string itemId = idProp.GetString() ?? "";

            try
            {
                using var contentRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"https://graph.microsoft.com/v1.0/sites/{graphPath}/drive/items/{itemId}/content");
                contentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using HttpResponseMessage contentResponse = await http.SendAsync(contentRequest, ct);
                if (!contentResponse.IsSuccessStatusCode) { skipped++; continue; }

                byte[] bytes = await contentResponse.Content.ReadAsByteArrayAsync(ct);
                string text = extension is ".xlsx" or ".xls"
                    ? spreadsheets.Extract(bytes, name)
                    : new UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');

                string block = $"== {name} ==\n{text}\n\n";
                if (sb.Length + block.Length > DataSourceFetchLimits.MaxChars)
                {
                    block = block[..Math.Max(0, DataSourceFetchLimits.MaxChars - sb.Length)];
                    truncated = true;
                }

                sb.Append(block);
                fetched++;
            }
            catch
            {
                skipped++;
            }
        }

        if (fetched == 0)
        {
            return new DataSourceFetchResult(true,
                "No readable files (.txt/.csv/.md/.json/.log/.xlsx/.xls) found at this location.", null, 0, false);
        }

        string content = sb.ToString();
        string message = $"Read {fetched} file(s) from SharePoint" +
            (skipped > 0 ? $" ({skipped} skipped — too large or unreadable)" : "") +
            (truncated ? " — content truncated to fit the size limit." : ".");

        return new DataSourceFetchResult(true, message, content, content.Length, truncated);
    }
}

/// <summary>Mirrors <see cref="DataLakeConnectionTester"/> — no platform chosen yet, so no fetch is possible.</summary>
public class DataLakeFetcher : IDataSourceFetcher
{
    public string SourceType => DataSourceTypes.DataLake;

    public Task<DataSourceFetchResult> FetchAsync(
        Dictionary<string, string> config, string? secret, string? scopeFilter, CancellationToken ct)
        => Task.FromResult(new DataSourceFetchResult(false,
            "No Data Lake / Lakehouse connector is implemented yet — nothing can be fetched from this source.",
            null, 0, false));
}
