using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public class LoginRateLimiter(IConnectionMultiplexer redis, AppConfig appConfig)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _maxAttempts = appConfig.MaxLoginAttempts;
    private readonly int _lockoutMinutes = appConfig.LockoutMinutes;

    // Atomically increments counter and returns true if the new count >= max.
    // Sets expiry on first increment. Single round-trip — no TOCTOU.
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

    public async Task ResetAsync(string ipAddress, Guid userId, string keyPrefix = "login")
    {
        await _db.KeyDeleteAsync($"rate:{keyPrefix}:{ipAddress}");
        await _db.KeyDeleteAsync($"rate:{keyPrefix}:user:{userId}");
    }
}
