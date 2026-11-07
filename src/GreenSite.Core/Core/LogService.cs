using System.Globalization;
using System.Text;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 拦截日志。按天分文件，TSV 只含 时间 / Host / 命中组 / 命中规则 / allow|block，
/// 从结构上杜绝记录 URL 与查询参数。
/// </summary>
public sealed class LogService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _directory;
    private readonly object _sync = new();

    public LogService(string? directory = null)
    {
        _directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GreenSite", "logs");
        System.IO.Directory.CreateDirectory(_directory);
    }

    public string Directory => _directory;

    private string TodayFile(DateTimeOffset time) =>
        System.IO.Path.Combine(_directory, $"block-{time:yyyyMMdd}.log");

    public void Append(BlockLogEntry entry)
    {
        var line = string.Join('\t',
            entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Sanitize(entry.Host), Sanitize(entry.GroupName), Sanitize(entry.RuleValue),
            entry.Whitelisted ? "allow" : "block") + "\n";
        lock (_sync) File.AppendAllText(TodayFile(entry.Time), line, Utf8NoBom);
    }

    private static string Sanitize(string value) =>
        (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public int TodayCount
    {
        get
        {
            var path = TodayFile(DateTimeOffset.Now);
            if (!File.Exists(path)) return 0;
            lock (_sync) return File.ReadAllLines(path, Utf8NoBom).Count(l => l.EndsWith("\tblock", StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<BlockLogEntry> ReadRecent(int days = 7, int max = 500)
    {
        var result = new List<BlockLogEntry>();
        lock (_sync)
        {
            for (var i = 0; i < days; i++)
            {
                var path = TodayFile(DateTimeOffset.Now.AddDays(-i));
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path, Utf8NoBom))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 5) continue;
                    if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var time)) continue;
                    result.Add(new BlockLogEntry(time, parts[1], parts[2], parts[3],
                        parts[4].Equals("allow", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        return result.OrderByDescending(e => e.Time).Take(max).ToList();
    }

    /// <summary>只清理本服务生成的 block-yyyyMMdd.log，绝不碰其它文件。</summary>
    public void Cleanup(int retentionDays)
    {
        if (retentionDays <= 0) return;
        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        lock (_sync)
        {
            foreach (var file in System.IO.Directory.GetFiles(_directory, "block-????????.log"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.Length != 14 || !DateTime.TryParseExact(name.AsSpan(6), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (date < cutoff.Date) File.Delete(file);
            }
        }
    }
}
