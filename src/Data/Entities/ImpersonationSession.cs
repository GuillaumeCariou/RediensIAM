namespace RediensIAM.Data.Entities;

/// <summary>
/// A delegated session: an operator acting <b>for</b> a customer organisation, for a bounded time,
/// with a stated reason.
///
/// <para>
/// The row is the session. There is no separate token store — <see cref="TokenHash"/> is the
/// SHA-256 of the credential handed out once at issuance, exactly as
/// <see cref="PersonalAccessToken"/> does, so the deployment never holds anything that can be
/// replayed if the table leaks.
/// </para>
///
/// <para>
/// <b>Expiry is a predicate, not a job.</b> Every read filters on <c>ExpiresAt</c> and
/// <c>RevokedAt</c>, so a session stops being usable the instant it expires rather than when a
/// sweeper next runs. Nothing here needs a background task to be correct.
/// </para>
/// </summary>
public class ImpersonationSession
{
    public Guid Id { get; set; }

    /// <summary>The operator. This is the <c>act.sub</c> of every token minted from this row.</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>
    /// The operator's management level at issuance, recorded rather than re-derived: what the
    /// audit trail must show is the authority the session was opened under, which is a fact about
    /// that moment and does not change when the operator's grants later do.
    /// </summary>
    public string ActorLevel { get; set; } = string.Empty;

    /// <summary>The organisation being entered. Also the tenant scope of the audit row.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The authentication boundary — mandatory everywhere else in this API, mandatory here.</summary>
    public Guid ProjectId { get; set; }

    /// <summary><c>read</c> or <c>write</c>. Decided at issuance, never inferred from a role.</summary>
    public string Mode { get; set; } = ImpersonationModes.Read;

    /// <summary>Free text, required. An impersonation with no stated reason is not auditable.</summary>
    public string Reason { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when revoked explicitly, or when the operator opened a newer session.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// The two modes, as constants rather than a bool: <c>mode == "read"</c> reads as what it is at
/// every enforcement point, and a third mode later is an addition rather than a signature change.
/// </summary>
public static class ImpersonationModes
{
    public const string Read  = "read";
    public const string Write = "write";

    public static bool IsValid(string? mode) => mode is Read or Write;
}
