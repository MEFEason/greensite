using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenSite.Core.Models;
using GreenSite.Core.Security.Win32;

namespace GreenSite.Core.Security;

/// <summary>配置校验失败。密码错误与配置篡改统一用同一消息，不泄露原因。</summary>
public sealed class ConfigCorruptedException : Exception
{
    public ConfigCorruptedException(string message) : base(message) { }
}

/// <summary>
/// 加密配置仓库。文件格式：
/// MAGIC "GSIT"(4) | SchemaVersion(4) | bodyLen(4) | DPAPI(JSON)(bodyLen) | HMAC-SHA256(32)
/// HMAC 覆盖 header + body，密钥由管理密码派生，仅在解锁后驻留内存。
/// </summary>
public sealed class SecureConfigStore
{
    public const string UnifiedError = "密码或配置校验失败";
    private const int SchemaVersion = 1;
    private const int HeaderSize = 12;
    private const int MacSize = 32;
    private static readonly byte[] Magic = "GSIT"u8.ToArray();

    public string Path { get; }
    public string BackupPath { get; }
    public bool Exists => File.Exists(Path);

    public SecureConfigStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "config.bin");
        BackupPath = System.IO.Path.Combine(directory, "config.bak");
    }

    /// <summary>
    /// 保存配置。secret 用于首次生成密码哈希；此后哈希已存于 config，HMAC 密钥统一由
    /// 「存储的密码哈希」派生（DeriveStoredKey），使恢复码解锁路径也能校验完整性。
    /// </summary>
    public void Save(AppConfig config, string? secretForInit = null)
    {
        if (string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.PasswordSalt))
        {
            if (string.IsNullOrEmpty(secretForInit))
                throw new ArgumentException("首次保存必须提供密码明文");
            var (hash, salt) = PasswordHasher.Create(secretForInit);
            config.PasswordHash = hash;
            config.PasswordSalt = salt;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        var body = Crypt32.Protect(json);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), body.Length);

        var key = PasswordHasher.DeriveStoredKey(config.PasswordHash!, config.PasswordSalt!, "config-hmac");
        byte[] bytes;
        try
        {
            bytes = new byte[HeaderSize + body.Length + MacSize];
            header.CopyTo(bytes, 0);
            body.CopyTo(bytes, HeaderSize);
            var mac = HMACSHA256.HashData(key, bytes.AsSpan(0, HeaderSize + body.Length));
            mac.CopyTo(bytes, HeaderSize + body.Length);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        if (File.Exists(Path)) File.Copy(Path, BackupPath, overwrite: true);
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>用管理密码加载。校验顺序：header → DPAPI → 凭据哈希 → HMAC；任一失败统一报错。</summary>
    public AppConfig Load(string password) =>
        LoadVerifying(config => PasswordHasher.Verify(
            password, config.PasswordHash, config.PasswordSalt));

    /// <summary>用恢复码加载（解锁后调用方须立即设置新密码并轮换恢复码）。</summary>
    public AppConfig LoadWithRecoveryCode(string code) =>
        LoadVerifying(config => !string.IsNullOrEmpty(config.RecoveryHash) &&
            PasswordHasher.Verify(code, config.RecoveryHash, config.RecoverySalt));

    private delegate bool SecretCheck(AppConfig config);

    private AppConfig LoadVerifying(SecretCheck check)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path);
            if (bytes.Length < HeaderSize + MacSize) throw new InvalidDataException();
            if (!bytes.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException();
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != SchemaVersion)
                throw new InvalidDataException();

            var bodyLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            if (bodyLen <= 0 || bodyLen != bytes.Length - HeaderSize - MacSize)
                throw new InvalidDataException();

            var plain = Crypt32.Unprotect(bytes.AsSpan(HeaderSize, bodyLen).ToArray());
            var config = JsonSerializer.Deserialize<AppConfig>(plain)
                ?? throw new InvalidDataException();
            CryptographicOperations.ZeroMemory(plain);

            if (!check(config))
                throw new InvalidDataException();

            var key = PasswordHasher.DeriveStoredKey(config.PasswordHash!, config.PasswordSalt!, "config-hmac");
            try
            {
                var expected = HMACSHA256.HashData(key, bytes.AsSpan(0, HeaderSize + bodyLen));
                if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(HeaderSize + bodyLen)))
                    throw new InvalidDataException();
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            return config;
        }
        catch (Exception e) when (e is InvalidDataException or FormatException
                                     or JsonException or InvalidOperationException
                                     or IOException or UnauthorizedAccessException
                                     or ArgumentException)
        {
            // 不区分「凭据错」与「配置被篡改」，避免信息泄露
            throw new ConfigCorruptedException(UnifiedError);
        }
    }
}
