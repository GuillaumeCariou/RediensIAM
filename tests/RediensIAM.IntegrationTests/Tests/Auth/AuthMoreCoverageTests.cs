using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RediensIAM.Controllers;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController lines not yet exercised by other test files:
///   - POST /auth/login   — project_id mismatch (lines 149-151)
///   - POST /auth/register — invalid challenge catch block (lines 590-593)
///   - POST /auth/password-reset/request — SMS-only path (lines 833-834)
///   - Ipv6InRange static helper (lines 1001-1012)
/// </summary>
[Collection("RediensIAM")]
public class AuthMoreCoverageTests(TestFixture fixture)
{
    // ── POST /auth/login — project_id mismatch (lines 149-151) ───────────────

    [Fact]
    public async Task Login_ProjectIdMismatch_RejectsWithRedirect()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var challenge = Guid.NewGuid().ToString("N");

        // oidc_context has the real project ID; client.metadata has a different one
        fixture.Hydra.SetupLoginChallengeWithProjectIdMismatch(
            challenge,
            project.HydraClientId,
            oidcProjectId:        project.Id.ToString(),
            registeredProjectId:  Guid.NewGuid().ToString());   // deliberately different

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            username        = "anyone",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_id_mismatch");
    }

    // ── POST /auth/register — invalid challenge (lines 590-593) ──────────────

    [Fact]
    public async Task Register_InvalidChallenge_Returns400()
    {
        // Hydra stub returns 404 for unknown challenges → GetLoginRequestAsync throws →
        // catch block (lines 590-593) runs → BadRequest with "invalid_challenge"
        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = "completely-nonexistent-challenge-xyz",
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_challenge");
    }

    // ── POST /auth/password-reset/request — SMS-only path (lines 833-834) ────

    [Fact]
    public async Task PasswordResetRequest_SmsOnlyProject_SendsSmsCode()
    {
        // Arrange: project with SmsVerificationEnabled=true, EmailVerificationEnabled=false
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.EmailVerificationEnabled = false;
        project.SmsVerificationEnabled   = true;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.Phone         = "+15550001234";
        user.PhoneVerified = true;
        await fixture.Db.SaveChangesAsync();

        fixture.SmsStub.SentMessages.Clear();

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email,
            phone      = user.Phone
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.SmsStub.SentMessages.Should().ContainSingle(m => m.Purpose == "password_reset");
    }
}

// ── Ipv6InRange unit tests (no fixture required) ─────────────────────────────

/// <summary>
/// Pure unit tests for AuthController.Ipv6InRange / IpInRange static helpers via reflection.
/// Covers lines 1001-1012 which can't be reached from integration tests because TestServer
/// always sets the remote IP to 127.0.0.1 (IPv4).
/// </summary>
public class AuthControllerIpRangeTests
{
    private static readonly MethodInfo IpInRange =
        typeof(AuthController)
            .GetMethod("IpInRange", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool Invoke(string ip, string cidr) =>
        (bool)IpInRange.Invoke(null, [IPAddress.Parse(ip), cidr])!;

    // ── IPv6 full-byte match (lines 1002-1005, 1006→false, 1011) ─────────────

    [Fact]
    public void Ipv6InRange_ExactNetwork_ReturnsTrue()
    {
        // 2001:db8::1 is in 2001:db8::/32 (4 full bytes identical, remBits=0)
        Invoke("2001:db8::1", "2001:db8::/32").Should().BeTrue();
    }

    [Fact]
    public void Ipv6InRange_DifferentNetwork_ReturnsFalse()
    {
        // 2001:db9::1 is NOT in 2001:db8::/32 (byte[2] differs → inner return false, line 1005)
        Invoke("2001:db9::1", "2001:db8::/32").Should().BeFalse();
    }

    // ── IPv6 partial-byte match (lines 1006→true, 1008, 1009, 1011) ──────────

    [Fact]
    public void Ipv6InRange_PartialByteMatch_ReturnsTrue()
    {
        // /33 → fullBytes=4, remBits=1, byteMask=0x80
        // 2001:db8:0000:: byte[4]=0x00 & 0x80 == 0x00 → match → true
        Invoke("2001:db8::", "2001:db8::/33").Should().BeTrue();
    }

    [Fact]
    public void Ipv6InRange_PartialByteMismatch_ReturnsFalse()
    {
        // 2001:db8:8000:: byte[4]=0x80 & 0x80 == 0x80, net byte[4]=0x00 & 0x80 == 0x00 → mismatch → false (line 1009)
        Invoke("2001:db8:8000::", "2001:db8::/33").Should().BeFalse();
    }
}
