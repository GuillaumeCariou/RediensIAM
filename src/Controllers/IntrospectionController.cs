using Microsoft.AspNetCore.Mvc;
using RediensIAM.Config;
using RediensIAM.Middleware;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Token introspection and authorisation for external resource servers — the surface a gateway
/// needs to centralise identity, authentication and authorisation on RediensIAM.
///
/// Without it an integrator had three bad options: call Hydra's admin API directly (bypasses
/// RediensIAM entirely and requires opening the most locked-down NetworkPolicy in the
/// deployment), relay a user token to /account/me (an oracle — any bearer can probe validity),
/// or validate JWTs locally against JWKS (no live revocation, inherits the stale-claims problem).
///
/// Callers authenticate as a service account, by PAT or by client_credentials. The answer
/// reflects live state: revoked roles, deactivated service accounts and suspended organisations
/// are all reflected immediately rather than at token expiry.
/// </summary>
[ApiController]
[Route("api")]
public class IntrospectionController(
    HydraService hydra,
    PatService pats,
    KetoService keto,
    LiveAuthorizationService live,
    AuditLogService audit,
    AppConfig appConfig) : ControllerBase
{
    private TokenClaims Caller => HttpContext.GetClaims()!;

    /// <summary>
    /// The organisation this caller is allowed to ask about, or null for a deployment-level
    /// caller. A service account attached to an organisation may only introspect that
    /// organisation's tokens; without this, one tenant's gateway credential resolved every token
    /// the deployment had issued, and every relation in the tuple store.
    ///
    /// Empty <c>org_id</c> means the service account hangs off the <c>__system__</c> user list
    /// and holds no org-scoped role — i.e. it is a deployment-wide credential — so it stays
    /// unscoped. That is also what keeps a multi-tenant gateway working: it needs a system
    /// service account, not a tenant one.
    /// </summary>
    private Guid? CallerOrgScope => Guid.TryParse(Caller.OrgId, out var g) ? g : null;

    /// <summary>
    /// RFC 7662 token introspection. Accepts form-encoded parameters as the RFC specifies.
    /// Answers <c>{ "active": false }</c> for anything not currently valid — never an error,
    /// so a caller cannot distinguish "malformed" from "revoked" from "expired".
    /// </summary>
    [HttpPost("introspect")]
    [ProducesResponseType(typeof(IntrospectionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Introspect([FromForm] IntrospectionRequest body)
    {
        if (!IsServiceAccountCaller())
            return StatusCode(403, new { error = "service_account_required" });

        if (string.IsNullOrWhiteSpace(body.Token))
            return Ok(IntrospectionResult.Inactive);

        var claims = await ResolveAsync(body.Token);
        if (claims is null) return Ok(IntrospectionResult.Inactive);

        // Out of scope answers "inactive", not "forbidden": the RFC 7662 contract here is that a
        // caller cannot distinguish malformed from revoked from expired, and telling it "that
        // token exists but belongs to someone else" would be the disclosure this closes.
        if (!await IsInCallerScopeAsync(claims, "api.introspect.out_of_scope"))
            return Ok(IntrospectionResult.Inactive);

        // Roles are re-verified against Keto/DB, so a role revoked after the token was minted
        // does not survive in the answer.
        var level = claims.GetManagementLevel();
        var roles = level != ManagementLevel.None && !await live.IsStillGrantedAsync(claims, level)
            ? claims.Roles.Where(r => !IsManagementRole(r)).ToList()
            : claims.Roles;

        return Ok(new IntrospectionResult(
            Active:    true,
            Sub:       claims.UserId,
            UserId:    claims.ParsedUserId == Guid.Empty ? null : claims.ParsedUserId.ToString(),
            OrgId:     NullIfEmpty(claims.OrgId),
            ProjectId: NullIfEmpty(claims.ProjectId),
            Roles:     roles,
            ClientId:  NullIfEmpty(claims.ClientId),
            IsServiceAccount: claims.IsServiceAccount));
    }

    /// <summary>
    /// Permission decision for a gateway: "may the bearer of this token do X?", expressed as a
    /// Keto relation check. Keeps the policy in one place instead of every gateway
    /// reimplementing its own interpretation of the roles claim.
    /// </summary>
    [HttpPost("authorize")]
    [ProducesResponseType(typeof(AuthorizationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Authorize([FromBody] AuthorizationRequest body)
    {
        if (!IsServiceAccountCaller())
            return StatusCode(403, new { error = "service_account_required" });

        var claims = await ResolveAsync(body.Token);
        if (claims is null) return Ok(new AuthorizationResult(false, null));

        if (!await IsInCallerScopeAsync(claims, "api.authorize.out_of_scope"))
            return Ok(new AuthorizationResult(false, null));

        // The System namespace holds exactly one object and one interesting relation:
        // rediensiam#super_admin. A tenant credential asking about it is enumerating the
        // deployment's administrators, never authorising its own request.
        if (CallerOrgScope is not null &&
            body.Namespace.Equals(Roles.KetoSystemNamespace, StringComparison.OrdinalIgnoreCase))
        {
            await audit.RecordAsync(CallerOrgScope, null, Caller.ParsedUserId,
                "api.authorize.out_of_scope", "keto_namespace", body.Namespace);
            return Ok(new AuthorizationResult(false, null));
        }

        var subject = $"user:{claims.ParsedUserId}";
        var allowed = await keto.CheckAsync(body.Namespace, body.Object, body.Relation, subject);

        return Ok(new AuthorizationResult(allowed, claims.ParsedUserId.ToString()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Both token shapes RediensIAM issues: personal access tokens and OAuth2 access tokens.</summary>
    private async Task<TokenClaims?> ResolveAsync(string token)
    {
        if (token.StartsWith(appConfig.PatPrefix, StringComparison.Ordinal))
        {
            var pat = await pats.IntrospectAsync(token);
            return pat is { Active: true }
                ? new TokenClaims
                {
                    UserId = pat.Sub, OrgId = pat.OrgId, ProjectId = pat.ProjectId,
                    Roles = pat.Roles, IsServiceAccount = true,
                }
                : null;
        }
        return await hydra.ValidateJwtAsync(token);
    }

    /// <summary>
    /// True when the resolved token belongs to the caller's organisation, or the caller is a
    /// deployment-level service account. Refusals are audited — this surface previously wrote no
    /// record at all, so a compromised resource-server credential could enumerate every tenant
    /// with nothing to find afterwards. Only refusals are recorded: a row per introspection would
    /// be a row per API request behind every gateway.
    /// </summary>
    private async Task<bool> IsInCallerScopeAsync(TokenClaims subject, string auditAction)
    {
        var scope = CallerOrgScope;
        if (scope is null) return true;
        if (Guid.TryParse(subject.OrgId, out var subjectOrg) && subjectOrg == scope) return true;

        await audit.RecordAsync(scope, null, Caller.ParsedUserId, auditAction,
            "token", subject.ParsedUserId == Guid.Empty ? null : subject.ParsedUserId.ToString());
        return false;
    }

    private bool IsServiceAccountCaller() =>
        Caller.IsServiceAccount
        || Caller.ClientId.StartsWith(Roles.ServiceAccountClientPrefix, StringComparison.Ordinal);

    private static bool IsManagementRole(string role) =>
        role is Roles.SuperAdmin or Roles.OrgAdmin or Roles.ProjectAdmin;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

// ── Wire contract ─────────────────────────────────────────────────────────────

public record IntrospectionRequest(string Token, string? TokenTypeHint = null);

/// <summary>RFC 7662 response. Field names are serialised snake_case by the global JSON options.</summary>
public record IntrospectionResult(
    bool Active,
    string? Sub = null,
    string? UserId = null,
    string? OrgId = null,
    string? ProjectId = null,
    List<string>? Roles = null,
    string? ClientId = null,
    bool IsServiceAccount = false)
{
    public static readonly IntrospectionResult Inactive = new(false);
}

public record AuthorizationRequest(
    string Token,
    string Namespace,
    string Object,
    string Relation);

public record AuthorizationResult(bool Allowed, string? UserId);
