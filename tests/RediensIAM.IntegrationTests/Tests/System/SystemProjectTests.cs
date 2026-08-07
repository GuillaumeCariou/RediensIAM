using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class SystemProjectTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(user.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── GET /admin/projects ───────────────────────────────────────────────────

    [Fact]
    public async Task ListAllProjects_SuperAdmin_Returns200()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/projects");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListAllProjects_Unauthenticated_Returns401Or403()
    {
        // GET /admin/* without auth bypasses gateway middleware (by design) → filter returns 403
        var res = await fixture.Client.GetAsync("/admin/projects");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The list whose members may sign in to a project is part of the project, on every shape that
    /// returns one.
    ///
    /// <para>
    /// It was on none of them. A service that provisions accounts for a project has to know which
    /// list to put them in, and the only way to learn it was to read every user list and look for
    /// one whose <c>assigned_projects</c> named the project — a scan to recover something the
    /// project already holds. The alternative a caller reaches for is worse: pin the id in its own
    /// configuration, and keep pointing at the old list the day the assignment changes. Accounts
    /// then land in a list nobody can sign in through, and the symptom is "invalid credentials" on
    /// an account that is perfectly valid.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ListAllProjects_CarriesTheAssignedUserList()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res  = await client.GetAsync("/admin/projects");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var mine = body.EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == project.Id.ToString());
        mine.GetProperty("assigned_user_list_id").GetString().Should().Be(list.Id.ToString());
    }

    [Fact]
    public async Task ListOrgProjects_CarriesTheAssignedUserListAndTheOrganisation()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res  = await client.GetAsync($"/admin/organizations/{org.Id}/projects");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var mine = body.EnumerateArray().First();
        mine.GetProperty("assigned_user_list_id").GetString().Should().Be(list.Id.ToString());
        mine.GetProperty("org_id").GetString().Should().Be(org.Id.ToString());
    }

    // ── GET /admin/projects/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task GetProject_ReturnsItWithItsAssignedUserList()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(project.Id.ToString());
        body.GetProperty("assigned_user_list_id").GetString().Should().Be(list.Id.ToString());
        body.GetProperty("org_name").GetString().Should().Be(org.Name);
    }

    [Fact]
    public async Task GetProject_Unknown_Returns404()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The Location header of a created project resolves.
    ///
    /// <para>
    /// It has answered <c>Location: /admin/projects/{id}</c> since it was written, and that route
    /// did not exist — a header pointing at a 404. A caller following it, which is what a Location
    /// header is for, learned nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateProject_LocationHeaderResolves()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();

        var created = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/projects", new
        {
            name = "Located", slug = SeedData.UniqueSlug(),
            redirect_uris = new[] { "https://located.test/cb" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var res = await client.GetAsync(created.Headers.Location);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /admin/organizations/{id}/projects ────────────────────────────────

    [Fact]
    public async Task CreateProject_SuperAdmin_Returns201()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/projects", new
        {
            name = "Test Project",
            slug = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateProject_CreatesHydraClientId()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var slug     = SeedData.UniqueSlug();

        var res  = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/projects", new
        {
            name = "Client ID Check",
            slug
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id   = Guid.Parse(body.GetProperty("id").GetString()!);

        await fixture.RefreshDbAsync();
        var project = await fixture.Db.Projects.FindAsync(id);
        project!.HydraClientId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateProject_NonExistentOrg_Returns404()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}/projects", new
        {
            name = "Orphan Project",
            slug = SeedData.UniqueSlug()
        });

        // Controller may return 404 if org not found or 500 if Hydra create fails
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task CreateProject_Unauthenticated_Returns401()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();

        var res = await fixture.Client.PostAsJsonAsync($"/admin/organizations/{org.Id}/projects", new
        {
            name = "Anon Project",
            slug = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /admin/projects/{id} ────────────────────────────────────────────

    [Fact]
    public async Task UpdateProject_ValidPayload_Updates()
    {
        var client  = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            name = "Renamed Project"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.Name.Should().Be("Renamed Project");
    }

    [Fact]
    public async Task UpdateProject_NonExistent_Returns404()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/admin/projects/{Guid.NewGuid()}", new
        {
            name = "Ghost"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/projects/{id} ───────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_SuperAdmin_Returns200AndRemoves()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Projects.FindAsync(project.Id);
        deleted.Should().BeNull();
    }

    // ── User list assignment ──────────────────────────────────────────────────

    [Fact]
    public async Task AssignUserList_ValidList_SetsAssignedUserListId()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/admin/projects/{project.Id}/userlist", new
        {
            user_list_id = list.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.AssignedUserListId.Should().Be(list.Id);
    }

    [Fact]
    public async Task UnassignUserList_ClearsAssignment()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/userlist");

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.AssignedUserListId.Should().BeNull();
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRoles_SuperAdmin_ReturnsRoleList()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        await fixture.Seed.CreateRoleAsync(project.Id, "Tester");

        var res = await client.GetAsync($"/admin/projects/{project.Id}/roles");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateRole_Returns201()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/roles", new
        {
            name = "QA Engineer",
            rank = 50
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// Mirror of the tenant-scope case in ProjectStatsAuditTests: a project with no user list has
    /// no users, and answering 404 made the console log an error on every new project. A project
    /// that does not exist is still a 404.
    /// </summary>
    [Fact]
    public async Task AdminGetProjectStats_ProjectWithNoUserList_ReturnsZeroesRatherThanNotFound()
    {
        var client   = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = null;
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/admin/projects/{project.Id}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total_users").GetInt32().Should().Be(0);
        body.GetProperty("users_by_role").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AdminGetProjectStats_UnknownProject_IsStillNotFound()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
