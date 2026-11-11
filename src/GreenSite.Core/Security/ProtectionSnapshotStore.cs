using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using GreenSite.Core.Models;
using GreenSite.Core.Security.Win32;

namespace GreenSite.Core.Security;

/// <summary>
/// 开机自启快照：只包含「无需密码即可进入防护状态」所需的非敏感数据。
/// 绝不含 PasswordHash / PasswordSalt / RecoveryHash / RecoverySalt / ProxyBackup。
/// 规则清单本身与出厂模板一致，属公开数据；DPAPI(CurrentUser) 已绑定当前 Windows 账户。
/// </summary>
public sealed class ProtectionSnapshot
{
    public List<RuleGroup> Groups { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];
    public int Port { get; set; }
    public long PacSeq { get; set; }
    public bool ProtectionEnabled { get; set; } = true;
    public bool FollowUpstreamProxy { get; set; } = true;
    public bool NotifyEnabled { get; set; } = true;
    public AllowlistSchedule AllowlistSchedule { get; set; } = new();

    public static ProtectionSnapshot FromConfig(AppConfig config) => new()
    {
        Groups = config.Groups,
        Rules = config.Rules,
        Port = config.Port,
        PacSeq = config.PacSeq,
        ProtectionEnabled = config.ProtectionEnabled,
        FollowUpstreamProxy = config.FollowUpstreamProxy,
        NotifyEnabled = config.NotifyEnabled,
        AllowlistSchedule = config.AllowlistSchedule,
    };

    /// <summary>还原为可直接启动保护的配置（不含任何凭据哈希）。</summary>
    public AppConfig ToConfig() => new()
    {
        Groups = Groups,
        Rules = Rules,
        Port = Port,
        PacSeq = PacSeq,
        ProtectionEnabled = true, // 开机自启的明确预期：启动后自动进入防护状态
        FollowUpstreamProxy = FollowUpstreamProxy,
        NotifyEnabled = NotifyEnabled,
        AllowlistSchedule = AllowlistSchedule,
    };
}

/// <summary>
/// 快照仓库。格式：MAGIC "GSPS"(4) | bodyLen(4) | DPAPI(JSON)。
/// 不含密码派生 HMAC —— 冷启动必须能在无密码情况下读取。
/// 文件损坏一律返回 null（不阻塞启动、不弹错误），退化为「等待解锁」旧行为。
/// </summary>
public sealed class ProtectionSnapshotStore
{
    private const int HeaderSize = 8;
    private static readonly byte[] Magic = "GSPS"u8.ToArray();

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public ProtectionSnapshotStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "protection.bin");
    }

    public void Save(ProtectionSnapshot snapshot)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var body = Crypt32.Protect(json);
        var bytes = new byte[HeaderSize + body.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length);
        body.CopyTo(bytes, HeaderSize);

        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path, overwrite: true);
    }

    public ProtectionSnapshot? TryLoad()
    {
        try
        {
            var bytes = File.ReadAllBytes(Path);
            if (bytes.Length <= HeaderSize) return null;
            if (!bytes.AsSpan(0, 4).SequenceEqual(Magic)) return null;
            var bodyLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
            if (bodyLen <= 0 || bodyLen != bytes.Length - HeaderSize) return null;

            var plain = Crypt32.Unprotect(bytes.AsSpan(HeaderSize, bodyLen).ToArray());
            var snapshot = JsonSerializer.Deserialize<ProtectionSnapshot>(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (snapshot is null || snapshot.Groups.Count == 0 || snapshot.Rules.Count == 0)
                return null;
            return snapshot;
        }
        catch
        {
            return null; // 损坏 / 换账户 / DPAPI 不可用：退化到旧行为，不阻塞启动
        }
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { /* 清理路径不上抛 */ }
    }
}
