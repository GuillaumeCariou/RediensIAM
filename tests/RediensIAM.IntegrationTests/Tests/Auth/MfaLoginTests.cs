using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests MFA verification endpoints when called without an active MFA session.
/// Full end-to-end MFA flow (TOTP confirm after login) requires session cookies
/// which are tied to the same HttpClient; those scenarios are covered here by
/// verifying the "no session" guard on each MFA endpoint.
/// </summary>
[Collection("RediensIAM")]
public class MfaLoginTests(TestFixture fixture)
{
    // ── TOTP ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyTotp_NoMfaSession_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/mfa/totp/verify", new
        {
            code = "123456"
        });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Backup codes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyBackupCode_NoMfaSession_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/mfa/backup-codes/verify", new
        {
            code = "ABCDE-12345"
        });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── SMS OTP ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifySmsOtp_NoMfaSession_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/mfa/phone/verify", new
        {
            code = "123456"
        });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task SendSmsOtp_NoMfaSession_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/mfa/phone/send", new { });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Login triggers TOTP redirect ──────────────────────────────────────────

    [Fact]
    public async Task Login_UserWithTotpEnabled_ReturnsMfaRequiredTrue()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled = true;
        user.TotpSecret  = "FAKE_ENCRYPTED_SECRET";
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfa_type").GetString().Should().Be("totp");
    }
}
