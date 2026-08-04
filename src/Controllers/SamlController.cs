using System.Security.Authentication;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[Route("auth/saml")]
// Eight dependencies, one over S107's threshold. The alternative is a DI aggregate for a single
// added service, which relocates the count without lowering what this controller depends on.
#pragma warning disable S107
public class SamlController(
    RediensIamDbContext db,
    HydraService hydra,
    AuditLogService audit,
    SamlService saml,
    KetoService keto,
    AppConfig appConfig,
    ClientOriginsService clientOrigins,
    OtpCacheService pending,
    TenantScopeInterceptor tenantScope,
    ILogger<SamlController> logger) : ControllerBase
#pragma warning restore S107
{
    // The ACS is a cross-site POST issued by the IdP, so the ASP.NET session cookie
    // (SameSite=Strict) is never sent with it. Holding the pending AuthnRequest ID in the
    // session meant InResponseTo could never be validated and every SAML login failed with
    // saml_no_pending_request. It lives in Redis instead, keyed by the request ID itself —
    // which is exactly what the response echoes back in InResponseTo.
    private const string SamlRequestPrefix = "saml_req";

    private Uri AcsUrl      => new($"{appConfig.PublicUrl}/auth/saml/acs");
    private string SpEntity => $"{appConfig.PublicUrl}/auth/saml/metadata";

    // ── RLS scope ─────────────────────────────────────────────────────────────
    //
    // Same mechanism and the same rule as AuthController (see the note above its PinScopeAsync):
    // a SAML login carries no token, so without a pin every query here runs as 'system' and
    // row-level security enforces nothing on it. The argument is always a value the server read
    // back — the organisation on the challenge's registered client metadata, or the one on the
    // IdP's own project row — never request input.
    //
    // The two entry points differ in what they can pin from, and the difference is real:
    //   Start has the Hydra login challenge in hand before it reads anything, so it pins from
    //   client.metadata.org_id at zero database reads, exactly as the password path does.
    //   The ACS does not: its challenge arrives in RelayState, which is browser-controlled and
    //   outside the assertion signature, so it is not a scope source. It pins from the project
    //   row the IdP hangs off instead — one read, the same documented limit
    //   EnsureScopedToProjectAsync carries. That read is what decides the scope, so it cannot
    //   run under it.

    private Task PinScopeAsync(Guid orgId) =>
        tenantScope.PinToOrganisationAsync(db, orgId, HttpContext.RequestAborted);

    /// <summary>
    /// Confirms the request is running under <paramref name="project"/>'s organisation, pinning
    /// it from the project row if the challenge could not. False means the challenge client's
    /// registered organisation and the project's disagree — impossible for a client this
    /// application minted, and refused rather than reconciled.
    /// </summary>
    private async Task<bool> EnsureScopedToProjectAsync(Project project)
    {
        var scope = tenantScope.CurrentScope();
        if (scope != TenantScopeInterceptor.SystemScope) return scope == project.OrgId.ToString();
        await PinScopeAsync(project.OrgId);
        return true;
    }

    // ── SP-initiated SSO: build AuthnRequest and redirect to IdP ─────────────

    [HttpGet("start")]
    public async Task<IActionResult> Start(
        [FromQuery] string login_challenge,
        [FromQuery] Guid idp_id)
    {
        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(login_challenge); }
        catch { return BadRequest(new { error = "invalid_login_challenge" }); }

        // Bind the IdP to the project the calling client is registered for. Without this any
        // tenant could start a flow against another tenant's IdP on its own login_challenge and
        // receive an authorization code for the victim's user (see SEC-02).
        var projectId = LoginChallengeProject.ResolveOrNull(req);
        if (projectId == null || !Guid.TryParse(projectId, out var challengeProjectId))
            return BadRequest(new { error = "missing_project_id" });

        if (LoginChallengeProject.ResolveOrgOrNull(req) is { } challengeOrgId)
            await PinScopeAsync(challengeOrgId);

        var idp = await db.SamlIdpConfigs
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == idp_id && x.Active && x.ProjectId == challengeProjectId);
        if (idp == null) return NotFound(new { error = "saml_idp_not_found" });
        if (!await EnsureScopedToProjectAsync(idp.Project))
            return BadRequest(new { error = "project_org_mismatch" });

        var config = await saml.BuildConfigAsync(idp, SpEntity, AcsUrl);

        var binding = new Saml2RedirectBinding();
        binding.SetRelayStateQuery(new Dictionary<string, string>
        {
            ["login_challenge"] = login_challenge,
            ["idp_id"]          = idp_id.ToString()
        });

        var authnRequest = new Saml2AuthnRequest(config)
        {
            AssertionConsumerServiceUrl = AcsUrl,
        };

        var result = binding.Bind(authnRequest);
        // Store the challenge with the IdP: ACS must confirm the response answers the request
        // WE issued, for the project we issued it for. RelayState is browser-controlled and is
        // not covered by the assertion signature, so it cannot be trusted for either.
        await pending.StorePendingAsync(SamlRequestPrefix, authnRequest.Id.Value,
            $"{idp_id}|{login_challenge}");

        return result.ToActionResult();
    }

    // ── ACS: receive and validate SAMLResponse ────────────────────────────────

    [HttpPost("acs")]
    [Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
    // SAML ACS receives an IdP-signed assertion via the user agent; the SAML signature
    // verification IS the integrity / replay defence. ASP.NET anti-forgery tokens (designed
    // for browser-originated form posts) do not apply here.
    public async Task<IActionResult> AssertionConsumerService([FromForm(Name = "RelayState")] string relayState = "")
    {
        var (parsed, parseError) = await ParseSamlResponseAsync(relayState);
        if (parseError != null) return BadRequest(new { error = parseError });

        var (idp, loginChallenge, identity) = parsed!;
        var email = SamlService.ExtractEmail(identity, idp.EmailAttributeName);
        if (string.IsNullOrEmpty(email)) return BadRequest(new { error = "saml_email_missing" });

        var project = idp.Project;
        if (project.AssignedUserListId == null) return StatusCode(503, new { error = "project_not_configured" });

        var (user, accessError) = await ResolveSamlUserAsync(project, idp, email,
            SamlService.ExtractDisplayName(identity, idp.DisplayNameAttributeName), loginChallenge);
        if (accessError != null) return Unauthorized(new { error = accessError });

        if (string.IsNullOrEmpty(loginChallenge)) return BadRequest(new { error = "invalid_login_challenge" });

        // The tenant's own login controls, which this path used to skip entirely — see
        // FederatedLoginGuard. A project running an IdP is precisely the kind that turns these on.
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!FederatedLoginGuard.IsIpAllowed(project, clientIp))
        {
            await audit.RecordAsync(project.OrgId, project.Id, user!.Id, "user.login.failure",
                metadata: new Dictionary<string, object> { ["reason"] = "ip_not_allowed" });
            var rejected = await hydra.RejectLoginAsync(loginChallenge, "access_denied", "ip_not_allowed");
            return Redirect(rejected);
        }

        user!.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var (factorRequired, needsEnrolment) = await FederatedLoginGuard.RequiresFactorAsync(db, user, project);
        if (factorRequired)
        {
            FederatedLoginGuard.SetMfaSession(
                HttpContext.Session, user.Id, loginChallenge, project.Id, project.OrgId, needsEnrolment);
            return Redirect(FederatedLoginGuard.MfaRedirectPath(loginChallenge, needsEnrolment));
        }

        var subject = $"{project.OrgId}:{user.Id}";
        var context = new Dictionary<string, object>
        {
            ["org_id"]     = project.OrgId.ToString(),
            ["project_id"] = project.Id.ToString(),
            ["user_id"]    = user.Id.ToString()
        };

        var redirectUrl = await hydra.AcceptLoginAsync(loginChallenge, subject, context);
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.login.saml",
            metadata: new Dictionary<string, object> { ["idp_id"] = idp.Id.ToString() });

        // Same authority as SafeRedirect and the CSP header — one list, not a third copy of it.
        if (!RedirectValidator.TryReconstruct(redirectUrl, clientOrigins.Snapshot, out var safeUrl))
        {
            logger.LogWarning("SAML ACS refused redirect URL {Url}", redirectUrl);
            return BadRequest(new { error = "invalid_redirect" });
        }
        Response.Headers.Location = safeUrl;
        Response.StatusCode = StatusCodes.Status302Found;
        return new EmptyResult();
    }

    private sealed record SamlParsed(SamlIdpConfig Idp, string? LoginChallenge, System.Security.Claims.ClaimsIdentity Identity);

    private async Task<(SamlParsed? Parsed, string? Error)> ParseSamlResponseAsync(string relayState)
    {
        var httpRequest = await Request.ToGenericHttpRequestAsync(validate: true);
        try
        {
            httpRequest.Binding.RelayState = relayState;
            var relayQuery = httpRequest.Binding.GetRelayStateQuery();
            if (!relayQuery.TryGetValue("login_challenge", out var loginChallenge) ||
                !relayQuery.TryGetValue("idp_id", out var idpIdStr) ||
                !Guid.TryParse(idpIdStr, out var idpId))
                return (null, "invalid_relay_state");

            var idp = await db.SamlIdpConfigs
                .Include(x => x.Project)
                .FirstOrDefaultAsync(x => x.Id == idpId && x.Active);
            if (idp == null) return (null, "saml_idp_not_found");

            // Everything from here on — the user lookup, JIT provisioning, the role check, the
            // audit row — runs under the IdP's own tenant.
            await PinScopeAsync(idp.Project.OrgId);

            var config = await saml.BuildConfigAsync(idp, SpEntity, AcsUrl);

            var saml2AuthnResponse = new Saml2AuthnResponse(config);
            httpRequest.Binding.ReadSamlResponse(httpRequest, saml2AuthnResponse);
            if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
                throw new AuthenticationException($"SAML status: {saml2AuthnResponse.Status}");

            // Endpoint binding. AllowedAudienceUris already refuses an assertion addressed to a
            // different service provider and remains the primary control; this is the secondary
            // one the spec asks for, refusing a response that was legitimately issued for some
            // *other endpoint of this same SP* and then relayed here.
            //
            // Ordered before the pending record is consumed on purpose: GetAndDeletePendingAsync
            // is single-use, so validating afterwards would let a misdirected response burn the
            // InResponseTo of a legitimate login still in flight. Nothing is lost by checking
            // pre-signature — Unbind below re-parses the identical document and still rejects any
            // tampering, so an attacker who rewrites Destination to match only reaches a failed
            // signature check.
            var destination = saml2AuthnResponse.Destination;
            if (destination == null)
                // Optional per §3.2.2, so absence is not a failure — but it does mean this IdP's
                // responses carry nothing to bind them to an endpoint, which is worth saying once
                // per login rather than discovering during an incident.
                logger.LogWarning(
                    "SAML response from IdP {IdpId} has no Destination attribute; the endpoint-binding check does not apply to it",
                    idp.Id);
            else if (!SamlService.DestinationMatches(destination, AcsUrl))
                throw new AuthenticationException(
                    $"Destination '{destination}' does not name this ACS endpoint");

            // Signature validation, ordered ahead of the consume for the same reason the
            // Destination check above is (I-10). GetAndDeletePendingAsync is single use, so
            // validating afterwards let any unauthenticated caller who could guess or replay a
            // request id destroy a legitimate login still in flight by POSTing a garbage
            // document at it. Unbind re-parses the identical bytes ReadSamlResponse already
            // parsed — Destination and InResponseTo come out the same — so nothing downstream
            // changes except that a forged response never reaches the record.
            //
            // The residue, stated rather than papered over: an attacker who controls *any*
            // registered active IdP can still sign a response of their own, name that IdP in
            // RelayState and burn a guessed request id, because the checks that bind the
            // response to the IdP and challenge it was issued for need the record in hand. Only
            // consuming atomically prevents a valid captured response being redeemed twice, and
            // that is worth more than closing the rest of this.
            httpRequest.Binding.Unbind(httpRequest, saml2AuthnResponse);

            // Consume the pending request: single use, so a captured response cannot be replayed.
            var inResponseTo = saml2AuthnResponse.InResponseTo?.Value;
            if (string.IsNullOrEmpty(inResponseTo)) return (null, "saml_no_pending_request");
            var pendingRecord = await pending.GetAndDeletePendingAsync(SamlRequestPrefix, inResponseTo);
            if (string.IsNullOrEmpty(pendingRecord)) return (null, "saml_no_pending_request");

            var parts = pendingRecord.Split('|', 2);
            var pendingIdpId = parts[0];
            var pendingChallenge = parts.Length == 2 ? parts[1] : null;

            // The response must come from the IdP the request was actually sent to.
            if (!string.Equals(pendingIdpId, idpId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new AuthenticationException("InResponseTo belongs to a different IdP");

            // ...and must be redeemed against the challenge it was issued for. Without this a
            // caller could complete a genuine flow at their own IdP and then swap the
            // login_challenge in RelayState for one belonging to another tenant's client.
            if (!string.Equals(pendingChallenge, loginChallenge, StringComparison.Ordinal))
                throw new AuthenticationException("InResponseTo belongs to a different login challenge");

            // Same binding Start enforces: the IdP must belong to the challenge's project.
            var challengeProjectId = LoginChallengeProject.ResolveOrNull(
                await hydra.GetLoginRequestAsync(loginChallenge));
            if (challengeProjectId == null || idp.ProjectId.ToString() != challengeProjectId)
                throw new AuthenticationException("IdP does not belong to the challenge's project");

            return (new SamlParsed(idp, loginChallenge, saml2AuthnResponse.ClaimsIdentity), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML ACS validation failed");
            return (null, "saml_response_invalid");
        }
    }

    private async Task<(User? User, string? Error)> ResolveSamlUserAsync(
        Project project, SamlIdpConfig idp, string email, string? displayName, string? loginChallenge)
    {
        var domainErr = await CheckEmailDomainAsync(project, email, loginChallenge);
        if (domainErr != null) return (null, domainErr);

        var emailLower = email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserListId == project.AssignedUserListId && u.Email == emailLower);

        if (user == null && !idp.JitProvisioning) return (null, "user_not_provisioned");
        if (user == null) user = await ProvisionUserAsync(project, email, displayName, idp.DefaultRoleId);
        if (!user.Active) return (null, "account_disabled");

        var roleErr = await CheckRoleRequirementAsync(project, user, loginChallenge);
        if (roleErr != null) return (null, roleErr);

        return (user, null);
    }

    private async Task<string?> CheckEmailDomainAsync(Project project, string email, string? loginChallenge)
    {
        if (project.AllowedEmailDomains.Length == 0) return null;
        var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
        if (project.AllowedEmailDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase))) return null;
        if (!string.IsNullOrEmpty(loginChallenge))
            await hydra.RejectLoginAsync(loginChallenge, "access_denied", "email_domain_not_allowed");
        return "email_domain_not_allowed";
    }

    private async Task<string?> CheckRoleRequirementAsync(Project project, User user, string? loginChallenge)
    {
        if (!project.RequireRoleToLogin) return null;
        var hasRole = await db.UserProjectRoles.AnyAsync(r => r.UserId == user.Id && r.ProjectId == project.Id);
        if (hasRole) return null;
        if (!string.IsNullOrEmpty(loginChallenge))
            await hydra.RejectLoginAsync(loginChallenge, "access_denied", "no_role_assigned");
        return "no_role_assigned";
    }

    // ── SP Metadata ───────────────────────────────────────────────────────────

    [HttpGet("metadata")]
    public IActionResult Metadata()
    {
        var entityId = System.Security.SecurityElement.Escape(SpEntity);
        var acsUrl   = System.Security.SecurityElement.Escape(AcsUrl.ToString());
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <EntityDescriptor entityID="{entityId}" xmlns="urn:oasis:names:tc:SAML:2.0:metadata">
              <SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="true"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <AssertionConsumerService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                    Location="{acsUrl}" index="0" isDefault="true"/>
              </SPSSODescriptor>
            </EntityDescriptor>
            """;
        return Content(xml, "application/xml");
    }

    // ── JIT user provisioning ─────────────────────────────────────────────────

    private async Task<User> ProvisionUserAsync(
        Project project, string email, string? displayName, Guid? defaultRoleId)
    {
        var username = email.Split('@')[0];
        string discriminator;
        var discIter = 0;
        do
        {
            if (++discIter > 100) throw new InvalidOperationException("discriminator_space_exhausted");
            discriminator = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000, 10000).ToString();
        }
        while (await db.Users.AnyAsync(u =>
            u.UserListId == project.AssignedUserListId &&
            u.Username == username && u.Discriminator == discriminator));

        var user = new User
        {
            UserListId    = project.AssignedUserListId!.Value,
            Email         = email.ToLowerInvariant(),
            Username      = username,
            Discriminator = discriminator,
            DisplayName   = displayName,
            EmailVerified = true,
            PasswordHash  = null,   // SAML-provisioned user — no password
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (defaultRoleId.HasValue)
        {
            // Written through Keto rather than straight into the table. A row with no matching
            // tuple is exactly what GrantReconciler classifies as an orphan, so the first repair
            // run deleted the role this provisioning had just granted — and on a project with
            // require_role_to_login that locked the user out of the tenant they had just joined.
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == defaultRoleId.Value);
            if (role != null)
            {
                await keto.WriteRelationTupleAsync(
                    Roles.KetoProjectsNamespace, project.Id.ToString(), role.Name, user.Id.ToString());
            }

            db.UserProjectRoles.Add(new UserProjectRole
            {
                UserId    = user.Id,
                ProjectId = project.Id,
                RoleId    = defaultRoleId.Value,
                GrantedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await audit.RecordAsync(project.OrgId, project.Id, user.Id,
            "user.saml_provisioned", "user", user.Id.ToString());

        return user;
    }
}
