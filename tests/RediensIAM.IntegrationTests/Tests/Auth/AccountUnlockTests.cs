using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// B2: Dedicated admin unlock endpoints.
/// B3: Mandatory MFA per project.
/// </summary>
[Collection("RediensIAM")]
public class AccountUnlockTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    // ── B2: POST /admin/users/{id}/unlock ─────────────────────────────────────

    [Fact]
    public async Task AdminUnlock_LockedUser_Returns200AndClearsLock()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user         = await fixture.Seed.CreateUserAsync(orgList.Id);
        user.LockedUntil      = DateTimeOffset.UtcNow.AddHours(1);
        user.FailedLoginCount = 5;
        await fixture.Db.SaveChangesAsync();

        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/admin/users/{user.Id}/unlock", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("user_unlocked");

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.LockedUntil.Should().BeNull();
        updated.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task AdminUnlock_NonExistentUser_Returns404()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/unlock", new { });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminUnlock_Unauthenticated_Returns401Or403()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);

        var res = await fixture.Client.PostAsJsonAsync($"/admin/users/{user.Id}/unlock", new { });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── B2: POST /org/userlists/{id}/users/{uid}/unlock ───────────────────────

    [Fact]
    public async Task OrgUnlock_LockedUser_Returns200AndClearsLock()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list   = await fixture.Seed.CreateUserListAsync(org.Id);
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        user.LockedUntil      = DateTimeOffset.UtcNow.AddHours(2);
        user.FailedLoginCount = 3;
        await fixture.Db.SaveChangesAsync();

        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}/unlock", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.LockedUntil.Should().BeNull();
        updated.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task OrgUnlock_UserInOtherOrg_Returns404()
    {
        var (org, orgList)       = await fixture.Seed.CreateOrgAsync();
        var (otherOrg, _)        = await fixture.Seed.CreateOrgAsync();
        var otherList            = await fixture.Seed.CreateUserListAsync(otherOrg.Id);
        var foreignUser          = await fixture.Seed.CreateUserAsync(otherList.Id);
        foreignUser.LockedUntil  = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Db.SaveChangesAsync();

        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync(
            $"/org/userlists/{otherList.Id}/users/{foreignUser.Id}/unlock", new { });

        // Can't see the other org's list → 404
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── B3: Mandatory MFA per project ─────────────────────────────────────────

    [Fact]
    public async Task Login_RequireMfa_UserWithoutMfa_Returns200RequiresMfaSetup()
    {
        await fixture.FlushCacheAsync();
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa         = true;   // B3 flag
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        // user has no TOTP, no phone, no WebAuthn

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.NewSessionClient().PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa_setup").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Login_RequireMfa_UserWithTotp_ProceedsToMfaChallenge()
    {
        await fixture.FlushCacheAsync();
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa         = true;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        // Give the user a TOTP method
        var encKey        = Convert.FromHexString(new string('0', 64));
        user.TotpEnabled  = true;
        user.TotpSecret   = RediensIAM.Services.TotpEncryption.Encrypt(encKey, new byte[20]);
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        var res    = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // Should go to normal MFA challenge, not setup
        body.TryGetProperty("requires_mfa_setup", out _).Should().BeFalse();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfa_type").GetString().Should().Be("totp");
    }

    [Fact]
    public async Task Login_RequireMfaFalse_UserWithoutMfa_LogsInNormally()
    {
        await fixture.FlushCacheAsync();
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa         = false;  // default — no MFA enforcement
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.NewSessionClient().PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // Should get redirect_to directly — no MFA setup required
        body.TryGetProperty("requires_mfa_setup", out _).Should().BeFalse();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    // ── B3: RequireMfa persists via PATCH ─────────────────────────────────────

    [Fact]
    public async Task AdminPatchProject_SetRequireMfa_Persists()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var (org2, _)    = await fixture.Seed.CreateOrgAsync();
        var project      = await fixture.Seed.CreateProjectAsync(org2.Id);
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client       = fixture.ClientWithToken(token);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { require_mfa = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.RequireMfa.Should().BeTrue();
    }
}
