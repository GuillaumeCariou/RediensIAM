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
        // Enveloppe, comme aux deux autres portées : la recherche est partagée, donc la réponse
        // aussi — sans quoi la console aurait trois formes à lire pour une même page.
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("users").ValueKind.Should().Be(JsonValueKind.Array);
        body.TryGetProperty("total", out _).Should().BeTrue();
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
        body.GetProperty("users").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// Le filtre par rôle n'existe qu'à cette portée : un rôle n'a de sens que dans son projet, et
    /// deux locataires peuvent en nommer un pareil.
    /// </summary>
    [Fact]
    public async Task ListUsers_FilteredByRole_ReturnsOnlyItsHolders()
    {
        var (org, project, list, _, client) = await ScaffoldAsync();
        var holder = await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "porteur");
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            UserId = holder.Id, ProjectId = project.Id, RoleId = role.Id,
            GrantedBy = holder.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var res  = await client.GetAsync($"/project/users?project_id={project.Id}&role_id={role.Id}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("users")[0].GetProperty("id").GetString().Should().Be(holder.Id.ToString());
    }

    /// <summary>
    /// Les rôles projetés sont ceux de CE projet. Le panneau des membres en fait des puces qu'on
    /// retire ; celles d'un autre projet y seraient à la fois fausses et irretirables.
    /// </summary>
    [Fact]
    public async Task ListUsers_DoesNotShowRolesOfAnotherProject()
    {
        var (org, project, list, _, client) = await ScaffoldAsync();
        var other  = await fixture.Seed.CreateProjectAsync(org.Id);
        var member = await fixture.Seed.CreateUserAsync(list.Id);
        var ailleurs = await fixture.Seed.CreateRoleAsync(other.Id, "ailleurs");
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            UserId = member.Id, ProjectId = other.Id, RoleId = ailleurs.Id,
            GrantedBy = member.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var res  = await client.GetAsync($"/project/users?project_id={project.Id}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var mine = body.GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == member.Id.ToString());
        mine.GetProperty("roles").GetArrayLength().Should().Be(0);
    }
}
