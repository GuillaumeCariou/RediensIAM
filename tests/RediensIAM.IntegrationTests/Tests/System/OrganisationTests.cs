using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class OrganisationTests(TestFixture fixture)
{
    private async Task<(User admin, string token, HttpClient client)> SuperAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(user.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.AllowAll();
        return (user, token, client);
    }

    // ── GET /admin/organizations ──────────────────────────────────────────────

    [Fact]
    public async Task ListOrgs_SuperAdmin_Returns200WithList()
    {
        var (_, _, client) = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListOrgs_Unauthenticated_Returns401Or403()
    {
        // GET /admin/* without auth header bypasses the gateway middleware (by design for SPA nav),
        // so the RequireManagementLevel filter fires and returns 403 instead of 401.
        var res = await fixture.Client.GetAsync("/admin/organizations");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListOrgs_RegularUser_Returns403()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var token    = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client   = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /admin/organizations ─────────────────────────────────────────────

    [Fact]
    public async Task CreateOrg_SuperAdmin_Returns201WithId()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        fixture.Keto.AllowAll();

        var res = await client.PostAsJsonAsync("/admin/organizations", new
        {
            name = "Test Org " + Guid.NewGuid().ToString("N")[..6],
            slug = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrg_CreatesOrgListInDb()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        fixture.Keto.AllowAll();
        var slug = SeedData.UniqueSlug();

        var res  = await client.PostAsJsonAsync("/admin/organizations", new
        {
            name = "DB Verify Org",
            slug
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id   = Guid.Parse(body.GetProperty("id").GetString()!);

        await fixture.RefreshDbAsync();
        var org = await fixture.Db.Organisations.FindAsync(id);
        org.Should().NotBeNull();
        org!.Slug.Should().Be(slug);
    }

    [Fact]
    public async Task CreateOrg_DuplicateSlug_Returns409Or400()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        fixture.Keto.AllowAll();
        var slug = SeedData.UniqueSlug();

        await client.PostAsJsonAsync("/admin/organizations", new { name = "Org 1", slug });
        var res = await client.PostAsJsonAsync("/admin/organizations", new { name = "Org 2", slug });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task CreateOrg_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.PostAsJsonAsync("/admin/organizations", new
        {
            name = "Hacker Org",
            slug = "hacker"
        });

        // POST triggers the gateway middleware (method != GET) → 401 from middleware
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── GET /admin/organizations/{id} ─────────────────────────────────────────

    [Fact]
    public async Task GetOrg_ExistingOrg_Returns200()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(org.Id.ToString());
    }

    [Fact]
    public async Task GetOrg_NonExistentId_Returns404()
    {
        var (_, _, client) = await SuperAdminClientAsync();

        var res = await client.GetAsync($"/admin/organizations/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/organizations/{id} ───────────────────────────────────────

    [Fact]
    public async Task UpdateOrg_SuperAdmin_UpdatesName()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();
        var newName        = "Renamed Org " + Guid.NewGuid().ToString("N")[..4];

        var res = await client.PatchAsJsonAsync($"/admin/organizations/{org.Id}", new { name = newName });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Organisations.FindAsync(org.Id);
        updated!.Name.Should().Be(newName);
    }

    [Fact]
    public async Task UpdateOrg_NonExistentId_Returns404()
    {
        var (_, _, client) = await SuperAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}", new { name = "x" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOrg_SetAuditRetentionDays_PersistsValue()
    {
        // Covers SystemAdminController line 93: HasValue=true, value != -1 → sets int value
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();

        var res = await client.PatchAsJsonAsync($"/admin/organizations/{org.Id}", new { audit_retention_days = 30 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Organisations.FindAsync(org.Id);
        updated!.AuditRetentionDays.Should().Be(30);
    }

    [Fact]
    public async Task UpdateOrg_ClearAuditRetentionDays_SetsNull()
    {
        // Covers SystemAdminController line 93: HasValue=true, value == -1 → sets null
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();
        org.AuditRetentionDays = 90;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/organizations/{org.Id}", new { audit_retention_days = -1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Organisations.FindAsync(org.Id);
        updated!.AuditRetentionDays.Should().BeNull();
    }

    // ── POST /admin/organizations/{id}/suspend ────────────────────────────────

    [Fact]
    public async Task SuspendOrg_ExistingOrg_MarksOrgSuspended()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsync($"/admin/organizations/{org.Id}/suspend", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Organisations.FindAsync(org.Id);
        updated!.Active.Should().BeFalse();
        updated.SuspendedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UnsuspendOrg_SuspendedOrg_RestoresOrg()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();
        await client.PostAsync($"/admin/organizations/{org.Id}/suspend", null);

        var res = await client.PostAsync($"/admin/organizations/{org.Id}/unsuspend", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Organisations.FindAsync(org.Id);
        updated!.Active.Should().BeTrue();
    }

    // ── DELETE /admin/organizations/{id} ──────────────────────────────────────

    [Fact]
    public async Task DeleteOrg_ExistingOrg_Returns200AndRemovesFromDb()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, _)       = await fixture.Seed.CreateOrgAsync();

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Organisations.FindAsync(org.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteOrg_NonExistentId_Returns404()
    {
        var (_, _, client) = await SuperAdminClientAsync();

        var res = await client.DeleteAsync($"/admin/organizations/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Admin assignment ──────────────────────────────────────────────────────

    [Fact]
    public async Task AssignOrgAdmin_ValidUser_CreatesOrgRole()
    {
        var (admin, _, client) = await SuperAdminClientAsync();
        var (org, orgList)     = await fixture.Seed.CreateOrgAsync();
        var targetUser         = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();

        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id = targetUser.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);

        await fixture.RefreshDbAsync();
        var role = fixture.Db.OrgRoles.FirstOrDefault(r =>
            r.OrgId == org.Id && r.UserId == targetUser.Id);
        role.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveOrgAdmin_ExistingRole_DeletesIt()
    {
        var (_, _, client) = await SuperAdminClientAsync();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var targetUser     = await fixture.Seed.CreateUserAsync(orgList.Id);
        var role           = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");
        fixture.Keto.AllowAll();

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/admins/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.OrgRoles.FindAsync(role.Id);
        deleted.Should().BeNull();
    }
}
