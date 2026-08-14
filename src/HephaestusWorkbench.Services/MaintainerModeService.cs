using System.Security.Cryptography;
using System.Text;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 管理规则维护者模式的短期解锁状态。
/// 维护者密钥只从受控环境注入，不写入配置文件；本服务只负责界面解锁，真正的仓库权限仍由 GitHub Token 决定。
/// </summary>
public sealed class MaintainerModeService
{
    private readonly byte[] _expectedKey;
    private readonly TimeProvider _timeProvider;
    private int _failedAttempts;
    private DateTimeOffset? _lockedUntil;

    public MaintainerModeService(string? expectedKey, TimeProvider? timeProvider = null)
    {
        _expectedKey = Encoding.UTF8.GetBytes(expectedKey ?? string.Empty);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsConfigured => _expectedKey.Length > 0;
    public bool IsUnlocked { get; private set; }
    public int FailedAttempts => _failedAttempts;
    public DateTimeOffset? LockedUntil => _lockedUntil;

    public bool TryUnlock(string? candidate)
    {
        if (!IsConfigured || IsLocked()) return false;

        var candidateBytes = Encoding.UTF8.GetBytes(candidate ?? string.Empty);
        var matches = CryptographicOperations.FixedTimeEquals(_expectedKey, candidateBytes);
        CryptographicOperations.ZeroMemory(candidateBytes);
        if (matches)
        {
            _failedAttempts = 0;
            _lockedUntil = null;
            IsUnlocked = true;
            return true;
        }

        _failedAttempts++;
        if (_failedAttempts >= 3)
        {
            _lockedUntil = _timeProvider.GetUtcNow().AddSeconds(30);
            _failedAttempts = 0;
        }
        return false;
    }

    public TimeSpan GetLockoutRemaining()
    {
        if (_lockedUntil is not { } lockedUntil) return TimeSpan.Zero;
        var remaining = lockedUntil - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _lockedUntil = null;
            return TimeSpan.Zero;
        }
        return remaining;
    }

    public void Clear()
    {
        IsUnlocked = false;
    }

    private bool IsLocked() => GetLockoutRemaining() > TimeSpan.Zero;
}
