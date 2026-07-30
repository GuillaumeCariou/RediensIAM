using OtpNet;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// R-24 + T-N2 — silent MFA factor takeover.
///
/// <c>POST /account/mfa/totp/confirm</c> overwrote an existing TOTP secret and reissued the
/// backup codes with nothing but a valid access token. An attacker holding a stolen token
/// enrolled their own authenticator, and the victim's factor stopped working with no audit
/// record anywhere: <c>AccountController</c> had exactly one <c>audit.RecordAsync</c> call, for
/// password change. The takeover also outlived the victim's remediation — <c>ChangePassword</c>
/// revokes Hydra sessions but never touches the TOTP secret.
///
/// Every mutation of an existing factor now requires re-authentication and writes an audit row.
/// </summary>
[Collection("RediensIAM")]
public class MfaTakeoverRegressionTests(TestFixture fixture)
{
    private const string Password = "P@ssw0rd!Test";

    private async Task<(User User, HttpClient Client, Guid OrgId)> ScaffoldAsync(bool withTotp)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id, password: Password);
        if (withTotp)
        {
            var appConfig = fixture.GetService<AppConfig>();
            user.TotpSecret  = TotpEncryption.Encrypt(appConfig.TotpEncKey, KeyGeneration.GenerateRandomKey(20));
            user.TotpEnabled = true;
            await fixture.Db.SaveChangesAsync();
        }

        return (user, fixture.ClientWithToken(fixture.Seed.UserToken(user.Id, org.Id, project.Id)), org.Id);
    }

    private async Task<bool> HasAuditAsync(Guid userId, string action) =>
        await fixture.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.ActorId == userId && a.Action == action);

    // ── R-24: a bearer token alone must not replace a live factor ────────────

    [Fact]
    public async Task ConfirmTotp_OverExistingFactor_WithoutReauth_IsRefusedAndSecretSurvives()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: true);
        var originalSecret = user.TotpSecret;

        var setup = await client.PostAsync("/account/mfa/totp/setup", null);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var newSecret = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var code = new Totp(Base32Encoding.ToBytes(newSecret)).ComputeTotp();

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("reauthentication_required");

        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.TotpSecret.Should().Be(originalSecret,
            "a stolen access token must not be able to replace the victim's second factor");
    }

    [Fact]
    public async Task ConfirmTotp_OverExistingFactor_WithCurrentPassword_Succeeds()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: true);
        var originalSecret = user.TotpSecret;

        var setup = await client.PostAsync("/account/mfa/totp/setup", null);
        var newSecret = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var code = new Totp(Base32Encoding.ToBytes(newSecret)).ComputeTotp();

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm",
            new { code, reauth = new { current_password = Password } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.TotpSecret.Should().NotBe(originalSecret);
        (await HasAuditAsync(user.Id, "user.mfa.totp_replaced")).Should().BeTrue();
    }

    /// <summary>First enrolment is not a takeover — it must stay a one-step flow.</summary>
    [Fact]
    public async Task ConfirmTotp_FirstEnrolment_NeedsNoReauth()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: false);

        var setup = await client.PostAsync("/account/mfa/totp/setup", null);
        var secret = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await HasAuditAsync(user.Id, "user.mfa.totp_enabled")).Should().BeTrue();
    }

    [Fact]
    public async Task RegenerateBackupCodes_WithoutReauth_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var (_, client, _) = await ScaffoldAsync(withTotp: true);

        var res = await client.PostAsJsonAsync("/account/mfa/backup-codes", new { });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegenerateBackupCodes_WithCurrentPassword_SucceedsAndIsAudited()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: true);

        var res = await client.PostAsJsonAsync("/account/mfa/backup-codes",
            new { current_password = Password });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await HasAuditAsync(user.Id, "user.mfa.backup_codes_regenerated")).Should().BeTrue();
    }

    [Fact]
    public async Task RemovePhone_OnAVerifiedFactor_WithoutReauth_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: false);
        user.Phone = "+33600000000";
        user.PhoneVerified = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/account/mfa/phone")
        {
            Content = JsonContent.Create(new { }),
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).PhoneVerified.Should().BeTrue();
    }

    [Fact]
    public async Task DeletePasskey_WithoutReauth_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: false);
        var cred = new WebAuthnCredential
        {
            Id = Guid.NewGuid(), UserId = user.Id, CredentialId = [1, 2, 3],
            PublicKey = [4, 5, 6], SignCount = 0, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.WebAuthnCredentials.Add(cred);
        user.WebAuthnEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/account/mfa/webauthn/credentials/{cred.Id}")
            {
                Content = JsonContent.Create(new { }),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.WebAuthnCredentials.AsNoTracking().AnyAsync(c => c.Id == cred.Id)).Should().BeTrue();
    }

    // ── T-N2: MFA mutations leave a trace ────────────────────────────────────

    [Fact]
    public async Task TotpSetup_IsAudited_EvenBeforeAnythingIsPersisted()
    {
        await fixture.FlushCacheAsync();
        var (user, client, _) = await ScaffoldAsync(withTotp: true);

        await client.PostAsync("/account/mfa/totp/setup", null);

        (await HasAuditAsync(user.Id, "user.mfa.totp_setup_started")).Should().BeTrue(
            "an enrolment started against an account that already has TOTP is the first "
            + "observable step of a factor takeover");
    }
}
