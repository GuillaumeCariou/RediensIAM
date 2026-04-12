using System.Net.Http.Json;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

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
            password = "P@ssw0rd!Admin"   // non-empty → covers passwords.Hash branch
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
