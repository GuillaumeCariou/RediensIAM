using System.Net.Http.Headers;
using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Covers SmtpEmailService SMTP send paths (lines 124-128, 191-195, 214-217, 227-230)
/// that are only reachable with a working SMTP connection.
/// Uses the MailHog container via CreateRealSmtpClient() — no IEmailService stub.
/// </summary>
[Collection("RediensIAM")]
public class SmtpEmailServiceCoverageTests(TestFixture fixture)
{
    // ── CheckConnectivityAsync (lines 227-230) ────────────────────────────────

    /// <summary>
    /// GET /admin/system/health with real SmtpEmailService + MailHog.
    /// SystemHealthController calls emailService.CheckConnectivityAsync() which
    /// connects, authenticates (L228), and disconnects (L229-230).
    /// </summary>
    [Fact]
    public async Task Health_RealSmtp_CheckConnectivity_CoversSmtpLines()
    {
        var (client, factory) = fixture.CreateRealSmtpClient();
        await using var _f = factory;

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var smtp = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "SMTP");
        // MailHog accepts the connection → status should be Ok
        smtp.GetProperty("status").GetString().Should().Be("Ok");
    }

    // ── SendOtpAsync (lines 124-128) ─────────────────────────────────────────

    /// <summary>
    /// Registration with email verification enabled triggers SmtpEmailService.SendOtpAsync
    /// which connects to MailHog, authenticates (L125), sends the OTP (L127), disconnects (L128).
    /// </summary>
    [Fact]
    public async Task Register_RealSmtp_SendOtp_CoversSmtpLines()
    {
        var (client, factory) = fixture.CreateRealSmtpClient();
        await using var _f = factory;

        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.AllowSelfRegistration    = true;
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var email     = SeedData.UniqueEmail();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email,
            password = "P@ssw0rd!MailTest"
        });

        // 200 means OTP was sent via real SMTP (MailHog accepted it)
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("session_id").GetString().Should().NotBeNullOrEmpty();
    }

    // ── SendInviteAsync (lines 191-195) ──────────────────────────────────────

    /// <summary>
    /// POST /org/userlists/{id}/users without a password triggers the invite flow,
    /// which calls SmtpEmailService.SendInviteAsync → connects, authenticates (L192),
    /// sends (L194), disconnects (L195).
    /// </summary>
    [Fact]
    public async Task Invite_RealSmtp_SendInvite_CoversSmtpLines()
    {
        var (client, factory) = fixture.CreateRealSmtpClient();
        await using var _f = factory;

        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        fixture.Keto.AllowAll();

        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        // No password → invite flow → calls SendInviteAsync via real SmtpEmailService
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email = SeedData.UniqueEmail(),
            // password omitted intentionally — triggers invite email
        });

        // 201 Created means invite email was sent via real SMTP (MailHog accepted it)
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── SendNewDeviceAlertAsync (lines 214-217) ───────────────────────────────

    /// <summary>
    /// First login for a user (new device) fires SmtpEmailService.SendNewDeviceAlertAsync
    /// via Task.Run inside AuthController. With MailHog, it connects, authenticates (L215),
    /// sends (L216), disconnects (L217).
    /// </summary>
    [Fact]
    public async Task Login_RealSmtp_SendNewDeviceAlert_CoversSmtpLines()
    {
        const string password = "P@ssw0rd!DeviceAlert";
        var (client, factory) = fixture.CreateRealSmtpClient();
        await using var _f = factory;

        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = orgList.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(orgList.Id, password: password);
        // Ensure new-device alerts are enabled (default is true)
        user.NewDeviceAlertsEnabled = true;
        await fixture.Db.SaveChangesAsync();

        fixture.Keto.AllowAll();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Wait for the Task.Run background task to connect, auth, send, and disconnect via MailHog
        await Task.Delay(3000);
    }
}
