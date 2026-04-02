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
public class SamlController(
    RediensIamDbContext db,
    HydraService hydra,
    AuditLogService audit,
    PasswordService passwords,
    SamlService saml,
    AppConfig appConfig,
    ILogger<SamlController> logger) : ControllerBase
{
    private Uri AcsUrl      => new($"{appConfig.PublicUrl}/auth/saml/acs");
    private string SpEntity => $"{appConfig.PublicUrl}/auth/saml/metadata";

    // ── SP-initiated SSO: build AuthnRequest and redirect to IdP ─────────────

    [HttpGet("/auth/saml/start")]
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

    [HttpPost("/auth/saml/acs")]
    public async Task<IActionResult> AssertionConsumerService()
    {
        var httpRequest = Request.ToGenericHttpRequest(validate: true);
        Saml2AuthnResponse? saml2AuthnResponse = null;

        Guid idpId;
        string? loginChallenge;
        SamlIdpConfig? idp;

        try
        {
            // Read relay state first to know which IdP/challenge we're handling
            var relayQuery = httpRequest.Binding.GetRelayStateQuery();
            if (!relayQuery.TryGetValue("login_challenge", out loginChallenge) ||
                !relayQuery.TryGetValue("idp_id", out var idpIdStr) ||
                !Guid.TryParse(idpIdStr, out idpId))
                return BadRequest(new { error = "invalid_relay_state" });

            idp = await db.SamlIdpConfigs
                .Include(x => x.Project)
                .FirstOrDefaultAsync(x => x.Id == idpId && x.Active);
            if (idp == null) return BadRequest(new { error = "saml_idp_not_found" });

            var config = await saml.BuildConfigAsync(idp, SpEntity, AcsUrl);

            // Validate InResponseTo: ensure the response was triggered by a request we initiated
            var expectedReqId = HttpContext.Session.GetString($"saml_req:{idpId}");
            HttpContext.Session.Remove($"saml_req:{idpId}");

            saml2AuthnResponse = new Saml2AuthnResponse(config);
            httpRequest.Binding.ReadSamlResponse(httpRequest, saml2AuthnResponse);

            if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
                throw new AuthenticationException($"SAML status: {saml2AuthnResponse.Status}");

            // Manual InResponseTo check against session-stored request ID
            if (expectedReqId != null && saml2AuthnResponse.InResponseTo?.Value != expectedReqId)
                throw new AuthenticationException("InResponseTo mismatch");

            httpRequest.Binding.Unbind(httpRequest, saml2AuthnResponse);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML ACS validation failed");
            return BadRequest(new { error = "saml_response_invalid", detail = ex.Message });
        }

        var identity = saml2AuthnResponse.ClaimsIdentity;
        var email = SamlService.ExtractEmail(identity, idp.EmailAttributeName);
        if (string.IsNullOrEmpty(email))
            return BadRequest(new { error = "saml_email_missing" });

        var displayName = SamlService.ExtractDisplayName(identity, idp.DisplayNameAttributeName);
        var project = idp.Project;

        if (project.AssignedUserListId == null)
            return StatusCode(503, new { error = "project_not_configured" });

        // Find or JIT-provision user
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserListId == project.AssignedUserListId && u.Email == email.ToLowerInvariant());

        if (user == null)
        {
            if (!idp.JitProvisioning)
                return Unauthorized(new { error = "user_not_provisioned" });
            user = await ProvisionUserAsync(project, email, displayName, idp.DefaultRoleId);
        }

        if (!user.Active)
            return Unauthorized(new { error = "account_disabled" });

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var subject = $"{project.OrgId}:{user.Id}";
        var context = new Dictionary<string, object>
        {
            ["org_id"]     = project.OrgId.ToString(),
            ["project_id"] = project.Id.ToString(),
            ["user_id"]    = user.Id.ToString()
        };

        var redirectUrl = await hydra.AcceptLoginAsync(loginChallenge!, subject, context);
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.login.saml",
            metadata: new Dictionary<string, object> { ["idp_id"] = idp.Id.ToString() });

        return Redirect(redirectUrl);
    }

    // ── SP Metadata ───────────────────────────────────────────────────────────

    [HttpGet("/auth/saml/metadata")]
    public IActionResult Metadata()
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <EntityDescriptor entityID="{SpEntity}" xmlns="urn:oasis:names:tc:SAML:2.0:metadata">
              <SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="true"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <AssertionConsumerService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                    Location="{AcsUrl}" index="0" isDefault="true"/>
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
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
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
            PasswordHash  = passwords.Hash(
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
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
