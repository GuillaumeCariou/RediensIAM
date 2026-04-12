using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
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
}
