using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>默认清单加载器。数据文件是唯一来源，位于输出目录 data/ 下。</summary>
public static class DefaultRules
{
    public static (List<RuleGroup> Groups, List<Rule> Rules) CreateDefault(string? dataDir = null) =>
        Load(dataDir ?? Path.Combine(AppContext.BaseDirectory, "data"));

    public static (List<RuleGroup> Groups, List<Rule> Rules) Load(string dataDir)
    {
        var groups = new List<RuleGroup>();
        var rules = new List<Rule>();
        var order = 0;
        LoadFile(Path.Combine(dataDir, "default_blocklist.txt"), false, groups, rules, ref order);
        LoadFile(Path.Combine(dataDir, "default_allowlist.txt"), true, groups, rules, ref order);
        if (groups.Count == 0) throw new InvalidDataException($"默认清单为空: {dataDir}");
        return (groups, rules);
    }

    private static void LoadFile(
        string path, bool isAllow,
        List<RuleGroup> groups, List<Rule> rules, ref int order)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("默认清单缺失", path);

        RuleGroup? current = null;
        foreach (var raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new RuleGroup
                {
                    Name = line[1..^1].Trim(),
                    IsAllow = isAllow,
                    SortOrder = order++,
                };
                groups.Add(current);
                continue;
            }
            if (current is null)
                throw new InvalidDataException($"规则出现在分组之前: {path}: {line}");

            var note = (string?)null;
            var hash = line.IndexOf('#');
            var valuePart = hash >= 0 ? line[..hash].Trim() : line;
            if (hash >= 0) note = line[(hash + 1)..].Trim();

            if (!RuleNormalizer.TryNormalizeDomain(valuePart, out var value, out var error))
                throw new InvalidDataException($"{path}: 非法规则「{valuePart}」: {error}");
            if (rules.Any(r => r.GroupId == current.Id && r.Value == value)) continue;

            rules.Add(new Rule
            {
                GroupId = current.Id,
                Type = RuleType.Domain,
                Value = value,
                RawValue = valuePart,
                Note = note,
            });
        }
    }
}
