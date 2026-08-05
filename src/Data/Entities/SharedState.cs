namespace RediensIAM.Data.Entities;

/// <summary>
/// One row of the shared key/value store that used to live in Dragonfly.
///
/// <para>
/// The deployment ran two datastores where one would do, and the second one carried more than a
/// cache: the DataProtection key ring that signs session cookies, the pending-MFA session, the
/// OAuth2 social <c>state</c>, the TOTP anti-replay set. Losing any of those to a pod restart —
/// or serving them from two independent replicas — is an outage or a security hole, not a slower
/// response. Postgres already holds every durable thing this system owns, is already replicated,
/// backed up, TLS-terminated and NetworkPolicy-locked; a second store bought nothing and cost a
/// secret, a certificate, a health check, two client abstractions and four settings.
/// </para>
///
/// <para>
/// <b>Expiry is a column, not a mechanism.</b> Every read filters on it, so an expired row is
/// invisible the instant it expires — exactly like a TTL. <see cref="Services.ExpiredStateSweeper"/>
/// only reclaims the space; correctness never waits for it.
/// </para>
/// </summary>
public class SharedStateEntry
{
    /// <summary>The full key, prefix included — the same string the cache API is called with.</summary>
    public string Key { get; set; } = string.Empty;

    public byte[] Value { get; set; } = [];

    /// <summary>Null means "no expiry". Sliding expiration is not supported and is not used.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// A fixed-window counter — login attempts per IP, per user, SMS per user.
///
/// <para>
/// Redis did this with <c>INCR</c> plus <c>EXPIRE</c> inside one Lua script, because the read and
/// the increment must not be separable: a read-then-write pair lets concurrent attempts each
/// observe the pre-increment count and share one slot of the budget. PostgreSQL does the same in
/// one statement — <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> — which is atomic by
/// definition and needs no script, no <c>SCRIPT LOAD</c> and no EVALSHA round trip.
/// </para>
/// </summary>
public class RateCounter
{
    public string Key { get; set; } = string.Empty;
    public long Count { get; set; }

    /// <summary>End of the fixed window. A row past it is treated as absent and reset to 1.</summary>
    public DateTimeOffset WindowEnd { get; set; }
}

/// <summary>
/// A webhook delivery waiting to be dispatched — the durable half of the queue.
///
/// <para>
/// This was a Redis sorted set, which is why it was a sorted set: the score is the earliest time
/// the job may be retried. A table with an index on <see cref="Score"/> answers the same question,
/// and unlike the sorted set it survives a cache restart — which the Dragonfly deployment had no
/// PVC for, so every in-flight delivery was lost on any rollout.
/// </para>
/// </summary>
public class WebhookPending
{
    /// <summary>The serialised <c>WebhookJob</c>. Primary key, as it was the sorted-set member.</summary>
    public string JobJson { get; set; } = string.Empty;

    /// <summary>Unix milliseconds: the earliest moment this job may be attempted.</summary>
    public long Score { get; set; }
}

