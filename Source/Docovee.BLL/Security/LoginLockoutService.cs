using Microsoft.Extensions.Caching.Memory;

namespace Docovee.BLL.Security;

public interface ILoginLockoutService
{
    bool IsLockedOut(string accountType, string username);
    void RecordFailure(string accountType, string username);
    void Reset(string accountType, string username);
    TimeSpan? GetRemainingLockout(string accountType, string username);
}

/// <summary>
/// In-memory failed-login lockout: 5 failures → 15 minutes locked.
/// Suitable for single-instance deployments; multi-instance would need shared store.
/// </summary>
public sealed class LoginLockoutService : ILoginLockoutService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _cache;

    public LoginLockoutService(IMemoryCache cache) => _cache = cache;

    public bool IsLockedOut(string accountType, string username)
    {
        var entry = GetEntry(accountType, username);
        return entry is { LockedUntilUtc: { } until } && until > DateTimeOffset.UtcNow;
    }

    public TimeSpan? GetRemainingLockout(string accountType, string username)
    {
        var entry = GetEntry(accountType, username);
        if (entry?.LockedUntilUtc is not { } until || until <= DateTimeOffset.UtcNow)
            return null;
        return until - DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string accountType, string username)
    {
        var key = CacheKey(accountType, username);
        var entry = GetEntry(accountType, username) ?? new LockoutEntry();
        if (entry.LockedUntilUtc is { } until && until > DateTimeOffset.UtcNow)
            return;

        entry.FailedCount++;
        if (entry.FailedCount >= MaxFailedAttempts)
        {
            entry.LockedUntilUtc = DateTimeOffset.UtcNow.Add(LockoutDuration);
            entry.FailedCount = 0;
        }

        _cache.Set(key, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = LockoutDuration.Add(TimeSpan.FromMinutes(5))
        });
    }

    public void Reset(string accountType, string username)
    {
        _cache.Remove(CacheKey(accountType, username));
    }

    private LockoutEntry? GetEntry(string accountType, string username) =>
        _cache.TryGetValue(CacheKey(accountType, username), out LockoutEntry? entry) ? entry : null;

    private static string CacheKey(string accountType, string username) =>
        $"login-lockout:{accountType}:{username.Trim().ToLowerInvariant()}";

    private sealed class LockoutEntry
    {
        public int FailedCount { get; set; }
        public DateTimeOffset? LockedUntilUtc { get; set; }
    }
}
