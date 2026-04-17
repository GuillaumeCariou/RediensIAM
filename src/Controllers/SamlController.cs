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
public class SamlController(
    RediensIamDbContext db,
    HydraService hydra,
    AuditLogService audit,
    SamlService saml,
    AppConfig appConfig,
    ILogger<SamlController> logger) : ControllerBase
{
    private Uri AcsUrl      => new($"{appConfig.PublicUrl}/auth/saml/acs");
    private string SpEntity => $"{appConfig.PublicUrl}/auth/saml/metadata";

    // ── SP-initiated SSO: build AuthnRequest and redirect to IdP ─────────────

    [HttpGet("start")]
    public async Task<IActionResult> Start(
        [FromQuery] string login_challenge,
        [FromQuery] Guid idp_id)
    {
        try { await hydra.GetLoginRequestAsync(login_challenge); }
        catch { return BadRequest(new { error = "invalid_login_challenge" }); }

        var idp = await db.SamlIdpConfigs
            .FirstOrDefaultAsync(x => x.Id == idp_id && x.Active);
        if (idp == null) return NotFound(new { error = "saml_idp_not_found" });

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

        // Store request ID in session to validate InResponseTo on ACS
        var result = binding.Bind(authnRequest);
        HttpContext.Session.SetString($"saml_req:{idp_id}", authnRequest.Id.Value);

        return result.ToActionResult();
    }

    // ── ACS: receive and validate SAMLResponse ────────────────────────────────

    [HttpPost("acs")]
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

        user!.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (string.IsNullOrEmpty(loginChallenge)) return BadRequest(new { error = "invalid_login_challenge" });

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

        return Redirect(redirectUrl);
    }

    private record SamlParsed(SamlIdpConfig Idp, string? LoginChallenge, System.Security.Claims.ClaimsIdentity Identity);

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

            var config = await saml.BuildConfigAsync(idp, SpEntity, AcsUrl);
            var expectedReqId = HttpContext.Session.GetString($"saml_req:{idpId}");
            HttpContext.Session.Remove($"saml_req:{idpId}");
            if (string.IsNullOrEmpty(expectedReqId)) return (null, "saml_no_pending_request");

            var saml2AuthnResponse = new Saml2AuthnResponse(config);
            httpRequest.Binding.ReadSamlResponse(httpRequest, saml2AuthnResponse);
            if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
                throw new AuthenticationException($"SAML status: {saml2AuthnResponse.Status}");
            if (saml2AuthnResponse.InResponseTo?.Value != expectedReqId)
                throw new AuthenticationException("InResponseTo mismatch");
            httpRequest.Binding.Unbind(httpRequest, saml2AuthnResponse);

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
        if (project.AllowedEmailDomains.Length > 0)
        {
            var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
            if (!project.AllowedEmailDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(loginChallenge))
                    await hydra.RejectLoginAsync(loginChallenge, "access_denied", "email_domain_not_allowed");
                return (null, "email_domain_not_allowed");
            }
        }

        var emailLower = email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserListId == project.AssignedUserListId && u.Email == emailLower);

        if (user == null && !idp.JitProvisioning) return (null, "user_not_provisioned");
        if (user == null) user = await ProvisionUserAsync(project, email, displayName, idp.DefaultRoleId);
        if (!user.Active) return (null, "account_disabled");

        if (project.RequireRoleToLogin)
        {
            var hasRole = await db.UserProjectRoles.AnyAsync(r => r.UserId == user.Id && r.ProjectId == project.Id);
            if (!hasRole)
            {
                if (!string.IsNullOrEmpty(loginChallenge))
                    await hydra.RejectLoginAsync(loginChallenge, "access_denied", "no_role_assigned");
                return (null, "no_role_assigned");
            }
        }

        return (user, null);
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
            discriminator = Random.Shared.Next(1000, 9999).ToString();
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
