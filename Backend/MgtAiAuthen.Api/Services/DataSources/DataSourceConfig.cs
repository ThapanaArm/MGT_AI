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
        // Microsoft Fabric — a Power BI semantic model (DAX), a Warehouse/Lakehouse SQL endpoint
        // (T-SQL), or an "API for GraphQL" item (GraphQL), picked per source via "connectionMode".
        // tenantId/clientId are common to all three; the mode-specific fields (datasetId+daxQuery,
        // sqlEndpoint+database+sqlQuery, or graphqlEndpoint+graphqlQuery) are validated below rather
        // than listed here, since exactly one set is required depending on the mode. See
        // PowerBiConnectionTester for the admin-side prerequisites this cannot check.
        [DataSourceTypes.DataLake] = ["tenantId", "clientId"],
        // Supabase - "connectionMode" Sql (host + sqlQuery) or Rest (projectUrl + restQuery); validated below.
        [DataSourceTypes.Supabase] = [],
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

            // Blank = 2 minutes (see ApiConnectionTester.ResolveTimeout).
            string timeoutMinutes = config.GetValueOrDefault("timeoutMinutes", "").Trim();
            if (timeoutMinutes.Length > 0
                && (!double.TryParse(timeoutMinutes, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double minutes)
                    || minutes <= 0))
            {
                throw new AppException("timeoutMinutes must be a positive number");
            }
        }

        ValidateQueryMode(sourceType, config);

        if (string.Equals(sourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase))
        {
            ValidateSupabase(config);
        }

        if (string.Equals(sourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase))
        {
            // Blank = PowerBi, so every source registered before this field existed is unaffected.
            string mode = config.GetValueOrDefault("connectionMode", "").Trim();
            if (mode.Length > 0 && !new[] { "PowerBi", "Warehouse", "GraphQL" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppException("connectionMode must be PowerBi, Warehouse, or GraphQL");
            }

            bool isWarehouse = string.Equals(mode, "Warehouse", StringComparison.OrdinalIgnoreCase);
            bool isGraphQl = string.Equals(mode, "GraphQL", StringComparison.OrdinalIgnoreCase);
            string modeLabel = isWarehouse ? "Warehouse" : isGraphQl ? "GraphQL" : "Power BI";
            string[] modeRequired = isWarehouse
                ? (IsGeneratedMode(config) ? ["sqlEndpoint", "database"] : ["sqlEndpoint", "database", "sqlQuery"])
                : isGraphQl
                    ? ["graphqlEndpoint", "graphqlQuery"]
                    : ["datasetId", "daxQuery"];

            List<string> missingMode = modeRequired
                .Where(key => string.IsNullOrWhiteSpace(config.GetValueOrDefault(key)))
                .ToList();

            if (missingMode.Count > 0)
            {
                throw new AppException(
                    $"Missing required setting(s) for {sourceType} ({modeLabel} mode): {string.Join(", ", missingMode)}");
            }

            string variablesRaw = config.GetValueOrDefault("graphqlVariables", "").Trim();
            if (isGraphQl && variablesRaw.Length > 0)
            {
                try
                {
                    JsonSerializer.Deserialize<JsonElement>(variablesRaw);
                }
                catch (JsonException ex)
                {
                    throw new AppException($"graphqlVariables is not valid JSON: {ex.Message}");
                }
            }
        }
    }
    private static void ValidateSupabase(Dictionary<string, string> config)
    {
        // Blank = Sql, so a source saved without a mode behaves as the default.
        string mode = config.GetValueOrDefault("connectionMode", "").Trim();
        if (mode.Length > 0 && !new[] { "Sql", "Rest" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("connectionMode must be Sql or Rest");
        }

        bool isRest = string.Equals(mode, "Rest", StringComparison.OrdinalIgnoreCase);
        bool generated = IsGeneratedMode(config);
        string[] modeRequired = isRest
            ? (generated ? ["projectUrl"] : ["projectUrl", "restQuery"])
            : (generated ? ["host"] : ["host", "sqlQuery"]);

        List<string> missing = modeRequired
            .Where(key => string.IsNullOrWhiteSpace(config.GetValueOrDefault(key)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new AppException(
                $"Missing required setting(s) for Supabase ({(isRest ? "REST" : "SQL")} mode): {string.Join(", ", missing)}");
        }

        if (isRest)
        {
            string url = SupabaseQuery.NormalizeProjectUrl(config.GetValueOrDefault("projectUrl", ""));
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !SupabaseQuery.IsAllowedScheme(uri))
            {
                throw new AppException("projectUrl must be an https:// address, e.g. https://abcdefgh.supabase.co");
            }
        }
        else
        {
            string port = config.GetValueOrDefault("port", "").Trim();
            if (port.Length > 0 && (!int.TryParse(port, out int number) || number is < 1 or > 65535))
            {
                throw new AppException("port must be a number between 1 and 65535 (5432 direct, 6543 for the transaction pooler)");
            }
        }

        string timeoutMinutes = config.GetValueOrDefault("timeoutMinutes", "").Trim();
        if (timeoutMinutes.Length > 0
            && (!double.TryParse(timeoutMinutes, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double minutes)
                || minutes <= 0))
        {
            throw new AppException("timeoutMinutes must be a positive number");
        }
    }
    private static bool IsGeneratedMode(Dictionary<string, string> config)
        => string.Equals(config.GetValueOrDefault("queryMode", "").Trim(), "Generated", StringComparison.OrdinalIgnoreCase);

    /// <summary>queryMode: blank/Fixed = run the queries the admin typed; Generated = the AI writes a query per question.</summary>
    private static void ValidateQueryMode(string sourceType, Dictionary<string, string> config)
    {
        string mode = config.GetValueOrDefault("queryMode", "").Trim();
        if (mode.Length == 0) return;

        if (!new[] { "Fixed", "Generated" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("queryMode must be Fixed or Generated");
        }

        if (!IsGeneratedMode(config)) return;

        bool supported = string.Equals(sourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(sourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase)
                && string.Equals(config.GetValueOrDefault("connectionMode", "").Trim(), "Warehouse", StringComparison.OrdinalIgnoreCase));

        if (!supported)
        {
            throw new AppException(
                "queryMode Generated is available for Supabase and for Data Lake sources in Warehouse mode only");
        }

        foreach (string key in new[] { "generatedMaxRows", "generatedMaxChars" })
        {
            string value = config.GetValueOrDefault(key, "").Trim();
            if (value.Length > 0 && (!int.TryParse(value, out int n) || n < 1))
            {
                throw new AppException($"{key} must be a positive whole number");
            }
        }
    }
}
