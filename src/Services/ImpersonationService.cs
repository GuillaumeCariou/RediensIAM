using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Models;

namespace RediensIAM.Services;

/// <summary>
/// Delegated sessions — an operator acting for a customer organisation.
///
/// <para>
/// <b>What a delegated token is.</b> An opaque RediensIAM credential, not an OAuth2 token. Every
/// gateway already calls <c>/api/introspect</c> on every request, so a consumer learns nothing new:
/// it receives one extra field, <c>act</c>. Nothing in the OIDC flow changes, no CSP is reopened
/// and no cookie attribute is weakened for the customer population.
/// </para>
///
/// <para>
/// <b>What it is not.</b> It carries <i>who acts for whom</i>, never <i>what they may do</i>. The
/// token names no roles at all — <c>Roles</c> is empty by construction, which is what makes
/// "management roles are stripped" true by shape rather than by a filter someone must remember to
/// apply. Authority still comes from the enforcement point, as it does for every other token this
/// server issues.
/// </para>
///
/// <para>
/// <b>Why the subject is not a user.</b> A session names an organisation, not a person, so its
/// subject is <c>imp_&lt;session id&gt;</c> — deliberately not the <c>prefix:guid</c> shape that
/// <see cref="TokenClaims.ParsedUserId"/> parses. A delegated token therefore resolves to
/// <see cref="Guid.Empty"/> as a user id everywhere in this codebase, because there is no user:
/// entering a customer's mailbox to fix a billing setting is a stronger capability than the job
/// needs, and the weaker one is the default. Naming a user later is an addition to this shape, not
/// a change to it.
/// </para>
/// </summary>
public class ImpersonationService(RediensIamDbContext db, AuditLogService audit)
{
    /// <summary>
    /// Literal, and deliberately not <c>Security:PatPrefix</c>: a leaked delegated token must be
    /// recognisable as one in a log, and telling it apart from a service-account credential at a
    /// glance is the point of a distinct prefix.
    /// </summary>
    public const string TokenPrefix = "rediens_imp_";

    /// <summary>Subject prefix. Underscore, not colon — see the class note on <c>ParsedUserId</c>.</summary>
    public const string SubjectPrefix = "imp_";

    public const int DefaultTtlSeconds = 900;
    public const int MaxTtlSeconds     = 3600;

    /// <summary>A delegated token is a loan, not a role: the ceiling is hard, the default is short.</summary>
    public static int ClampTtl(int? requested) =>
        Math.Clamp(requested ?? DefaultTtlSeconds, 60, MaxTtlSeconds);

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string SubjectFor(Guid sessionId) => SubjectPrefix + sessionId;

    /// <summary>
    /// Opens a session and returns the credential, shown once.
    ///
    /// <para>
    /// Opening revokes the operator's previous session. One active impersonation at a time removes
    /// every question about which customer is currently entered, and costs one statement instead of
    /// a concurrent-session model nobody can reason about later.
    /// </para>
    /// </summary>
    public async Task<(string RawToken, ImpersonationSession Session)> OpenAsync(
        OpenSession request, CancellationToken token = default)
    {
        var (actorUserId, actorLevel, orgId, projectId, mode, reason, ttlSeconds) = request;
        await RevokeAllForActorAsync(actorUserId, actorUserId, token);

        var raw = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(40))
            .Replace("+", "a").Replace("/", "b").Replace("=", "c");

        var session = new ImpersonationSession
        {
            Id          = Guid.NewGuid(),
            ActorUserId = actorUserId,
            ActorLevel  = actorLevel,
            OrgId       = orgId,
            ProjectId   = projectId,
            Mode        = mode,
            Reason      = reason,
            TokenHash   = Hash(raw),
            CreatedAt   = DateTimeOffset.UtcNow,
            ExpiresAt   = DateTimeOffset.UtcNow.AddSeconds(ClampTtl(ttlSeconds)),
        };
        db.ImpersonationSessions.Add(session);
        await db.SaveChangesAsync(token);

        // Written against the entered organisation, never the operator's (they have none): the
        // tenant must be able to see in their own audit log that somebody entered.
        await audit.RecordAsync(orgId, projectId, actorUserId, "impersonation.opened",
            "impersonation", session.Id.ToString(),
            new()
            {
                ["mode"]       = mode,
                ["reason"]     = reason,
                ["expires_at"] = session.ExpiresAt.ToString("O"),
                ["act_sub"]    = actorUserId.ToString(),
                ["act_level"]  = actorLevel,
            });

        return (raw, session);
    }

    /// <summary>
    /// The live session behind a credential, or null. One statement, and the predicate is the
    /// whole liveness rule: not revoked, not expired.
    /// </summary>
    public async Task<ImpersonationSession?> ResolveAsync(string rawToken, CancellationToken token = default)
    {
        var hash = Hash(rawToken);
        var session = await db.ImpersonationSessions.FirstOrDefaultAsync(
            s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > DateTimeOffset.UtcNow, token);
        if (session is null) return null;

        // Best-effort: a failed touch must never fail the request it was observing.
        session.LastUsedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(token); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        return session;
    }

    /// <summary>The claims a delegated credential resolves to. No roles, ever — see the class note.</summary>
    public static TokenClaims ClaimsFor(ImpersonationSession s) => new()
    {
        UserId    = SubjectFor(s.Id),
        OrgId     = s.OrgId.ToString(),
        ProjectId = s.ProjectId.ToString(),
        Roles     = [],
        Act       = new ActorClaim(s.ActorUserId.ToString(), s.ActorLevel, s.Mode, s.Id.ToString()),
    };

    public Task<List<ImpersonationSession>> ListActiveAsync(Guid? actorUserId = null, CancellationToken token = default) =>
        db.ImpersonationSessions
            .Where(s => s.RevokedAt == null && s.ExpiresAt > DateTimeOffset.UtcNow)
            .Where(s => actorUserId == null || s.ActorUserId == actorUserId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(token);

    /// <summary>
    /// Ends a session. Returns false when there is nothing live to end, so the caller can answer
    /// 404 rather than pretend.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid sessionId, Guid revokedBy, CancellationToken token = default)
    {
        var session = await db.ImpersonationSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.RevokedAt == null, token);
        if (session is null) return false;

        session.RevokedAt = DateTimeOffset.UtcNow;
        session.RevokedBy = revokedBy;
        await db.SaveChangesAsync(token);

        await audit.RecordAsync(session.OrgId, session.ProjectId, revokedBy, "impersonation.revoked",
            "impersonation", session.Id.ToString(),
            new() { ["act_sub"] = session.ActorUserId.ToString() });
        return true;
    }

    private async Task RevokeAllForActorAsync(Guid actorUserId, Guid revokedBy, CancellationToken token)
    {
        var live = await db.ImpersonationSessions
            .Where(s => s.ActorUserId == actorUserId && s.RevokedAt == null)
            .ToListAsync(token);
        if (live.Count == 0) return;

        foreach (var session in live)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            session.RevokedBy = revokedBy;
        }
        await db.SaveChangesAsync(token);
    }
}

/// <summary>
/// What opening a session needs, as one value.
///
/// <para>
/// Seven positional arguments is where a call starts being read by counting commas — and two of
/// them are ids of the same type, side by side, which is the pair a caller transposes. Named once
/// here, they are named at every call site.
/// </para>
/// </summary>
public record OpenSession(
    Guid ActorUserId,
    string ActorLevel,
    Guid OrgId,
    Guid ProjectId,
    string Mode,
    string Reason,
    int? TtlSeconds);
