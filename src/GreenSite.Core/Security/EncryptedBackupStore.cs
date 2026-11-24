using System.IO.Compression;
using System.Security.Cryptography;
using GreenSite.Core.Security.Win32;

namespace GreenSite.Core.Security;

public enum BackupResult
{
    Success,
    NoBackup,
    Corrupted,
    Failed,
}

/// <summary>
/// 加密文件备份：先打包 zip → 再 DPAPI(CurrentUser) 加密 → 只保留一份。
/// 存放于 %LOCALAPPDATA%\stgren\bkp\cache.idx：混淆后缀，避免被按 .zip/.bak 搜索或清理。
/// 解包兜底：配置读取失败时可把备份解到临时目录，供恢复流程读取。
/// </summary>
public sealed class EncryptedBackupStore
{
    private static readonly byte[] Magic = "GSBK"u8.ToArray();
    private const int HeaderSize = 8;

    public string DirectoryPath { get; }
    public string BackupPath => Path.Combine(DirectoryPath, "cache.idx");
    public string ObfuscatedFileName => "cache.idx";
    public bool Exists => File.Exists(BackupPath);

    public EncryptedBackupStore(string appDataRoot)
    {
        DirectoryPath = Path.Combine(appDataRoot, "stgren", "bkp");
        Directory.CreateDirectory(DirectoryPath);
    }

    public BackupResult CreateBackup(IEnumerable<string> sourceFiles)
    {
        try
        {
            using var zipStream = new MemoryStream();
            var count = 0;
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var source in sourceFiles)
                {
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
                    var entry = zip.CreateEntry(Path.GetFileName(source), CompressionLevel.Optimal);
                    using var target = entry.Open();
                    using var input = File.OpenRead(source);
                    input.CopyTo(target);
                    count++;
                }
            }
            if (count == 0) return BackupResult.NoBackup;

            var body = Crypt32.Protect(zipStream.ToArray());
            var bytes = new byte[HeaderSize + body.Length];
            Magic.CopyTo(bytes, 0);
            BitConverter.GetBytes(body.Length).CopyTo(bytes, 4);
            body.CopyTo(bytes, HeaderSize);

            var tmp = BackupPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, BackupPath, overwrite: true); // 单份：覆盖旧备份
            return BackupResult.Success;
        }
        catch
        {
            return BackupResult.Failed;
        }
    }

    public BackupResult RestoreBackup(string targetDirectory)
    {
        try
        {
            if (!Exists) return BackupResult.NoBackup;
            var plain = UnprotectBackup();
            if (plain is null) return BackupResult.Corrupted;

            Directory.CreateDirectory(targetDirectory);
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // 防路径穿越：只取文件名，不允许子目录与绝对路径
                var name = Path.GetFileName(entry.FullName);
                if (string.IsNullOrWhiteSpace(name) || name != entry.FullName) continue;
                var target = Path.Combine(targetDirectory, name);
                entry.ExtractToFile(target, overwrite: true);
            }
            CryptographicOperations.ZeroMemory(plain);
            return BackupResult.Success;
        }
        catch
        {
            return BackupResult.Failed;
        }
    }

    /// <summary>配置读取失败时的解包兜底：把备份解到临时目录，返回给调用方自行取用。</summary>
    public bool TryOpenFallback(out string tempDirectory, out string error)
    {
        tempDirectory = "";
        error = "";
        try
        {
            if (!Exists) { error = "没有可用备份"; return false; }
            var plain = UnprotectBackup();
            if (plain is null) { error = "备份已损坏或不属于当前 Windows 账户"; return false; }

            tempDirectory = Path.Combine(Path.GetTempPath(), "gs-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (string.IsNullOrWhiteSpace(name) || name != entry.FullName) continue;
                entry.ExtractToFile(Path.Combine(tempDirectory, name), overwrite: true);
            }
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (Exception e)
        {
            error = "解包失败：" + e.GetType().Name;
            return false;
        }
    }

    private byte[]? UnprotectBackup()
    {
        try
        {
            var bytes = File.ReadAllBytes(BackupPath);
            if (bytes.Length <= HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return null;
            var len = BitConverter.ToInt32(bytes, 4);
            if (len <= 0 || len != bytes.Length - HeaderSize) return null;
            return Crypt32.Unprotect(bytes.AsSpan(HeaderSize, len).ToArray());
        }
        catch
        {
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
        catch { /* 清理路径不上抛 */ }
    }
}
