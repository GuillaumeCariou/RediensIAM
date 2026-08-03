using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ManagedApi;

// ── from ManagedApiTests.cs ───────────────────────────────

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
        // Omitting the password is what turns this into an invite, and the invite send is the only
        // caller of ManagedApiServices.Email.
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

// ── from ManagedApiCoverageTests.cs ───────────────────────

/// <summary>
/// Covers /api/manage lines not hit by ManagedApiTests. The handlers now live on
/// SystemAdminController — /api/manage is its second route prefix, not a second controller.
///   - POST /api/manage/organizations/{id}/projects — Hydra unavailable (lines 131-136)
/// </summary>
[Collection("RediensIAM")]
public class ManagedApiCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── POST /api/manage/organizations/{id}/projects — Hydra failure ──────────

    [Fact]
    public async Task CreateProject_WhenHydraUnavailable_Returns502AndRollsBack()
    {
        var (org, client) = await SuperAdminClientAsync();

        fixture.Hydra.SetupClientCreationFailure();
        try
        {
            var res = await client.PostAsJsonAsync($"/api/manage/organizations/{org.Id}/projects", new
            {
                name = "Hydra-fail project",
                slug = SeedData.UniqueSlug()
            });

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("hydra_unavailable");
        }
        finally
        {
            // Restore default client creation stub — does NOT reset token stubs
            fixture.Hydra.RestoreClientCreation();
        }
    }
}

// ── from ManagedApiMoreCoverageTests.cs ───────────────────

/// <summary>
/// Covers /api/manage lines not hit by existing tests. The handlers now live on
/// SystemAdminController — /api/manage is its second route prefix, not a second controller.
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
