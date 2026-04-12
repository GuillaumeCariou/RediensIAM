using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ManagedApi;

/// <summary>
/// Covers ManagedApiController lines not hit by existing tests:
///   - POST /api/manage/userlists/{id}/users — system list (OrgId=null, Immovable=true) → super_admin keto tuple (line 188)
///   - POST /api/manage/userlists/{id}/users — list with assigned project → default role (line 192)
/// </summary>
[Collection("RediensIAM")]
public class ManagedApiMoreCoverageTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── POST /api/manage/userlists/{id}/users — system list (line 188) ────────

    [Fact]
    public async Task AddUser_SystemList_WritesSuperAdminTuple()
    {
        var client = await SuperAdminClientAsync();

        var systemList = new RediensIAM.Data.Entities.UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-api-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{systemList.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── POST /api/manage/userlists/{id}/users — list with assigned project (line 192) ─

    [Fact]
    public async Task AddUser_ListWithAssignedProject_AssignsDefaultRole()
    {
        var client = await SuperAdminClientAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
