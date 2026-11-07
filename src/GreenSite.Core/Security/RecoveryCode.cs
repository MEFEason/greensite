using System.Security.Cryptography;

namespace GreenSite.Core.Security;

/// <summary>
/// 密保恢复码：首次设置密码时生成的一次性高熵代码（Crockford Base32，25 字符 ≈ 130 bit）。
/// 只存其 PBKDF2 哈希（与密码同等门禁），用于忘记密码时解锁并重置密码；
/// 不是无密码后门——恢复码本身即秘密，且使用后强制立即设置新密码并轮换恢复码。
/// </summary>
public static class RecoveryCode
{
    // Crockford Base32：去掉易混字符 I L O U
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Groups = 5;
    private const int GroupSize = 5;

    /// <summary>生成形如 XXXXX-XXXXX-XXXXX-XXXXX-XXXXX 的恢复码。</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(Groups * GroupSize);
        var chars = new char[Groups * GroupSize];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[bytes[i] & 31];

        var parts = new string[Groups];
        for (var g = 0; g < Groups; g++)
            parts[g] = new string(chars, g * GroupSize, GroupSize);
        return string.Join('-', parts);
    }

    /// <summary>用户输入规范化：大写、只保留 Base32 字符、按 5 位分组。非法输入返回 null。</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var chars = new List<char>(Groups * GroupSize);
        foreach (var ch in raw.ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '\t') continue;
            // 常见混淆字符按 Crockford 规则映射
            var mapped = ch switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                'U' => 'V',
                _ => ch,
            };
            if (Alphabet.IndexOf(mapped) < 0) return null;
            chars.Add(mapped);
        }
        if (chars.Count != Groups * GroupSize) return null;

        var parts = new string[Groups];
        for (var g = 0; g < Groups; g++)
            parts[g] = new string(chars.ToArray(), g * GroupSize, GroupSize);
        return string.Join('-', parts);
    }
}
