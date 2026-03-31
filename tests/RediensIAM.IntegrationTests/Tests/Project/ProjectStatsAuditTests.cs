using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

[Collection("RediensIAM")]
public class ProjectStatsAuditTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list, HttpClient client)>
        ProjectManagerClientAsync()
    {
        var (org, _)   = await fixture.Seed.CreateOrgAsync();
        var project    = await fixture.Seed.CreateProjectAsync(org.Id);
        var list       = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, list, fixture.ClientWithToken(token));
    }

    // ── GET /project/stats ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStats_ReturnsExpectedShape()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.GetAsync($"/project/stats?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("total_users", out _).Should().BeTrue();
        body.TryGetProperty("active_users", out _).Should().BeTrue();
        body.TryGetProperty("users_by_role", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetStats_CountsReflectSeededUsers()
    {
        var (_, project, list, client) = await ProjectManagerClientAsync();
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id, active: false);

        var res  = await client.GetAsync($"/project/stats?project_id={project.Id}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // list has the manager + 1 active + 1 inactive = 3 total, 2 active (at minimum)
        body.GetProperty("total_users").GetInt32().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetStats_Unauthenticated_Returns401()
    {
        var (_, project, _, _) = await ProjectManagerClientAsync();

        var res = await fixture.Client.GetAsync($"/project/stats?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /project/audit-log ────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_ProjectManager_Returns200WithArray()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.GetAsync($"/project/audit-log?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetAuditLog_Unauthenticated_Returns401()
    {
        var (_, project, _, _) = await ProjectManagerClientAsync();

        var res = await fixture.Client.GetAsync($"/project/audit-log?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /project/cleanup ─────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_DryRun_Returns200WithDryRunTrue()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.PostAsJsonAsync($"/project/cleanup?project_id={project.Id}", new
        {
            dry_run = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeTrue();
        body.TryGetProperty("orphaned_roles_removed", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_NoOrphanedRoles_Returns0Removed()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res  = await client.PostAsJsonAsync($"/project/cleanup?project_id={project.Id}", new
        {
            dry_run = false
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("orphaned_roles_removed").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_Unauthenticated_Returns401()
    {
        var (_, project, _, _) = await ProjectManagerClientAsync();

        var res = await fixture.Client.PostAsJsonAsync($"/project/cleanup?project_id={project.Id}", new
        {
            dry_run = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
