using System.Security.Cryptography;

namespace MgtAiAuthen.Api.Security;

/// <summary>
/// แฮชรหัสผ่านด้วย PBKDF2-SHA512 (มีอยู่ใน .NET เอง ไม่ต้องพึ่ง package ภายนอก)
/// รูปแบบที่เก็บลงคอลัมน์ Users.PasswordHash: PBKDF2$&lt;iterations&gt;$&lt;salt_b64&gt;$&lt;hash_b64&gt;
/// (base64 ไม่มีอักขระ '$' จึงแยกส่วนด้วย '$' ได้อย่างปลอดภัย)
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int DefaultIterations = 210_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA512;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, Algorithm, KeyBytes);

        return string.Join('$', Prefix, DefaultIterations, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    /// <summary>ตรวจรหัสผ่านแบบ constant-time — คืน false ถ้ารูปแบบที่เก็บไว้ไม่ถูกต้อง</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out int iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>SHA-256 (base64) — ใช้เก็บ refresh token ในฐานข้อมูลโดยไม่เก็บค่าจริง</summary>
    public static string Sha256(string value)
        => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>สร้าง refresh token แบบสุ่ม 32 ไบต์ (base64url)</summary>
    public static string NewRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
