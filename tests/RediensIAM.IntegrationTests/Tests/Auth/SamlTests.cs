using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens.Saml2;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests for SamlController (/auth/saml/*) and SamlService static helpers.
/// SamlController requires no bearer token — it is part of the public login flow.
/// </summary>
[Collection("RediensIAM")]
public partial class SamlControllerTests(TestFixture fixture)
{
    [GeneratedRegex(@"name=""SAMLResponse""[^>]*value=""([^""]+)""")]
    private static partial Regex SamlResponseByNameRegex();
    [GeneratedRegex(@"value=""([^""]+)""[^>]*name=""SAMLResponse""")]
    private static partial Regex SamlResponseByValueRegex();
    [GeneratedRegex(@"name=""RelayState""[^>]*value=""([^""]+)""")]
    private static partial Regex RelayStateByNameRegex();
    [GeneratedRegex(@"value=""([^""]+)""[^>]*name=""RelayState""")]
    private static partial Regex RelayStateByValueRegex();

    private async Task<SamlIdpConfig> SeedIdpAsync(Guid projectId,
        string ssoUrl = "https://idp.example.com/sso",
        bool active = true)
    {
        var idp = new SamlIdpConfig
        {
            Id        = Guid.NewGuid(),
            ProjectId = projectId,
            EntityId  = "https://idp.example.com",
            SsoUrl    = ssoUrl,
            Active    = active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(idp);
        await fixture.Db.SaveChangesAsync();
        return idp;
    }

    // ── GET /auth/saml/metadata ───────────────────────────────────────────────

    [Fact]
    public async Task Metadata_Returns200WithXml()
    {
        var res = await fixture.Client.GetAsync("/auth/saml/metadata");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await res.Content.ReadAsStringAsync();
        content.Should().Contain("EntityDescriptor");
        content.Should().Contain("SPSSODescriptor");
        content.Should().Contain("AssertionConsumerService");
    }

    // ── GET /auth/saml/start ──────────────────────────────────────────────────

    [Fact]
    public async Task Start_InvalidLoginChallenge_Returns400()
    {
        // Hydra stub returns 404 for unknown challenge → GetLoginRequestAsync throws → 400
        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge=nonexistent-challenge&idp_id={Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_login_challenge");
    }

    [Fact]
    public async Task Start_ValidChallenge_NonExistentIdp_Returns404()
    {
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "some-client");

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_idp_not_found");
    }

    [Fact]
    public async Task Start_ValidChallenge_InactiveIdp_Returns404()
    {
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "some-client");

        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var idp       = await SeedIdpAsync(project.Id, active: false);  // inactive

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_ValidChallenge_ValidIdp_ReturnsRedirect()
    {
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "some-client");

        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var idp      = await SeedIdpAsync(project.Id);  // SsoUrl set, Active=true

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        // ITfoxtec builds a SAML redirect binding — expect 302 to IdP's SSO URL
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("idp.example.com");
    }

    // ── POST /auth/saml/acs ───────────────────────────────────────────────────

    [Fact]
    public async Task Acs_MalformedRequest_Returns400()
    {
        // POST with no SAMLResponse form data — ITfoxtec binding throws → caught → 400
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["garbage"] = "data"
        });
        var res = await fixture.Client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_relay_state");
    }

    [Fact]
    public async Task Acs_InvalidRelayState_Returns400()
    {
        // SAMLResponse present but relay state missing login_challenge/idp_id
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<Response/>")),
            ["RelayState"]   = "no_challenge_here"
        });
        var res = await fixture.Client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers for full ACS flow tests ──────────────────────────────────────

    // Seeds a project + list (optionally) + SamlIdpConfig backed by a test RSA cert.
    private async Task<(SamlIdpConfig idp, X509Certificate2 cert)> SeedAcsSamlIdpAsync(
        bool assignList = true, bool jit = true)
    {
        // Export as PFX and re-import so the cert holds its own self-contained key,
        // independent of the RSA object that may be disposed by the time Bind() is called.
        X509Certificate2 cert;
        {
            using var rsa = RSA.Create(2048);
            var certReq = new CertificateRequest(
                "CN=TestIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var rawCert = certReq.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var pfxBytes = rawCert.Export(X509ContentType.Pfx);
            cert = X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.EphemeralKeySet);
        }

        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        if (assignList)
        {
            var list = await fixture.Seed.CreateUserListAsync(org.Id);
            project.AssignedUserListId = list.Id;
            await fixture.Db.SaveChangesAsync();
        }

        var idp = new SamlIdpConfig
        {
            Id              = Guid.NewGuid(),
            ProjectId       = project.Id,
            EntityId        = "https://acs-test-idp.example.com",
            SsoUrl          = "https://acs-test-idp.example.com/sso",
            CertificatePem  = cert.ExportCertificatePem(),
            Active          = true,
            JitProvisioning = jit,
            CreatedAt       = DateTimeOffset.UtcNow,
            UpdatedAt       = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(idp);
        await fixture.Db.SaveChangesAsync();

        return (idp, cert);
    }

    // Calls Start (to populate session), decodes the redirect to extract the authn-
    // request ID, then builds and returns a valid signed SAMLResponse POST form.
    private static async Task<FormUrlEncodedContent> BuildAcsFormAsync(
        HttpClient client, string challenge, SamlIdpConfig idp, X509Certificate2 cert,
        ClaimsIdentity? identity = null,
        Saml2StatusCodes responseStatus = Saml2StatusCodes.Success)
    {
        // Start populates the session with saml_req:{idpId}
        var startRes = await client.GetAsync(
            $"/auth/saml/start?login_challenge={Uri.EscapeDataString(challenge)}&idp_id={idp.Id}");
        startRes.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Extract SAMLRequest and RelayState from the redirect URL.
        // The RelayState here was encoded by the SP — reuse it verbatim in the
        // ACS POST so the server can decode it with the exact same encoder it used.
        var location  = startRes.Headers.Location!;
        var rawQuery  = location.Query.TrimStart('?');
        var qp        = rawQuery.Split('&')
                           .Select(p => p.Split('=', 2))
                           .Where(p => p.Length == 2)
                           .ToDictionary(p => Uri.UnescapeDataString(p[0]),
                                         p => Uri.UnescapeDataString(p[1]));

        // SP-generated relay state
        var spRelayState = qp.TryGetValue("RelayState", out var rs) ? rs : string.Empty;
        spRelayState.Should().NotBeNullOrEmpty("relay state must be present in the Start redirect URL");

        // Decode the deflate-compressed SAMLRequest to get the authn-request ID
        var compressed = Convert.FromBase64String(qp["SAMLRequest"]);
        string authnXml;
        using (var ms      = new MemoryStream(compressed))
        using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
        using (var sr      = new StreamReader(deflate, Encoding.UTF8))
            authnXml = await sr.ReadToEndAsync();

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(authnXml);
        var authnReqId = xmlDoc.DocumentElement!.GetAttribute("ID");

        return BuildSignedResponseForm(challenge, idp, cert, spRelayState, authnReqId,
            identity, responseStatus);
    }

    // Builds the ACS POST form directly without calling Start (no session populated).
    // Use this to test IdP-initiated flows, missing-session branches, etc.
    private static FormUrlEncodedContent BuildAcsFormNoSession(
        string challenge, SamlIdpConfig idp, X509Certificate2 cert,
        ClaimsIdentity? identity = null)
    {
        var relayState = $"login_challenge={Uri.EscapeDataString(challenge)}&idp_id={idp.Id}";
        return BuildSignedResponseForm(challenge, idp, cert, relayState,
            authnReqId: "_fake_req_no_session", identity);
    }

    private static FormUrlEncodedContent BuildSignedResponseForm(
        string challenge, SamlIdpConfig idp, X509Certificate2 cert,
        string relayState, string authnReqId,
        ClaimsIdentity? identity = null,
        Saml2StatusCodes responseStatus = Saml2StatusCodes.Success)
    {
        const string spEntityId = "http://localhost/auth/saml/metadata";
        var acsUri = new Uri("http://localhost/auth/saml/acs");

        var idpCfg = new Saml2Configuration
        {
            Issuer             = idp.EntityId,
            SigningCertificate = cert,
        };
        idpCfg.AllowedAudienceUris.Add(spEntityId);

        identity ??= new ClaimsIdentity(new[] { new Claim("email", "saml-user@test.com") });

        // Derive NameId from the identity's email claim so that ExtractEmail's
        // NameIdentifier fallback stays consistent with the actual email present.
        var emailForNameId = identity.FindFirst("email")?.Value
                          ?? identity.FindFirst(ClaimTypes.Email)?.Value;
        var nameId = emailForNameId != null
            ? new Saml2NameIdentifier(emailForNameId)
            : null;

        var authResp = new Saml2AuthnResponse(idpCfg)
        {
            Status               = responseStatus,
            Destination          = acsUri,
            InResponseToAsString = authnReqId,
            NameId               = nameId,
            ClaimsIdentity       = identity,
        };

        // Only create the assertion for success responses — failure responses have no assertion.
        if (responseStatus == Saml2StatusCodes.Success)
            authResp.CreateSecurityToken(spEntityId);

        var postBind = new Saml2PostBinding();
        postBind.RelayState = relayState;
        postBind.Bind(authResp);

        var html = postBind.PostContent;
        var m = SamlResponseByNameRegex().Match(html);
        if (!m.Success)
            m = SamlResponseByValueRegex().Match(html);

        var rsMatch = RelayStateByNameRegex().Match(html);
        if (!rsMatch.Success)
            rsMatch = RelayStateByValueRegex().Match(html);
        var formRelayState = rsMatch.Success
            ? WebUtility.HtmlDecode(rsMatch.Groups[1].Value)
            : relayState;

        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = m.Groups[1].Value,
            ["RelayState"]   = formRelayState,
        });
    }

    // ── ACS: line 86 — idp not found after relay state parsed ────────────────

    [Fact]
    public async Task Acs_ValidRelayStateUnknownIdp_ReturnsBadRequest()
    {
        var challenge = Guid.NewGuid().ToString("N");
        var fakeIdpId = Guid.NewGuid();

        // The relay state is a plain URL-encoded query string (no base64 wrapping).
        // Build it the same way the SP does in the Start endpoint.
        var rawRelayState = $"login_challenge={Uri.EscapeDataString(challenge)}&idp_id={Uri.EscapeDataString(fakeIdpId.ToString())}";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<dummy/>")),
            ["RelayState"]   = rawRelayState,
        });

        var res = await fixture.Client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_idp_not_found");
    }

    // ── ACS: line 123 — project has no user list ──────────────────────────────

    [Fact]
    public async Task Acs_ValidResponse_ProjectNotConfigured_Returns503()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync(assignList: false);
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_configured");
    }

    // ── ACS: line 133 — JIT disabled and user not found ──────────────────────

    [Fact]
    public async Task Acs_ValidResponse_JitDisabled_UserNotFound_ReturnsUnauthorized()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync(jit: false);
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("user_not_provisioned");
    }

    // ── ACS: line 138 — user exists but is disabled ───────────────────────────

    [Fact]
    public async Task Acs_ValidResponse_UserInactive_ReturnsUnauthorized()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        // Lookup the project's userListId so we can seed the user in the right list
        var dbIdp    = await fixture.Db.SamlIdpConfigs.Include(x => x.Project).FirstAsync(x => x.Id == idp.Id);
        var listId   = dbIdp.Project.AssignedUserListId!.Value;
        var existing = await fixture.Seed.CreateUserAsync(listId);
        existing.Email  = "saml-user@test.com";
        existing.Active = false;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("account_disabled");
    }

    // ── ACS: lines 130–155 — JIT provisioning happy path ─────────────────────

    [Fact]
    public async Task Acs_ValidResponse_JitProvisioning_ReturnsRedirect()
    {
        // User does not exist → JIT-provisioned → login accepted → redirect
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Verify the user was JIT-provisioned in the DB
        var provisioned = await fixture.Db.Users
            .FirstOrDefaultAsync(u => u.Email == "saml-user@test.com");
        provisioned.Should().NotBeNull();
    }

    // ── ACS: line 127 — user already exists (no JIT needed) ──────────────────

    [Fact]
    public async Task Acs_ValidResponse_ExistingUser_ReturnsRedirect()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var dbIdp  = await fixture.Db.SamlIdpConfigs.Include(x => x.Project).FirstAsync(x => x.Id == idp.Id);
        var listId = dbIdp.Project.AssignedUserListId!.Value;
        var user   = await fixture.Seed.CreateUserAsync(listId);
        user.Email  = "saml-user@test.com";
        user.Active = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    // ── ACS: lines 207–213 — JIT with DefaultRoleId ───────────────────────────

    [Fact]
    public async Task Acs_ValidResponse_JitWithDefaultRole_ReturnsRedirect()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var dbIdp   = await fixture.Db.SamlIdpConfigs.Include(x => x.Project).FirstAsync(x => x.Id == idp.Id);
        var project = dbIdp.Project;
        var role    = new Role
        {
            Id        = Guid.NewGuid(),
            ProjectId = project.Id,
            Name      = "saml-default-viewer",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.Roles.Add(role);
        dbIdp.DefaultRoleId = role.Id;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert,
            new ClaimsIdentity(new[] { new Claim("email", "saml-role-user@test.com") }));

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var roleAssigned = await fixture.Db.UserProjectRoles
            .AnyAsync(r => r.RoleId == role.Id);
        roleAssigned.Should().BeTrue();
    }

    // ── ACS: relay state parses but idp_id is not a valid GUID ───────────────

    [Fact]
    public async Task Acs_RelayStateInvalidGuid_ReturnsBadRequest()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<dummy/>")),
            ["RelayState"]   = "login_challenge=abc&idp_id=not-a-guid",
        });

        var res = await fixture.Client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_relay_state");
    }

    // ── ACS: IdP returns a failure status (e.g. auth denied) ─────────────────

    [Fact]
    public async Task Acs_SamlStatusError_ReturnsBadRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        var form   = await BuildAcsFormAsync(client, challenge, idp, cert,
            responseStatus: Saml2StatusCodes.Requester);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_response_invalid");
    }

    // ── ACS: valid response but no pending session (IdP-initiated / replay) ───

    [Fact]
    public async Task Acs_NoSession_ReturnsBadRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        // Do NOT call Start — no session entry will exist for this IDP
        var client = fixture.NewSessionClient();
        var form   = BuildAcsFormNoSession(challenge, idp, cert);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_no_pending_request");
    }

    // ── ACS: InResponseTo in response doesn't match the session-stored request ID ──

    [Fact]
    public async Task Acs_InResponseToMismatch_ReturnsBadRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();

        // Call Start to populate session with the real request ID
        var startRes = await client.GetAsync(
            $"/auth/saml/start?login_challenge={Uri.EscapeDataString(challenge)}&idp_id={idp.Id}");
        startRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var rawQuery   = startRes.Headers.Location!.Query.TrimStart('?');
        var spRelayState = rawQuery.Split('&')
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]))
            .GetValueOrDefault("RelayState", "");

        // Build a response with a DIFFERENT InResponseTo than what was stored in the session
        var form = BuildSignedResponseForm(challenge, idp, cert, spRelayState,
            authnReqId: "_wrong_request_id");

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_response_invalid");
    }

    // ── ACS: response has no email claim ─────────────────────────────────────

    [Fact]
    public async Task Acs_EmailMissing_ReturnsBadRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client");
        var client = fixture.NewSessionClient();
        // ClaimsIdentity with no email-related claims
        var identity = new ClaimsIdentity(new[] { new Claim("displayName", "No Email User") });
        var form     = await BuildAcsFormAsync(client, challenge, idp, cert, identity);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_email_missing");
    }
}

// ── SamlService unit tests (no fixture required) ─────────────────────────────

/// <summary>
/// Pure unit tests for SamlService static helpers — no I/O, no containers.
/// </summary>
public class SamlServiceUnitTests
{
    private static SamlService BuildService() =>
        new(null!, NullLogger<SamlService>.Instance);

    // ── ExtractEmail ──────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEmail_NullIdentity_ReturnsNull()
    {
        SamlService.ExtractEmail(null, "email").Should().BeNull();
    }

    [Fact]
    public void ExtractEmail_MatchesAttributeName_ReturnsValue()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("mail", "user@example.com") });

        SamlService.ExtractEmail(identity, "mail").Should().Be("user@example.com");
    }

    [Fact]
    public void ExtractEmail_FallsBackToClaimTypesEmail()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, "fallback@example.com") });

        SamlService.ExtractEmail(identity, "email").Should().Be("fallback@example.com");
    }

    [Fact]
    public void ExtractEmail_FallsBackToEmailAddressClaim()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "addr@example.com")
        });

        SamlService.ExtractEmail(identity, "no_match").Should().Be("addr@example.com");
    }

    [Fact]
    public void ExtractEmail_NoEmailClaim_ReturnsNull()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "nameid@example.com") });

        SamlService.ExtractEmail(identity, "no_match").Should().BeNull();
    }

    // ── ExtractDisplayName ────────────────────────────────────────────────────

    [Fact]
    public void ExtractDisplayName_NullIdentity_ReturnsNull()
    {
        SamlService.ExtractDisplayName(null, "displayName").Should().BeNull();
    }

    [Fact]
    public void ExtractDisplayName_MatchesAttributeName_ReturnsValue()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("cn", "Alice Smith") });

        SamlService.ExtractDisplayName(identity, "cn").Should().Be("Alice Smith");
    }

    [Fact]
    public void ExtractDisplayName_NullAttributeName_FallsBackToGivenName()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.GivenName, "Bob") });

        SamlService.ExtractDisplayName(identity, null).Should().Be("Bob");
    }

    [Fact]
    public void ExtractDisplayName_NullAttributeName_FallsBackToDisplayName()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("displayName", "Charlie") });

        SamlService.ExtractDisplayName(identity, null).Should().Be("Charlie");
    }

    [Fact]
    public void ExtractDisplayName_NullAttributeName_FallsBackToClaimName()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "Dave") });

        SamlService.ExtractDisplayName(identity, null).Should().Be("Dave");
    }

    [Fact]
    public void ExtractDisplayName_NoMatchingClaim_ReturnsNull()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("unrelated", "value") });

        SamlService.ExtractDisplayName(identity, null).Should().BeNull();
    }

    // ── BuildConfigAsync — ApplyMetadataAsync happy path ────────────────────

    private static (X509Certificate2 cert, string metadataXml) BuildIdpMetadataXml(
        string entityId, string ssoUrl)
    {
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=MetaIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var raw = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certB64 = Convert.ToBase64String(raw.RawData);
        var pfx  = raw.Export(X509ContentType.Pfx);
        var cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <EntityDescriptor entityID="{entityId}"
                xmlns="urn:oasis:names:tc:SAML:2.0:metadata"
                xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
              <IDPSSODescriptor WantAuthnRequestsSigned="false"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <KeyDescriptor use="signing">
                  <ds:KeyInfo>
                    <ds:X509Data>
                      <ds:X509Certificate>{certB64}</ds:X509Certificate>
                    </ds:X509Data>
                  </ds:KeyInfo>
                </KeyDescriptor>
                <SingleSignOnService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"
                    Location="{ssoUrl}"/>
              </IDPSSODescriptor>
            </EntityDescriptor>
            """;
        return (cert, xml);
    }

    private static SamlService BuildServiceWithMetadata(string metadataXml)
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(metadataXml, Encoding.UTF8, "application/xml")
            });
        var httpClient = new HttpClient(handler);
        var factory    = new FakeHttpClientFactory(httpClient);
        return new SamlService(factory, NullLogger<SamlService>.Instance);
    }

    [Fact]
    public async Task BuildConfigAsync_ValidMetadataUrl_PopulatesConfigFromMetadata()
    {
        const string entityId = "https://meta-idp.example.com";
        const string ssoUrl   = "https://meta-idp.example.com/sso";
        var (cert, xml) = BuildIdpMetadataXml(entityId, ssoUrl);
        var svc = BuildServiceWithMetadata(xml);

        var idp = new SamlIdpConfig
        {
            Id          = Guid.NewGuid(),
            EntityId    = entityId,
            MetadataUrl = "https://meta-idp.example.com/metadata",
        };

        var config = await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        config.SingleSignOnDestination.Should().Be(new Uri(ssoUrl));
        config.AllowedIssuer.Should().Be(entityId);
        config.SignatureValidationCertificates.Should().HaveCount(1);
        config.SignatureValidationCertificates[0].RawData.Should().BeEquivalentTo(cert.RawData);
    }

    [Fact]
    public async Task BuildConfigAsync_MetadataWithNoSigningCerts_LogsWarningAndSucceeds()
    {
        // Metadata XML without a KeyDescriptor — warning logged, but no exception
        const string entityId = "https://meta-idp.example.com";
        const string ssoUrl   = "https://meta-idp.example.com/sso";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <EntityDescriptor entityID="{entityId}"
                xmlns="urn:oasis:names:tc:SAML:2.0:metadata">
              <IDPSSODescriptor WantAuthnRequestsSigned="false"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <SingleSignOnService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"
                    Location="{ssoUrl}"/>
              </IDPSSODescriptor>
            </EntityDescriptor>
            """;
        var svc = BuildServiceWithMetadata(xml);

        var idp = new SamlIdpConfig
        {
            Id          = Guid.NewGuid(),
            EntityId    = entityId,
            MetadataUrl = "https://meta-idp.example.com/metadata",
        };

        var config = await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        config.SignatureValidationCertificates.Should().BeEmpty();
        config.SingleSignOnDestination.Should().Be(new Uri(ssoUrl));
    }

    [Fact]
    public async Task BuildConfigAsync_MetadataWithNoIdPSsoDescriptor_ThrowsInvalidOperation()
    {
        // Metadata XML with an SP (not IdP) descriptor — causes "No IdPSsoDescriptor" error
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <EntityDescriptor entityID="https://sp.example.com"
                xmlns="urn:oasis:names:tc:SAML:2.0:metadata">
              <SPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol"/>
            </EntityDescriptor>
            """;
        var svc = BuildServiceWithMetadata(xml);

        var idp = new SamlIdpConfig
        {
            Id          = Guid.NewGuid(),
            EntityId    = "https://idp.example.com",
            MetadataUrl = "https://meta-idp.example.com/metadata",
        };

        var act = async () => await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{idp.Id}*failed to load metadata*");
    }

    // ── BuildConfigAsync — ApplyExplicitConfig paths ──────────────────────────

    [Fact]
    public async Task BuildConfigAsync_ExplicitSsoUrl_SetsSingleSignOnDestination()
    {
        var svc = BuildService();
        var idp = new SamlIdpConfig
        {
            Id       = Guid.NewGuid(),
            EntityId = "https://idp.example.com",
            SsoUrl   = "https://idp.example.com/sso",
        };

        var config = await svc.BuildConfigAsync(idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        config.SingleSignOnDestination.Should().Be(new Uri("https://idp.example.com/sso"));
    }

    [Fact]
    public async Task BuildConfigAsync_EmptySsoUrl_NoMetadata_ThrowsInvalidOperation()
    {
        var svc = BuildService();
        var idp = new SamlIdpConfig
        {
            Id       = Guid.NewGuid(),
            EntityId = "https://idp.example.com",
            SsoUrl   = null,   // no SsoUrl and no MetadataUrl → throws
        };

        var act = async () => await svc.BuildConfigAsync(idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML IdP has neither MetadataUrl nor SsoUrl");
    }

    // ── BuildConfigAsync — ApplyMetadataAsync HTTP URL rejection ─────────────

    [Fact]
    public async Task BuildConfigAsync_HttpMetadataUrl_ThrowsInvalidOperationWithLoadMessage()
    {
        // MetadataUrl uses http:// — the HTTPS-only guard fires, the catch block
        // wraps it, and ApplyMetadataAsync re-throws as InvalidOperationException.
        var svc = BuildService();
        var idp = new SamlIdpConfig
        {
            Id          = Guid.NewGuid(),
            EntityId    = "https://idp.example.com",
            MetadataUrl = "http://not-https.example.com/metadata",
            SsoUrl      = null,
        };

        var act = async () => await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{idp.Id}*failed to load metadata*");
    }

    // ── ApplyExplicitConfig — CertificatePem branch (line 73) ────────────────

    [Fact]
    public async Task BuildConfigAsync_WithCertificatePem_AddsSigningCertificate()
    {
        // Generate a real ephemeral self-signed cert so CreateFromPem doesn't throw.
        using var rsa  = RSA.Create(2048);
        var req  = new CertificateRequest(
            "CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        var pemCert    = cert.ExportCertificatePem();

        var svc = BuildService();
        var idp = new SamlIdpConfig
        {
            Id             = Guid.NewGuid(),
            EntityId       = "https://idp.example.com",
            SsoUrl         = "https://idp.example.com/sso",
            CertificatePem = pemCert,
        };

        var config = await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        config.SignatureValidationCertificates.Should().HaveCount(1);
    }
}

// ── Test helpers for SamlService unit tests ───────────────────────────────────

file sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response);
}

file sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name = "") => client;
}
