using RediensIAM.IntegrationTests.Infrastructure;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using RediensIAM.Services;
using System.Net;
using System.Reflection;
using RediensIAM.Controllers;
using System.Security.Cryptography;
using System.Text;
using OtpNet;
using RediensIAM.Data.Entities;
using StackExchange.Redis;

namespace RediensIAM.IntegrationTests.Tests.Auth;

// ── from AuthBranchCoverageTests.cs ─────────────────────────────

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
        // A SAML-provisioned admin has no password hash. Password login must refuse before it
        // reaches the verifier, never treat "no hash" as "any password matches".
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

    /// <summary>
    /// A LockedUntil in the past must not keep the account locked: the guard has to compare the
    /// timestamp, not merely check that one is present. A regression to a HasValue-only check would
    /// lock every previously-locked account out permanently.
    /// </summary>
    [Fact]
    public async Task AdminLogin_ExpiredLock_Succeeds()
    {
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

// ── from AuthCoverageTests.cs ───────────────────────────────────

/// <summary>
/// Covers AuthController lines not exercised by the existing test files:
///   - GET /auth/login/theme — Hydra failure catch block (lines 120-121)
///   - GET /auth/consent   — admin client (client_admin_system) accept path (lines 491-524)
///   - GET /auth/consent   — admin client no roles → reject (lines 496-500)
///   - GET /auth/login/theme — ExtractProjectId URL fallback (lines 1283-1305)
///   - GET /auth/consent   — admin client OrgAdmin/ProjectAdmin roles (lines 492, 494)
///   - POST /auth/mfa/totp/verify — no session (line 429)
///   - POST /auth/mfa/phone/verify — no session (line 388)
///   - POST /auth/login — username without # discriminator (lines 196-197)
///   - POST /auth/register — SMS verification path (lines 703-704)
/// </summary>
[Collection("RediensIAM")]
public class AuthCoverageTests(TestFixture fixture)
{
    // ── GET /auth/login/theme — Hydra failure catch block (lines 120-121) ─────

    [Fact]
    public async Task GetTheme_HydraFails_Returns400()
    {
        // Default stub returns 404 for unknown challenges → GetLoginRequestAsync throws
        // → catch block runs → BadRequest()
        var res = await fixture.Client.GetAsync("/auth/login/theme?login_challenge=nonexistent-challenge-abc");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /auth/consent — admin client (super_admin path, lines 491-524) ────

    [Fact]
    public async Task GetConsent_AdminClient_SuperAdmin_AcceptsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.AllowAll();   // super_admin check returns true

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        fixture.Hydra.ConsentWasAccepted(challenge).Should().BeTrue();
    }

    // ── GET /auth/consent — admin client no roles → reject (lines 496-500) ───

    [Fact]
    public async Task GetConsent_AdminClient_NoRoles_RejectsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.DenyAll();   // all Keto checks return false → no roles

        try
        {
            var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            fixture.Hydra.ConsentWasRejected(challenge).Should().BeTrue();
        }
        finally
        {
            fixture.Keto.AllowAll();
        }
    }

    // ── GET /auth/login/theme — ExtractProjectId URL fallback (lines 1283-1305)

    [Fact]
    public async Task GetTheme_ProjectIdInUrlNotOidcContext_Returns200()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var challenge = Guid.NewGuid().ToString("N");

        // Challenge has project_id in the request_url but NOT in oidc_context extras
        fixture.Hydra.SetupLoginChallengeProjectInUrl(challenge, project.HydraClientId, project.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("login_theme", out _).Should().BeTrue();
    }

    // ── GET /auth/consent — admin client OrgAdmin + ProjectAdmin roles (lines 492, 494) ─

    [Fact]
    public async Task GetConsent_AdminClient_OrgAndProjectAdminRoles_AcceptsConsent()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var user      = await fixture.Seed.CreateUserAsync(list.Id);
        var challenge = Guid.NewGuid().ToString("N");

        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");
        fixture.Keto.AllowAll();
        // Make HasAnyRelationAsync return true → OrgAdmin and ProjectAdmin lines fire
        fixture.Keto.SimulateRelationExists($"user:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        fixture.Hydra.ConsentWasAccepted(challenge).Should().BeTrue();
    }

    // ── POST /auth/mfa/totp/verify — no MFA session (line 429) ───────────────

    [Fact]
    public async Task VerifyTotp_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();   // fresh session, no MFA state

        var res = await client.PostAsJsonAsync("/auth/mfa/totp/verify", new { code = "123456" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/mfa/phone/verify — no MFA session (line 388) ──────────────

    [Fact]
    public async Task VerifySmsOtp_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.PostAsJsonAsync("/auth/mfa/phone/verify", new { code = "123456" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/login — username without # (lines 196-197) ────────────────

    [Fact]
    public async Task Login_UsernameWithoutDiscriminator_Returns401()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId    = list.Id;
        project.AllowSelfRegistration = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            username        = "justusername",   // no "#discriminator" — lookup cannot resolve it
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_credentials");
    }

    // ── POST /auth/register — SMS verification path (lines 703-704) ──────────

    [Fact]
    public async Task Register_SmsVerificationEnabled_SendsSmsOtpAndReturnsSessionId()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId        = list.Id;
        project.AllowSelfRegistration     = true;
        project.SmsVerificationEnabled    = true;
        project.EmailVerificationEnabled  = false;   // SMS only
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test",
            phone           = "+1234567890"    // without a phone the SMS branch is never taken
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_verification").GetBoolean().Should().BeTrue();
        fixture.SmsStub.SentMessages.Should().NotBeEmpty();
    }
}

// ── from AuthMissingCoverageTests.cs ────────────────────────────

/// <summary>
/// Covers AuthController lines not yet hit by existing test files:
///   - AdminLogin lockout after MaxLoginAttempts failures (line 913)
///   - VerifyRegistration duplicate email (line 730)
///   - CheckNewDeviceAsync catch block (lines 959-962)
/// </summary>
[Collection("RediensIAM")]
public class AuthMissingCoverageTests(TestFixture fixture)
{
    // ── AdminLogin — lockout after MaxLoginAttempts (line 913) ───────────────

    /// <summary>
    /// After 5 failed admin login attempts (MaxLoginAttempts=5),
    /// user.LockedUntil is set — covers line 913.
    /// </summary>
    [Fact]
    public async Task AdminLogin_MaxFailedAttempts_SetsLockedUntil()
    {
        // Create a system-level user (OrgId=null, Immovable=true) for admin login
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-lock-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: "Correct@Pass123!");

        // One below MaxLoginAttempts (5 in TestFixture's configuration), so the single wrong
        // password below is the one that trips the lockout.
        user.FailedLoginCount = 4;
        await fixture.Db.SaveChangesAsync();

        fixture.Keto.AllowAll();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");
        await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "WRONG_PASSWORD"
        });

        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.FindAsync(user.Id);
        reloaded!.LockedUntil.Should().NotBeNull();
        reloaded.LockedUntil.Should().BeAfter(DateTimeOffset.UtcNow);

        // Reset IP rate counter so subsequent tests are not blocked (only 1 failure added)
        await fixture.FlushCacheAsync();
    }

    // ── VerifyRegistration — duplicate email race condition (line 730) ────────

    /// <summary>
    /// When registration with email verification is in flight and another user
    /// registers the same email, VerifyRegistration returns 409 — covers line 730.
    /// </summary>
    [Fact]
    public async Task VerifyRegistration_DuplicateEmailRace_Returns409()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.AllowSelfRegistration    = true;
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var email     = SeedData.UniqueEmail();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        fixture.EmailStub.SentEmails.Clear();

        var regRes = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email,
            password = "P@ssw0rd!Test"
        });
        regRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var regBody   = await regRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = regBody.GetProperty("session_id").GetString()!;

        var sent = fixture.EmailStub.SentEmails.LastOrDefault(e => e.To == email && e.Purpose == "registration");
        sent.Should().NotBeNull("email stub should capture the OTP");
        var otp = sent!.Code;

        // Seeded between start and verify to stand in for the race where another path claims the
        // address while an OTP is outstanding.
        await fixture.Seed.CreateUserAsync(list.Id, email: email);

        var verifyRes = await fixture.Client.PostAsJsonAsync("/auth/register/verify", new
        {
            session_id = sessionId,
            code       = otp
        });

        verifyRes.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await verifyRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("email_already_exists");
    }

    // ── CheckNewDeviceAsync — catch block (lines 959-962) ────────────────────

    /// <summary>
    /// Verifies that when SendNewDeviceAlertAsync throws, the catch block at
    /// AuthController.cs:959-962 fires and login still completes successfully.
    /// Uses a custom factory whose email service throws on SendNewDeviceAlertAsync.
    /// </summary>
    [Fact]
    public async Task Login_SendNewDeviceAlertThrows_CatchesAndLoginSucceeds()
    {
        const string password = "P@ssw0rd!NDA";
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);

        // Project needs AssignedUserListId for login to proceed, and must opt out of the
        // now-default MFA requirement so the login reaches the new-device alert.
        project.AssignedUserListId = orgList.Id;
        project.RequireMfa         = false;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(orgList.Id, password: password);
        fixture.Keto.AllowAll();

        var throwingEmail = new ThrowingNewDeviceEmailService();
        var (client, factory) = fixture.CreateSmtpEnabledClient(throwingEmail);
        await using var _f = factory;

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password
        });

        // Login succeeds even though the background new-device alert threw
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("redirect_to");

        // Wait for the Task.Run background task to complete so coverage records it.
        // The task performs two Redis round-trips then calls the email service, so 2 s is ample.
        await Task.Delay(2000);
    }
}

