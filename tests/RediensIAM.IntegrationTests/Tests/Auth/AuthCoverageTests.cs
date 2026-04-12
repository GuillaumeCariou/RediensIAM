using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController lines not exercised by the existing test files:
///   - GET /auth/login/theme — Hydra failure catch block (lines 120-121)
///   - GET /auth/consent   — admin client (client_admin_system) accept path (lines 491-524)
///   - GET /auth/consent   — admin client no roles → reject (lines 496-500)
///   - GET /auth/login/theme — ExtractProjectId URL fallback (lines 1283-1305)
///   - GET /auth/consent   — admin client OrgAdmin/ProjectAdmin roles (lines 492, 494)
///   - POST /auth/mfa/totp/verify — no session (line 429)
///   - POST /auth/mfa/phone/verify — no session (line 388)
///   - POST /auth/login — username without # discriminator (lines 196-197)
///   - POST /auth/register — SMS verification path (lines 703-704)
/// </summary>
[Collection("RediensIAM")]
public class AuthCoverageTests(TestFixture fixture)
{
    // ── GET /auth/login/theme — Hydra failure catch block (lines 120-121) ─────

    [Fact]
    public async Task GetTheme_HydraFails_Returns400()
    {
        // Default stub returns 404 for unknown challenges → GetLoginRequestAsync throws
        // → catch block runs → BadRequest()
        var res = await fixture.Client.GetAsync("/auth/login/theme?login_challenge=nonexistent-challenge-abc");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /auth/consent — admin client (super_admin path, lines 491-524) ────

    [Fact]
    public async Task GetConsent_AdminClient_SuperAdmin_AcceptsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.AllowAll();   // super_admin check returns true

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        fixture.Hydra.ConsentWasAccepted(challenge).Should().BeTrue();
    }

    // ── GET /auth/consent — admin client no roles → reject (lines 496-500) ───

    [Fact]
    public async Task GetConsent_AdminClient_NoRoles_RejectsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.DenyAll();   // all Keto checks return false → no roles

        try
        {
            var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            fixture.Hydra.ConsentWasRejected(challenge).Should().BeTrue();
        }
        finally
        {
            fixture.Keto.AllowAll();
        }
    }

    // ── GET /auth/login/theme — ExtractProjectId URL fallback (lines 1283-1305)

    [Fact]
    public async Task GetTheme_ProjectIdInUrlNotOidcContext_Returns200()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var challenge = Guid.NewGuid().ToString("N");

        // Challenge has project_id in the request_url but NOT in oidc_context extras
        fixture.Hydra.SetupLoginChallengeProjectInUrl(challenge, project.HydraClientId, project.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("login_theme", out _).Should().BeTrue();
    }

    // ── GET /auth/consent — admin client OrgAdmin + ProjectAdmin roles (lines 492, 494) ─

    [Fact]
    public async Task GetConsent_AdminClient_OrgAndProjectAdminRoles_AcceptsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.AllowAll();
        // Make HasAnyRelationAsync return true → OrgAdmin and ProjectAdmin lines fire
        fixture.Keto.SimulateRelationExists($"user:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        fixture.Hydra.ConsentWasAccepted(challenge).Should().BeTrue();
    }

    // ── POST /auth/mfa/totp/verify — no MFA session (line 429) ───────────────

    [Fact]
    public async Task VerifyTotp_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();   // fresh session, no MFA state

        var res = await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code = "123456" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/mfa/phone/verify — no MFA session (line 388) ──────────────

    [Fact]
    public async Task VerifySmsOtp_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.PostAsJsonAsync("/auth/mfa/phone/verify", new { code = "123456" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/login — username without # (lines 196-197) ────────────────

    [Fact]
    public async Task Login_UsernameWithoutDiscriminator_Returns401()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId    = list.Id;
        project.AllowSelfRegistration = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        // username has no "#" separator → parts.Length != 2 → LookupUserByCredentialsAsync returns null
        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            username        = "justusername",   // no discriminator → returns null at line 197
            password        = "P@ssw0rd!Test"
        });

        // User not found → 401 invalid_credentials
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    // ── POST /auth/register — SMS verification path (lines 703-704) ──────────

    [Fact]
    public async Task Register_SmsVerificationEnabled_SendsSmsOtpAndReturnsSessionId()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId        = list.Id;
        project.AllowSelfRegistration     = true;
        project.SmsVerificationEnabled    = true;
        project.EmailVerificationEnabled  = false;   // SMS only → line 703 evaluates false
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test",
            phone           = "+1234567890"    // required for SMS branch (line 703: body.Phone != null)
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_verification").GetBoolean().Should().BeTrue();
        // SMS OTP was sent via the stub
        fixture.SmsStub.SentMessages.Should().NotBeEmpty();
    }
}
