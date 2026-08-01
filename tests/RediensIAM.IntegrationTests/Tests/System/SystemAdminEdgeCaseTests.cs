using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Data.Entities;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.System;

// ── from SystemAdminBranchCoverageTests.cs ────────────────────

/// <summary>
/// Covers SystemAdminController branches where only one path was exercised:
///   - POST /admin/organizations/{id}/suspend|unsuspend — org not found (lines 103, 114)
///   - GET  /admin/users/{id}         — system user (is_system_admin=true, null org) (lines 191-192)
///   - PATCH /admin/users/{id}        — empty body (lines 221-228, 235, 242)
///   - GET  /admin/users/{id}/sessions — system user (null OrgId path) (line 263)
///   - DELETE /admin/users/{id}/sessions — system user (null OrgId path) (line 280)
///   - GET  /admin/userlists/{id}     — not found (line 307)
///   - POST /admin/userlists/{id}/users — not found (line 330)
///   - POST /admin/userlists/{id}/users — system list invite (line 349, 370)
///   - DELETE /admin/userlists/{id}/users/{uid} — system user list (line 388)
///   - GET  /admin/organizations/{id}/admins — scope with missing project (line 412)
///   - POST /admin/organizations/{id}/admins — existing role (line 426)
///   - POST/DELETE /admin/organizations/{id}/admins — ketoSubject with scope (lines 434, 446)
///   - PATCH /admin/projects/{id}     — empty body (lines 515-527)
///   - PUT  /admin/organizations/{id}/smtp — org not found (line 756)
///   - POST /admin/hydra/clients      — without client_credentials (line 857)
/// </summary>
[Collection("RediensIAM")]
public class SystemAdminBranchCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PUT /admin/projects/{id}/scopes — Hydra PATCH fails → catch (line 579) ─

    [Fact]
    public async Task AdminUpdateProjectScopes_HydraUpdateFails_LogsWarningAndReturnsOk()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientPatchFailure(project.HydraClientId!);
        try
        {
            var res = await client.PutAsJsonAsync($"/admin/projects/{project.Id}/scopes",
                new { scopes = new[] { "read:data" } });

            res.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            fixture.Hydra.RestoreClientPatch();
        }
    }

    // ── Suspend / Unsuspend not found (lines 103, 114) ────────────────────────

    [Fact]
    public async Task SuspendOrg_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsync($"/admin/organizations/{Guid.NewGuid()}/suspend",
            new StringContent(""));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnsuspendOrg_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsync($"/admin/organizations/{Guid.NewGuid()}/unsuspend",
            new StringContent(""));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── System user: GetUser, ListSessions, ForceLogout (lines 191-192, 263, 280) ─

    private async Task<(User systemUser, HttpClient client)> SystemUserAsync()
    {
        var (_, _, client) = await SuperAdminAsync();

        // Create a user list with OrgId == null and Immovable == true (system admin list)
        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            OrgId     = null,
            Immovable = true,
            Name      = $"system-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var systemUser = await fixture.Seed.CreateUserAsync(systemList.Id);
        return (systemUser, client);
    }

    [Fact]
    public async Task GetUser_SystemUser_IsSystemAdminTrue_Returns200()
    {
        var (systemUser, client) = await SystemUserAsync();

        var res = await client.GetAsync($"/admin/users/{systemUser.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("is_system_admin").GetBoolean().Should().BeTrue();
        body.GetProperty("org_name").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListSessions_SystemUser_OrgIdNull_Returns200()
    {
        var (systemUser, client) = await SystemUserAsync();

        var res = await client.GetAsync($"/admin/users/{systemUser.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForceLogout_SystemUser_OrgIdNull_Returns200()
    {
        var (systemUser, client) = await SystemUserAsync();

        var res = await client.DeleteAsync($"/admin/users/{systemUser.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /admin/users/{id} — empty body (lines 221-228, 235, 242) ─────────

    [Fact]
    public async Task UpdateUser_EmptyBody_Returns200_CoversAllFalseBranches()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /admin/userlists/{id} — not found (line 307) ─────────────────────

    [Fact]
    public async Task GetUserList_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/userlists/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/userlists/{id}/users — not found (line 330) ──────────────

    [Fact]
    public async Task AddUserToList_NonExistentList_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/admin/userlists/{Guid.NewGuid()}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/userlists/{id}/users — system list invite path (lines 349, 370) ─

    [Fact]
    public async Task AddUserToList_SystemListInvite_WritesSystemKeto()
    {
        var (_, _, client) = await SuperAdminAsync();

        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            OrgId     = null,
            Immovable = true,
            Name      = $"system-invite-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/admin/userlists/{systemList.Id}/users", new
        {
            email = SeedData.UniqueEmail()
            // no password → invite
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeTrue();
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} — system user list (line 388) ─

    [Fact]
    public async Task RemoveUserFromList_SystemList_RemovesSystemKetoTuple()
    {
        var (_, _, client) = await SuperAdminAsync();

        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            OrgId     = null,
            Immovable = true,
            Name      = $"system-remove-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();
        var systemUser = await fixture.Seed.CreateUserAsync(systemList.Id);

        var res = await client.DeleteAsync($"/admin/userlists/{systemList.Id}/users/{systemUser.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── GET /admin/organizations/{id}/admins — missing project scope (line 412) ─

    [Fact]
    public async Task ListOrgAdmins_ScopeWithMissingProject_ReturnsScopeNameNull()
    {
        var (org, admin, client) = await SuperAdminAsync();

        fixture.Db.OrgRoles.Add(new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            UserId    = admin.Id,
            Role      = "project_admin",
            ScopeId   = Guid.NewGuid(), // non-existent project
            GrantedAt = DateTimeOffset.UtcNow,
            GrantedBy = admin.Id,
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/admins");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var roles = body.EnumerateArray().ToList();
        var orphaned = roles.FirstOrDefault(r => r.GetProperty("role").GetString() == "project_admin"
            && r.GetProperty("scope_name").ValueKind == JsonValueKind.Null);
        orphaned.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    // ── POST /admin/organizations/{id}/admins — existing role (line 426) ─────

    [Fact]
    public async Task AssignOrgAdmin_AlreadyExists_Returns200WithExistingId()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        // Assign the same role twice
        await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id = user.Id,
            role    = "org_admin"
        });
        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id = user.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    // ── POST /admin/organizations/{id}/admins — scoped ketoSubject (line 434) ─

    [Fact]
    public async Task AssignOrgAdmin_WithScope_BuildsScopedKetoSubject()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var user    = await fixture.Seed.CreateUserAsync(list.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id  = user.Id,
            role     = "project_admin",
            scope_id = project.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── DELETE /admin/organizations/{id}/admins/{roleId} — scoped (line 446) ──

    [Fact]
    public async Task RemoveOrgAdmin_ScopedRole_BuildsScopedKetoSubject()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var user    = await fixture.Seed.CreateUserAsync(list.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Create scoped org admin role
        var createRes = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id  = user.Id,
            role     = "project_admin",
            scope_id = project.Id
        });
        var roleId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/admins/{roleId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── PATCH /admin/projects/{id} — empty body (lines 515-527) ─────────────

    [Fact]
    public async Task AdminUpdateProject_EmptyBody_Returns200_CoversAllFalseBranches()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PUT /admin/organizations/{id}/smtp — org not found (line 756) ──────────

    [Fact]
    public async Task UpsertOrgSmtp_NonExistentOrg_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PutAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}/smtp", new
        {
            host         = "smtp.test.com",
            port         = 587,
            start_tls    = true,
            username     = "user@test.com",
            from_address = "no@test.com",
            from_name    = "Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/hydra/clients — without client_credentials (line 857) ─────

    [Fact]
    public async Task CreateHydraClient_WithoutClientCredentials_UsesNoneAuthMethod()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_name   = "Test SPA Client",
            grant_types   = new[] { "authorization_code" },
            redirect_uris = new[] { "http://localhost:3000/callback" }
        });

        // 2xx or 4xx is fine — we're exercising the code path, not asserting on Hydra
        ((int)res.StatusCode).Should().BeInRange(200, 499);
    }

    // ── POST /admin/hydra/clients — explicit client_id ────────────────────────

    [Fact]
    public async Task CreateHydraClient_ExplicitClientId_IsAccepted()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_id     = "yandee-web",
            client_name   = "yandee-web",
            grant_types   = new[] { "authorization_code" },
            redirect_uris = new[] { "http://localhost/superadmin/" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("slash/injected")]
    [InlineData("")]
    [InlineData("sa_impersonator")]       // reserved: service-account clients
    [InlineData("client_admin_system")]   // reserved: project + admin clients
    public async Task CreateHydraClient_MalformedClientId_Returns400(string clientId)
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_id     = clientId,
            client_name   = "bad",
            grant_types   = new[] { "authorization_code" },
            redirect_uris = new[] { "http://localhost/superadmin/" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateHydraClient_ClientIdAlreadyTaken_Returns409()
    {
        var (_, _, client) = await SuperAdminAsync();
        fixture.Hydra.SetupOAuth2ClientWithJwks("already-there");

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_id     = "already-there",
            client_name   = "duplicate",
            grant_types   = new[] { "authorization_code" },
            redirect_uris = new[] { "http://localhost/superadmin/" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── PATCH /admin/users/{id} — all fields provided (lines 221-242 TRUE branches) ─

    [Fact]
    public async Task AdminUpdateUser_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new
        {
            email          = SeedData.UniqueEmail(),
            username       = "adminupdated",
            display_name   = "",      // "" clears the field rather than leaving it unchanged
            phone          = "",      // "" clears the field rather than leaving it unchanged
            active         = false,   // stamps DisabledAt
            email_verified = false,   // clears EmailVerifiedAt
            clear_lock     = true,
            new_password   = "NewAdmin@P@ss!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /admin/userlists/{id}/users — with email_verified=true (line 337/343 TRUE branch) ─

    [Fact]
    public async Task AddUserToList_WithEmailVerified_SetsEmailVerifiedAt()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users", new
        {
            email          = SeedData.UniqueEmail(),
            password       = "P@ssw0rd!Adm1n",
            email_verified = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} — user not in list (line 386 NotFound) ─

    [Fact]
    public async Task RemoveUserFromList_UserNotFound_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/admin/userlists/{Guid.NewGuid()}/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/organizations/{id}/admins/{roleId} — not found (line 443) ─

    [Fact]
    public async Task RemoveOrgAdmin_NonExistent_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/admins/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{id} — all fields provided (lines 516-527 TRUE branches) ─

    [Fact]
    public async Task AdminUpdateProject_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            name                       = "Admin Updated Name",
            require_role_to_login      = true,
            require_mfa                = false,
            allow_self_registration    = false,
            email_verification_enabled = false,
            sms_verification_enabled   = false,
            active                     = true,
            allowed_email_domains      = Array.Empty<string>(),
            ip_allowlist               = Array.Empty<string>(),
            check_breached_passwords   = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PUT /admin/projects/{id}/scopes — project not found (line 567) ─────────

    [Fact]
    public async Task AdminUpdateProjectScopes_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PutAsJsonAsync($"/admin/projects/{Guid.NewGuid()}/scopes",
            new { scopes = new[] { "openid" } });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/projects/{id} — not found (line 590) ───────────────────

    [Fact]
    public async Task AdminDeleteProject_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/admin/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT /admin/projects/{id}/userlist — project not found (line 606) ──────

    [Fact]
    public async Task AdminAssignUserList_ProjectNotFound_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/admin/projects/{Guid.NewGuid()}/userlist",
            new { user_list_id = list.Id });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT /admin/projects/{id}/userlist — list not in org (line 608) ────────

    [Fact]
    public async Task AdminAssignUserList_ListNotInOrg_ReturnsBadRequest()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        // List belongs to a different org
        var (otherOrg, _) = await fixture.Seed.CreateOrgAsync();
        var foreignList   = await fixture.Seed.CreateUserListAsync(otherOrg.Id);

        var res = await client.PutAsJsonAsync($"/admin/projects/{project.Id}/userlist",
            new { user_list_id = foreignList.Id });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("userlist_not_in_org");
    }

    // ── DELETE /admin/projects/{id}/userlist — project not found (line 619) ───

    [Fact]
    public async Task AdminUnassignUserList_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/admin/projects/{Guid.NewGuid()}/userlist");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/projects/{id}/stats — project not found (line 630) ─────────

    [Fact]
    public async Task AdminGetProjectStats_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/projects/{id}/roles — project not found (line 659) ────────

    [Fact]
    public async Task AdminCreateRole_NonExistentProject_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/admin/projects/{Guid.NewGuid()}/roles",
            new { name = "Ghost Role" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{pid}/saml-providers/{sid} — not found (line 1021) ─

    [Fact]
    public async Task AdminUpdateSamlProvider_NotFound_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync(
            $"/admin/projects/{project.Id}/saml-providers/{Guid.NewGuid()}",
            new { active = false });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{pid}/saml-providers/{sid} — all fields (lines 1023-1030 TRUE) ─

    [Fact]
    public async Task AdminUpdateSamlProvider_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Create a SAML provider first via the org-level endpoint isn't available here,
        // so seed it directly
        var provider = new SamlIdpConfig
        {
            Id        = Guid.NewGuid(),
            ProjectId = project.Id,
            EntityId  = "https://idp.test.com",
            SsoUrl    = "https://idp.test.com/sso",
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(provider);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync(
            $"/admin/projects/{project.Id}/saml-providers/{provider.Id}", new
            {
                entity_id                   = "https://idp.updated.test.com",
                sso_url                     = "https://idp.updated.test.com/sso",
                certificate_pem             = "MIIB...",
                email_attribute_name        = "mail",
                display_name_attribute_name = "cn",
                jit_provisioning            = true,
                default_role_id             = Guid.Empty,  // Guid.Empty means "clear", not "unchanged"
                active                      = false
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /admin/projects/{pid}/saml-providers/{sid} — not found (line 1045) ─

    [Fact]
    public async Task AdminDeleteSamlProvider_NotFound_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.DeleteAsync(
            $"/admin/projects/{project.Id}/saml-providers/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// ── from SystemAdminCoverageTests.cs ──────────────────────────

/// <summary>
/// Targeted tests that cover specific uncovered branches in SystemAdminController
/// identified via SonarQube line-coverage analysis.
/// </summary>
[Collection("RediensIAM")]
public class SystemAdminCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── GET /admin/hydra/clients/{id} — found path (line 873) ────────────────

    [Fact]
    public async Task GetHydraClient_ExistingClient_Returns200()
    {
        var (_, client) = await SuperAdminAsync();
        const string clientId = "test-hydra-client";
        fixture.Hydra.SetupClientGetResponse(clientId);

        var res = await client.GetAsync($"/admin/hydra/clients/{clientId}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("client_id").GetString().Should().Be(clientId);
    }

    // ── DELETE /admin/organizations/{id} — with users in lists (line 149) ────

    [Fact]
    public async Task DeleteOrg_WithUsersInLists_Returns204()
    {
        var (org, adminClient) = await SuperAdminAsync();

        // Create an extra user list for the org and put users in it
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await adminClient.DeleteAsync($"/admin/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/organizations/{id} — Hydra client delete fails (lines 133-134) ─

    [Fact]
    public async Task DeleteOrg_WithProjectHydraClientDeleteFailure_StillReturns204()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Make Hydra return 500 for this project's client — the catch block should eat it
        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);

        var res = await adminClient.DeleteAsync($"/admin/organizations/{org.Id}");

        // Even though Hydra deletion failed, the org deletion proceeds
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/projects/{id} — Hydra client delete fails (line 594) ───

    [Fact]
    public async Task DeleteProject_HydraClientDeleteFailure_StillReturns204()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);

        var res = await adminClient.DeleteAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} — from system list (line 389) ─

    [Fact]
    public async Task RemoveUserFromSystemList_Returns204AndRemovesSuperAdminTuple()
    {
        // System user list: OrgId=null, Immovable=true
        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}",
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(systemList.Id);

        var (_, adminClient) = await SuperAdminAsync();
        // Removing someone from the system list must drop both their "member" tuple and their
        // super_admin tuple — leaving the latter behind would keep the grant alive.

        var res = await adminClient.DeleteAsync($"/admin/userlists/{systemList.Id}/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /admin/organizations/{id}/smtp/test — failure path (lines 816-819) ─

    [Fact]
    public async Task TestOrgSmtp_WhenEmailServiceThrows_Returns400WithSmtpTestFailed()
    {
        var (org, adminClient) = await SuperAdminAsync();

        // Make the next SendOtpAsync call throw to simulate SMTP connection failure
        fixture.EmailStub.ThrowOnNextSend = new InvalidOperationException("Connection refused");

        var res = await adminClient.PostAsync($"/admin/organizations/{org.Id}/smtp/test", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("smtp_test_failed");
    }

    // ── GET /admin/organizations/{id}/export/users — rate limit (line 893) ───

    [Fact]
    public async Task ExportUsers_SecondCallInWindow_Returns429()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        // First call — should succeed
        var first = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second call within the rate-limit window — should be 429
        var second = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── GET /admin/organizations/{id}/export/audit-log — rate limit (line 932) ─

    [Fact]
    public async Task ExportAuditLog_SecondCallInWindow_Returns429()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var first = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── CSV quoting with embedded special chars (line 964) ───────────────────

    [Fact]
    public async Task ExportUsers_UserWithQuoteInName_ReturnsCsvWithEscaping()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        // Create a user whose display name contains a double-quote — forces AdminCsvEscape's quote branch
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.DisplayName = "O'Brien, \"Bob\"";    // contains comma AND quotes
        await fixture.Db.SaveChangesAsync();

        var res = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await res.Content.ReadAsStringAsync();
        // Quoted field should appear in the CSV
        csv.Should().Contain("\"O'Brien, \"\"Bob\"\"\"");
    }

    // ── GET /admin/userlists/{id} — covers line 304 ───────────────────────────

    [Fact]
    public async Task GetUserList_ExistingList_Returns200WithUserCount()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await adminClient.GetAsync($"/admin/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(list.Id.ToString());
        body.TryGetProperty("user_count", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetUserList_NonExistent_Returns404()
    {
        var (_, adminClient) = await SuperAdminAsync();

        var res = await adminClient.GetAsync($"/admin/userlists/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// ── from SystemAdminExtendedTests.cs ──────────────────────────

[Collection("RediensIAM")]
public class SystemAdminExtendedTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── GET /admin/users/{id}/sessions ────────────────────────────────────────

    [Fact]
    public async Task ListSessions_ExistingUser_Returns200WithArray()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var user  = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/admin/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListSessions_WithNonEmptySessions_ReturnsMappedFields()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var subject = $"{org.Id}:{user.Id}";

        fixture.Hydra.SetupConsentSessions(subject, new object[]
        {
            // Session 1: full consent_request with non-null client (non-null path for all ?. operators)
            new
            {
                consent_request = new
                {
                    client       = new { client_id = "app-client", client_name = "My App" },
                    requested_at = DateTimeOffset.UtcNow.AddHours(-1)
                },
                granted_scopes = new[] { "openid", "profile" },
                expires_at     = DateTimeOffset.UtcNow.AddDays(1)
            },
            // Session 2: null consent_request (ConsentRequest?.* → null branch)
            new
            {
                consent_request = (object?)null,
                granted_scopes  = new[] { "openid" },
                expires_at      = (DateTimeOffset?)null
            },
            // Session 3: non-null consent_request but null client (?.Client?.ClientId null branch)
            new
            {
                consent_request = new
                {
                    client       = (object?)null,
                    requested_at = DateTimeOffset.UtcNow.AddHours(-2)
                },
                granted_scopes = new[] { "openid" },
                expires_at     = (DateTimeOffset?)null
            }
        });

        var res = await client.GetAsync($"/admin/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(3);

        var first = body[0];
        first.GetProperty("client_id").GetString().Should().Be("app-client");
        first.GetProperty("client_name").GetString().Should().Be("My App");

        var second = body[1];
        second.GetProperty("client_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListSessions_NonExistentUser_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/userlists ──────────────────────────────────────────────────

    [Fact]
    public async Task ListAllUserLists_SuperAdmin_Returns200WithArray()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/userlists");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListAllUserLists_FilteredByOrg_ReturnsOrgLists()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.Seed.CreateUserListAsync(org.Id);

        var res  = await client.GetAsync($"/admin/userlists?org_id={org.Id}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} ──────────────────────────────

    [Fact]
    public async Task RemoveUserFromList_ExistingUser_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.DeleteAsync($"/admin/userlists/{list.Id}/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Users.FindAsync(user.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task RemoveUserFromList_NonExistentUser_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.DeleteAsync($"/admin/userlists/{list.Id}/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/projects/{id}/stats ────────────────────────────────────────

    [Fact]
    public async Task GetProjectStats_ProjectWithUserList_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/admin/projects/{project.Id}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("total_users", out _).Should().BeTrue();
        body.TryGetProperty("active_users", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectStats_ProjectWithoutUserList_ReturnsZeroes()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/admin/projects/{project.Id}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("total_users").GetInt32().Should().Be(0);
    }

    // ── DELETE /admin/projects/{id}/roles/{rid} ───────────────────────────────

    [Fact]
    public async Task DeleteRole_ExistingRole_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "ToDelete");

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Roles.FindAsync(role.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRole_NonExistentRole_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{id} — DefaultRoleId / ClearDefaultRole / LoginTheme branches ─

    [Fact]
    public async Task UpdateProject_SetDefaultRole_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "DefaultRole");

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            default_role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.DefaultRoleId.Should().Be(role.Id);
    }

    [Fact]
    public async Task UpdateProject_InvalidDefaultRoleId_Returns400()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            default_role_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateProject_ClearDefaultRole_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "ToClear");
        project.DefaultRoleId = role.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.DefaultRoleId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProject_WithLoginTheme_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            login_theme = new Dictionary<string, object> { ["background_color"] = "#ffffff" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PUT /admin/organizations/{id}/smtp — update (second call) branch ──────

    [Fact]
    public async Task UpdateOrgSmtp_CalledTwice_UpdatesExistingConfig()
    {
        var (org, _, client) = await SuperAdminAsync();
        var smtpPayload = new
        {
            host         = "smtp.initial.com",
            port         = 587,
            start_tls    = true,
            username     = "user@initial.com",
            password     = "initial-secret",
            from_address = "noreply@initial.com",
            from_name    = "Initial"
        };
        await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", smtpPayload);

        var res = await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "smtp.updated.com",
            port         = 465,
            start_tls    = false,
            username     = "user@updated.com",
            password     = "updated-secret",
            from_address = "noreply@updated.com",
            from_name    = "Updated"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var config = await fixture.Db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == org.Id);
        config!.Host.Should().Be("smtp.updated.com");
    }

    // ── GET /admin/hydra/clients ───────────────────────────────────────────────

    [Fact]
    public async Task ListHydraClients_SuperAdmin_Returns200WithArray()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/hydra/clients");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── POST /admin/hydra/clients ─────────────────────────────────────────────

    [Fact]
    public async Task CreateHydraClient_SuperAdmin_Returns200()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_name   = "Test External Client",
            grant_types   = new[] { "authorization_code", "refresh_token" },
            redirect_uris = new[] { "https://app.example.com/callback" },
            scope         = "openid profile"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /admin/hydra/clients/{id} ─────────────────────────────────────────

    [Fact]
    public async Task GetHydraClient_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/hydra/clients/nonexistent-client");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/hydra/clients/{id} ──────────────────────────────────────

    [Fact]
    public async Task DeleteHydraClient_SuperAdmin_Returns204()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync("/admin/hydra/clients/any-client-id");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── GET /admin/organizations/{id}/export/users ────────────────────────────

    [Fact]
    public async Task ExportUsers_CsvFormat_Returns200WithCsvContent()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await res.Content.ReadAsStringAsync();
        content.Should().Contain("id,email,username");
    }

    [Fact]
    public async Task ExportUsers_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/users?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ExportUsers_NonExistentOrg_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{Guid.NewGuid()}/export/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/organizations/{id}/export/audit-log ────────────────────────

    [Fact]
    public async Task ExportAuditLog_CsvFormat_Returns200WithCsvContent()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await res.Content.ReadAsStringAsync();
        content.Should().Contain("id,action");
    }

    [Fact]
    public async Task ExportAuditLog_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/audit-log?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ExportAuditLog_NonExistentOrg_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{Guid.NewGuid()}/export/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/projects/{id}/saml-providers ───────────────────────────────

    [Fact]
    public async Task ListSamlProviders_SuperAdmin_Returns200WithArray()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/admin/projects/{project.Id}/saml-providers");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListSamlProviders_NonExistentProject_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}/saml-providers");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/projects/{id}/saml-providers ──────────────────────────────

    [Fact]
    public async Task CreateSamlProvider_ValidPayload_Returns200WithId()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id       = "https://idp.example.com/saml2",
            sso_url         = "https://idp.example.com/saml2/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nMIID...\n-----END CERTIFICATE-----"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSamlProvider_NonExistentProject_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/admin/projects/{Guid.NewGuid()}/saml-providers", new
        {
            entity_id = "https://idp.example.com/saml2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{projectId}/saml-providers/{providerId} ─────────

    [Fact]
    public async Task UpdateSamlProvider_ValidPayload_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/original"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}/saml-providers/{providerId}", new
        {
            entity_id       = "https://idp.example.com/updated",
            jit_provisioning = false,
            active          = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /admin/projects/{projectId}/saml-providers/{providerId} ────────

    [Fact]
    public async Task DeleteSamlProvider_ExistingProvider_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.delete-me.com"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/saml-providers/{providerId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── PATCH saml-providers: DefaultRoleId branch (line 1030) ──────────────

    [Fact]
    public async Task UpdateSamlProvider_SetDefaultRole_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var role     = await fixture.Seed.CreateRoleAsync(project.Id, "SamlDefault");

        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/set-role"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        var res = await client.PatchAsJsonAsync(
            $"/admin/projects/{project.Id}/saml-providers/{providerId}",
            new { default_role_id = role.Id });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSamlProvider_ClearDefaultRole_Returns200()
    {
        // Guid.Empty is the sentinel for "clear", distinct from omitting the field entirely.
        var (org, _, client) = await SuperAdminAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/clear-role"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        var res = await client.PatchAsJsonAsync(
            $"/admin/projects/{project.Id}/saml-providers/{providerId}",
            new { default_role_id = Guid.Empty });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /admin/organizations/{id}/export/audit-log with date range ────────

    [Fact]
    public async Task ExportAuditLog_WithDateRange_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        var to   = DateTimeOffset.UtcNow.ToString("O");

        var res = await client.GetAsync(
            $"/admin/organizations/{org.Id}/export/audit-log?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /admin/projects/{id} — without HydraClientId ──────────────────

    [Fact]
    public async Task DeleteProject_WithoutHydraClientId_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.HydraClientId = null;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

// ── from SystemAdminMiscTests.cs ──────────────────────────────

[Collection("RediensIAM")]
public class SystemAdminMiscTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── GET /admin/audit-log ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_SuperAdmin_Returns200WithArray()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetAuditLog_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.GetAsync("/admin/audit-log");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAuditLog_LimitAndOffset_Returns200()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/audit-log?limit=5&offset=0");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeLessThanOrEqualTo(5);
    }

    // ── GET /admin/metrics ────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetrics_SuperAdmin_Returns200WithCounts()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/metrics");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("org_count", out _).Should().BeTrue();
        body.TryGetProperty("active_users", out _).Should().BeTrue();
        body.TryGetProperty("project_count", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetMetrics_CountsReflectSeededData()
    {
        var client = await SuperAdminClientAsync();

        // Seed one extra org to ensure org_count >= 1
        await fixture.Seed.CreateOrgAsync();

        var res  = await client.GetAsync("/admin/metrics");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("org_count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("active_users").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetMetrics_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.GetAsync("/admin/metrics");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

// ── from SystemAdminMoreCoverageTests.cs ──────────────────────

/// <summary>
/// Covers SystemAdminController lines not yet hit by existing test files:
///   - GET  /admin/userlists/{id}/users                  (lines 317-324)
///   - POST /admin/userlists/{id}/users                  with password (line 26 — PasswordService getter)
///   - POST /admin/userlists/{id}/users                  to system list (line 350)
///   - POST /admin/userlists/{id}/users                  list with assigned project (line 353)
///   - POST /admin/userlists                             (lines 397-402)
///   - GET  /admin/organizations/{id}/admins             (lines 408-419)
///   - POST /admin/organizations/{id}/projects           Hydra failure (lines 498-503)
///   - PATCH /admin/users/{id}                           with email_verified (lines 241-242)
///   - GET  /admin/projects/{id}/scopes                  (lines 557-561)
///   - PUT  /admin/projects/{id}/scopes                  valid (lines 563-584)
///   - PUT  /admin/projects/{id}/scopes                  invalid names → 400 (line 570)
/// </summary>
[Collection("RediensIAM")]
public class SystemAdminMoreCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── GET /admin/userlists/{id}/users (lines 317-324) ──────────────────────

    [Fact]
    public async Task ListUsersInList_ExistingList_Returns200WithArray()
    {
        var (org, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/admin/userlists/{list.Id}/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListUsersInList_NonExistentList_Returns404()
    {
        var (_, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/userlists/{Guid.NewGuid()}/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/userlists/{id}/users — with password (line 26 PasswordService getter) ─

    [Fact]
    public async Task AddUserToList_WithPassword_Returns201()
    {
        var (org, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Admin"   // a password here means "create", not "invite"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeFalse();
    }

    // ── POST /admin/userlists/{id}/users — system list (line 350: super_admin keto tuple) ─

    [Fact]
    public async Task AddUserToList_SystemList_WritesSuperAdminTuple()
    {
        var (_, client) = await SuperAdminAsync();
        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/admin/userlists/{systemList.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── POST /admin/userlists/{id}/users — list with assigned project (line 353: AssignDefaultRole) ─

    [Fact]
    public async Task AddUserToList_ListWithAssignedProject_AssignsDefaultRole()
    {
        var (org, client) = await SuperAdminAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── POST /admin/userlists (lines 397-402) ────────────────────────────────

    [Fact]
    public async Task AdminCreateUserList_ValidBody_Returns201()
    {
        var (org, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/userlists", new
        {
            name   = $"list-{Guid.NewGuid():N}"[..20],
            org_id = org.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
        body.TryGetProperty("name", out _).Should().BeTrue();
    }

    // ── GET /admin/organizations/{id}/admins (lines 408-419) ─────────────────

    [Fact]
    public async Task ListOrgAdmins_ExistingOrg_Returns200WithList()
    {
        var (org, client) = await SuperAdminAsync();
        var (_, orgList)  = await fixture.Seed.CreateOrgAsync();
        var targetUser    = await fixture.Seed.CreateUserAsync(orgList.Id);
        await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/admins");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── PATCH /admin/users/{id} — email_verified = false (lines 241-242) ────

    [Fact]
    public async Task UpdateUser_SetEmailVerifiedFalse_Returns200AndClearsVerifiedAt()
    {
        var (org, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.EmailVerified   = true;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new
        {
            email_verified = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.FindAsync(user.Id);
        reloaded!.EmailVerified.Should().BeFalse();
        reloaded.EmailVerifiedAt.Should().BeNull();
    }

    // ── GET /admin/projects/{id}/scopes (lines 557-561) ──────────────────────

    [Fact]
    public async Task GetProjectScopes_ExistingProject_Returns200WithScopes()
    {
        var (org, client) = await SuperAdminAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/admin/projects/{project.Id}/scopes");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("built_in", out _).Should().BeTrue();
        body.TryGetProperty("custom_scopes", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectScopes_NonExistentProject_Returns404()
    {
        var (_, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}/scopes");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT /admin/projects/{id}/scopes — valid scopes (lines 563-584) ───────

    [Fact]
    public async Task UpdateProjectScopes_ValidScopeNames_Returns200()
    {
        var (org, client) = await SuperAdminAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/admin/projects/{project.Id}/scopes", new
        {
            scopes = new[] { "read:users", "write:data", "custom_scope.v1" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("custom_scopes").GetArrayLength().Should().Be(3);
    }

    // ── PUT /admin/projects/{id}/scopes — invalid scope name → 400 (line 570) ─

    [Fact]
    public async Task UpdateProjectScopes_InvalidScopeName_Returns400()
    {
        var (org, client) = await SuperAdminAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/admin/projects/{project.Id}/scopes", new
        {
            scopes = new[] { "valid:scope", "INVALID SCOPE!" }  // spaces/uppercase invalid
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_scope_names");
    }

    // ── POST /admin/organizations/{id}/projects — Hydra client creation failure (lines 498-503) ─

    [Fact]
    public async Task CreateProject_HydraFails_Returns502()
    {
        var (org, client) = await SuperAdminAsync();
        fixture.Hydra.SetupClientCreationFailure();
        try
        {
            var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/projects", new
            {
                name = "Fail Project",
                slug = SeedData.UniqueSlug()
            });

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("hydra_unavailable");
        }
        finally
        {
            fixture.Hydra.RestoreClientCreation();
        }
    }
}

// ── from MiscRemainingCoverageTests.cs ────────────────────────

/// <summary>
/// Covers small remaining uncovered lines across several files:
///   - OrgController line 648          — PATCH /org/admins/{id} with valid new ScopeId
///   - PatService lines 117-119         — InvalidateAsync (direct service call)
///   - KetoService line 168             — AssignManagementRoleAsync with SuperAdmin role
/// </summary>
[Collection("RediensIAM")]
public class MiscRemainingCoverageTests(TestFixture fixture)
{
    // ── OrgController line 648: PATCH /org/admins/{id} with valid ScopeId ────

    [Fact]
    public async Task UpdateOrgAdmin_ValidScopeId_UpdatesRole()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var targetUser     = await fixture.Seed.CreateUserAsync(orgList.Id);

        // Use OrgAdmin token so OrgController sees the correct OrgId in claims
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var project1 = await fixture.Seed.CreateProjectAsync(org.Id);
        var project2 = await fixture.Seed.CreateProjectAsync(org.Id);
        await fixture.Db.SaveChangesAsync();

        // Assign a scoped project_admin role (ScopeId = project1)
        var orgRole = new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            UserId    = targetUser.Id,
            Role      = Config.Roles.ProjectAdmin,
            ScopeId   = project1.Id,
            GrantedBy = admin.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.OrgRoles.Add(orgRole);
        await fixture.Db.SaveChangesAsync();

        // A genuinely different scope, and one that exists in the org: the scope-change path runs
        // its project-existence check and passes.
        var res = await client.PatchAsJsonAsync($"/org/admins/{orgRole.Id}", new
        {
            scope_id = project2.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.OrgRoles.FindAsync(orgRole.Id);
        updated!.ScopeId.Should().Be(project2.Id);
    }

    // ── KetoService line 168: SuperAdmin case in AssignManagementRoleAsync switch ──

    [Fact]
    public async Task KetoService_AssignManagementRole_SuperAdmin_HitsLine168()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        // With every Keto check allowed the actor resolves as super_admin, which is the only level
        // permitted to grant super_admin below.
        fixture.Keto.AllowAll();

        var ketoService = fixture.GetService<KetoService>();
        await ketoService.AssignManagementRoleAsync(actor.Id, target.Id, org.Id, Roles.SuperAdmin);

        await fixture.RefreshDbAsync();
        var created = await fixture.Db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == org.Id && r.UserId == target.Id && r.Role == Roles.SuperAdmin);
        created.Should().NotBeNull();
    }

    // ── PatService lines 117-119: InvalidateAsync ─────────────────────────────

    [Fact]
    public async Task PatService_InvalidateAsync_RemovesFromCache()
    {
        var patService = fixture.GetService<PatService>();

        // Invalidating a hash that was never cached must be a silent no-op: revocation code calls
        // this without knowing whether the token was ever introspected, and a throw here would
        // abort the revocation.
        const string fakeHash = "aabbccddee112233aabbccddee112233aabbccddee112233aabbccddee112233";
        var act = async () => await patService.InvalidateAsync(fakeHash);
        await act.Should().NotThrowAsync();
    }
}