// ── Local stubs ───────────────────────────────────────────────────────────────

/// <summary>
/// Email service that faults SendNewDeviceAlertAsync via a faulted Task (not a synchronous throw)
/// so OpenCover can record the await-point sequence point — triggers the catch at L959-962.
/// </summary>
file sealed class ThrowingNewDeviceEmailService : IEmailService
{
    public Task CheckConnectivityAsync() => Task.CompletedTask;

    public Task SendOtpAsync(string to, string code, string purpose,
        Guid? orgId = null, Guid? projectId = null) => Task.CompletedTask;

    public Task SendInviteAsync(string to, string inviteUrl, string orgName,
        Guid? projectId = null) => Task.CompletedTask;

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent,
        DateTimeOffset loginAt, Guid? orgId = null)
    {
        var tcs = new TaskCompletionSource<bool>();
        tcs.SetException(new InvalidOperationException("Simulated new-device alert failure"));
        return tcs.Task;
    }
}

// ── from AuthMoreCoverageTests.cs ───────────────────────────────

/// <summary>
/// Covers AuthController lines not yet exercised by other test files:
///   - POST /auth/login   — project_id mismatch (lines 149-151)
///   - POST /auth/register — invalid challenge catch block (lines 590-593)
///   - POST /auth/password-reset/request — SMS-only path (lines 833-834)
///   - Ipv6InRange static helper (lines 1001-1012)
/// </summary>
[Collection("RediensIAM")]
public class AuthMoreCoverageTests(TestFixture fixture)
{
    // ── POST /auth/login — project_id mismatch (lines 149-151) ───────────────

