using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController branches not yet hit by existing test files:
///   - GET  /auth/login — admin client (line 68), no projectId (line 72), invalid project (line 75)
///   - GET  /auth/login/theme — no projectId (line 108), project not found (line 111)
///   - POST /auth/login — no projectId (line 145), project not ready (line 158)
///   - GET  /auth/consent — null context (line 481-483), no projectIdStr (line 529)
///   - POST /auth/register — no projectId (line 597), project not found (line 603),
///                           project not allowing reg (line 604), domain blocked (line 647), SMS path (line 703)
///   - POST /auth/password-reset/request — SMS path (line 834)
///   - POST /auth/login (admin) — no email (line 889), account locked (line 906), null hash (line 909)
///   - IP allowlist — invalid CIDR (line 976), mismatched family (line 979), invalid prefix (line 981), /0 (line 994)
///   - POST /auth/register — registration_not_allowed (line 604)
/// </summary>
[Collection("RediensIAM")]
public class AuthBranchCoverageTests(TestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    // ── GET /auth/login — admin client (line 68) ─────────────────────────────

    [Fact]
    public async Task GetLogin_AdminClient_ReturnsAdminInfo()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("is_admin_login").GetBoolean().Should().BeTrue();
    }

    // ── GET /auth/login — no projectId (line 72) ─────────────────────────────

    [Fact]
    public async Task GetLogin_NoProjectId_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithNoProjectId(ch, "some-client");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_project_id");
    }

    // ── GET /auth/login — invalid project (line 75) ───────────────────────────

    [Fact]
    public async Task GetLogin_InvalidProject_ReturnsBadRequest()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var ch       = Guid.NewGuid().ToString("N");
        // Point challenge to a non-existent project
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            Guid.NewGuid().ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_project");
    }

    // ── GET /auth/login/theme — no projectId (line 108) ──────────────────────

    [Fact]
    public async Task GetTheme_NoProjectId_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithNoProjectId(ch, "some-client");

        var res = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /auth/login/theme — project not found (line 111) ─────────────────

    [Fact]
    public async Task GetTheme_ProjectNotFound_ReturnsNotFound()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var ch       = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            Guid.NewGuid().ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /auth/login — no projectId (line 145) ───────────────────────────

    [Fact]
    public async Task Login_NoProjectId_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithNoProjectId(ch, "some-client");

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = "user@test.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_project_id");
    }

    // ── POST /auth/login — project not ready (no AssignedUserList) (line 158) ─

    [Fact]
    public async Task Login_ProjectNotReady_ReturnsBadRequest()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        // Deliberately no AssignedUserListId
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = "user@test.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }

    // ── GET /auth/consent — null context → missing_context (lines 481-483) ───

    [Fact]
    public async Task GetConsent_NullContext_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupConsentChallengeNullContext(ch, "some-client");

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_context");
    }

    // ── GET /auth/consent — user_id present but no projectIdStr (line 529) ───

    [Fact]
    public async Task GetConsent_NoProjectId_ReturnsBadRequest()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);
        var ch       = Guid.NewGuid().ToString("N");
        // clientId is not admin client, no projectId in context
        fixture.Hydra.SetupConsentChallenge(ch, user.Id.ToString(), "some-client");
        fixture.Keto.AllowAll();

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={ch}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_context");
    }

    // ── POST /auth/register — no projectId (line 597) ────────────────────────

    [Fact]
    public async Task Register_NoProjectId_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithNoProjectId(ch, "some-client");

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_project_id");
    }

    // ── POST /auth/register — project not found (line 603) ───────────────────

    [Fact]
    public async Task Register_ProjectNotFound_ReturnsNotFound()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var ch       = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            Guid.NewGuid().ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_found");
    }

    // ── POST /auth/register — registration not allowed (line 604) ────────────

    [Fact]
    public async Task Register_RegistrationNotAllowed_Returns403()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId   = list.Id;
        project.AllowSelfRegistration = false;  // explicitly not allowed
        await fixture.Db.SaveChangesAsync();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("registration_not_allowed");
    }

    // ── POST /auth/register — domain not allowed (line 647) ──────────────────

    [Fact]
    public async Task Register_DomainNotAllowed_Returns403()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId    = list.Id;
        project.AllowSelfRegistration = true;
        project.AllowedEmailDomains   = ["allowed.com"];
        await fixture.Db.SaveChangesAsync();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = $"{Guid.NewGuid():N}@blocked.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("domain_not_allowed");
    }

    // ── POST /auth/register — SMS verification path (line 703) ───────────────

    [Fact]
    public async Task Register_SmsVerification_RequiresVerification()
    {
        // EmailVerificationEnabled=false, SmsVerificationEnabled=true → SMS path (line 703)
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId        = list.Id;
        project.AllowSelfRegistration     = true;
        project.EmailVerificationEnabled  = false;
        project.SmsVerificationEnabled    = true;
        await fixture.Db.SaveChangesAsync();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test",
            phone           = "+15555551234"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_verification").GetBoolean().Should().BeTrue();
        body.GetProperty("session_id").GetString().Should().NotBeNullOrEmpty();
    }

    // ── POST /auth/password-reset/request — SMS path (line 834) ─────────────

    [Fact]
    public async Task RequestPasswordReset_SmsOnly_SendsSms()
    {
        // EmailVerificationEnabled=false, SmsVerificationEnabled=true → SMS path (line 834)
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId        = list.Id;
        project.EmailVerificationEnabled  = false;
        project.SmsVerificationEnabled    = true;
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email,
            phone      = user.Phone ?? "+15555550000"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("session_id").GetString().Should().NotBeNullOrEmpty();
    }

    // ── POST /auth/login (admin) — no email (line 889) ───────────────────────

    [Fact]
    public async Task AdminLogin_NoEmail_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");

        // Send username instead of email → body.Email is null
        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            username        = "noone#0000",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("email_required");
    }

    // ── POST /auth/login (admin) — account locked (line 906) ─────────────────

    [Fact]
    public async Task AdminLogin_AccountLocked_ReturnsUnauthorized()
    {
        // Create system user with LockedUntil in future
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: "Correct@Pass123!");
        user.LockedUntil = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Db.SaveChangesAsync();
        fixture.Keto.AllowAll();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "Correct@Pass123!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("account_locked");
    }

    // ── POST /auth/login (admin) — null password hash (line 909) ─────────────

    [Fact]
    public async Task AdminLogin_NullPasswordHash_ReturnsUnauthorized()
    {
        // SAML-provisioned admin user: PasswordHash is null → short-circuit on line 909
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();

        var user = new User
        {
            Id            = Guid.NewGuid(),
            UserListId    = list.Id,
            Email         = SeedData.UniqueEmail(),
            Username      = "samladmin",
            Discriminator = "0001",
            PasswordHash  = null,   // SAML user
            EmailVerified = true,
            Active        = true,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow,
        };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();
        fixture.Keto.AllowAll();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "anything"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    // ── IP allowlist — invalid CIDR IP (line 976 FALSE branch) ───────────────

    [Fact]
    public async Task Login_AllowlistInvalidCidrIp_ReturnsForbidden()
    {
        // IpInRange: IPAddress.TryParse("not.a.cidr") fails → returns false → 401
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.IpAllowlist        = ["not.a.valid.cidr/24"];
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var ch   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ip_not_allowed");
    }

    // ── IP allowlist — mismatched address family (line 979) ──────────────────

    [Fact]
    public async Task Login_AllowlistIpv6CidrVsIpv4Client_ReturnsForbidden()
    {
        // Client is IPv4 (127.0.0.1), allowlist is IPv6 CIDR → different families → false → 401
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.IpAllowlist        = ["fd00::/8"];   // non-loopback IPv6 → different family from IPv4 client
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var ch   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ip_not_allowed");
    }

    // ── IP allowlist — invalid prefix length (line 981) ──────────────────────

    [Fact]
    public async Task Login_AllowlistInvalidPrefixLength_ReturnsForbidden()
    {
        // IpInRange: int.TryParse("abc") fails → returns false → 401
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.IpAllowlist        = ["127.0.0.0/abc"];  // invalid prefix
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var ch   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ip_not_allowed");
    }

    // ── IP allowlist — /0 prefix covers all IPs (line 994) ───────────────────

    [Fact]
    public async Task Login_AllowlistZeroPrefixMatchesAll_Succeeds()
    {
        // prefixLen == 0 → mask = 0 → any IP matches (line 994 TRUE)
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.IpAllowlist        = ["0.0.0.0/0"];   // match any IPv4
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var ch   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /auth/register — self-reg allowed but no user list (line 605) ──

    [Fact]
    public async Task Register_AllowSelfRegistrationNoList_ReturnsBadRequest()
    {
        // Covers line 605: AllowSelfRegistration=true but AssignedUserListId=null → project_not_ready
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AllowSelfRegistration = true;
        project.AssignedUserListId    = null;   // no list assigned
        await fixture.Db.SaveChangesAsync();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }

    // ── POST /auth/register — breached password (line 638) ───────────────────

    [Fact]
    public async Task Register_BreachedPassword_ReturnsBadRequest()
    {
        // Covers line 638: CheckBreachedPasswords=true and HIBP count > 0 → password_breached
        const string breached = "BreachTestRegister_P@ss!Coverage";
        fixture.HibpStub.Setup(breached, count: 42);
        try
        {
            var (org, _) = await fixture.Seed.CreateOrgAsync();
            var project  = await fixture.Seed.CreateProjectAsync(org.Id);
            var list     = await fixture.Seed.CreateUserListAsync(org.Id);
            project.AssignedUserListId     = list.Id;
            project.AllowSelfRegistration  = true;
            project.CheckBreachedPasswords = true;
            await fixture.Db.SaveChangesAsync();

            var ch = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
                project.Id.ToString(), org.Id.ToString());

            var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
            {
                login_challenge = ch,
                email           = SeedData.UniqueEmail(),
                password        = breached
            });

            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("password_breached");
        }
        finally
        {
            fixture.HibpStub.Clear();
        }
    }

    // ── POST /auth/login (admin) — expired lock (line 906 false branch) ───────

    [Fact]
    public async Task AdminLogin_ExpiredLock_Succeeds()
    {
        // Covers line 906 uncovered condition: LockedUntil.HasValue=true but LockedUntil <= now
        // (lock already expired → should NOT return account_locked)
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: "Correct@Pass123!");
        user.LockedUntil = DateTimeOffset.UtcNow.AddHours(-1);  // lock ALREADY EXPIRED
        await fixture.Db.SaveChangesAsync();
        fixture.Keto.AllowAll();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "Correct@Pass123!"
        });

        // Expired lock → login should proceed (not return account_locked)
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("error", out _).Should().BeFalse();
    }

    // ── GET /auth/oauth2/start — no projectId (line 1043) ────────────────────

    [Fact]
    public async Task OAuthStart_NoProjectId_ReturnsBadRequest()
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithNoProjectId(ch, "some-client");

        var res = await fixture.Client.GetAsync($"/auth/oauth2/start?login_challenge={ch}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_project_id");
    }

    // ── GET /auth/oauth2/start — project not ready (line 1046) ───────────────

    [Fact]
    public async Task OAuthStart_ProjectNotReady_ReturnsBadRequest()
    {
        // Challenge points to a project with no AssignedUserListId → project_not_ready
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        // AssignedUserListId is null by default → project_not_ready
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/oauth2/start?login_challenge={ch}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }

    // ── GET /auth/oauth2/start — provider not found (line 1049, covers 1257) ──

    [Fact]
    public async Task OAuthStart_ProviderNotFound_ReturnsBadRequest()
    {
        // Project has an assigned list but LoginTheme is empty (no providers) →
        // GetProviderConfig(theme={}, "github") → theme != null but no "providers" key → returns null → "provider_not_found"
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        // LoginTheme is empty dict by default — no "providers" key
        await fixture.Db.SaveChangesAsync();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/oauth2/start?login_challenge={ch}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("provider_not_found");
    }

    // ── GET /auth/oauth2/link/start — unauthenticated (line 1062) ────────────

    [Fact]
    public async Task OAuthLinkStart_Unauthenticated_ReturnsUnauthorized()
    {
        // No bearer token → GetClaims() returns null → 401
        var res = await fixture.Client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /auth/oauth2/link/start — no projectId claim (line 1065) ─────────

    [Fact]
    public async Task OAuthLinkStart_NoProjectId_ReturnsBadRequest()
    {
        // OrgAdmin token has no projectId claim → missing_project_id
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_project_id");
    }

    // ── GET /auth/oauth2/link/start — project not ready (line 1068) ──────────

    [Fact]
    public async Task OAuthLinkStart_ProjectNotReady_ReturnsBadRequest()
    {
        // Project exists but AssignedUserListId=null → project_not_ready
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        // AssignedUserListId is null by default
        var manager = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }
}
