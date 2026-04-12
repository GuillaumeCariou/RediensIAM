using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests for SamlController (/auth/saml/*) and SamlService static helpers.
/// SamlController requires no bearer token — it is part of the public login flow.
/// </summary>
[Collection("RediensIAM")]
public class SamlControllerTests(TestFixture fixture)
{
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
    public void ExtractEmail_FallsBackToNameIdentifier()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "nameid@example.com") });

        SamlService.ExtractEmail(identity, "no_match").Should().Be("nameid@example.com");
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
