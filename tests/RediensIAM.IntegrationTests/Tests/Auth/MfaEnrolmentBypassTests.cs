using System.Security.Cryptography;
using System.Text;
using OtpNet;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// The login-time TOTP enrolment endpoints must not be reachable by an account that already holds
/// a second factor.
///
/// <para>
/// The defect: <c>/auth/mfa/setup/totp/start</c> and <c>/confirm</c> authenticate off the
/// <c>mfa_pending_user</c> session key — which <c>SetMfaSession</c> also writes on the
/// <i>challenge</i> path, when the server has just told the caller "you still owe me a factor".
/// The <c>mfa_setup_required</c> flag that was meant to separate the two states is written twice
/// and read nowhere. So a caller who has proved only the password can enrol a fresh authenticator
/// over the victim's, which wipes and reissues the backup codes and completes the login.
/// <c>AccountController.ConfirmTotp</c> guards the identical operation with a re-authentication
/// check; this path had nothing.
/// </para>
///
/// <para>
/// The last test is the one that keeps a fix honest: enrolment must still work for an account that
/// has no factor at all, which is the case the endpoints exist for.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class MfaEnrolmentBypassTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private static readonly KeyRing TestEncKey =
        new(1, HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Convert.FromHexString(new string('0', 64)),
            32,
            info: Encoding.UTF8.GetBytes("rediensiam-totp-secret-v1")));

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldAsync(bool requireMfa = false)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa         = requireMfa;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    /// <summary>Signs in with the password alone and stops at the factor challenge.</summary>
    private async Task<(HttpClient Client, User User, byte[] Secret)> PasswordOnlySessionAsync()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var secret = RandomNumberGenerator.GetBytes(20);
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        user.TotpEnabled = true;
        user.TotpSecret  = TotpEncryption.Encrypt(TestEncKey, secret);
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        var login  = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requires_mfa").GetBoolean().Should().BeTrue();

        return (client, user, secret);
    }

    [Fact]
    public async Task StartTotpEnrolment_WhenTheAccountAlreadyHasAFactor_IsRefused()
    {
        var (client, _, _) = await PasswordOnlySessionAsync();

        var res = await client.PostAsJsonAsync("/auth/mfa/setup/totp/start", new { });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400,
            "a caller who has proved only the password must not be handed a fresh TOTP secret");
    }

    [Fact]
    public async Task ConfirmTotpEnrolment_WhenTheAccountAlreadyHasAFactor_LeavesTheSecretIntact()
    {
        var (client, user, originalSecret) = await PasswordOnlySessionAsync();

        // The attack: ask for a secret, then confirm it with a code from *that* secret.
        var start = await client.PostAsJsonAsync("/auth/mfa/setup/totp/start", new { });
        if (start.StatusCode == HttpStatusCode.OK)
        {
            var issued = (await start.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("secret").GetString()!;
            var code = new Totp(Base32Encoding.ToBytes(issued)).ComputeTotp();

            var confirm = await client.PostAsJsonAsync("/auth/mfa/setup/totp/confirm", new { code });
            ((int)confirm.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        }

        // Whatever the endpoints answered, the enrolled factor must be the one the user owns.
        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.Users.FindAsync(user.Id);
        TotpEncryption.Decrypt(TestEncKey, after!.TotpSecret!)
            .Should().Equal(originalSecret, "the victim's authenticator must still be the enrolled one");
    }

    /// <summary>
    /// The case the endpoints exist for: a project that demands MFA, and an account with no factor.
    /// A fix that closed the bypass by refusing everyone would break first-time enrolment.
    /// </summary>
    [Fact]
    public async Task StartTotpEnrolment_WhenTheAccountHasNoFactor_IsAllowed()
    {
        var (org, project, list) = await ScaffoldAsync(requireMfa: true);
        await fixture.FlushCacheAsync();

        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        var login  = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });
        (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requires_mfa_setup").GetBoolean().Should().BeTrue();

        var res = await client.PostAsJsonAsync("/auth/mfa/setup/totp/start", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
