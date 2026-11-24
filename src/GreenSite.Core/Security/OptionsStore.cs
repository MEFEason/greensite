using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using GreenSite.Core.Models;
using GreenSite.Core.Security.Win32;

namespace GreenSite.Core.Security;

/// <summary>
/// v1.1.0 新配置的独立存储：GSO1(4) | bodyLen(4) | DPAPI(json)。
/// 与 config.bin 的密码派生加密完全隔离——这里不存任何凭据，冷启动/守护均可无密码读取。
/// 损坏一律回落默认值，不抛异常、不阻塞启动。
/// </summary>
public sealed class OptionsStore
{
    private static readonly byte[] Magic = "GSO1"u8.ToArray();
    private const int HeaderSize = 8;

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public OptionsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "options.bin");
    }

    public AdvancedOptions Load()
    {
        try
        {
            var bytes = File.ReadAllBytes(Path);
            if (bytes.Length <= HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return new();
            var len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
            if (len <= 0 || len != bytes.Length - HeaderSize) return new();
            var plain = Crypt32.Unprotect(bytes.AsSpan(HeaderSize, len).ToArray());
            var options = JsonSerializer.Deserialize<AdvancedOptions>(plain) ?? new();
            CryptographicOperations.ZeroMemory(plain);
            options.Normalize();
            return options;
        }
        catch
        {
            return new(); // 损坏 / 换账户 / 无权限：回落默认，不阻塞
        }
    }

    public void Save(AdvancedOptions options)
    {
        options.Normalize();
        var json = JsonSerializer.SerializeToUtf8Bytes(options);
        var body = Crypt32.Protect(json);
        var bytes = new byte[HeaderSize + body.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length);
        body.CopyTo(bytes, HeaderSize);

        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { /* 清理路径不上抛 */ }
    }
}
