using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

[Collection("RediensIAM")]
public class LoginTests(TestFixture fixture) : IAsyncLifetime
{
    // Flush Dragonfly before every login test so the IP rate limiter starts clean.
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync()    => Task.CompletedTask;
    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Organisation org, Project project, UserList list, User user)> ScaffoldAsync(
        string password = "P@ssw0rd!Test")
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);

        // Assign user list to project
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id, password: password);
        return (org, project, list, user);
    }

    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    // ── GET /auth/login ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogin_ValidChallenge_Returns200WithLoginInfo()
    {
        var (_, project, _, _) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), project.OrgId.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("project_id").GetString().Should().Be(project.Id.ToString());
    }

    [Fact]
    public async Task GetLogin_InvalidChallenge_Returns400()
    {
        var res = await fixture.Client.GetAsync("/auth/login?login_challenge=invalid-challenge-xyz");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLogin_SkipTrue_RedirectsImmediately()
    {
        var (_, project, _, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), project.OrgId.ToString(),
            skip: true, subject: $"{project.OrgId}:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        // skip=true → server auto-accepts and redirects (AllowAutoRedirect=false → 3xx)
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(200).And.BeLessThan(400);
    }

    // ── POST /auth/login — happy path ─────────────────────────────────────────

    [Fact]
    public async Task Login_ValidCredentials_ReturnsRedirectTo()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        fixture.Hydra.ResetLog();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
        fixture.Hydra.LoginWasAccepted(challenge).Should().BeTrue();
    }

    [Fact]
    public async Task Login_ValidCredentials_ResetsFailedLoginCount()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        user.FailedLoginCount = 2;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.FailedLoginCount.Should().Be(0);
    }

    // ── POST /auth/login — failure paths ──────────────────────────────────────

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "WrongPassword!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task Login_WrongPassword_IncrementsFailedCount()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "WrongPassword!"
        });

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401()
    {
        var (org, project, _, _) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = "nobody@nowhere.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_InactiveUser_Returns401()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var inactiveUser = await fixture.Seed.CreateUserAsync(list.Id, active: false);
        var challenge    = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = inactiveUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_LockedAccount_Returns401WithLockedError()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var lockedUser = await fixture.Seed.CreateUserAsync(list.Id);
        lockedUser.LockedUntil = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = lockedUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("account_locked");
    }

    [Fact]
    public async Task Login_ExpiredLock_Succeeds()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var lockedUser = await fixture.Seed.CreateUserAsync(list.Id);
        lockedUser.LockedUntil = DateTimeOffset.UtcNow.AddHours(-1); // expired
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = lockedUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ExceedsMaxAttempts_LocksAccount()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);

        // Submit 5 wrong passwords (MaxLoginAttempts = 5 per TestFixture config)
        for (var i = 0; i < 5; i++)
        {
            var ch = NewChallenge();
            fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
                project.Id.ToString(), org.Id.ToString());
            await fixture.Client.PostAsJsonAsync("/auth/login", new
            {
                login_challenge = ch,
                email           = targetUser.Email,
                password        = "Wrong!"
            });
        }

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(targetUser.Id);
        updated!.LockedUntil.Should().NotBeNull();
        updated.LockedUntil.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_InvalidChallenge_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = "nonexistent-challenge",
            email           = "test@test.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_challenge");
    }

    [Fact]
    public async Task Login_ProjectNotReady_Returns400()
    {
        var (org, project, _, _) = await ScaffoldAsync();
        // Remove user list assignment
        project.AssignedUserListId = null;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = "any@test.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }

    // ── POST /auth/login — require role ───────────────────────────────────────

    [Fact]
    public async Task Login_RequireRoleEnabled_UserWithoutRole_RejectsLogin()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        project.RequireRoleToLogin = true;
        await fixture.Db.SaveChangesAsync();
        var userNoRole = await fixture.Seed.CreateUserAsync(list.Id);

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        fixture.Hydra.ResetLog();

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = userNoRole.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Found);
        fixture.Hydra.LoginWasRejected(challenge).Should().BeTrue();
    }

    // ── POST /auth/login — MFA ────────────────────────────────────────────────

    [Fact]
    public async Task Login_TotpEnabled_ReturnsMfaRequired()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var mfaUser = await fixture.Seed.CreateUserAsync(list.Id);
        mfaUser.TotpEnabled = true;
        mfaUser.TotpSecret  = "ENCRYPTEDTESTSECRET"; // opaque to this test
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = mfaUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfa_type").GetString().Should().Be("totp");
    }

    // ── Username login ────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidUsername_Succeeds()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            username        = $"{user.Username}#{user.Discriminator}",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_ValidCredentials_SetsLastLoginAt()
    {
        var (org, project, _, user) = await ScaffoldAsync();
        var before    = DateTimeOffset.UtcNow.AddSeconds(-1);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.LastLoginAt.Should().NotBeNull();
        updated.LastLoginAt.Should().BeAfter(before);
    }

    [Fact]
    public async Task Login_SmsEnabled_ReturnsMfaRequired()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var smsUser = await fixture.Seed.CreateUserAsync(list.Id);
        smsUser.PhoneVerified = true;
        smsUser.Phone         = "+33600000001";
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = smsUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfa_type").GetString().Should().Be("sms");
    }

    [Fact]
    public async Task Login_WebAuthnEnabled_ReturnsMfaRequired()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        var waUser = await fixture.Seed.CreateUserAsync(list.Id);
        waUser.WebAuthnEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = waUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfa_type").GetString().Should().Be("webauthn");
    }

    [Fact]
    public async Task Login_RequireRole_UserWithRole_Succeeds()
    {
        var (org, project, list, _) = await ScaffoldAsync();
        project.RequireRoleToLogin = true;
        await fixture.Db.SaveChangesAsync();
        var roleUser = await fixture.Seed.CreateUserAsync(list.Id);
        var role     = await fixture.Seed.CreateRoleAsync(project.Id);
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            UserId    = roleUser.Id,
            ProjectId = project.Id,
            RoleId    = role.Id,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = roleUser.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("redirect_to", out var redirect).Should().BeTrue();
        redirect.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetLogin_AdminClientId_ReturnsIsAdminLoginTrue()
    {
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallenge(challenge, "client_admin_system");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("is_admin_login").GetBoolean().Should().BeTrue();
    }
}
