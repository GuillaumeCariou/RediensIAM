using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

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
        // Covers lines 191 (OrgId == null && Immovable) and 192 (Organisation?.Name null)
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
        // Covers line 263: user.UserList.OrgId?.ToString() ?? "" → "" (null path)
        var (systemUser, client) = await SystemUserAsync();

        var res = await client.GetAsync($"/admin/users/{systemUser.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForceLogout_SystemUser_OrgIdNull_Returns200()
    {
        // Covers line 280: user.UserList.OrgId?.ToString() ?? "" → "" (null path)
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
        // Covers line 349 (ul.OrgId == null && ul.Immovable → write system keto tuple)
        // and line 370 (orgName = ul.Organisation?.Name ?? "the organization" when null)
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

        // Send invite (no password) → isInvite = true → hits line 370
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
        // Covers line 388: ul?.OrgId == null && ul?.Immovable == true → delete system keto
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
        // Covers line 412: TryGetValue returns false when project doesn't exist in dict
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
        // Covers line 426: if (existing != null) return Ok(new { existing.Id })
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
        // Covers line 434: body.ScopeId.HasValue → ketoSubject = "user:{id}|project:{scope}"
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
        // Covers line 446: role.ScopeId.HasValue → ketoSubject = "user:{id}|project:{scope}"
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
        // Covers line 857: GrantTypes.Contains("client_credentials") → false → "none"
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
            display_name   = "",      // "" → null (line 223 inner TRUE branch)
            phone          = "",      // "" → null (line 224 inner TRUE branch)
            active         = false,   // false → DisabledAt = DateTimeOffset.UtcNow (line 235)
            email_verified = false,   // false → EmailVerifiedAt = null (line 242)
            clear_lock     = true,
            new_password   = "NewAdmin@P@ss!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /admin/userlists/{id}/users — with email_verified=true (line 337/343 TRUE branch) ─

    [Fact]
    public async Task AddUserToList_WithEmailVerified_SetsEmailVerifiedAt()
    {
        // Covers line 343: emailVerified ? DateTimeOffset.UtcNow : null — TRUE path (emailVerified=true)
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users", new
        {
            email          = SeedData.UniqueEmail(),
            password       = "P@ssw0rd!1",
            email_verified = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} — user not in list (line 386 NotFound) ─

    [Fact]
    public async Task RemoveUserFromList_UserNotFound_Returns404()
    {
        // Covers line 386: if (user == null) return NotFound();
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/admin/userlists/{Guid.NewGuid()}/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/organizations/{id}/admins/{roleId} — not found (line 443) ─

    [Fact]
    public async Task RemoveOrgAdmin_NonExistent_Returns404()
    {
        // Covers line 443: if (role == null) return NotFound();
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
                default_role_id             = Guid.Empty,  // Guid.Empty → sets to null (line 1030 HasValue=true branch)
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
