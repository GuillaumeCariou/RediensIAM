using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Middleware;
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
    RediensIamDbContext db,
    HydraService hydra,
    PatService pats,
    KetoService keto,
    LiveAuthorizationService live,
    AuditLogService audit,
    AppConfig appConfig) : ControllerBase
{
    /// <summary>
    /// Contract version of this surface. It exists so a client can tell an audience-enforcing
    /// server from one that silently ignores the <c>aud</c> it sent: an older RediensIAM drops
    /// the unknown field and answers without <c>ver</c>, so an SDK that requires
    /// <c>ver &gt;= 1</c> fails closed instead of believing it is bound when it is not. Every
    /// answer carries it, including <c>{"active": false}</c>.
    /// </summary>
    public const int ContractVersion = 1;

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
    ///
    /// <para><b>Breaking:</b> <c>aud</c> is required. A resource server that does not declare
    /// which tenant it serves is refused with 400, because the alternative — the pre-1 behaviour
    /// — is that a deployment-scoped gateway credential resolves every tenant's token as
    /// <c>active: true</c> and the resource server has to remember to compare
    /// <c>project_id</c> itself. Nobody remembers. See P-06.</para>
    /// </summary>
    // SCS0016 fires on any [FromForm] POST without an anti-forgery token. It does not apply
    // here: CSRF needs an ambient credential the browser attaches by itself, and this endpoint
    // authenticates by bearer only — GatewayAuthMiddleware 401s without one, and no browser
    // attaches a bearer cross-site. The form encoding is not a choice either: RFC 7662 §2.1
    // specifies it, and the callers are service accounts rather than pages.
#pragma warning disable SCS0016 // bearer-only surface: no ambient credential to forge against
    [HttpPost("introspect")]
    [ProducesResponseType(typeof(IntrospectionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Introspect([FromForm] IntrospectionRequest body)
#pragma warning restore SCS0016
    {
        if (!IsServiceAccountCaller())
            return StatusCode(403, new { error = "service_account_required" });

        // 400, not {"active":false}: this is a defect in the caller's own request, not a
        // statement about the token, and answering "inactive" would let an un-migrated
        // integration keep running while believing it had merely been handed a dead token.
        if (string.IsNullOrWhiteSpace(body.Aud))
            return BadRequest(new { error = "audience_required", ver = ContractVersion });

        if (string.IsNullOrWhiteSpace(body.Token))
            return Ok(IntrospectionResult.Inactive);

        var claims = await ResolveAsync(body.Token);
        if (claims is null) return Ok(IntrospectionResult.Inactive);

        if (!await IsBoundToAudienceAsync(claims, body.Aud, "api.introspect.audience_mismatch"))
            return Ok(IntrospectionResult.Inactive);

        // Out of scope answers "inactive", not "forbidden": the RFC 7662 contract here is that a
        // caller cannot distinguish malformed from revoked from expired, and telling it "that
        // token exists but belongs to someone else" would be the disclosure this closes.
        if (!await IsInCallerScopeAsync(claims, "api.introspect.out_of_scope"))
            return Ok(IntrospectionResult.Inactive);

        // Roles are re-verified against Keto/DB, so a role revoked after the token was minted
        // does not survive in the answer.
        // Legitimate: this asks what the *presented* token claims, not what the caller is granted.
        // The name says so, so it cannot be mistaken for an authorisation decision (S-1).
        var level = GrantedLevel.ClaimedLevel(claims);
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
            IsServiceAccount: claims.IsServiceAccount,
            Aud:       body.Aud));
    }

    /// <summary>
    /// Permission decision for a gateway: "may the bearer of this token do X?", expressed as a
    /// Keto relation check. Keeps the policy in one place instead of every gateway
    /// reimplementing its own interpretation of the roles claim.
    ///
    /// <para><b>Breaking:</b> <c>aud</c> is required here too, and the <c>object</c> is now
    /// scoped to the tenant the answer is about (P-05).</para>
    /// </summary>
    [HttpPost("authorize")]
    [ProducesResponseType(typeof(AuthorizationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Authorize([FromBody] AuthorizationRequest body)
    {
        if (!IsServiceAccountCaller())
            return StatusCode(403, new { error = "service_account_required" });

        if (string.IsNullOrWhiteSpace(body.Aud))
            return BadRequest(new { error = "audience_required", ver = ContractVersion });

        var claims = await ResolveAsync(body.Token);
        if (claims is null) return Ok(AuthorizationResult.Denied);

        if (!await IsBoundToAudienceAsync(claims, body.Aud, "api.authorize.audience_mismatch"))
            return Ok(AuthorizationResult.Denied);

        if (!await IsInCallerScopeAsync(claims, "api.authorize.out_of_scope"))
            return Ok(AuthorizationResult.Denied);

        // The System namespace holds exactly one object and one interesting relation:
        // rediensiam#super_admin. Asking about it is enumerating the deployment's
        // administrators, never authorising the caller's own request — so it is refused to
        // every caller, not only tenant-scoped ones. This was conditioned on CallerOrgScope,
        // which left a __system__ service account free to ask. A resource server that needs
        // to know whether a subject is a super_admin reads the roles field of
        // /api/introspect, which re-verifies against Keto before answering.
        if (body.Namespace.Equals(Roles.KetoSystemNamespace, StringComparison.OrdinalIgnoreCase))
        {
            await audit.RecordAsync(CallerOrgScope, null, Caller.ParsedUserId,
                "api.authorize.out_of_scope", "keto_namespace", body.Namespace);
            return Ok(AuthorizationResult.Denied);
        }

        if (!await IsObjectInScopeAsync(claims, body.Namespace, body.Object))
            return Ok(AuthorizationResult.Denied);

        var subject = $"user:{claims.ParsedUserId}";
        var allowed = await keto.CheckAsync(body.Namespace, body.Object, body.Relation, subject);

        return Ok(new AuthorizationResult(allowed, claims.ParsedUserId.ToString()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// P-06. The resource server names the tenant it serves; a token from anywhere else is not
    /// its business and reads inactive.
    ///
    /// A token is bound to <paramref name="aud"/> when the value is the token's
    /// <c>project_id</c>, its <c>org_id</c> (the only tenant a token without a project has), or
    /// one of the OAuth2 audiences Hydra minted it for. Both id forms are accepted because a
    /// service-account PAT carries no project — a gateway fronting a whole organisation declares
    /// the org id, one fronting a single application declares the project id, and neither can
    /// name a tenant it does not belong to.
    ///
    /// The comparison is fail-closed on emptiness: a token whose <c>project_id</c> and
    /// <c>org_id</c> are both blank matches no audience at all and can only be introspected by
    /// naming an explicit <c>aud</c> claim on it.
    /// </summary>
    private async Task<bool> IsBoundToAudienceAsync(TokenClaims subject, string aud, string auditAction)
    {
        if (Matches(subject.ProjectId) || Matches(subject.OrgId)
            || subject.Audiences.Contains(aud, StringComparer.Ordinal))
            return true;

        await audit.RecordAsync(CallerOrgScope, null, Caller.ParsedUserId, auditAction, "audience", aud);
        return false;

        bool Matches(string? value) =>
            !string.IsNullOrEmpty(value) && value.Equals(aud, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P-05. <c>object</c> used to reach Keto unchecked, so an org-scoped gateway could ask
    /// "does my user hold relation R on <i>someone else's</i> object?" and read the answer one
    /// bit at a time — enumeration of another tenant's relation graph through a decision
    /// endpoint. The subject is always the presented token's user, so the answer was never a
    /// forged decision; it was still a disclosure.
    ///
    /// The scope is the same one introspection uses — the caller's organisation — falling back
    /// to the subject token's organisation for a deployment-level caller, which the audience
    /// binding above has already pinned to one tenant. When neither side names a tenant there is
    /// no ownership to compare and the request is refused: "nobody owns this object" is not
    /// "you own it".
    /// </summary>
    private async Task<bool> IsObjectInScopeAsync(TokenClaims subject, string ns, string obj)
    {
        var scope = CallerOrgScope
                 ?? (Guid.TryParse(subject.OrgId, out var subjectOrg) ? subjectOrg : (Guid?)null);

        // No scope on either side — a deployment-level caller asking about a token that names
        // no organisation. Reaching it needs a token carrying neither org_id nor project_id,
        // which IsBoundToAudienceAsync admits only through subject.Audiences: a Hydra client
        // with grant_access_token_audience set. RediensIAM does not mint one, but Hydra will
        // honour one written into its client store directly, so this is a narrow live path
        // rather than the dead code the earlier fix took it for. Answering the Keto question
        // with no owner checked is the fail-open; refusing is the honest answer.
        if (scope is null) return await RefuseAsync();

        return await IsOwnedByAsync(ns, obj, scope.Value) || await RefuseAsync();

        async Task<bool> RefuseAsync()
        {
            await audit.RecordAsync(scope, null, Caller.ParsedUserId,
                "api.authorize.object_out_of_scope", ns, obj);
            return false;
        }
    }

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

    /// <summary>
    /// Whether <paramref name="obj"/> is an object of <paramref name="orgId"/>. Unknown
    /// namespaces are not owned by anyone here, so they answer false.
    /// </summary>
    private async Task<bool> IsOwnedByAsync(string ns, string obj, Guid orgId)
    {
        if (!Guid.TryParse(obj, out var objectId)) return false;

        if (Same(ns, Roles.KetoOrgsNamespace))      return objectId == orgId;
        if (Same(ns, Roles.KetoProjectsNamespace))  return await db.Projects.AnyAsync(p => p.Id == objectId && p.OrgId == orgId);
        if (Same(ns, Roles.KetoUserListsNamespace)) return await db.UserLists.AnyAsync(l => l.Id == objectId && l.OrgId == orgId);
        return false;

        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsServiceAccountCaller() =>
        Caller.IsServiceAccount
        || Caller.ClientId.StartsWith(Roles.ServiceAccountClientPrefix, StringComparison.Ordinal);

    private static bool IsManagementRole(string role) =>
        role is Roles.SuperAdmin or Roles.OrgAdmin or Roles.ProjectAdmin;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

// ── Wire contract ─────────────────────────────────────────────────────────────

/// <summary>
/// RFC 7662 §2.1 also defines an optional <c>token_type_hint</c>. It is deliberately absent:
/// the RFC makes it a lookup hint the server MAY ignore and MUST NOT reject a token over, and
/// <c>ResolveAsync</c> already picks the token shape in constant time from its prefix — so
/// honouring it could only be a no-op. Declaring a field nothing reads
/// is worse than not declaring it; a client that sends it is unaffected, the value is discarded
/// during model binding exactly as it was before.
/// </summary>
/// <summary>
/// <c>aud</c> is <b>required</b> and is the tenant this resource server serves — a project id,
/// or an organisation id for a gateway that fronts a whole organisation. RFC 7662 §2.1 does not
/// define it; RFC 8693 and the OAuth2 audience-restriction model do, and without it this
/// endpoint answers for every tenant in the deployment at once.
/// </summary>
public record IntrospectionRequest(string Token, string? Aud = null);

/// <summary>
/// RFC 7662 response. Field names are serialised snake_case by the global JSON options.
///
/// <para><c>Aud</c> echoes the audience the answer was scoped to and is null on an inactive
/// answer. <c>Ver</c> is always present — see
/// <see cref="IntrospectionController.ContractVersion"/>.</para>
/// </summary>
public record IntrospectionResult(
    bool Active,
    string? Sub = null,
    string? UserId = null,
    string? OrgId = null,
    string? ProjectId = null,
    List<string>? Roles = null,
    string? ClientId = null,
    bool IsServiceAccount = false,
    string? Aud = null,
    int Ver = IntrospectionController.ContractVersion)
{
    /// <summary>
    /// Never null on the wire. The inactive answer — by far the most common one this endpoint
    /// gives — used to serialise <c>"roles": null</c>, and every SDK models the field as a list:
    /// the Rust client's <c>Vec&lt;String&gt;</c> failed to deserialise it and returned a transport
    /// error for every expired, revoked or out-of-audience token, and the .NET client's non-null
    /// initialiser was overwritten by the null so <c>HasRole</c> threw. An absent role set is an
    /// empty one; saying so costs two characters.
    /// </summary>
    public List<string> Roles { get; init; } = Roles ?? [];

    public static readonly IntrospectionResult Inactive = new(false);
}

public record AuthorizationRequest(
    string Token,
    string Namespace,
    string Object,
    string Relation,
    string? Aud = null);

public record AuthorizationResult(bool Allowed, string? UserId, int Ver = IntrospectionController.ContractVersion)
{
    public static readonly AuthorizationResult Denied = new(false, null);
}
