using System.Security.Cryptography;
using System.Text;
using OtpNet;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Full end-to-end MFA login flows that require a live session cookie.
/// Each test creates its own fresh HttpClient so sessions never bleed across tests.
/// </summary>
[Collection("RediensIAM")]
public class MfaLoginFlowTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private static readonly byte[] TestEncKey =
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Convert.FromHexString(new string('0', 64)),
            32,
            info: Encoding.UTF8.GetBytes("rediensiam-totp-secret-v1"));

    private string BackupHash(string raw) =>
        fixture.GetService<PasswordService>().Hash(raw.ToUpperInvariant());

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── TOTP ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyTotp_ValidCode_ReturnsRedirectTo()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
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

        var code = new Totp(totpSecret).ComputeTotp();
        var res  = await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyTotp_InvalidCode_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
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

        var res  = await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code = "000000" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_totp");
    }

    [Fact]
    public async Task VerifyTotp_ReplayedCode_Returns401CodeAlreadyUsed()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        await fixture.Db.SaveChangesAsync();

        var code   = new Totp(totpSecret).ComputeTotp();
        var client = fixture.NewSessionClient();

        // First login + verify — succeeds, marks code as used in Redis
        var ch1 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch1, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch1,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });
        await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code });

        // Second login on the same client — fresh MFA session, same code
        var ch2 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch2, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch2,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });

        var res  = await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("code_already_used");
    }

    // ── Backup codes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyBackupCode_ValidCode_ReturnsRedirectTo()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;

        const string rawCode = "ABCDE-12345";
        fixture.Db.BackupCodes.Add(new BackupCode
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            CodeHash  = BackupHash(rawCode),
            CreatedAt = DateTimeOffset.UtcNow,
        });
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

        var res  = await client.PostAsJsonAsync("/auth/mfa/backup-codes/verify",
            new { code = rawCode });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyBackupCode_AlreadyUsed_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;

        const string rawCode = "USED-99999";
        fixture.Db.BackupCodes.Add(new BackupCode
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            CodeHash  = BackupHash(rawCode),
            UsedAt    = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });

        var res  = await client.PostAsJsonAsync("/auth/mfa/backup-codes/verify",
            new { code = rawCode });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [Fact]
    public async Task VerifyBackupCode_InvalidCode_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });

        var res  = await client.PostAsJsonAsync("/auth/mfa/backup-codes/verify",
            new { code = "WRONG-CODE" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [Fact]
    public async Task VerifyBackupCode_MarksCodeAsUsed()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;

        const string rawCode = "FRESH-11111";
        var backupCode = new BackupCode
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            CodeHash  = BackupHash(rawCode),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.BackupCodes.Add(backupCode);
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });
        await client.PostAsJsonAsync("/auth/mfa/backup-codes/verify", new { code = rawCode });

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.BackupCodes.FindAsync(backupCode.Id);
        updated!.UsedAt.Should().NotBeNull();
    }

    // ── SMS / Phone ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendSmsOtp_ValidSession_Returns200Sent()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user           = await fixture.Seed.CreateUserAsync(list.Id);
        user.PhoneVerified = true;
        user.Phone         = "+33600000001";
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });
        fixture.SmsStub.SentMessages.Clear();

        var res  = await client.PostAsJsonAsync("/auth/mfa/phone/send", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sent").GetBoolean().Should().BeTrue();
        fixture.SmsStub.SentMessages.Should().ContainSingle(m => m.To == user.Phone);
    }

    [Fact]
    public async Task VerifySmsOtp_ValidCode_ReturnsRedirectTo()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user           = await fixture.Seed.CreateUserAsync(list.Id);
        user.PhoneVerified = true;
        user.Phone         = "+33600000002";
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });
        fixture.SmsStub.SentMessages.Clear();
        await client.PostAsJsonAsync("/auth/mfa/phone/send", new { });

        var smsCode = fixture.SmsStub.SentMessages.Last().Code;
        var res     = await client.PostAsJsonAsync("/auth/mfa/phone/verify", new { code = smsCode });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifySmsOtp_InvalidCode_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user           = await fixture.Seed.CreateUserAsync(list.Id);
        user.PhoneVerified = true;
        user.Phone         = "+33600000003";
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email    = user.Email,
            password = "P@ssw0rd!Test"
        });
        await client.PostAsJsonAsync("/auth/mfa/phone/send", new { });

        var res  = await client.PostAsJsonAsync("/auth/mfa/phone/verify", new { code = "000000" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }
}
