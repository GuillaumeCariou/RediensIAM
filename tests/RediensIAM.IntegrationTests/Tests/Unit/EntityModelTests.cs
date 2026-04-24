using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
using RediensIAM.Controllers;
using RediensIAM.Data;
using RediensIAM.Exceptions;
using RediensIAM.Filters;
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

    // ── SamlIdpConfig.DefaultRole navigation property ─────────────────────────

    [Fact]
    public void SamlIdpConfig_DefaultRole_ReadWrite()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "admin" };
        var cfg  = new SamlIdpConfig { DefaultRole = role };
        cfg.DefaultRole.Should().BeSameAs(role);
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

    // ── AppConfig — null-fallback branches (config keys omitted → use defaults) ─

    [Fact]
    public void AppConfig_OptionalKeys_Omitted_UsesDefaults()
    {
        // Create AppConfig with only the mandatory keys.
        // Accessing optional properties covers the null-fallback branch of every ?? operator.
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]           = "Host=localhost;Database=test",
                ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                ["App:Domain"]                          = "localhost",
                ["IAM_PUBLIC_PORT"]                     = "5000",
                ["IAM_ADMIN_PORT"]                      = "5001",
            })
            .Build());

        cfg.AdminPath.Should().Be("/admin");
        cfg.CacheConnectionString.Should().Contain("localhost");
        cfg.CacheInstanceName.Should().Be("rediensiam:");
        cfg.PublicUrl.Should().Be("http://localhost");
        cfg.SmtpFromName.Should().Be("RediensIAM");
        cfg.SmtpFromAddress.Should().Be("noreply@localhost");
        cfg.PatPrefix.Should().Be("rediens_pat_");
        cfg.HydraAdminUrl.Should().Be("http://rediensiam-hydra-admin:4445");
        cfg.HydraPublicUrl.Should().Be("http://rediensiam-hydra-public:4444");
        cfg.KetoReadUrl.Should().Be("http://rediensiam-keto-read:4466");
        cfg.KetoWriteUrl.Should().Be("http://rediensiam-keto-write:4467");
        cfg.GithubUserApiUrl.Should().Be("https://api.github.com/user");
        cfg.GithubEmailsApiUrl.Should().Be("https://api.github.com/user/emails");
    }

    [Fact]
    public void AppConfig_AdminSpaOrigin_Omitted_FallsBackToPublicUrl()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]           = "Host=localhost;Database=test",
                ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                ["App:Domain"]                          = "localhost",
                // App:AdminSpaOrigin not set → falls back to PublicUrl
                // App:PublicUrl also not set → falls back to "http://localhost"
            })
            .Build());

        cfg.AdminSpaOrigin.Should().Be("http://localhost");
    }

    [Fact]
    public void AppConfig_AdminSpaOrigin_Set_ReturnsConfigValue()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"]           = "Host=localhost;Database=test",
                ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                ["App:Domain"]                          = "localhost",
                ["App:AdminSpaOrigin"]                  = "https://admin.example.com",
            })
            .Build());

        cfg.AdminSpaOrigin.Should().Be("https://admin.example.com");
    }

    [Fact]
    public void AppConfig_ConnectionString_Missing_Throws()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
            })
            .Build());

        var act = () => cfg.ConnectionString;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Default*");
    }

    [Fact]
    public void AppConfig_Domain_Missing_Throws()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
            })
            .Build());

        var act = () => cfg.Domain;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*App:Domain*");
    }

    [Fact]
    public void AppConfig_TotpSecretEncryptionKey_Missing_Throws()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:Domain"] = "localhost",
            })
            .Build());

        var act = () => cfg.TotpSecretEncryptionKey;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TotpSecretEncryptionKey*");
    }

    // ── TotpEncryption — branch coverage for StripSecretsFromTheme ────────────

    [Fact]
    public void TotpEncryption_StripSecretsFromTheme_NullTheme_ReturnsNull()
    {
        TotpEncryption.StripSecretsFromTheme(null).Should().BeNull();
    }

    [Fact]
    public void TotpEncryption_StripSecretsFromTheme_NoProvidersKey_ReturnsTheme()
    {
        var theme = new Dictionary<string, object> { ["color"] = "red" };
        TotpEncryption.StripSecretsFromTheme(theme).Should().BeSameAs(theme);
    }

    [Fact]
    public void TotpEncryption_StripSecretsFromTheme_ProvidersNotJsonElement_ReturnsTheme()
    {
        // raw is not a JsonElement → returns theme unchanged
        var theme = new Dictionary<string, object> { ["providers"] = new List<string> { "oauth2" } };
        TotpEncryption.StripSecretsFromTheme(theme).Should().BeSameAs(theme);
    }

    [Fact]
    public void TotpEncryption_StripSecretsFromTheme_ProvidersJsonElementNotArray_ReturnsTheme()
    {
        // raw IS a JsonElement but ValueKind != Array → returns theme unchanged
        var doc = JsonDocument.Parse("{\"providers\": \"not-array\"}");
        var theme = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        TotpEncryption.StripSecretsFromTheme(theme).Should().BeSameAs(theme);
    }

    [Fact]
    public void TotpEncryption_StripSecretsFromTheme_ProvidersArray_StripsSecrets()
    {
        var doc = JsonDocument.Parse("""
            {"providers": [{"id": "gh", "client_id": "abc", "client_secret": "s3cr3t", "client_secret_enc": "enc"}]}
            """);
        var theme = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        var result = TotpEncryption.StripSecretsFromTheme(theme);
        result.Should().NotBeNull();
        var providers = result!["providers"] as List<object>;
        providers.Should().NotBeNull();
        var first = providers![0] as Dictionary<string, object>;
        first.Should().ContainKey("id");
        first.Should().NotContainKey("client_secret");
        first.Should().NotContainKey("client_secret_enc");
    }

    // ── TotpEncryption — branch coverage for EncryptProviderSecretsInTheme ────

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_NullIncoming_ReturnsNull()
    {
        var key = Convert.FromHexString(new string('0', 64));
        TotpEncryption.EncryptProviderSecretsInTheme(null, null, key).Should().BeNull();
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_NoProvidersKey_ReturnsIncoming()
    {
        var key     = Convert.FromHexString(new string('0', 64));
        var incoming = new Dictionary<string, object> { ["color"] = "blue" };
        TotpEncryption.EncryptProviderSecretsInTheme(incoming, null, key).Should().BeSameAs(incoming);
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ProvidersNotArray_ReturnsIncoming()
    {
        var key = Convert.FromHexString(new string('0', 64));
        var doc = JsonDocument.Parse("{\"providers\": \"not-array\"}");
        var incoming = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        TotpEncryption.EncryptProviderSecretsInTheme(incoming, null, key).Should().BeSameAs(incoming);
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ExistingNull_BuildsEmptyMap()
    {
        // existing is null → BuildExistingSecretsMap returns empty map
        var key = Convert.FromHexString(new string('0', 64));
        var doc = JsonDocument.Parse("""{"providers": [{"id": "gh", "client_id": "abc"}]}""");
        var incoming = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        var result = TotpEncryption.EncryptProviderSecretsInTheme(incoming, null, key);
        result.Should().NotBeNull();
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ExistingNoProviders_UsesEmptyMap()
    {
        // existing dict exists but has no "providers" key → BuildExistingSecretsMap returns empty
        var key = Convert.FromHexString(new string('0', 64));
        var doc = JsonDocument.Parse("""{"providers": [{"id": "gh", "client_id": "abc"}]}""");
        var incoming = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        var existing = new Dictionary<string, object> { ["color"] = "red" };
        var result = TotpEncryption.EncryptProviderSecretsInTheme(incoming, existing, key);
        result.Should().NotBeNull();
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ExistingProvidersNotArray_UsesEmptyMap()
    {
        // existing has "providers" but it's a non-array JsonElement
        var key = Convert.FromHexString(new string('0', 64));
        var inDoc = JsonDocument.Parse("""{"providers": [{"id": "gh"}]}""");
        var exDoc = JsonDocument.Parse("""{"providers": "not-array"}""");
        var incoming = new Dictionary<string, object>
        {
            ["providers"] = inDoc.RootElement.GetProperty("providers")
        };
        var existing = new Dictionary<string, object>
        {
            ["providers"] = exDoc.RootElement.GetProperty("providers")
        };
        var result = TotpEncryption.EncryptProviderSecretsInTheme(incoming, existing, key);
        result.Should().NotBeNull();
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ProviderNoId_NoSecret_LeavesDictClean()
    {
        // Provider has no "id" property → providerId is null → else-if skipped
        var key = Convert.FromHexString(new string('0', 64));
        var doc = JsonDocument.Parse("""{"providers": [{"client_id": "abc"}]}""");
        var incoming = new Dictionary<string, object>
        {
            ["providers"] = doc.RootElement.GetProperty("providers")
        };
        var result = TotpEncryption.EncryptProviderSecretsInTheme(incoming, null, key);
        result.Should().NotBeNull();
    }

    [Fact]
    public void TotpEncryption_EncryptProviderSecrets_ProviderHasId_ExistingSecretPreserved()
    {
        // Provider has id but no client_secret → falls to else-if → looks up existing secret
        var key = Convert.FromHexString(new string('0', 64));
        var plaintext = "original-secret";
        var existingEnc = TotpEncryption.EncryptString(key, plaintext);

        var inDoc = JsonDocument.Parse("""{"providers": [{"id": "gh", "client_id": "abc"}]}""");
        var exDoc = JsonDocument.Parse(
            $$"""{"providers": [{"id": "gh", "client_secret_enc": "{{existingEnc}}"}]}""");

        var incoming = new Dictionary<string, object>
        {
            ["providers"] = inDoc.RootElement.GetProperty("providers")
        };
        var existing = new Dictionary<string, object>
        {
            ["providers"] = exDoc.RootElement.GetProperty("providers")
        };

        var result = TotpEncryption.EncryptProviderSecretsInTheme(incoming, existing, key);
        result.Should().NotBeNull();
        var providers = result!["providers"] as List<object>;
        var first = providers![0] as Dictionary<string, object>;
        first.Should().ContainKey("client_secret_enc");
    }

    // ── AuditLogRetentionService — catch block (lines 22-23) + delay (line 25) ─

    [Fact]
    public async Task AuditLogRetentionService_DbFails_CatchesLogsThenDelayUntilCancel()
    {
        var cfg = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
                ["IAM_PUBLIC_PORT"]                  = "5000",
                ["IAM_ADMIN_PORT"]                   = "5001",
            })
            .Build());

        var scopeFactory = new AuditRetentionFailingScopeFactory();
        var svc = new AuditLogRetentionService(
            scopeFactory, cfg, NullLogger<AuditLogRetentionService>.Instance);

        using var cts = new CancellationTokenSource();

        // PurgeExpiredLogsAsync throws immediately (no DB provider).
        // The catch block runs (lines 22-23), then Task.Delay(24h) starts.
        // We cancel after 50ms — Task.Delay throws OperationCanceledException.
        _ = Task.Delay(50).ContinueWith(_ => cts.Cancel());

        var method = typeof(AuditLogRetentionService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(svc, [cts.Token])!;

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── WebhookDispatcherService.ExecuteAsync — normal exit when channel completed (line 109) ─

    [Fact]
    public async Task WebhookDispatcherService_ExecuteAsync_ExitsWhenChannelCompleted()
    {
        var ch = Channel.CreateUnbounded<WebhookJob>();
        ch.Writer.Complete();   // no items; ReadAllAsync returns immediately

        var svc = new WebhookDispatcherService(
            ch,
            new WebhookNoOpScopeFactory(),
            NullLogger<WebhookDispatcherService>.Instance,
            new WebhookNoOpHttpClientFactory(),
            BuildNoOpAppConfig(),
            new NoOpWebhookQueue(),
            new NoOpSsrfValidator());

        var method = typeof(WebhookDispatcherService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(svc, [CancellationToken.None])!;

        var act = async () => await task;
        await act.Should().NotThrowAsync();
    }

    // ── WebhookDispatcherService.ProcessJobAsync — RecordDelivery catch (lines 174-177) ─

    [Fact]
    public async Task WebhookDispatcherService_ProcessJobAsync_RecordDeliveryFails_LogsError()
    {
        var ch = Channel.CreateUnbounded<WebhookJob>();

        // HTTP client that always returns 200 so delivery "succeeds"
        var httpFactory = new WebhookSucceedingHttpClientFactory();
        // Scope factory that throws when WebhookService is requested
        var scopeFactory = new WebhookNoOpScopeFactory();

        var svc = new WebhookDispatcherService(
            ch,
            scopeFactory,
            NullLogger<WebhookDispatcherService>.Instance,
            httpFactory,
            BuildNoOpAppConfig(),
            new NoOpWebhookQueue(),
            new NoOpSsrfValidator());

        // Write a job and complete the channel
        await ch.Writer.WriteAsync(new WebhookJob(
            Guid.NewGuid(), "user.created", "{}", "secret", "http://localhost/hook"));
        ch.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var method = typeof(WebhookDispatcherService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(svc, [cts.Token])!;

        // ExecuteAsync completes (channel done); wait a bit for fire-and-forget ProcessJobAsync
        var act = async () => await task;
        await act.Should().NotThrowAsync();
        await Task.Delay(200); // let the background Task.Run complete (covers lines 174-177)
    }

    private static AppConfig BuildNoOpAppConfig() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
                ["App:Domain"]                       = "localhost",
                ["IAM_PUBLIC_PORT"]                  = "5000",
                ["IAM_ADMIN_PORT"]                   = "5001",
                ["Webhook:TimeoutSeconds"]            = "5",
            })
            .Build());

    // ── RequireManagementLevelAttribute — null claims path (lines 20-22) ─────

    [Fact]
    public void RequireManagementLevelAttribute_NullClaims_ReturnsUnauthorized()
    {
        var attr        = new RequireManagementLevelAttribute(ManagementLevel.OrgAdmin);
        var httpContext = new DefaultHttpContext();
        // No claims in Items → GetClaims() returns null

        var actionContext = new ActionContext(
            httpContext, new RouteData(), new ActionDescriptor());
        var execContext = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), controller: null!);

        attr.OnActionExecuting(execContext);

        execContext.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // ── WebhookDispatcherService — Redis recovery path (lines 143-152) ───────

    [Fact]
    public async Task WebhookDispatcherService_ExecuteAsync_RecoversPendingJobsFromQueue()
    {
        var job     = new WebhookJob(Guid.NewGuid(), "user.created", "{}", "", "http://localhost/hook");
        var jobJson = JsonSerializer.Serialize(job, new JsonSerializerOptions());
        var ch      = Channel.CreateUnbounded<WebhookJob>();

        var svc = new WebhookDispatcherService(
            ch,
            new WebhookNoOpScopeFactory(),
            NullLogger<WebhookDispatcherService>.Instance,
            new WebhookNoOpHttpClientFactory(),
            BuildNoOpAppConfig(),
            new SingleJobWebhookQueue(jobJson),
            new NoOpSsrfValidator());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var method = typeof(WebhookDispatcherService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task)method.Invoke(svc, [cts.Token])!;

        // Recovery writes job to channel → foreach loop waits for more → CancellationToken fires
        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── WebhookDispatcherService — SSRF blocked at delivery (lines 189-192) ──

    [Fact]
    public async Task WebhookDispatcherService_ProcessJobAsync_SsrfBlocked_DoesNotCallHttp()
    {
        var httpFactory = new TrackingHttpClientFactory();
        var ch = Channel.CreateUnbounded<WebhookJob>();
        await ch.Writer.WriteAsync(
            new WebhookJob(Guid.NewGuid(), "user.created", "{}", "", "http://127.0.0.1/hook"));
        ch.Writer.Complete();

        var svc = new WebhookDispatcherService(
            ch,
            new WebhookNoOpScopeFactory(),
            NullLogger<WebhookDispatcherService>.Instance,
            httpFactory,
            BuildNoOpAppConfig(),
            new NoOpWebhookQueue(),
            new WebhookSsrfValidator());

        var method = typeof(WebhookDispatcherService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(svc, [CancellationToken.None])!;
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);

        httpFactory.RequestCount.Should().Be(0, "SSRF block must prevent any HTTP delivery");
    }

    // ── HydraService.ParseNextPageToken (lines 167-181) via reflection ────────

    [Fact]
    public void HydraService_ParseNextPageToken_ValidNextLink_ReturnsToken()
    {
        var method = typeof(HydraService)
            .GetMethod("ParseNextPageToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        var link   = "<http://localhost/admin/clients?page_size=250&page_token=abc123>; rel=\"next\"";

        var result = (string?)method.Invoke(null, [link]);

        result.Should().Be("abc123");
    }

    [Fact]
    public void HydraService_ParseNextPageToken_NoPrevRelOnly_ReturnsNull()
    {
        var method = typeof(HydraService)
            .GetMethod("ParseNextPageToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        var link   = "<http://localhost/admin/clients?page=1>; rel=\"prev\"";

        var result = (string?)method.Invoke(null, [link]);

        result.Should().BeNull();
    }

    [Fact]
    public void HydraService_ParseNextPageToken_NextLinkWithoutPageToken_ReturnsNull()
    {
        var method = typeof(HydraService)
            .GetMethod("ParseNextPageToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        var link   = "<http://localhost/admin/clients?page_size=250>; rel=\"next\"";

        var result = (string?)method.Invoke(null, [link]);

        result.Should().BeNull();
    }

    [Fact]
    public void HydraService_ParseNextPageToken_ShortSegmentThenValidNext_SkipsShortAndReturnsToken()
    {
        var method = typeof(HydraService)
            .GetMethod("ParseNextPageToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        var link   = "nosegs, <http://localhost/admin/clients?page_token=tok99>; rel=\"next\"";

        var result = (string?)method.Invoke(null, [link]);

        result.Should().Be("tok99");
    }
}

// ── Stubs used by AuditLogRetentionService and WebhookDispatcherService tests ─

file sealed class AuditRetentionFailingScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new FailingScope();

    private sealed class FailingScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new FailingProvider();
        public void Dispose() { }
    }

    private sealed class FailingProvider : IServiceProvider
    {
        // Return null → GetRequiredService throws InvalidOperationException (no service registered)
        public object? GetService(Type serviceType) => null;
    }
}

file sealed class WebhookNoOpScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new NoOpScope();

    private sealed class NoOpScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new ThrowingProvider();
        public void Dispose() { }
    }

    private sealed class ThrowingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("Simulated scope failure");
    }
}

file sealed class WebhookNoOpHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

file sealed class WebhookSucceedingHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new SuccessHandler()) { BaseAddress = null };

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

file sealed class NoOpWebhookQueue : IWebhookQueue
{
    public Task PersistAsync(string jobJson, long score) => Task.CompletedTask;
    public Task<string[]> RecoverAllAsync() => Task.FromResult(Array.Empty<string>());
    public Task RemoveAsync(string jobJson) => Task.CompletedTask;
}

file sealed class NoOpSsrfValidator : IWebhookSsrfValidator
{
    public Task<bool> IsPrivateOrReservedAsync(string url) => Task.FromResult(false);
}

file sealed class SingleJobWebhookQueue(string jobJson) : IWebhookQueue
{
    public Task PersistAsync(string j, long score) => Task.CompletedTask;
    public Task<string[]> RecoverAllAsync() => Task.FromResult(new[] { jobJson });
    public Task RemoveAsync(string j) => Task.CompletedTask;
}

file sealed class TrackingHttpClientFactory : IHttpClientFactory
{
    public int RequestCount { get; private set; }

    public HttpClient CreateClient(string name) =>
        new(new TrackingHandler(this));

    private sealed class TrackingHandler(TrackingHttpClientFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

