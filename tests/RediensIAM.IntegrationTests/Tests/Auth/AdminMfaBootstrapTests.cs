using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Whether an administrator may sign in without a second factor is derived, not configured.
///
/// <para>
/// It used to be <c>Security:RequireAdminMfa</c>, whose own documentation admitted the problem:
/// off is right for the first ten minutes of a deployment's life and wrong for the rest of it. A
/// setting whose correct value changes by itself after ten minutes is not a setting — and it was
/// dangerous in both directions. Left at false it leaves a <c>super_admin</c> on a password
/// forever; set true too early it locks the operator out of the console they need in order to
/// configure the SMTP or SMS that makes a factor deliverable in the first place.
/// </para>
///
/// <para>
/// The rule now closes itself: <b>the first administrator signs in without a factor, every one
/// after that must enrol.</b> The exception ends at the first enrolment, with nothing to remember
/// to turn back on.
/// </para>
///
/// <para>
/// The population is the members of the administrator's own system user list. A deployment has
/// exactly one (<c>__system__</c>, <c>OrgId IS NULL</c> and immovable), so that is the same set as
/// "the administrators of this deployment" — and naming it explicitly is what keeps this suite,
/// which shares one database across every test, from having each case decide the answer for the
/// next.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class AdminMfaBootstrapTests(TestFixture fixture)
{
    private const string AdminPassword = "Admin@Test123!";

    /// <summary>A fresh deployment-level list, so each test owns its own administrator population.</summary>
    private async Task<UserList> NewSystemListAsync()
    {
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}",
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        return list;
    }

    private string NewAdminChallenge()
    {
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "client_admin_system");
        return challenge;
    }

    private async Task<JsonElement> LoginAsync(User user)
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = NewAdminChallenge(),
            email           = user.Email,
            password        = AdminPassword,
        });
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── 1. The first administrator gets in ────────────────────────────────────

    [Fact]
    public async Task FirstAdmin_WithNoFactorAnywhere_SignsInOnAPasswordAlone()
    {
        var list  = await NewSystemListAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        fixture.Keto.AllowAll();

        var body = await LoginAsync(admin);

        body.TryGetProperty("requires_mfa_setup", out _).Should().BeFalse(
            "there is no console to reach and no SMTP to configure until somebody gets in once");
        body.TryGetProperty("redirect_to", out var redirect).Should().BeTrue();
        redirect.GetString().Should().NotBeNullOrEmpty();
    }

    // ── 2. …and then the exception closes behind them ─────────────────────────

    [Fact]
    public async Task TheSameAdmin_OnceEnrolled_IsChallengedForTheFactor()
    {
        var list  = await NewSystemListAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        fixture.Keto.AllowAll();

        admin.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var body = await LoginAsync(admin);

        body.TryGetProperty("requires_mfa", out var requires).Should().BeTrue();
        requires.GetBoolean().Should().BeTrue("a factor that exists is always demanded");
        body.TryGetProperty("redirect_to", out _).Should().BeFalse();
    }

    // ── 3. The second administrator has to enrol ──────────────────────────────

    [Fact]
    public async Task SecondAdmin_WithNoFactor_MustEnrolOnceAnyAdminHasOne()
    {
        var list     = await NewSystemListAsync();
        var enrolled = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        var newcomer = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        enrolled.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();
        fixture.Keto.AllowAll();

        var body = await LoginAsync(newcomer);

        body.TryGetProperty("requires_mfa_setup", out var setup).Should().BeTrue(
            "the deployment has left its bootstrap — somebody was able to enrol, so this one can too");
        setup.GetBoolean().Should().BeTrue();
        body.TryGetProperty("redirect_to", out _).Should().BeFalse();
    }

    // ── 4. It is an administrator rule and nothing else ───────────────────────

    /// <summary>
    /// Tenant users are governed by <c>Project.RequireMfa</c>, which is a tenant's decision about
    /// its own users. An enrolled administrator must not start demanding factors of them.
    /// </summary>
    [Fact]
    public async Task TenantLogin_IsUnaffectedByWhatAdministratorsHaveEnrolled()
    {
        var list  = await NewSystemListAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        admin.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var (org, _)   = await fixture.Seed.CreateOrgAsync();
        var project    = await fixture.Seed.CreateProjectAsync(org.Id);
        var tenantList = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = tenantList.Id;
        project.RequireMfa = false;
        await fixture.Db.SaveChangesAsync();
        var tenantUser = await fixture.Seed.CreateUserAsync(tenantList.Id, password: AdminPassword);

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, $"client_{project.Id}");
        fixture.Keto.AllowAll();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = tenantUser.Email,
            password        = AdminPassword,
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("requires_mfa_setup", out _).Should().BeFalse(
            "an administrator's enrolment says nothing about a tenant's own MFA policy");
    }
}
