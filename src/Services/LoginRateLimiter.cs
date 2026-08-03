using StackExchange.Redis;
using RediensIAM.Config;

namespace RediensIAM.Services;

public class LoginRateLimiter(IConnectionMultiplexer redis, AppConfig appConfig)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _maxAttempts = appConfig.MaxLoginAttempts;
    private readonly int _lockoutMinutes = appConfig.LockoutMinutes;

    /// <summary>
    /// Increment-and-expire in a single round trip, because the check and the increment must not be
    /// separable: a read-then-write pair lets concurrent attempts each observe the pre-increment
    /// count and share one slot of the budget. The expiry is set inside the same script for the
    /// same reason — a counter that failed to acquire a TTL would lock the principal out for ever.
    /// </summary>
    private static readonly LuaScript _incrScript = LuaScript.Prepare("""
        local count = redis.call('INCR', @key)
        if count == 1 then redis.call('EXPIRE', @key, @window) end
        return count
        """);

    public async Task<bool> IsBlockedAsync(string ipAddress, Guid? userId = null, string keyPrefix = "login")
    {
        var ipCount = (long?)await _db.StringGetAsync($"rate:{keyPrefix}:{ipAddress}") ?? 0;
        if (ipCount >= _maxAttempts) return true;

        if (userId.HasValue)
        {
            var userCount = (long?)await _db.StringGetAsync($"rate:{keyPrefix}:user:{userId}") ?? 0;
            if (userCount >= _maxAttempts) return true;
        }
        return false;
    }

    public async Task<bool> RecordFailureAsync(string ipAddress, Guid? userId = null, string keyPrefix = "login")
    {
        var window = _lockoutMinutes * 60;
        var ipCount = (long)await _db.ScriptEvaluateAsync(_incrScript,
            new { key = (RedisKey)$"rate:{keyPrefix}:{ipAddress}", window });
        var blocked = ipCount >= _maxAttempts;

        if (userId.HasValue)
        {
            var userCount = (long)await _db.ScriptEvaluateAsync(_incrScript,
                new { key = (RedisKey)$"rate:{keyPrefix}:user:{userId}", window });
            blocked = blocked || userCount >= _maxAttempts;
        }
        return blocked;
    }

    /// <summary>
    /// Clears the per-user counter after a successful authentication.
    ///
    /// The per-IP counter is deliberately NOT cleared: it is shared across every account
    /// targeted from that address, so clearing it would let an attacker holding one valid
    /// account reset the budget at will and brute-force other accounts indefinitely from the
    /// same IP. The per-IP counter expires only by TTL.
    /// </summary>
    public async Task ResetAsync(string ipAddress, Guid userId, string keyPrefix = "login")
    {
        await _db.KeyDeleteAsync($"rate:{keyPrefix}:user:{userId}");
    }
}
