using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public class LoginRateLimiter(IConnectionMultiplexer redis, AppConfig appConfig)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _maxAttempts = appConfig.MaxLoginAttempts;
    private readonly int _lockoutMinutes = appConfig.LockoutMinutes;

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

    public async Task RecordFailureAsync(string ipAddress, Guid? userId = null, string keyPrefix = "login")
    {
        var window = TimeSpan.FromMinutes(_lockoutMinutes);
        var ipCount = await _db.StringIncrementAsync($"rate:{keyPrefix}:{ipAddress}");
        if (ipCount == 1) await _db.KeyExpireAsync($"rate:{keyPrefix}:{ipAddress}", window);

        if (userId.HasValue)
        {
            var userCount = await _db.StringIncrementAsync($"rate:{keyPrefix}:user:{userId}");
            if (userCount == 1) await _db.KeyExpireAsync($"rate:{keyPrefix}:user:{userId}", window);
        }
    }

    public async Task ResetAsync(string ipAddress, Guid userId)
    {
        await _db.KeyDeleteAsync($"rate:login:{ipAddress}");
        await _db.KeyDeleteAsync($"rate:login:user:{userId}");
    }
}
