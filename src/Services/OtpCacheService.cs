using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public class OtpCacheService(IConnectionMultiplexer redis, IConfiguration config)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _ttlSeconds = config.GetValue<int>("Security:OtpTtlSeconds", 300);
    private readonly int _maxSmsPerWindow = 3;
    private readonly int _smsWindowMinutes = 10;

    public async Task StoreOtpAsync(string prefix, Guid userId, string code)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        await _db.StringSetAsync($"otp:{prefix}:{userId}", hash, TimeSpan.FromSeconds(_ttlSeconds));
    }

    public async Task<bool> VerifyOtpAsync(string prefix, Guid userId, string code)
    {
        var key = $"otp:{prefix}:{userId}";
        var stored = await _db.StringGetAsync(key);
        if (stored.IsNull) return false;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored.ToString()),
            Encoding.UTF8.GetBytes(hash)))
            return false;

        await _db.KeyDeleteAsync(key);
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
        var key = $"otp:{prefix}:{sessionId}";
        var stored = await _db.StringGetAsync(key);
        if (stored.IsNull) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored.ToString()),
            Encoding.UTF8.GetBytes(hash)))
            return false;
        await _db.KeyDeleteAsync(key);
        return true;
    }

    public async Task StoreTotpUsedAsync(Guid userId, string code)
    {
        await _db.StringSetAsync($"otp:totp_used:{userId}:{code}", "1", TimeSpan.FromSeconds(90));
    }

    public async Task<bool> IsTotpUsedAsync(Guid userId, string code)
        => await _db.KeyExistsAsync($"otp:totp_used:{userId}:{code}");
}
