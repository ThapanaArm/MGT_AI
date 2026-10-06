using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MgtAiAuthen.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

public enum ExternalLoginStatus
{
    Success,
    InvalidCredentials,
    Unavailable,
}

public record ExternalUser(string Username, string Email, string FullName, string? Department);

public record ExternalLoginResult(ExternalLoginStatus Status, ExternalUser? User = null, string? Detail = null);

/// <summary>Checks a username-or-email and password against the company user table in another SQL Server.</summary>
public interface IExternalUserDirectory
{
    bool Enabled { get; }
    bool AllowLocalFallback { get; }

    Task<ExternalLoginResult> AuthenticateAsync(string identifier, string password, CancellationToken ct = default);
}

public partial class ExternalUserDirectory : IExternalUserDirectory
{
    private const int MaxRowsChecked = 5;

    private readonly ExternalAuthOptions _options;
    private readonly ILogger<ExternalUserDirectory> _logger;
    private readonly string? _query;

    public ExternalUserDirectory(IOptions<ExternalAuthOptions> options, ILogger<ExternalUserDirectory> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.Enabled)
        {
            // Table and column names are configuration, not user input, but they still go into SQL text -
            // so reject anything that is not a plain identifier instead of trying to escape it.
            _query = BuildQuery(_options);
        }
    }

    public bool Enabled => _options.Enabled;

    public bool AllowLocalFallback => _options.AllowLocalFallback;

    public async Task<ExternalLoginResult> AuthenticateAsync(
        string identifier, string password, CancellationToken ct = default)
    {
        if (!Enabled || _query is null)
        {
            return new ExternalLoginResult(ExternalLoginStatus.Unavailable, Detail: "External sign-in is not enabled");
        }

        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = _options.Server,
                InitialCatalog = _options.Database,
                UserID = _options.Username,
                Password = _options.Password,
                Encrypt = _options.Encrypt,
                TrustServerCertificate = _options.TrustServerCertificate,
                ConnectTimeout = _options.ConnectTimeoutSeconds,
                ApplicationName = "MGT_AI_Authen",
            };

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);

            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = _query;
            command.CommandTimeout = 15;
            command.Parameters.Add("@id", SqlDbType.NVarChar, 256).Value = identifier;

            await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);

            int checkedRows = 0;
            bool matchedButInactive = false;

            while (checkedRows < MaxRowsChecked && await reader.ReadAsync(ct))
            {
                checkedRows++;

                object stored = reader["P"];
                if (!ExternalPasswordVerifier.Verify(password, stored, _options.PasswordFormat))
                {
                    continue;
                }

                if (HasColumn(reader, "A") && !IsTruthy(reader["A"]))
                {
                    matchedButInactive = true;
                    continue;
                }

                string email = AsString(reader["E"]);
                string username = AsString(reader["U"]);

                // People may sign in with an email, so the local account is keyed on the user table's
                // own username when it has one, and on the email otherwise.
                if (username.Length == 0) username = email;
                if (username.Length == 0) continue;

                string fullName = HasColumn(reader, "N") ? AsString(reader["N"]) : string.Empty;
                string? department = HasColumn(reader, "D") ? NullIfEmpty(AsString(reader["D"])) : null;

                return new ExternalLoginResult(
                    ExternalLoginStatus.Success,
                    new ExternalUser(username, email, fullName.Length > 0 ? fullName : username, department));
            }

            return new ExternalLoginResult(
                ExternalLoginStatus.InvalidCredentials,
                Detail: matchedButInactive
                    ? "Account is inactive in the company user table"
                    : checkedRows == 0 ? "No such user in the company user table" : "Incorrect password");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never log the connection string or password - only what went wrong.
            _logger.LogWarning(
                "Company user table at {Server} could not be queried: {Error}", _options.Server, ex.Message);
            return new ExternalLoginResult(ExternalLoginStatus.Unavailable, Detail: ex.GetType().Name);
        }
    }

    private static string BuildQuery(ExternalAuthOptions o)
    {
        string table = string.Join('.', o.Table.Split('.').Select(Quote));
        var columns = new List<string>
        {
            $"{Quote(o.UsernameColumn)} AS U",
            $"{Quote(o.EmailColumn)} AS E",
            $"{Quote(o.PasswordColumn)} AS P",
        };

        if (!string.IsNullOrWhiteSpace(o.FullNameColumn)) columns.Add($"{Quote(o.FullNameColumn)} AS N");
        if (!string.IsNullOrWhiteSpace(o.DepartmentColumn)) columns.Add($"{Quote(o.DepartmentColumn)} AS D");
        if (!string.IsNullOrWhiteSpace(o.ActiveColumn)) columns.Add($"{Quote(o.ActiveColumn)} AS A");

        return $"SELECT TOP ({MaxRowsChecked}) {string.Join(", ", columns)} FROM {table} " +
               $"WHERE {Quote(o.UsernameColumn)} = @id OR {Quote(o.EmailColumn)} = @id";
    }

    private static string Quote(string identifier)
    {
        string name = identifier.Trim();
        if (!SafeIdentifier().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"ExternalAuth: \"{identifier}\" is not a valid table or column name (letters, digits and underscore only).");
        }

        return $"[{name}]";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    private static bool HasColumn(SqlDataReader reader, string name)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string AsString(object value) => value is DBNull ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static bool IsTruthy(object value) => value switch
    {
        DBNull => false,
        bool b => b,
        byte or short or int or long => Convert.ToInt64(value) != 0,
        decimal d => d != 0,
        _ => Convert.ToString(value)?.Trim().ToLowerInvariant() is "1" or "true" or "y" or "yes" or "active" or "a",
    };
}

