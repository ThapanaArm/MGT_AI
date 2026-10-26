namespace MgtAiAuthen.Api.Options;

/// <summary>
/// Sign-in against a user table in another SQL Server (section "ExternalAuth"). The connection
/// details are secrets - keep Server/Username/Password in appsettings.Development.json or
/// environment variables (ExternalAuth__Password), never in the committed appsettings.json.
/// </summary>
public class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    public bool Enabled { get; set; }

    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 8;

    /// <summary>Table holding the users, optionally schema-qualified ("dbo.MS_User").</summary>
    public string Table { get; set; } = "MS_User";

    /// <summary>A person may type either their username or their email, so both are searched.</summary>
    public string UsernameColumn { get; set; } = "Username";
    public string EmailColumn { get; set; } = "Email";
    public string PasswordColumn { get; set; } = "Password";

    /// <summary>Optional columns - leave blank when the table has no such column.</summary>
    public string? FullNameColumn { get; set; }
    public string? DepartmentColumn { get; set; }

    /// <summary>When set, only rows where this column is true / 1 / Y / Active may sign in.</summary>
    public string? ActiveColumn { get; set; }

    /// <summary>Auto, Plain, BCrypt, MD5, SHA1, SHA256 or SHA512 - how the password column is stored.</summary>
    public string PasswordFormat { get; set; } = "Auto";

    /// <summary>
    /// Company emails that get the Admin role the first time they sign in (their local account is
    /// created then). Later role changes made on the User management page are never overwritten.
    /// </summary>
    public string[] AdminEmails { get; set; } = [];

    /// <summary>Keep the accounts stored in this app's own Users table (e.g. admin01) able to sign in.</summary>
    public bool AllowLocalFallback { get; set; } = true;
}
