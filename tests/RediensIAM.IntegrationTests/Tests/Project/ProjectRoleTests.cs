using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

[Collection("RediensIAM")]
public class ProjectRoleTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, HttpClient client)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, fixture.ClientWithToken(token));
    }

    // ── GET /project/roles ────────────────────────────────────────────────────

    [Fact]
    public async Task ListRoles_ProjectManager_Returns200()
    {
        var (_, project, client) = await ScaffoldAsync();
        await fixture.Seed.CreateRoleAsync(project.Id, "Dev");

        var res = await client.GetAsync("/project/roles");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListRoles_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/project/roles");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /project/roles ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateRole_ProjectManager_Returns201()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/project/roles", new
        {
            name = "QA Engineer",
            rank = 50
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRole_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/project/roles", new
        {
            name = "Ghost Role",
            rank = 10
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /project/roles/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateRole_ProjectManager_Returns200()
    {
        var (_, project, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "RoleToUpdate");

        var res = await client.PatchAsJsonAsync($"/project/roles/{role.Id}", new
        {
            rank = 75  // UpdateRoleRequest supports rank and description, not name
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Roles.FindAsync(role.Id);
        updated!.Rank.Should().Be(75);
    }

    [Fact]
    public async Task UpdateRole_NonExistent_Returns404()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync($"/project/roles/{Guid.NewGuid()}", new
        {
            rank = 1
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /project/roles/{id} ────────────────────────────────────────────

    [Fact]
    public async Task DeleteRole_ProjectManager_Returns200()
    {
        var (_, project, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "ToDelete");

        var res = await client.DeleteAsync($"/project/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Roles.FindAsync(role.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRole_NonExistent_Returns404()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/project/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