    [Fact]
    public async Task Login_ProjectIdMismatch_RejectsWithRedirect()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var challenge = Guid.NewGuid().ToString("N");

        // oidc_context has the real project ID; client.metadata has a different one
        fixture.Hydra.SetupLoginChallengeWithProjectIdMismatch(
            challenge,
            project.HydraClientId,
            oidcProjectId:        project.Id.ToString(),
            registeredProjectId:  Guid.NewGuid().ToString());   // deliberately different

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            username        = "anyone",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_id_mismatch");
    }

    // ── POST /auth/register — invalid challenge (lines 590-593) ──────────────

    [Fact]
    public async Task Register_InvalidChallenge_Returns400()
    {
        // The Hydra stub 404s unknown challenges, so GetLoginRequestAsync throws; the controller
        // has to turn that into invalid_challenge rather than let it surface as a 500.
        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = "completely-nonexistent-challenge-xyz",
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_challenge");
    }

    // ── POST /auth/password-reset/request — SMS-only path (lines 833-834) ────

    [Fact]
    public async Task PasswordResetRequest_SmsOnlyProject_SendsSmsCode()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.EmailVerificationEnabled = false;
        project.SmsVerificationEnabled   = true;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.Phone         = "+15550001234";
        user.PhoneVerified = true;
        await fixture.Db.SaveChangesAsync();

        fixture.SmsStub.SentMessages.Clear();

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email,
            phone      = user.Phone
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.SmsStub.SentMessages.Should().ContainSingle(m => m.Purpose == "password_reset");
    }
}

// ── Ipv6InRange unit tests (no fixture required) ─────────────────────────────

/// <summary>
/// Pure unit tests for AuthController.Ipv6InRange / IpInRange static helpers via reflection.
/// Covers lines 1001-1012 which can't be reached from integration tests because TestServer
/// always sets the remote IP to 127.0.0.1 (IPv4).
/// </summary>
public class AuthControllerIpRangeTests
{
    private static readonly MethodInfo IpInRange =
        typeof(AuthController)
            .GetMethod("IpInRange", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool Invoke(string ip, string cidr) =>
        (bool)IpInRange.Invoke(null, [IPAddress.Parse(ip), cidr])!;

    // ── IPv6 full-byte match (lines 1002-1005, 1006→false, 1011) ─────────────

    [Fact]
    public void Ipv6InRange_ExactNetwork_ReturnsTrue()
    {
        // 2001:db8::1 is in 2001:db8::/32 (4 full bytes identical, remBits=0)
        Invoke("2001:db8::1", "2001:db8::/32").Should().BeTrue();
    }

    [Fact]
    public void Ipv6InRange_DifferentNetwork_ReturnsFalse()
    {
        // 2001:db9::1 is NOT in 2001:db8::/32 — byte[2] differs
        Invoke("2001:db9::1", "2001:db8::/32").Should().BeFalse();
    }

    // ── IPv6 partial-byte match (lines 1006→true, 1008, 1009, 1011) ──────────

    [Fact]
    public void Ipv6InRange_PartialByteMatch_ReturnsTrue()
    {
        // /33 → fullBytes=4, remBits=1, byteMask=0x80
        // 2001:db8:0000:: byte[4]=0x00 & 0x80 == 0x00 → match → true
        Invoke("2001:db8::", "2001:db8::/33").Should().BeTrue();
    }

    [Fact]
    public void Ipv6InRange_PartialByteMismatch_ReturnsFalse()
    {
        // 2001:db8:8000:: byte[4]=0x80 & 0x80 == 0x80, net byte[4]=0x00 & 0x80 == 0x00 → mismatch
        Invoke("2001:db8:8000::", "2001:db8::/33").Should().BeFalse();
    }
}

// ── from AuthRateLimiterCoverageTests.cs ────────────────────────

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
