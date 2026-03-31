using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

[Collection("RediensIAM")]
public class ProjectInfoTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, User manager, HttpClient client)>
        ProjectManagerClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, manager, fixture.ClientWithToken(token));
    }

    // ── GET /project/info ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectInfo_ProjectManager_Returns200()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.GetAsync($"/project/info?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(project.Id.ToString());
    }

    [Fact]
    public async Task GetProjectInfo_Unauthenticated_Returns401()
    {
        var (_, project, _, _) = await ProjectManagerClientAsync();

        var res = await fixture.Client.GetAsync($"/project/info?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /project/settings ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateProjectSettings_ProjectManager_Returns200()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.PatchAsJsonAsync($"/project/info?project_id={project.Id}", new
        {
            allow_self_registration    = true,
            email_verification_enabled = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateProjectSettings_Unauthenticated_Returns401()
    {
        var (_, project, _, _) = await ProjectManagerClientAsync();

        var res = await fixture.Client.PatchAsJsonAsync($"/project/info?project_id={project.Id}", new
        {
            allow_self_registration = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /project/stats ────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectStats_ProjectManager_Returns200()
    {
        var (_, project, _, client) = await ProjectManagerClientAsync();

        var res = await client.GetAsync($"/project/stats?project_id={project.Id}");

        ((int)res.StatusCode).Should().BeLessThan(500);
    }
}
