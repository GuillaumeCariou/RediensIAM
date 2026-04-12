using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// Covers OrgController project paths not exercised by existing test files:
///   - POST /org/projects — Hydra client creation failure → 502 (line 123)
///   - GET  /org/projects — SuperAdmin with org_id query param (line 80)
///   - PATCH /org/projects/{id} — valid DefaultRoleId path (lines 175-179)
///   - PATCH /org/projects/{id} — invalid DefaultRoleId → 400 (line 177)
/// </summary>
[Collection("RediensIAM")]
public class OrgProjectCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── POST /org/projects — Hydra failure (line 123) ────────────────────────

    [Fact]
    public async Task CreateProject_HydraFails_Returns502()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        fixture.Hydra.SetupClientCreationFailure();
        try
        {
            var res = await client.PostAsJsonAsync("/org/projects", new
            {
                org_id = org.Id,
                name   = "Fail Project",
                slug   = SeedData.UniqueSlug()
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

    // ── GET /org/projects — SuperAdmin with org_id param (line 80) ───────────

    [Fact]
    public async Task ListProjects_SuperAdmin_WithOrgIdParam_Returns200()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateProjectAsync(org.Id);

        // SuperAdmin token has no org_id in claims
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync($"/org/projects?org_id={org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── PATCH /org/projects/{id} — valid DefaultRoleId (lines 175-179) ───────

    [Fact]
    public async Task UpdateProject_WithValidDefaultRoleId_Returns200AndSetsRole()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            default_role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.DefaultRoleId.Should().Be(role.Id);
    }

    // ── PATCH /org/projects/{id} — invalid DefaultRoleId → 400 (line 177) ────

    [Fact]
    public async Task UpdateProject_WithInvalidDefaultRoleId_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            default_role_id = Guid.NewGuid()   // non-existent role
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_default_role");
    }
}
