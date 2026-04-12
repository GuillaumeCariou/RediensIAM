using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ManagedApi;

/// <summary>
/// TODO2: /api/manage/* endpoints — machine-to-machine provisioning API.
///
/// Accessible on the public port (:5000) with a super_admin PAT or
/// client_credentials token carrying super_admin role.
/// Org admins and unauthenticated callers are denied.
///
/// Endpoints under test:
///   GET  /api/manage/organizations
///   POST /api/manage/organizations
///   GET  /api/manage/organizations/{id}
///   GET  /api/manage/organizations/{id}/projects
///   POST /api/manage/organizations/{id}/projects
///   POST /api/manage/userlists
///   POST /api/manage/userlists/{id}/users
/// </summary>
[Collection("RediensIAM")]
public class ManagedApiTests(TestFixture fixture)
{
    // ── Scaffold helpers ──────────────────────────────────────────────────────

    private async Task<(User admin, HttpClient client)> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (admin, fixture.ClientWithToken(token));
    }

    private async Task<(User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (admin, fixture.ClientWithToken(token));
    }

    // ── GET /api/manage/organizations ─────────────────────────────────────────

    [Fact]
    public async Task ListOrgs_SuperAdmin_Returns200WithArray()
    {
        var (_, client) = await SuperAdminClientAsync();

        var res = await client.GetAsync("/api/manage/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListOrgs_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/api/manage/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListOrgs_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/api/manage/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/manage/organizations ────────────────────────────────────────

    [Fact]
    public async Task CreateOrg_SuperAdmin_Returns201WithId()
    {
        var (_, client) = await SuperAdminClientAsync();

        var res = await client.PostAsJsonAsync("/api/manage/organizations", new
        {
            name = SeedData.UniqueName(),
            slug = SeedData.UniqueSlug(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out var idProp).Should().BeTrue();
        idProp.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateOrg_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync("/api/manage/organizations", new
        {
            name = SeedData.UniqueName(),
            slug = SeedData.UniqueSlug(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /api/manage/organizations/{id} ────────────────────────────────────

    [Fact]
    public async Task GetOrg_SuperAdmin_Returns200()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync("Get Org Test");

        var res = await client.GetAsync($"/api/manage/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be("Get Org Test");
    }

    [Fact]
    public async Task GetOrg_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.GetAsync($"/api/manage/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /api/manage/organizations/{id}/projects ───────────────────────────

    [Fact]
    public async Task ListProjects_SuperAdmin_Returns200WithProjects()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateProjectAsync(org.Id, "Managed Project");

        var res = await client.GetAsync($"/api/manage/organizations/{org.Id}/projects");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.EnumerateArray().Should().Contain(p => p.GetProperty("name").GetString() == "Managed Project");
    }

    [Fact]
    public async Task ListProjects_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.GetAsync($"/api/manage/organizations/{org.Id}/projects");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /api/manage/organizations/{id}/projects ──────────────────────────

    [Fact]
    public async Task CreateProject_SuperAdmin_Returns201WithId()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync($"/api/manage/organizations/{org.Id}/projects", new
        {
            name                 = SeedData.UniqueName(),
            slug                 = SeedData.UniqueSlug(),
            require_role_to_login = false,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out var idProp).Should().BeTrue();
        idProp.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateProject_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync($"/api/manage/organizations/{org.Id}/projects", new
        {
            name                 = SeedData.UniqueName(),
            slug                 = SeedData.UniqueSlug(),
            require_role_to_login = false,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /api/manage/userlists ────────────────────────────────────────────

    [Fact]
    public async Task CreateUserList_SuperAdmin_Returns201WithId()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync("/api/manage/userlists", new
        {
            name   = SeedData.UniqueName(),
            org_id = org.Id,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out var idProp).Should().BeTrue();
        idProp.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateUserList_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync("/api/manage/userlists", new
        {
            name   = SeedData.UniqueName(),
            org_id = org.Id,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /api/manage/userlists/{id}/users ─────────────────────────────────

    [Fact]
    public async Task AddUser_SuperAdmin_Returns201WithUserId()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);
        var email       = SeedData.UniqueEmail();

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email,
            password = "ManagedP@ss1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out var idProp).Should().BeTrue();
        idProp.GetString().Should().NotBeNullOrEmpty();

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == email);
        user.Should().NotBeNull();
        user!.UserListId.Should().Be(list.Id);
    }

    [Fact]
    public async Task AddUser_OrgAdmin_Returns403()
    {
        var (_, client) = await OrgAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "ManagedP@ss1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddUser_DuplicateEmail_Returns409()
    {
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);
        var email       = SeedData.UniqueEmail();

        await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email,
            password = "ManagedP@ss1!"
        });

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email,
            password = "ManagedP@ss1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Invite flow — covers ManagedApiServices.Email ─────────────────────────

    [Fact]
    public async Task AddUser_WithoutPassword_SendsInviteEmailAndReturns201()
    {
        // Omitting password → isInvite = true → emailService.SendInviteAsync called
        // Covers ControllerServices.cs:99 (ManagedApiServices.Email property)
        var (_, client) = await SuperAdminClientAsync();
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);
        var email       = SeedData.UniqueEmail();

        var res = await client.PostAsJsonAsync($"/api/manage/userlists/{list.Id}/users", new
        {
            email,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeTrue();
        fixture.EmailStub.SentInvites.Should().Contain(i => i.To == email);
    }
}
