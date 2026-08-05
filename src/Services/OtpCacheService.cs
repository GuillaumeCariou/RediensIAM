using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

/// <summary>
/// One-time codes and short-lived flow state: registration, password reset, phone and TOTP
/// enrolment, and the TOTP anti-replay set.
///
/// <para>
/// Backed by <see cref="PostgresSharedState"/>. This <b>must</b> be shared across replicas: the
/// anti-replay set is a security control — a per-pod copy makes an observed TOTP code replayable
/// on the other replica — and every flow here spans more than one request.
/// </para>
/// </summary>
public class OtpCacheService(PostgresSharedState state, AppConfig appConfig)
{
    private const int MaxOtpAttempts = 5;

    private readonly int _ttlSeconds = appConfig.OtpTtlSeconds;
    private readonly int _maxSmsPerWindow = appConfig.MaxSmsPerWindow;
    private readonly int _smsWindowMinutes = appConfig.SmsWindowMinutes;

    /// <summary>TTL for enrolment flows (TOTP/WebAuthn/phone setup).</summary>
    public const int EnrolmentTtlSeconds = 900;

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private Task SetAsync(string key, string value, int ttlSeconds) =>
        state.SetStringAsync(key, value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds),
        });

    // ── User-keyed OTP ────────────────────────────────────────────────────────

    public Task StoreOtpAsync(string prefix, Guid userId, string code)
        => SetAsync($"otp:{prefix}:{userId}", Digest(code), _ttlSeconds);

    public Task<bool> VerifyOtpAsync(string prefix, Guid userId, string code)
        => VerifyAsync($"otp:{prefix}:{userId}", code);

    // ── Session-keyed OTP (no userId — pending registrations and resets) ──────

    public Task StoreSessionOtpAsync(string prefix, string sessionId, string code)
        => SetAsync($"otp:{prefix}:{sessionId}", Digest(code), _ttlSeconds);

    public Task<bool> VerifySessionOtpAsync(string prefix, string sessionId, string code)
        => VerifyAsync($"otp:{prefix}:{sessionId}", code);

    /// <summary>
    /// One verification path for both key shapes. The attempt counter and the constant-time
    /// comparison <i>are</i> this method; duplicating them per shape is how one copy loses the
    /// counter, or loses <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// </summary>
    private async Task<bool> VerifyAsync(string key, string code)
    {
        var failKey = $"{key}:fails";

        var stored = await state.GetStringAsync(key);
        if (stored is null) return false;

        if (await state.CounterAsync(failKey) >= MaxOtpAttempts)
        {
            await state.RemoveAsync(key);
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(Digest(code))))
        {
            await state.IncrementAsync(failKey, TimeSpan.FromSeconds(_ttlSeconds));
            return false;
        }

        await state.RemoveAsync(key);
        await state.ResetCounterAsync(failKey);
        return true;
    }

    // ── SMS rate limit ────────────────────────────────────────────────────────

    public async Task EnforceSmsRateLimitAsync(Guid userId)
    {
        var count = await state.IncrementAsync(
            $"rate:otp:sms:{userId}", TimeSpan.FromMinutes(_smsWindowMinutes));
        if (count > _maxSmsPerWindow)
            throw new RateLimitException("Too many SMS requests. Try again later.");
    }

    // ── Pending flow state ────────────────────────────────────────────────────

    /// <summary>
    /// Stores short-lived flow state server-side. <paramref name="ttlSeconds"/> overrides the OTP
    /// TTL for flows that legitimately take longer than a one-time code — enrolling an
    /// authenticator means scanning a QR code and waiting for the next window.
    /// </summary>
    public Task StorePendingAsync(string prefix, string sessionId, string data, int? ttlSeconds = null)
        => SetAsync($"pending:{prefix}:{sessionId}", data, ttlSeconds ?? _ttlSeconds);

    /// <summary>
    /// Reads pending state without consuming it — for flows where a wrong code should let the user
    /// retry rather than restart enrolment.
    /// </summary>
    public Task<string?> PeekPendingAsync(string prefix, string sessionId)
        => state.GetStringAsync($"pending:{prefix}:{sessionId}");

    public Task DeletePendingAsync(string prefix, string sessionId)
        => state.RemoveAsync($"pending:{prefix}:{sessionId}");

    public async Task<string?> GetAndDeletePendingAsync(string prefix, string sessionId)
    {
        var key = $"pending:{prefix}:{sessionId}";
        var value = await state.GetStringAsync(key);
        if (value is null) return null;
        await state.RemoveAsync(key);
        return value;
    }

    // ── TOTP anti-replay ──────────────────────────────────────────────────────
    //
    // A security control, not a cache: a code observed once must not be reusable, and a per-pod
    // copy would make it replayable on the other replica. 90 s covers VerificationWindow(1,1)
    // either side of a 30 s step with room to spare.

    public Task StoreTotpUsedAsync(Guid userId, string code)
        => SetAsync($"otp:totp_used:{userId}:{Digest(code)}", "1", 90);

    public Task<bool> IsTotpUsedAsync(Guid userId, string code)
        => state.ExistsAsync($"otp:totp_used:{userId}:{Digest(code)}");
}
