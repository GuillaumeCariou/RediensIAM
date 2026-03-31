using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

[Collection("RediensIAM")]
public class OrgProjectTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── GET /org/projects ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListProjects_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        await fixture.Seed.CreateProjectAsync(org.Id, "Visible Project");

        var res = await client.GetAsync("/org/projects");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListProjects_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/org/projects");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /org/projects ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_OrgAdmin_Returns201()
    {
        var (org, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync("/org/projects", new
        {
            org_id = org.Id,
            name   = "New Project",
            slug   = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateProject_Unauthenticated_Returns401()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();

        var res = await fixture.Client.PostAsJsonAsync("/org/projects", new
        {
            org_id = org.Id,
            name   = "Anon Project",
            slug   = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /org/projects/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task GetProject_ExistingProject_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/org/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(project.Id.ToString());
    }

    [Fact]
    public async Task GetProject_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/projects/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateProject_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            name = "Updated Name"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.Name.Should().Be("Updated Name");
    }

    // ── DELETE /org/projects/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.DeleteAsync($"/org/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Projects.FindAsync(project.Id);
        deleted.Should().BeNull();
    }

    // ── User list assignment ──────────────────────────────────────────────────

    [Fact]
    public async Task AssignUserList_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/userlist", new
        {
            user_list_id = list.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.AssignedUserListId.Should().Be(list.Id);
    }

    [Fact]
    public async Task UnassignUserList_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/org/projects/{project.Id}/userlist");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
