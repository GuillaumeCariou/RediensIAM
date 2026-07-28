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
    AppConfig appConfig) : ControllerBase
{
    private TokenClaims Caller => HttpContext.GetClaims()!;

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
