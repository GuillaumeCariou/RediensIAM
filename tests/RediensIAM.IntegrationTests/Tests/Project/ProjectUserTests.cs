using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

[Collection("RediensIAM")]
public class ProjectUserTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list, User manager, HttpClient client)>
        ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, list, manager, fixture.ClientWithToken(token));
    }

    // ── GET /project/users ────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_ProjectManager_Returns200()
    {
        var (_, _, _, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/project/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListUsers_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/project/users");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /project/users/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetUser_ExistingUser_Returns200()
    {
        var (_, _, list, _, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/project/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(user.Id.ToString());
    }

    [Fact]
    public async Task GetUser_NonExistent_Returns404()
    {
        var (_, _, _, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/project/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/users ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateUser_ProjectManager_Returns201()
    {
        var (_, _, _, _, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateUser_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /project/users/{id}/roles ────────────────────────────────────────

    [Fact]
    public async Task AssignRole_ValidRoleAndUser_Returns200()
    {
        var (_, project, list, _, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Tester");

        var res = await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new
        {
            role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssignRole_NonExistentRole_Returns404()
    {
        var (_, _, list, _, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new
        {
            role_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /project/users/{id}/roles/{roleId} ─────────────────────────────

    [Fact]
    public async Task RemoveRole_ExistingAssignment_Returns200()
    {
        var (org, project, list, _, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Tester");

        await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new { role_id = role.Id });

        var res = await client.DeleteAsync($"/project/users/{user.Id}/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /project/users/{id}/sessions ───────────────────────────────────

    [Fact]
    public async Task ForceLogout_ProjectManager_Returns200()
    {
        var (_, _, list, _, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.DeleteAsync($"/project/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Third instance of the shape already fixed in both stats handlers: a project with no user
    /// list assigned is an ordinary first-run state, not a missing resource. Answering 404 here
    /// also takes down the roles request beside it — they share a Promise.all in the console — so
    /// the whole members panel renders empty on every freshly created project.
    /// </summary>
    [Fact]
    public async Task ListUsers_ProjectWithNoUserList_ReturnsEmptyRatherThanNotFound()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = null;
        await fixture.Db.SaveChangesAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id));

        var res = await client.GetAsync($"/project/users?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(0);
    }
}
