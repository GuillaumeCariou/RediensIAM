using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using OtpNet;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;
using StackExchange.Redis;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController rate-limiter 429 branches and related uncovered lines:
///   - POST /auth/mfa/backup-codes/verify — rate-limited (line 327)
///   - POST /auth/mfa/phone/send          — rate-limited (line 370)
///   - POST /auth/mfa/phone/send          — phone not configured (line 373)
///   - POST /auth/mfa/phone/verify        — rate-limited (line 392)
///   - POST /auth/mfa/totp/verify         — rate-limited (line 433)
///   - POST /auth/invite/complete         — breached password (lines 776-779)
/// </summary>
[Collection("RediensIAM")]
public class AuthRateLimiterCoverageTests(TestFixture fixture)
{
    private static readonly KeyRing TestEncKey = new(1, Convert.FromHexString(new string('0', 64)));

    private static string BackupHash(string raw) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToUpperInvariant())));

    /// <summary>
    /// Directly sets the IP rate-limiter key to MaxLoginAttempts (5) so the
    /// next MFA call returns 429.  Caller must FlushCacheAsync() afterwards.
    /// </summary>
    private async Task BlockIpAsync()
    {
        var redis = fixture.GetService<IConnectionMultiplexer>();
        await redis.GetDatabase().StringSetAsync("rate:login:127.0.0.1", "5",
            TimeSpan.FromMinutes(15));
    }

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldProjectAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── TOTP — rate-limited (line 433) ───────────────────────────────────────

    [Fact]
    public async Task VerifyTotp_WhenRateLimited_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (org, project, list) = await ScaffoldProjectAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        // A successful login establishes the MFA session without charging the rate limiter, so the
        // block below is the only thing the MFA call can be refused for.
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await BlockIpAsync();
        try
        {
            var res = await client.PostAsJsonAsync("/auth/mfa/totp/verify",
                new { code = new Totp(totpSecret).ComputeTotp() });

            res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            await fixture.FlushCacheAsync();
        }
    }

    // ── Backup code — rate-limited (line 327) ────────────────────────────────

    [Fact]
    public async Task VerifyBackupCode_WhenRateLimited_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (org, project, list) = await ScaffoldProjectAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;

        const string rawCode = "RATE-99999";
        fixture.Db.BackupCodes.Add(new BackupCode
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            CodeHash  = BackupHash(rawCode),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await BlockIpAsync();
        try
        {
            var res = await client.PostAsJsonAsync("/auth/mfa/backup-codes/verify",
                new { code = rawCode });

            res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            await fixture.FlushCacheAsync();
        }
    }

    // ── SMS send — rate-limited (line 370) ───────────────────────────────────

    [Fact]
    public async Task SendSmsOtp_WhenRateLimited_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (org, project, list) = await ScaffoldProjectAsync();

        // User needs TOTP (not phone) to get an MFA session via login
        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await BlockIpAsync();
        try
        {
            var res = await client.PostAsync("/auth/mfa/phone/send", null);

            res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            await fixture.FlushCacheAsync();
        }
    }

    // ── SMS send — phone not configured (line 373) ───────────────────────────

    [Fact]
    public async Task SendSmsOtp_PhoneNotConfigured_Returns400()
    {
        await fixture.FlushCacheAsync();
        var (org, project, list) = await ScaffoldProjectAsync();

        // User with TOTP but no phone — MFA session is established via TOTP login
        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        // user.PhoneVerified = false (default) and user.Phone = null (default)
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        // Unlike the sibling tests the IP is deliberately not blocked, so a 429 here would mean
        // leakage from another test rather than the missing-phone refusal being asserted.
        var res = await client.PostAsync("/auth/mfa/phone/send", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("phone_not_configured");

        await fixture.FlushCacheAsync();
    }

    // ── SMS OTP verify — rate-limited (line 392) ─────────────────────────────

    [Fact]
    public async Task VerifySmsOtp_WhenRateLimited_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (org, project, list) = await ScaffoldProjectAsync();

        var totpSecret    = new byte[20];
        var encryptedTotp = TotpEncryption.Encrypt(TestEncKey, totpSecret);
        var user          = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled  = true;
        user.TotpSecret   = encryptedTotp;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await BlockIpAsync();
        try
        {
            var res = await client.PostAsJsonAsync("/auth/mfa/phone/verify",
                new { code = "123456" });

            res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            await fixture.FlushCacheAsync();
        }
    }

    // ── Invite complete — password not breached, breach check enabled (line 779) ──

    [Fact]
    public async Task InviteComplete_CheckBreachEnabled_CleanPassword_Completes()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId    = list.Id;
        project.CheckBreachedPasswords = true;
        await fixture.Db.SaveChangesAsync();

        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        fixture.EmailStub.SentInvites.Clear();
        var email = SeedData.UniqueEmail();
        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = (string?)null });

        var invite     = fixture.EmailStub.SentInvites.First(i => i.To == email);
        var inviteToken = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(new Uri(invite.InviteUrl).Query)["token"].ToString();

        // The HIBP stub reports 0 for anything not explicitly seeded, so with the breach check on
        // this password must still be accepted.
        var res = await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
            new { token = inviteToken, password = "CleanP@ss_NoBreach!999" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Invite complete — breached password (lines 776-779) ──────────────────

    [Fact]
    public async Task InviteComplete_BreachedPassword_Returns400()
    {
        const string breachedPassword = "BreachTest_P@ss_ForCoverage!";

        fixture.HibpStub.Setup(breachedPassword, count: 50);
        try
        {
            var (org, orgList) = await fixture.Seed.CreateOrgAsync();
            var list  = await fixture.Seed.CreateUserListAsync(org.Id);
            var admin = await fixture.Seed.CreateUserAsync(orgList.Id);

            // The breach check is per-project and off by default; without it the invite completes.
            var project = await fixture.Seed.CreateProjectAsync(org.Id);
            project.AssignedUserListId    = list.Id;
            project.CheckBreachedPasswords = true;
            await fixture.Db.SaveChangesAsync();

            var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
            fixture.Keto.AllowAll();
            var client = fixture.ClientWithToken(token);

            fixture.EmailStub.SentInvites.Clear();
            var email = SeedData.UniqueEmail();
            await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
                new { email, password = (string?)null });

            var invite     = fixture.EmailStub.SentInvites.First(i => i.To == email);
            var inviteToken = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                .ParseQuery(new Uri(invite.InviteUrl).Query)["token"].ToString();

            var res = await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
                new { token = inviteToken, password = breachedPassword });

            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("password_breached");
        }
        finally
        {
            fixture.HibpStub.Clear();
        }
    }
}
