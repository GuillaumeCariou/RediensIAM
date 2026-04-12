using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Exceptions;
using RediensIAM.Middleware;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Unit;

// No [Collection] — pure in-process tests, no I/O, no shared fixture.
public class EntityModelTests
{
    // ── Entity property coverage ──────────────────────────────────────────────

    [Fact]
    public void AuditLog_IdAndUserId_ReadWrite()
    {
        var uid = Guid.NewGuid();
        var log = new AuditLog { Id = 42L, UserId = uid };
        log.Id.Should().Be(42L);
        log.UserId.Should().Be(uid);
    }

    [Fact]
    public void EmailToken_NewEmail_ReadWrite()
    {
        var token = new EmailToken { NewEmail = "new@example.com" };
        token.NewEmail.Should().Be("new@example.com");
    }

    [Fact]
    public void PersonalAccessToken_LastUsedAt_ReadWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var pat = new PersonalAccessToken { LastUsedAt = now };
        pat.LastUsedAt.Should().Be(now);
    }

    [Fact]
    public void UserProjectRole_Id_ReadWrite()
    {
        var id = Guid.NewGuid();
        var upr = new UserProjectRole { Id = id };
        upr.Id.Should().Be(id);
    }

    [Fact]
    public void Webhook_ProjectId_ReadWrite()
    {
        var id = Guid.NewGuid();
        var wh = new Webhook { ProjectId = id };
        wh.ProjectId.Should().Be(id);
    }

    [Fact]
    public void UserList_CreatedBy_ReadWrite()
    {
        var id = Guid.NewGuid();
        var ul = new UserList { CreatedBy = id };
        ul.CreatedBy.Should().Be(id);
    }

    [Fact]
    public void OrgSmtpConfig_Id_ReadWrite()
    {
        var id = Guid.NewGuid();
        var cfg = new OrgSmtpConfig { Id = id };
        cfg.Id.Should().Be(id);
    }

    // ── TokenClaims.ParsedUserId branch coverage ──────────────────────────────

    [Fact]
    public void TokenClaims_ParsedUserId_WithoutColon_ParsesAsGuid()
    {
        var expected = Guid.NewGuid();
        var claims = new TokenClaims { UserId = expected.ToString(), OrgId = "", ProjectId = "", Roles = [] };
        claims.ParsedUserId.Should().Be(expected);
    }

    [Fact]
    public void TokenClaims_ParsedUserId_AfterColon_InvalidGuid_ReturnsEmpty()
    {
        // "org:" prefix + non-Guid suffix → Guid.TryParse returns false → Guid.Empty
        var claims = new TokenClaims { UserId = "org:not-a-valid-guid", OrgId = "", ProjectId = "", Roles = [] };
        claims.ParsedUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void IntrospectRequest_CanBeInstantiated()
    {
        var req = new IntrospectRequest("my-token");
        req.Token.Should().Be("my-token");
    }

    // ── ClaimsExtensions.HasRole ──────────────────────────────────────────────

    [Fact]
    public void HasRole_WhenRolePresent_ReturnsTrue()
    {
        var claims = new TokenClaims { UserId = "u", OrgId = "o", ProjectId = "p", Roles = ["admin", "member"] };
        claims.HasRole("admin").Should().BeTrue();
    }

    [Fact]
    public void HasRole_WhenRoleAbsent_ReturnsFalse()
    {
        var claims = new TokenClaims { UserId = "u", OrgId = "o", ProjectId = "p", Roles = ["member"] };
        claims.HasRole("admin").Should().BeFalse();
    }

    // ── TotpEncryption.DecryptString ──────────────────────────────────────────

    [Fact]
    public void TotpEncryption_DecryptString_RoundTrips()
    {
        var key       = Convert.FromHexString(new string('0', 64));
        var plaintext = "hello world";
        var encrypted = TotpEncryption.EncryptString(key, plaintext);
        TotpEncryption.DecryptString(key, encrypted).Should().Be(plaintext);
    }

    // ── PasswordService.Verify invalid inputs ─────────────────────────────────

    [Fact]
    public void PasswordService_Verify_TooFewParts_ReturnsFalse()
    {
        BuildPasswordService().Verify("password", "not-a-hash").Should().BeFalse();
    }

    [Fact]
    public void PasswordService_Verify_InvalidBase64InSalt_ReturnsFalse()
    {
        // 6 '$'-delimited parts but part[4] is not valid base64 → FormatException → catch → false
        BuildPasswordService()
            .Verify("password", "$argon2id$v=19$m=8192,t=1,p=1$!!!$also")
            .Should().BeFalse();
    }

    private static PasswordService BuildPasswordService()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ArgonTimeCost"]    = "1",
                ["Security:ArgonMemoryCost"]  = "8192",
                ["Security:ArgonParallelism"] = "1",
            })
            .Build();
        return new PasswordService(new AppConfig(cfg));
    }

    // ── SamlIdpConfig entity properties ──────────────────────────────────────

    [Fact]
    public void SamlIdpConfig_Properties_ReadWrite()
    {
        var roleId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var cfg = new SamlIdpConfig
        {
            Id                      = Guid.NewGuid(),
            ProjectId               = projId,
            EntityId                = "https://idp.example.com",
            MetadataUrl             = "https://idp.example.com/metadata",
            SsoUrl                  = "https://idp.example.com/sso",
            CertificatePem          = "CERT",
            EmailAttributeName      = "email",
            DisplayNameAttributeName = "displayName",
            JitProvisioning         = false,
            DefaultRoleId           = roleId,
            Active                  = false,
            CreatedAt               = DateTimeOffset.UtcNow,
            UpdatedAt               = DateTimeOffset.UtcNow,
        };
        cfg.ProjectId.Should().Be(projId);
        cfg.EntityId.Should().Be("https://idp.example.com");
        cfg.MetadataUrl.Should().Be("https://idp.example.com/metadata");
        cfg.SsoUrl.Should().Be("https://idp.example.com/sso");
        cfg.CertificatePem.Should().Be("CERT");
        cfg.EmailAttributeName.Should().Be("email");
        cfg.DisplayNameAttributeName.Should().Be("displayName");
        cfg.JitProvisioning.Should().BeFalse();
        cfg.DefaultRoleId.Should().Be(roleId);
        cfg.Active.Should().BeFalse();
    }

    // ── WebAuthnCredential nullable properties ────────────────────────────────

    [Fact]
    public void WebAuthnCredential_DeviceNameAndLastUsedAt_ReadWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var cred = new WebAuthnCredential { DeviceName = "My Key", LastUsedAt = now };
        cred.DeviceName.Should().Be("My Key");
        cred.LastUsedAt.Should().Be(now);
    }

    // ── AppException constructors ─────────────────────────────────────────────

    [Fact]
    public void AppExceptions_AllTypes_CanBeInstantiated()
    {
        new BadRequestException("bad").Message.Should().Be("bad");
        new ConflictException("conflict").Message.Should().Be("conflict");
        new RateLimitException("rate").Message.Should().Be("rate");
        new UnauthorizedException("unauth").Message.Should().Be("unauth");
    }

    // ── AppConfig property getters ────────────────────────────────────────────

    [Fact]
    public void AppConfig_AllUncoveredProperties_ReturnExpectedDefaults()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]           = "Host=localhost;Database=test",
                ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                ["App:Domain"]                          = "localhost",
                ["IAM_PUBLIC_PORT"]                     = "5000",
                ["IAM_ADMIN_PORT"]                      = "5001",
                ["IAM_ADMIN_PATH"]                      = "/myadmin",
                ["IAM_BOOTSTRAP_PASSWORD"]              = "secret",
                ["Smtp:Username"]                       = "smtpuser",
                ["Smtp:Password"]                       = "smtppass",
                ["Social:GithubUserApiUrl"]             = "https://api.github.com/user",
                ["Social:GithubEmailsApiUrl"]           = "https://api.github.com/user/emails",
            })
            .Build());

        cfg.PublicPort.Should().Be(5000);
        cfg.AdminPath.Should().Be("/myadmin");
        cfg.BootstrapPassword.Should().Be("secret");
        cfg.SmtpUsername.Should().Be("smtpuser");
        cfg.SmtpPassword.Should().Be("smtppass");
        cfg.GithubUserApiUrl.Should().Be("https://api.github.com/user");
        cfg.GithubEmailsApiUrl.Should().Be("https://api.github.com/user/emails");
    }

    // ── Production StubEmailService (NotificationService.cs) ─────────────────

    [Fact]
    public async Task StubEmailService_SendOtp_CompletesWithoutException()
    {
        var svc = new RediensIAM.Services.StubEmailService(NullLogger<RediensIAM.Services.StubEmailService>.Instance);
        var act = async () => await svc.SendOtpAsync("to@example.com", "123456", "registration", null, null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StubEmailService_SendInvite_CompletesWithoutException()
    {
        var svc = new RediensIAM.Services.StubEmailService(NullLogger<RediensIAM.Services.StubEmailService>.Instance);
        var act = async () => await svc.SendInviteAsync("to@example.com", "https://example.com/invite", "TestOrg");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StubEmailService_SendNewDeviceAlert_CompletesWithoutException()
    {
        var svc = new RediensIAM.Services.StubEmailService(NullLogger<RediensIAM.Services.StubEmailService>.Instance);
        var act = async () => await svc.SendNewDeviceAlertAsync("to@example.com", "127.0.0.1", "Chrome/100", DateTimeOffset.UtcNow);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StubEmailService_CheckConnectivity_CompletesWithoutException()
    {
        var svc = new RediensIAM.Services.StubEmailService(NullLogger<RediensIAM.Services.StubEmailService>.Instance);
        await svc.CheckConnectivityAsync();
    }

    // ── SmtpEmailService — no-SMTP-configured no-op paths ────────────────────

    private static SmtpEmailService BuildSmtpService()
    {
        var opts = new DbContextOptionsBuilder<RediensIamDbContext>().Options;
        var db   = new RediensIamDbContext(opts);
        var cfg  = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]        = "Host=localhost;Database=unused",
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
                ["IAM_PUBLIC_PORT"]                  = "5000",
                ["IAM_ADMIN_PORT"]                   = "5001",
                // Smtp:Host intentionally omitted → empty → triggers no-op path
            })
            .Build());
        return new SmtpEmailService(cfg, db, NullLogger<SmtpEmailService>.Instance);
    }

    [Fact]
    public async Task SmtpEmailService_SendOtp_NoSmtpConfigured_ReturnsNoOp()
    {
        var svc = BuildSmtpService();
        var act = async () => await svc.SendOtpAsync("to@example.com", "123456", "registration");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SmtpEmailService_SendInvite_NoSmtpConfigured_ReturnsNoOp()
    {
        var svc = BuildSmtpService();
        var act = async () => await svc.SendInviteAsync("to@example.com", "https://example.com/invite", "TestOrg");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SmtpEmailService_SendNewDeviceAlert_NoSmtpConfigured_ReturnsNoOp()
    {
        var svc = BuildSmtpService();
        var act = async () => await svc.SendNewDeviceAlertAsync("to@example.com", "127.0.0.1", "Chrome/100", DateTimeOffset.UtcNow);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SmtpEmailService_CheckConnectivity_NoSmtpConfigured_Throws()
    {
        var svc = BuildSmtpService();
        var act = async () => await svc.CheckConnectivityAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SMTP not configured");
    }

    // ── Production StubSmsService (NotificationService.cs) ───────────────────

    [Fact]
    public async Task StubSmsService_SendOtp_CompletesWithoutException()
    {
        var svc = new RediensIAM.Services.StubSmsService(NullLogger<RediensIAM.Services.StubSmsService>.Instance);
        var act = async () => await svc.SendOtpAsync("+15551234567", "654321", "registration");
        await act.Should().NotThrowAsync();
    }
}
