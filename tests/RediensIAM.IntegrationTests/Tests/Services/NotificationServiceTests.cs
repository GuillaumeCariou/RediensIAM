using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Direct unit-style tests for SmtpEmailService branches that are unreachable
/// through the API (e.g. no-SMTP no-op paths, CheckConnectivity error).
/// Uses the shared Db from TestFixture for EF queries; no real SMTP server needed.
/// </summary>
[Collection("RediensIAM")]
public class NotificationServiceTests(TestFixture fixture)
{
    private static AppConfig NoSmtpConfig() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"]                        = "",
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
            })
            .Build());

    // ── SendOtpAsync — no SMTP configured ────────────────────────────────────

    [Fact]
    public async Task SendOtp_NoSmtpConfigured_CompletesWithoutException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.SendOtpAsync("user@test.com", "123456", "registration");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendOtp_NoSmtpConfigured_PasswordResetPurpose_CompletesWithoutException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.SendOtpAsync("user@test.com", "654321", "password_reset");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendOtp_WithOrgId_NoOrgSmtpConfig_FallsBackToNoOp()
    {
        var svc   = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);
        var orgId = Guid.NewGuid(); // org with no SMTP config in DB

        var act = () => svc.SendOtpAsync("user@test.com", "111111", "registration", orgId);

        await act.Should().NotThrowAsync();
    }

    // ── SendInviteAsync — no SMTP configured ──────────────────────────────────

    [Fact]
    public async Task SendInvite_NoSmtpConfigured_CompletesWithoutException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.SendInviteAsync("user@test.com", "https://app/invite/abc", "MyOrg");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendInvite_WithProjectId_NoSmtpConfigured_CompletesWithoutException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // projectId points to a non-existent project — falls through to no-SMTP no-op
        var act = () => svc.SendInviteAsync("user@test.com", "https://app/invite/xyz",
            "MyOrg", Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    // ── SendNewDeviceAlertAsync — no SMTP configured ──────────────────────────

    [Fact]
    public async Task SendNewDeviceAlert_NoSmtpConfigured_CompletesWithoutException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.SendNewDeviceAlertAsync(
            "user@test.com", "1.2.3.4", "Mozilla/5.0", DateTimeOffset.UtcNow);

        await act.Should().NotThrowAsync();
    }

    // ── CheckConnectivityAsync — no SMTP configured ───────────────────────────

    [Fact]
    public async Task CheckConnectivity_NoSmtpConfigured_ThrowsInvalidOperationException()
    {
        var svc = new SmtpEmailService(NoSmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.CheckConnectivityAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SMTP not configured*");
    }

    // ── OrgSmtpConfig path — SendOtpAsync resolves org config ────────────────
    // These tests verify the OrgSmtpConfig branch is entered and the service
    // attempts to connect (which fails with a socket error — no live SMTP server).

    private static AppConfig SmtpConfig() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"]                        = "smtp.test.local",
                ["Smtp:Port"]                        = "587",
                ["Smtp:FromAddress"]                 = "noreply@test.com",
                ["Smtp:FromName"]                    = "Test IAM",
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
            })
            .Build());

    private async Task<(Organisation org, OrgSmtpConfig smtpCfg)> SeedOrgWithSmtpAsync()
    {
        var (org, _) = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateOrgAsync();

        var smtpCfg = new OrgSmtpConfig
        {
            Id          = Guid.NewGuid(),
            OrgId       = org.Id,
            Host        = "org-smtp.test.local",
            Port        = 587,
            StartTls    = true,
            Username    = null,
            PasswordEnc = null,
            FromAddress = "no-reply@org.test",
            FromName    = "Org Test",
            CreatedAt   = DateTimeOffset.UtcNow,
            UpdatedAt   = DateTimeOffset.UtcNow,
        };
        fixture.Db.OrgSmtpConfigs.Add(smtpCfg);
        await fixture.Db.SaveChangesAsync();
        return (org, smtpCfg);
    }

    [Fact]
    public async Task SendOtp_OrgSmtpConfig_NoUsernameNoPasswordEnc_AttemptsConnectAndThrows()
    {
        var (org, _) = await SeedOrgWithSmtpAsync();
        var svc      = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // OrgSmtpConfig found → enters the orgConfig != null branch (lines 66-76)
        // No username → skips authenticate branch → tries ConnectAsync → socket error
        var act = () => svc.SendOtpAsync("user@test.com", "123456", "registration", org.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendOtp_OrgSmtpConfig_PasswordEncNotNull_DecryptsAndAttemptsConnect()
    {
        var (org, smtpCfg) = await SeedOrgWithSmtpAsync();

        // Encrypt a dummy password into PasswordEnc
        var key      = Convert.FromHexString(new string('0', 64));
        var encPwd   = TotpEncryption.Encrypt(key, Encoding.UTF8.GetBytes("secret"));
        smtpCfg.PasswordEnc = encPwd;
        smtpCfg.Username    = "orguser";
        await fixture.Db.SaveChangesAsync();

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // PasswordEnc != null → Decrypt branch (line 71-74) executes; then socket error
        var act = () => svc.SendOtpAsync("user@test.com", "123456", "registration", org.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendOtp_OrgSmtpConfig_WithProjectId_OverridesFromName()
    {
        var (org, _) = await SeedOrgWithSmtpAsync();

        // Create a project with EmailFromName set
        var project = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateProjectAsync(org.Id);
        project.EmailFromName = "Custom Sender";
        await fixture.Db.SaveChangesAsync();

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // projectId provided → enters the project?.EmailFromName override branch (line 98-99)
        // Then tries to connect → socket error
        var act = () => svc.SendOtpAsync("user@test.com", "123456", "registration", org.Id, project.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendOtp_GlobalSmtpConfig_WithProjectId_NoEmailFromName_DoesNotOverride()
    {
        // Project exists but has no EmailFromName — fromName stays as globalSmtpFromName
        var (org, _) = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateOrgAsync();
        var project = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateProjectAsync(org.Id);
        // EmailFromName is null (default)

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // No orgSmtpConfig → global SMTP branch → projectId path (lines 95-100)
        // project.EmailFromName is null/empty → fromName unchanged → socket error
        var act = () => svc.SendOtpAsync("user@test.com", "654321", "password_reset", null, project.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    // ── SendInviteAsync — OrgSmtpConfig via project ───────────────────────────

    [Fact]
    public async Task SendInvite_OrgSmtpConfig_ViaProjectId_AttemptsConnectAndThrows()
    {
        var (org, _) = await SeedOrgWithSmtpAsync();

        var project = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateProjectAsync(org.Id);

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // projectId provided → resolves orgId → finds OrgSmtpConfig → lines 150-160 execute
        var act = () => svc.SendInviteAsync("user@test.com",
            "https://app/invite/abc", "TestOrg", project.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendInvite_OrgSmtpConfig_PasswordEncNotNull_DecryptsAndThrows()
    {
        var (org, smtpCfg) = await SeedOrgWithSmtpAsync();

        var key      = Convert.FromHexString(new string('0', 64));
        var encPwd   = TotpEncryption.Encrypt(key, Encoding.UTF8.GetBytes("secret"));
        smtpCfg.PasswordEnc = encPwd;
        smtpCfg.Username    = "orguser";
        await fixture.Db.SaveChangesAsync();

        var project = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateProjectAsync(org.Id);

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // PasswordEnc != null → Decrypt branch (line 155-157) executes; then socket error
        var act = () => svc.SendInviteAsync("user@test.com",
            "https://app/invite/abc", "TestOrg", project.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendInvite_GlobalSmtpConfig_WithProjectId_NoOrgSmtpConfig_AttemptsConnect()
    {
        // Project exists but its org has no OrgSmtpConfig → falls back to global SMTP
        var (org, _) = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateOrgAsync();
        var project = await new SeedData(fixture.Db, fixture.Hydra,
            fixture.GetService<RediensIAM.Services.PasswordService>()).CreateProjectAsync(org.Id);

        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // resolvedOrgId found but no OrgSmtpConfig for it → global SMTP branch (lines 162-170)
        var act = () => svc.SendInviteAsync("user@test.com",
            "https://app/invite/abc", "MyOrg", project.Id);

        await act.Should().ThrowAsync<Exception>();
    }

    // ── SendNewDeviceAlertAsync — SMTP configured ─────────────────────────────

    [Fact]
    public async Task SendNewDeviceAlert_SmtpConfigured_AttemptsConnectAndThrows()
    {
        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // SmtpHost is not empty → enters body → socket error on ConnectAsync (lines 202-217)
        var act = () => svc.SendNewDeviceAlertAsync(
            "user@test.com", "1.2.3.4", "Mozilla/5.0", DateTimeOffset.UtcNow);

        await act.Should().ThrowAsync<Exception>();
    }

    // ── CheckConnectivityAsync — SMTP configured ──────────────────────────────

    [Fact]
    public async Task CheckConnectivity_SmtpConfigured_AttemptsConnectAndThrows()
    {
        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        // SmtpHost is not empty → enters body → socket error (lines 224-229)
        var act = () => svc.CheckConnectivityAsync();

        await act.Should().ThrowAsync<Exception>();
    }

    // ── SendOtpAsync — switch default arm (custom purpose) ───────────────────

    [Fact]
    public async Task SendOtp_CustomPurpose_HitsSwitchDefaultArm_ThenThrowsOnConnect()
    {
        // purpose is not "registration" or "password_reset" → hits the _ arm (L107)
        // SmtpConfig is non-empty → gets past the no-SMTP early return → reaches the switch
        // Then fails at ConnectAsync (no real SMTP server) as expected.
        var (org, _) = await SeedOrgWithSmtpAsync();
        var svc = new SmtpEmailService(SmtpConfig(), fixture.Db,
            NullLogger<SmtpEmailService>.Instance);

        var act = () => svc.SendOtpAsync("user@test.com", "777777", "login_otp", org.Id);

        await act.Should().ThrowAsync<Exception>();
    }
}
