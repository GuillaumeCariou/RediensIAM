using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

[Collection("RediensIAM")]
public class OrgAdminTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── GET /org/info ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrgInfo_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(org.Id.ToString());
    }

    [Fact]
    public async Task GetOrgInfo_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /org/admins ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListOrgAdmins_OrgAdmin_Returns200WithList()
    {
        var (org, admin, client) = await OrgAdminClientAsync();
        await fixture.Seed.CreateOrgRoleAsync(org.Id, admin.Id, "org_admin");

        var res = await client.GetAsync("/org/admins");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListOrgAdmins_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/org/admins");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /org/admins ──────────────────────────────────────────────────────

    [Fact]
    public async Task AssignOrgAdmin_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = targetUser.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssignOrgAdmin_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/org/admins", new
        {
            user_id = Guid.NewGuid(),
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DELETE /org/admins/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task RemoveOrgAdmin_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);
        var role             = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.DeleteAsync($"/org/admins/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.OrgRoles.FindAsync(role.Id);
        deleted.Should().BeNull();
    }

    // ── POST /org/admins — project_admin role ────────────────────────────────

    [Fact]
    public async Task AssignOrgAdmin_ProjectAdminRole_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id  = targetUser.Id,
            role     = "project_admin",
            scope_id = project.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /org/admins — super_admin guard ──────────────────────────────────

    [Fact]
    public async Task AssignOrgAdmin_GrantSuperAdmin_Returns403()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = targetUser.Id,
            role    = "super_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("cannot_grant_super_admin");
    }

    // ── PATCH /org/admins/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateOrgAdmin_NotFound_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/org/admins/{Guid.NewGuid()}", new
        {
            role = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOrgAdmin_OwnRole_Returns403CannotModifyOwnRole()
    {
        var (org, admin, client) = await OrgAdminClientAsync();
        var ownRole              = await fixture.Seed.CreateOrgRoleAsync(org.Id, admin.Id, "org_admin");

        var res = await client.PatchAsJsonAsync($"/org/admins/{ownRole.Id}", new
        {
            role = "project_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("cannot_modify_own_role");
    }

    [Fact]
    public async Task UpdateOrgAdmin_GrantSuperAdmin_Returns403()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);
        var role             = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            role = "super_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("cannot_grant_super_admin");
    }

    [Fact]
    public async Task UpdateOrgAdmin_ValidUpdate_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser       = await fixture.Seed.CreateUserAsync(list.Id);
        var role             = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            role = "project_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /admin/users ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchUsers_SuperAdmin_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/admin/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task SearchUsers_WithQuery_FiltersResults()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.AllowAll();
        var uniquePart     = Guid.NewGuid().ToString("N")[..8];
        await fixture.Seed.CreateUserAsync(orgList.Id, $"{uniquePart}@search-test.com");

        var res = await client.GetAsync($"/admin/users?q={uniquePart}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        body.EnumerateArray()
            .Any(u => u.GetProperty("email").GetString()!.Contains(uniquePart))
            .Should().BeTrue();
    }
}
