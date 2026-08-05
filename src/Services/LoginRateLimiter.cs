using RediensIAM.Config;

namespace RediensIAM.Services;

/// <summary>
/// The account-lockout budget: failed attempts per source address and per account.
///
/// <para>
/// Shared state, not a cache. With more than one replica a per-pod counter multiplies the budget
/// by the replica count — five attempts become ten — which is the control silently not working
/// rather than working differently.
/// </para>
/// </summary>
public class LoginRateLimiter(PostgresSharedState state, AppConfig appConfig)
{
    private readonly int _maxAttempts = appConfig.MaxLoginAttempts;
    private readonly int _lockoutMinutes = appConfig.LockoutMinutes;

    private TimeSpan Window => TimeSpan.FromMinutes(_lockoutMinutes);

    private static string IpKey(string keyPrefix, string ip) => $"rate:{keyPrefix}:{ip}";
    private static string UserKey(string keyPrefix, Guid userId) => $"rate:{keyPrefix}:user:{userId}";

    public async Task<bool> IsBlockedAsync(string ipAddress, Guid? userId = null, string keyPrefix = "login")
    {
        if (await state.CounterAsync(IpKey(keyPrefix, ipAddress)) >= _maxAttempts) return true;
        return userId.HasValue
            && await state.CounterAsync(UserKey(keyPrefix, userId.Value)) >= _maxAttempts;
    }

    /// <summary>
    /// Counts one failure against both budgets.
    ///
    /// <para>
    /// The increment is atomic and returns the new value — <c>INSERT … ON CONFLICT DO UPDATE …
    /// RETURNING</c>, see <see cref="PostgresSharedState.IncrementAsync"/>. The check and the
    /// increment must not be separable: a read-then-write pair lets concurrent attempts each
    /// observe the pre-increment count and share one slot of the budget. That is the property the
    /// Lua script bought on Redis, and a single SQL statement has it by definition.
    /// </para>
    /// </summary>
    public async Task<bool> RecordFailureAsync(string ipAddress, Guid? userId = null, string keyPrefix = "login")
    {
        var blocked = await state.IncrementAsync(IpKey(keyPrefix, ipAddress), Window) >= _maxAttempts;

        if (userId.HasValue)
            blocked |= await state.IncrementAsync(UserKey(keyPrefix, userId.Value), Window) >= _maxAttempts;

        return blocked;
    }

    /// <summary>
    /// Clears the per-user counter after a successful authentication.
    ///
    /// <para>
    /// The per-IP counter is deliberately NOT cleared: it is shared across every account targeted
    /// from that address, so clearing it would let an attacker holding one valid account reset the
    /// budget at will and brute-force other accounts indefinitely from the same IP. The per-IP
    /// counter expires only with its window.
    /// </para>
    /// </summary>
    public Task ResetAsync(string ipAddress, Guid userId, string keyPrefix = "login")
        => state.ResetCounterAsync(UserKey(keyPrefix, userId));
}
