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

    public void Save(AppConfig config, string password)
    {
        if (string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.PasswordSalt))
        {
            var (hash, salt) = PasswordHasher.Create(password);
            config.PasswordHash = hash;
            config.PasswordSalt = salt;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        var body = Crypt32.Protect(json);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), body.Length);

        var key = PasswordHasher.DeriveKey(password, config.PasswordSalt!, "config-hmac");
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

    /// <summary>启动校验顺序：header 结构 → DPAPI 解密 → 密码哈希 → HMAC 完整性。任一失败统一报错。</summary>
    public AppConfig Load(string password)
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

            if (!PasswordHasher.Verify(password, config.PasswordHash, config.PasswordSalt))
                throw new InvalidDataException();

            var key = PasswordHasher.DeriveKey(password, config.PasswordSalt!, "config-hmac");
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
            // 不区分「密码错」与「配置被篡改」，避免信息泄露
            throw new ConfigCorruptedException(UnifiedError);
        }
    }
}
