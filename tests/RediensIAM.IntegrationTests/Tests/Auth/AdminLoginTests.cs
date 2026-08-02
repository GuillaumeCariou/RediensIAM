using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests for the AdminLogin path in AuthController.
/// POST /auth/login when Hydra returns client_id = "client_admin_system".
/// Covers AuthController.cs lines 887-934.
/// </summary>
[Collection("RediensIAM")]
public class AdminLoginTests(TestFixture fixture)
{
    private const string AdminPassword = "Admin@Test123!";

    /// <summary>Creates an immovable system-level user list (OrgId=null) and a user inside it.</summary>
    private async Task<(UserList list, User user)> CreateSystemUserAsync(string password = AdminPassword)
    {
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}",
            OrgId     = null,       // system-level: not org-scoped
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id, password: password);
        return (list, user);
    }

    private string NewAdminChallenge()
    {
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "client_admin_system");
        return challenge;
    }

    // ── Wrong password (lines 911-916) ───────────────────────────────────────

    [Fact]
    public async Task AdminLogin_WrongPassword_Returns401AndIncrementsFailedCount()
    {
        var (_, user) = await CreateSystemUserAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "WRONG_PASSWORD"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    // ── Success as super_admin (lines 926-933) ───────────────────────────────

    /// <summary>
    /// Once the deployment has left its bootstrap, credentials alone never complete an admin
    /// login: an account with no factor is sent through enrolment, and one with a factor is
    /// challenged. Neither answers `redirect_to` — the login is accepted at the far side of it.
    ///
    /// <para>
    /// This used to depend on `Security:RequireAdminMfa` being on in the fixture. The flag is
    /// gone; what makes enrolment mandatory now is that some other administrator already has a
    /// factor, so the test sets one up rather than setting a key. The first-administrator case it
    /// used to hide is covered in <c>AdminMfaBootstrapTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AdminLogin_SuperAdmin_WithNoFactor_RequiresEnrolment()
    {
        var (list, user) = await CreateSystemUserAsync();
        var enrolled = await fixture.Seed.CreateUserAsync(list.Id, password: AdminPassword);
        enrolled.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();  // super_admin check → true

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa_setup").GetBoolean().Should().BeTrue();
        body.TryGetProperty("redirect_to", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AdminLogin_SuperAdmin_WithAFactor_IsChallengedForIt()
    {
        var (_, user) = await CreateSystemUserAsync();
        user.TotpEnabled = true;
        user.TotpSecret  = RediensIAM.Services.TotpEncryption.Encrypt(
            fixture.GetService<RediensIAM.Config.AppConfig>().TotpEncKey,
            OtpNet.KeyGeneration.GenerateRandomKey(20));
        await fixture.Db.SaveChangesAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.TryGetProperty("requires_mfa_setup", out _).Should().BeFalse();
    }

    // ── Success as org_admin only (line 920: hasOrgAdmin branch) ────────────

    [Fact]
    public async Task AdminLogin_OrgAdminNotSuperAdmin_PassesTheRoleCheck()
    {
        var (_, user) = await CreateSystemUserAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();
        // Deny super_admin check → falls through to hasOrgAdmin
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{user.Id}");
        // HasAnyRelationAsync calls the list endpoint — simulate it returning a relation for this user
        fixture.Keto.SimulateRelationExists($"user:{user.Id}");

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        // Past the insufficient_role gate — which is the whole subject here. Which branch the login
        // lands in afterwards is the MFA rule's business, and pinning it made this test fail when
        // that rule changed even though the role check was untouched. AdminMfaBootstrapTests owns
        // that behaviour.
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("error", out var error).Should().BeFalse(
            error.ValueKind == JsonValueKind.String ? error.GetString()! : "the role check refused an org admin");
    }

    // ── No roles at all (lines 923-924) ─────────────────────────────────────

    [Fact]
    public async Task AdminLogin_NoAdminRoles_Returns401InsufficientRole()
    {
        var (_, user) = await CreateSystemUserAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.DenyAll();  // all Keto checks → false

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("insufficient_role");
    }

    // ── User not found in system list (line 900-903) ─────────────────────────

    [Fact]
    public async Task AdminLogin_UnknownEmail_Returns401()
    {
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = "nonexistent-admin@test.com",
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    // ── Locked account (lines 906-907) ───────────────────────────────────────

    [Fact]
    public async Task AdminLogin_LockedAccount_Returns401AccountLocked()
    {
        var (_, user) = await CreateSystemUserAsync();
        var challenge = NewAdminChallenge();
        fixture.Keto.AllowAll();

        user.LockedUntil = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Db.SaveChangesAsync();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = AdminPassword
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("account_locked");
    }
}