/// <summary>
/// Compares a typed password with whatever the company user table stores - plain text, a bcrypt
/// hash, or an unsalted MD5 / SHA digest (hex or base64, over UTF-8 or the UTF-16 bytes that SQL
/// Server's HASHBYTES(N'...') produces). Salted schemes other than bcrypt are not supported.
/// </summary>
public static partial class ExternalPasswordVerifier
{
    public static bool Verify(string supplied, object? stored, string format)
    {
        if (stored is null or DBNull || supplied.Length == 0) return false;

        format = (format ?? "Auto").Trim();
        bool auto = format.Equals("Auto", StringComparison.OrdinalIgnoreCase);

        if (stored is byte[] bytes)
        {
            return DigestMatches(supplied, format, auto, digest => FixedTimeEquals(digest, bytes));
        }

        string value = Convert.ToString(stored)?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;

        if (auto || format.Equals("BCrypt", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksLikeBcrypt(value))
            {
                try
                {
                    return BCrypt.Net.BCrypt.Verify(supplied, value);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            if (!auto) return false;
        }

        if (format.Equals("Plain", StringComparison.OrdinalIgnoreCase)
            || (auto && !LooksLikeDigest(value)))
        {
            // Not for Auto when the stored value looks like a digest: otherwise typing the stored
            // hash itself as the password would be accepted.
            return FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(value));
        }

        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

        return DigestMatches(supplied, format, auto, digest =>
            hex.Equals(Convert.ToHexString(digest), StringComparison.OrdinalIgnoreCase)
            || value.Equals(Convert.ToBase64String(digest), StringComparison.Ordinal));
    }

    private static bool DigestMatches(string supplied, string format, bool auto, Func<byte[], bool> isMatch)
    {
        var algorithms = new (string Name, Func<byte[], byte[]> Hash)[]
        {
            ("MD5", MD5.HashData),
            ("SHA1", SHA1.HashData),
            ("SHA256", SHA256.HashData),
            ("SHA512", SHA512.HashData),
        };

        foreach ((string name, Func<byte[], byte[]> hash) in algorithms)
        {
            if (!auto && !format.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            // UTF-8 is what apps normally hash; UTF-16LE is what HASHBYTES gives for an NVARCHAR value.
            if (isMatch(hash(Encoding.UTF8.GetBytes(supplied)))) return true;
            if (isMatch(hash(Encoding.Unicode.GetBytes(supplied)))) return true;
        }

        return false;
    }

    private static bool FixedTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);

    private static bool LooksLikeBcrypt(string value) => value.Length == 60 && value.StartsWith("$2", StringComparison.Ordinal);

    private static bool LooksLikeDigest(string value)
    {
        if (LooksLikeBcrypt(value)) return true;

        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length is 32 or 40 or 64 or 128 && HexOnly().IsMatch(hex)) return true;

        // Base64 of a 16 / 20 / 32 / 64 byte digest is 24 / 28 / 44 / 88 characters.
        return value.Length is 24 or 28 or 44 or 88 && value.EndsWith('=') && Base64Only().IsMatch(value);
    }

    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexOnly();

    [GeneratedRegex(@"^[A-Za-z0-9+/]+={0,2}$")]
    private static partial Regex Base64Only();
}
