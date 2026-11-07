namespace GreenSite.Core.Security;

/// <summary>敏感动作门禁的失败冷却。只在进程内内存计数，重启清零但拦截不因重启解除。</summary>
public sealed class PasswordGate
{
    private static readonly TimeSpan[] Cooldowns =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    private int _failures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public bool IsLocked(DateTimeOffset now) => now < _cooldownUntil;

    public TimeSpan RemainingCooldown(DateTimeOffset now) =>
        now < _cooldownUntil ? _cooldownUntil - now : TimeSpan.Zero;

    public void RecordSuccess()
    {
        _failures = 0;
        _cooldownUntil = DateTimeOffset.MinValue;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        var index = Math.Min(_failures, Cooldowns.Length - 1);
        _cooldownUntil = now + Cooldowns[index];
        _failures++;
    }
}
