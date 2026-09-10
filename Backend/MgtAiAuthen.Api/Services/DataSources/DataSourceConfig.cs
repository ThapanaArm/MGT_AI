using MgtAiAuthen.Api.Contracts;
using System.Text.Json;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>
/// The non-secret configuration keys each source type needs, and how to read/write the JSON blob
/// stored in <see cref="DataSource.ConfigJson"/>.
///
/// Kept as flat string dictionaries rather than one DTO per type — the four types share almost
/// nothing (a folder path has nothing in common with an OAuth tenant id), and a shared shape
/// would either carry a dozen nullable fields or need four request classes the controller has to
/// dispatch on. A flat map is also what the admin UI already needs: render one text field per key.
/// </summary>
public static class DataSourceConfig
{
    /// <summary>Keys that must be present (non-empty) before a source of this type is usable.</summary>
    public static readonly Dictionary<string, string[]> RequiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // authType: None | ApiKey | Bearer | Basic — see DescribeAuth below for what each expects.
        [DataSourceTypes.Api] = ["baseUrl", "authType"],
        [DataSourceTypes.LocalFolder] = ["path"],
        // siteUrl is the SharePoint site, e.g. https://contoso.sharepoint.com/sites/Sales
        [DataSourceTypes.SharePoint] = ["siteUrl", "tenantId", "clientId"],
        // Deliberately empty: the platform has not been chosen yet (see DataLakeConnectionTester).
        // Requiring fields for a connector that does not exist would just be validation theatre.
        [DataSourceTypes.DataLake] = [],
    };

    /// <summary>Keys whose value never needs to reach the frontend even unmasked — currently none, but kept for symmetry with the secret column.</summary>
    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string Serialize(Dictionary<string, string> config)
        => JsonSerializer.Serialize(config.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim()));

    /// <summary>Throws AppException naming every missing key at once, rather than one at a time.</summary>
    public static void Validate(string sourceType, Dictionary<string, string> config)
    {
        string[] required = RequiredKeys.GetValueOrDefault(sourceType, []);
        List<string> missing = required
            .Where(key => string.IsNullOrWhiteSpace(config.GetValueOrDefault(key)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new AppException(
                $"Missing required setting(s) for {sourceType}: {string.Join(", ", missing)}");
        }

        if (string.Equals(sourceType, DataSourceTypes.Api, StringComparison.OrdinalIgnoreCase))
        {
            string authType = config.GetValueOrDefault("authType", "");
            if (!new[] { "None", "ApiKey", "Bearer", "Basic" }.Contains(authType, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppException(
                    "authType must be one of: None, ApiKey, Bearer, Basic");
            }

            if (string.Equals(authType, "Basic", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(config.GetValueOrDefault("username")))
            {
                throw new AppException("authType Basic requires a \"username\" setting (the password goes in Secret)");
            }

            // Blank = GET, kept as the default so every source registered before this field
            // existed keeps behaving exactly as it did.
            string method = config.GetValueOrDefault("method", "");
            if (method.Length > 0 && !new[] { "GET", "POST" }.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppException("method must be GET or POST");
            }
        }
    }
}
