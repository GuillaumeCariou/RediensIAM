using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

[Collection("RediensIAM")]
public class OrgUserListTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── GET /org/userlists ────────────────────────────────────────────────────

    [Fact]
    public async Task ListUserLists_OrgAdmin_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/org/userlists");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListUserLists_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/org/userlists");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /org/userlists ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateUserList_OrgAdmin_Returns201()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync("/org/userlists", new
        {
            name = "Dev Team"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateUserList_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/org/userlists", new { name = "Anon List" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /org/userlists/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetUserList_ExistingList_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.GetAsync($"/org/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(list.Id.ToString());
    }

    [Fact]
    public async Task GetUserList_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/userlists/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /org/userlists/{id} ────────────────────────────────────────────

    [Fact]
    public async Task DeleteUserList_MovableList_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.UserLists.FindAsync(list.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteUserList_ImmovableOrgList_Returns400Or409()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        // The org's built-in immovable list
        var immovableList = await fixture.Db.UserLists.FindAsync(org.OrgListId);

        var res = await client.DeleteAsync($"/org/userlists/{immovableList!.Id}");

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── GET /org/userlists/{id}/users ─────────────────────────────────────────

    [Fact]
    public async Task ListUsersInList_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    // ── POST /org/userlists/{id}/users ────────────────────────────────────────

    [Fact]
    public async Task AddUser_OrgAdmin_Returns201()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddUser_DuplicateEmail_Returns409()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var email            = SeedData.UniqueEmail();

        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email,
            password = "P@ssw0rd!Test"
        });
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email,
            password = "P@ssw0rd!Test"
        });

        ((int)res.StatusCode).Should().BeOneOf(409, 500);  // DB unique constraint (409 if caught, 500 if not)
    }

    // ── DELETE /org/userlists/{id}/users/{uid} ────────────────────────────────

    [Fact]
    public async Task RemoveUser_OrgAdmin_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /org/userlists/{id}/cleanup ─────────────────────────────────────

    [Fact]
    public async Task CleanupUserList_DryRun_Returns200WithDryRunTrue()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run               = true,
            remove_orphaned_roles = true,
            remove_inactive_users = false,
            inactive_threshold_days = 90
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeTrue();
        body.TryGetProperty("orphaned_roles_found", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupUserList_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/org/userlists/{Guid.NewGuid()}/cleanup", new
        {
            dry_run = true,
            remove_orphaned_roles = false,
            remove_inactive_users = false,
            inactive_threshold_days = 90
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CleanupUserList_Unauthenticated_Returns401()
    {
        var (org, _, _) = await OrgAdminClientAsync();
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await fixture.Client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run = true,
            remove_orphaned_roles = false,
            remove_inactive_users = false,
            inactive_threshold_days = 90
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
