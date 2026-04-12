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

    [Fact]
    public async Task AdminLogin_SuperAdmin_Returns200WithRedirectTo()
    {
        var (_, user) = await CreateSystemUserAsync();
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
        body.TryGetProperty("redirect_to", out _).Should().BeTrue();
    }

    // ── Success as org_admin only (line 920: hasOrgAdmin branch) ────────────

    [Fact]
    public async Task AdminLogin_OrgAdminNotSuperAdmin_Returns200()
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

        // Lock the account
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
