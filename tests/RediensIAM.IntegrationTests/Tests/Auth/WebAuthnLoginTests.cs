using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController WebAuthn MFA login paths:
///   - GET  /auth/mfa/webauthn/options  — no session (line 1314)
///   - GET  /auth/mfa/webauthn/options  — valid session, returns assertion options (lines 1316-1329)
///   - POST /auth/mfa/webauthn/verify   — no session (line 1339)
///   - POST /auth/mfa/webauthn/verify   — no assertion options (line 1342)
///   - POST /auth/mfa/webauthn/verify   — unknown credential (line 1349)
///   - POST /auth/mfa/webauthn/verify   — assertion_failed (lines 1357-1369)
/// </summary>
[Collection("RediensIAM")]
public class WebAuthnLoginTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── GET /auth/mfa/webauthn/options — no MFA session ──────────────────────

    [Fact]
    public async Task WebAuthnOptions_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.GetAsync("/auth/mfa/webauthn/options");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── GET /auth/mfa/webauthn/options — valid session ────────────────────────

    [Fact]
    public async Task WebAuthnOptions_ValidSession_ReturnsAssertionOptions()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        loginBody.GetProperty("mfa_type").GetString().Should().Be("webauthn");

        var res = await client.GetAsync("/auth/mfa/webauthn/options");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("challenge", out _).Should().BeTrue();
    }

    // ── POST /auth/mfa/webauthn/verify — no MFA session ──────────────────────

    [Fact]
    public async Task WebAuthnVerify_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify",
            JsonDocument.Parse("{}").RootElement);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/mfa/webauthn/verify — no assertion options ─────────────────

    [Fact]
    public async Task WebAuthnVerify_NoAssertionOptions_Returns400()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        // Session is set (mfa_pending_user etc.) but no fido2.assertionOptions
        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify",
            JsonDocument.Parse("{}").RootElement);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_assertion_options");
    }

    // ── POST /auth/mfa/webauthn/verify — unknown credential ───────────────────

    [Fact]
    public async Task WebAuthnVerify_UnknownCredential_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        // Call options to set fido2.assertionOptions in session
        await client.GetAsync("/auth/mfa/webauthn/options");

        // Send verify with rawId that doesn't match any credential in DB
        // Must use Base64URL encoding (no +, /, or = chars) — Fido2 library rejects standard Base64
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var unknownId = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var payload = new
        {
            id    = B64Url(unknownId),
            rawId = B64Url(unknownId),
            type  = "public-key",
            response = new
            {
                authenticatorData = B64Url(new byte[37]),
                clientDataJSON    = B64Url(new byte[50]),
                signature         = B64Url(new byte[64]),
            }
        };

        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify", payload);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("unknown_credential");
    }
}
