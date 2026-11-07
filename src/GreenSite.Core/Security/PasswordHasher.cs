using System.Security.Cryptography;
using System.Text;

namespace GreenSite.Core.Security;

/// <summary>管理密码哈希。只存 PBKDF2 结果与随机 salt，绝不存明文。</summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;
    public const int MinLength = 8;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Hash, string Salt) Create(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            throw new ArgumentException($"管理密码至少 {MinLength} 位", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Hash(password, salt);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    public static bool Verify(string password, string? hashBase64, string? saltBase64)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64)) return false;
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>派生配置完整性密钥。purpose 与密码哈希隔离，避免同密钥复用。</summary>
    public static byte[] DeriveKey(string password, string saltBase64, string purpose)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var context = SHA256.HashData(Encoding.UTF8.GetBytes(saltBase64 + "|" + purpose));
        var combined = new byte[salt.Length + context.Length];
        salt.CopyTo(combined, 0);
        context.CopyTo(combined, salt.Length);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), combined, Iterations, HashAlgorithmName.SHA256, KeySize);
    }
}
