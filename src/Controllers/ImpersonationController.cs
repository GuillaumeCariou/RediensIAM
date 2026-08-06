using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Delegated sessions: an operator entering a customer organisation to diagnose, configure and
/// support — without an iframe, without reopening <c>frame-ancestors</c>, and without weakening a
/// cookie attribute for the whole customer population.
///
/// <para>
/// <b>Two gates, not one.</b> The caller must be a service account <i>and</i> hold
/// <c>SuperAdmin</c>. Service-account gating is the rule already applied to <c>/api/introspect</c>:
/// a plain user token is refused so this endpoint is never an oracle reachable from a browser
/// session. The management level is then re-resolved live against Keto by
/// <see cref="RequireManagementLevelAttribute"/> — the claimed level decides <i>which</i> level to
/// re-check, it is never the answer (S-1).
/// </para>
///
/// <para>
/// Reachable under both the admin prefix and the management prefix, like every other route a
/// service consumes rather than a console: this exists for other services first, and a console
/// second.
/// </para>
/// </summary>
[ApiController]
[Route("admin/impersonate")]
[Route("api/manage/impersonate")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class ImpersonationController(
    RediensIamDbContext db,
    ImpersonationService impersonation) : ControllerBase
{
    /// <summary>Matches the column width — one number, in one place, on both sides of the write.</summary>
    public const int MaxReasonLength = 500;

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid ActorId       => Claims.ParsedUserId;

    /// <summary>
    /// The same gate as <c>IntrospectionController.IsServiceAccountCaller</c>. Written here rather
    /// than shared: the two surfaces answer to different levels and a shared helper would invite
    /// changing both at once.
    ///
    /// <para>
    /// It guards <b>opening</b> only. Minting a delegated credential from a browser session is the
    /// act worth refusing — it is what would make this endpoint an oracle reachable with a cookie.
    /// Listing and revoking are supervision: an operator console holds a user token, and a session
    /// nobody can list is a session nobody can stop. Both remain <c>SuperAdmin</c>, re-checked live
    /// against Keto by the class filter, and revocation only ever ends access.
    /// </para>
    /// </summary>
    private bool IsServiceAccountCaller() =>
        Claims.IsServiceAccount
        || Claims.ClientId.StartsWith(Roles.ServiceAccountClientPrefix, StringComparison.Ordinal);

    // ── Open ──────────────────────────────────────────────────────────────────

    [HttpPost("")]
    public async Task<IActionResult> Open([FromBody] OpenImpersonationRequest body)
    {
        if (!IsServiceAccountCaller())
            return StatusCode(403, new { error = "service_account_required" });

        // A session with no stated reason is not auditable, which is the same as saying it must
        // not exist. Whitespace is not a reason.
        if (string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { error = "reason_required" });

        // The column is 500. Without this the overflow surfaces as a DbUpdateException and a 500,
        // which tells the caller nothing about the one thing it can fix — the same defect the
        // unique-index-as-500 case fixed in UserListOperations.
        if (body.Reason.Length > MaxReasonLength)
            return BadRequest(new { error = "reason_too_long", max_length = MaxReasonLength });

        if (!ImpersonationModes.IsValid(body.Mode))
            return BadRequest(new { error = "invalid_mode", allowed = new[] { ImpersonationModes.Read, ImpersonationModes.Write } });

        // Naming a user is refused outright, not ignored: an org-scoped session is the weaker
        // capability and therefore the only one on offer today. A caller that sends `user_id`
        // believes it is entering a person's account, and answering 200 to that belief is worse
        // than refusing it. Adding named-user impersonation later is an addition to this contract.
        if (body.UserId is not null)
            return BadRequest(new { error = "user_id_not_supported", detail = "sessions are organisation-scoped; see docs/IMPERSONATION.md" });

        // What this has to establish is "can this organisation authenticate on this project?", not
        // "does this organisation own this project". The two coincide only where every tenant has a
        // project of its own; they come apart on a shared surface, which is the model
        // docs/ORGANIZATIONS.md describes and the one impersonation exists for — a single
        // `yandee-client` project, one login page, one gateway, every customer behind it. Owning
        // the project would have been false for every pair a caller could form there, so the route
        // answered project_not_in_org to all of them.
        //
        // The assigned user list is the link that carries the answer in both models: a member of
        // the target organisation on the project's list means that organisation signs in here.
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == body.ProjectId);
        if (project is null)
            return BadRequest(new { error = "project_not_found" });

        var reachable = project.OrgId == body.OrgId
            || (project.AssignedUserListId is { } listId
                && await db.Users.AnyAsync(u => u.UserListId == listId && u.OrgId == body.OrgId));
        if (!reachable)
            return BadRequest(new { error = "project_not_in_org" });

        var (raw, session) = await impersonation.OpenAsync(new OpenSession(
            ActorId, Roles.SuperAdmin, body.OrgId, body.ProjectId, body.Mode, body.Reason, body.TtlSeconds));

        return Ok(new
        {
            access_token = raw,
            token_type   = "bearer",
            expires_in   = (int)(session.ExpiresAt - session.CreatedAt).TotalSeconds,
            session_id   = session.Id,
            act          = new { sub = ActorId, level = Roles.SuperAdmin, mode = session.Mode, session = session.Id },
            sub          = ImpersonationService.SubjectFor(session.Id),
            org_id       = session.OrgId,
            project_id   = session.ProjectId,
            message      = "store_this_token_shown_once",
        });
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Active sessions. An impersonation nobody can list is an impersonation nobody can stop.
    ///
    /// <para>
    /// The one-at-a-time rule is per operator, so this can legitimately return several rows across
    /// operators while each operator holds at most one.
    /// </para>
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> List([FromQuery(Name = "actor_id")] Guid? actorId)
    {
        var sessions = await impersonation.ListActiveAsync(actorId);
        return Ok(sessions.Select(s => new
        {
            session_id  = s.Id,
            act_sub     = s.ActorUserId,
            act_level   = s.ActorLevel,
            org_id      = s.OrgId,
            project_id  = s.ProjectId,
            s.Mode,
            s.Reason,
            created_at  = s.CreatedAt,
            expires_at  = s.ExpiresAt,
            last_used_at = s.LastUsedAt,
        }));
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    [HttpPost("{sessionId}/revoke")]
    public async Task<IActionResult> Revoke(Guid sessionId)
    {
        return await impersonation.RevokeAsync(sessionId, ActorId)
            ? NoContent()
            : NotFound();
    }
}

/// <summary>
/// <c>UserId</c> is declared so it can be <b>refused</b>. Dropping the field from the contract
/// would make a caller that sends it believe it was honoured — the same silent-acceptance defect
/// that <c>token_type_hint</c> was.
/// </summary>
public record OpenImpersonationRequest(
    [property: System.Text.Json.Serialization.JsonRequired] Guid OrgId,
    [property: System.Text.Json.Serialization.JsonRequired] Guid ProjectId,
    [property: System.Text.Json.Serialization.JsonRequired] string Mode,
    [property: System.Text.Json.Serialization.JsonRequired] string Reason,
    int? TtlSeconds = null,
    Guid? UserId = null);
