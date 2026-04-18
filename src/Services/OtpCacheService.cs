using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public class OtpCacheService(IConnectionMultiplexer redis, AppConfig appConfig)
{
    private const int MaxOtpAttempts = 5;

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _ttlSeconds = appConfig.OtpTtlSeconds;
    private readonly int _maxSmsPerWindow = appConfig.MaxSmsPerWindow;
    private readonly int _smsWindowMinutes = appConfig.SmsWindowMinutes;

    public async Task StoreOtpAsync(string prefix, Guid userId, string code)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        await _db.StringSetAsync($"otp:{prefix}:{userId}", hash, TimeSpan.FromSeconds(_ttlSeconds));
    }

    public async Task<bool> VerifyOtpAsync(string prefix, Guid userId, string code)
    {
        var key     = $"otp:{prefix}:{userId}";
        var failKey = $"otp:{prefix}:{userId}:fails";

        var stored = await _db.StringGetAsync(key);
        if (stored.IsNull) return false;

        var fails = (long?)await _db.StringGetAsync(failKey) ?? 0;
        if (fails >= MaxOtpAttempts)
        {
            await _db.KeyDeleteAsync(key);
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored.ToString()),
            Encoding.UTF8.GetBytes(hash)))
        {
            var newCount = await _db.StringIncrementAsync(failKey);
            if (newCount == 1) await _db.KeyExpireAsync(failKey, TimeSpan.FromSeconds(_ttlSeconds));
            return false;
        }

        await _db.KeyDeleteAsync(key);
        await _db.KeyDeleteAsync(failKey);
        return true;
    }

    public async Task EnforceSmsRateLimitAsync(Guid userId)
    {
        var key = $"rate:otp:sms:{userId}";
        var count = await _db.StringIncrementAsync(key);
        if (count == 1) await _db.KeyExpireAsync(key, TimeSpan.FromMinutes(_smsWindowMinutes));
        if (count > _maxSmsPerWindow)
            throw new RateLimitException("Too many SMS requests. Try again later.");
    }

    // ── Session-keyed OTP (no userId — for pending registrations and resets) ──

    public async Task StorePendingAsync(string prefix, string sessionId, string data)
        => await _db.StringSetAsync($"pending:{prefix}:{sessionId}", data, TimeSpan.FromSeconds(_ttlSeconds));

    public async Task<string?> GetAndDeletePendingAsync(string prefix, string sessionId)
    {
        var key = $"pending:{prefix}:{sessionId}";
        var val = await _db.StringGetAsync(key);
        if (val.IsNull) return null;
        await _db.KeyDeleteAsync(key);
        return val.ToString();
    }

    public async Task StoreSessionOtpAsync(string prefix, string sessionId, string code)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        await _db.StringSetAsync($"otp:{prefix}:{sessionId}", hash, TimeSpan.FromSeconds(_ttlSeconds));
    }

    public async Task<bool> VerifySessionOtpAsync(string prefix, string sessionId, string code)
    {
        var key     = $"otp:{prefix}:{sessionId}";
        var failKey = $"otp:{prefix}:{sessionId}:fails";

        var stored = await _db.StringGetAsync(key);
        if (stored.IsNull) return false;

        var fails = (long?)await _db.StringGetAsync(failKey) ?? 0;
        if (fails >= MaxOtpAttempts)
        {
            await _db.KeyDeleteAsync(key);
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored.ToString()),
            Encoding.UTF8.GetBytes(hash)))
        {
            var newCount = await _db.StringIncrementAsync(failKey);
            if (newCount == 1) await _db.KeyExpireAsync(failKey, TimeSpan.FromSeconds(_ttlSeconds));
            return false;
        }

        await _db.KeyDeleteAsync(key);
        await _db.KeyDeleteAsync(failKey);
        return true;
    }

    public async Task StoreTotpUsedAsync(Guid userId, string code)
    {
        var keyCode = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..16];
        await _db.StringSetAsync($"otp:totp_used:{userId}:{keyCode}", "1", TimeSpan.FromSeconds(90));
    }

    public async Task<bool> IsTotpUsedAsync(Guid userId, string code)
    {
        var keyCode = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..16];
        return await _db.KeyExistsAsync($"otp:totp_used:{userId}:{keyCode}");
    }
}
