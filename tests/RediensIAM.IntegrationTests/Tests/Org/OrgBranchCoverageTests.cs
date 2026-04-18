using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// Covers OrgController branches where only one path was exercised:
///   - PATCH /org/projects/{id}              — empty body (lines 151-164)
///   - PATCH /org/projects/{id}              — email_from_name / clear (lines 192-193)
///   - Various project endpoints             — project not found (lines 141, 150, 202, 210, 234, 252, 265)
///   - PUT /org/projects/{id}/userlist       — list not in org (line 254)
///   - DELETE /org/userlists/{id}            — not found (line 308)
///   - GET  /org/userlists/{id}/users        — not found (line 378)
///   - POST /org/userlists/{id}/users        — not found (line 394)
///   - PATCH /org/projects/{id}/saml-providers/{pid} — empty body (lines 826-833)
///   - PATCH /org/projects/{id}/saml-providers/{pid} — not found (line 825)
///   - GET  /org/admins                      — scope with missing project (line 644)
/// </summary>
[Collection("RediensIAM")]
public class OrgBranchCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PATCH /org/projects/{id} — empty body covers all false branches ────────

    [Fact]
    public async Task UpdateProject_EmptyBody_Returns200_CoversAllFalseBranches()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /org/projects/{id} — email_from_name field (lines 192-193) ──────

    [Fact]
    public async Task UpdateProject_SetEmailFromName_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            email_from_name = "My Application"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateProject_ClearEmailFromName_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.EmailFromName = "Old Name";
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            clear_email_from_name = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Project not-found guards ──────────────────────────────────────────────

    [Fact]
    public async Task GetProject_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateProject_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/org/projects/{Guid.NewGuid()}", new { name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProjectScopes_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/projects/{Guid.NewGuid()}/scopes");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateProjectScopes_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync($"/org/projects/{Guid.NewGuid()}/scopes",
            new { scopes = new[] { "openid" } });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT /org/projects/{id}/scopes — Hydra PATCH fails → catch block (line 222) ─

    [Fact]
    public async Task UpdateProjectScopes_HydraUpdateFails_LogsWarningAndReturnsOk()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientPatchFailure(project.HydraClientId!);
        try
        {
            var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/scopes",
                new { scopes = new[] { "read:data" } });

            // Hydra failure is caught and swallowed — response is still OK
            res.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            fixture.Hydra.RestoreClientPatch();
        }
    }

    [Fact]
    public async Task DeleteProject_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.DeleteAsync($"/org/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignUserList_ProjectNonExistent_Returns404()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{Guid.NewGuid()}/userlist",
            new { user_list_id = list.Id });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignUserList_ListNotInOrg_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        // Create a list in a DIFFERENT org
        var (otherOrg, _) = await fixture.Seed.CreateOrgAsync();
        var foreignList   = await fixture.Seed.CreateUserListAsync(otherOrg.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/userlist",
            new { user_list_id = foreignList.Id });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("userlist_not_in_org");
    }

    [Fact]
    public async Task UnassignUserList_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.DeleteAsync($"/org/projects/{Guid.NewGuid()}/userlist");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── UserList not-found guards ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteUserList_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.DeleteAsync($"/org/userlists/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListUsersInList_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/userlists/{Guid.NewGuid()}/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddUserToList_NonExistentList_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/org/userlists/{Guid.NewGuid()}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── SAML provider endpoints ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateSamlProvider_EmptyBody_Returns200_CoversAllFalseBranches()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Create SAML provider first
        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id    = "https://idp.example.com/saml",
            sso_url      = "https://idp.example.com/sso",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetGuid();

        // Patch with empty body → covers all false branches (826-833)
        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{providerId}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSamlProvider_NonExistent_Returns404()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{Guid.NewGuid()}", new
            {
                active = false
            });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListSamlProviders_NonExistentProject_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/projects/{Guid.NewGuid()}/saml-providers");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateSamlProvider_NonExistentProject_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/org/projects/{Guid.NewGuid()}/saml-providers", new
        {
            entity_id = "https://idp.example.com",
            sso_url   = "https://idp.example.com/sso"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /org/admins — scope pointing to deleted/missing project (line 644) ─

    [Fact]
    public async Task ListOrgAdmins_RoleWithMissingProject_ReturnsScopeNameNull()
    {
        // Covers OrgController line 644: TryGetValue returns false when project doesn't exist
        var (org, admin, client) = await OrgAdminClientAsync();

        // Directly seed an OrgRole with a ScopeId pointing to a non-existent project
        fixture.Db.OrgRoles.Add(new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            UserId    = admin.Id,
            Role      = "project_admin",
            ScopeId   = Guid.NewGuid(), // non-existent project
            GrantedAt = DateTimeOffset.UtcNow,
            GrantedBy = admin.Id,
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync("/org/admins");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var roles = body.EnumerateArray().ToList();
        // The role with missing project should have scope_name = null
        var orphanedRole = roles.FirstOrDefault(r => r.GetProperty("role").GetString() == "project_admin"
            && r.GetProperty("scope_name").ValueKind == JsonValueKind.Null);
        orphanedRole.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    // ── PATCH /org/projects/{id} — all fields provided (lines 152-164 TRUE branches) ─

    [Fact]
    public async Task UpdateProject_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            name                       = "Updated By Test",
            require_role_to_login      = true,
            require_mfa                = false,
            allow_self_registration    = false,
            email_verification_enabled = false,
            sms_verification_enabled   = false,
            active                     = true,
            allowed_email_domains      = Array.Empty<string>(),
            ip_allowlist               = Array.Empty<string>(),
            check_breached_passwords   = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /org/userlists/{id}/users/{uid} — all fields provided (lines 526-533, 540, 547) ─

    [Fact]
    public async Task UpdateUser_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new
        {
            email          = SeedData.UniqueEmail(),
            username       = "updateduser",
            display_name   = "",      // "" → sets to null (covers line 528 TRUE inner branch)
            phone          = "",      // "" → sets to null (covers line 529 TRUE inner branch)
            active         = false,   // false → DisabledAt = DateTimeOffset.UtcNow (covers line 540)
            email_verified = false,   // false → EmailVerifiedAt = null (covers line 547)
            clear_lock     = true,
            new_password   = "NewP@ssw0rd!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /org/projects/{id}/saml-providers/{pid} — all fields provided (lines 826-833 TRUE) ─

    [Fact]
    public async Task UpdateSamlProvider_AllFields_Returns200_CoversTrueBranches()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/saml",
            sso_url   = "https://idp.example.com/sso",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var providerId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{providerId}", new
            {
                entity_id                   = "https://idp.updated.com",
                sso_url                     = "https://idp.updated.com/sso",
                certificate_pem             = "MIIB...",  // dummy — not validated here
                email_attribute_name        = "mail",
                display_name_attribute_name = "cn",
                jit_provisioning            = true,
                active                      = false
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /org/info — org not found (line 53) ───────────────────────────────

    [Fact]
    public async Task GetOrgInfo_OrgNotFound_Returns404()
    {
        // Token with a non-existent OrgId → org == null → NotFound
        var userId = Guid.NewGuid();
        var fakeOrgId = Guid.NewGuid();
        var token = $"fake-{userId:N}";
        fixture.Hydra.RegisterToken(token, userId.ToString(), fakeOrgId.ToString(), null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/settings — org not found (line 61) ─────────────────────────

    [Fact]
    public async Task UpdateOrgSettings_OrgNotFound_Returns404()
    {
        var userId = Guid.NewGuid();
        var fakeOrgId = Guid.NewGuid();
        var token = $"fake2-{userId:N}";
        fixture.Hydra.RegisterToken(token, userId.ToString(), fakeOrgId.ToString(), null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = 30 });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /org/projects/{id}/saml-providers/{pid} — org mismatch (line 846 second condition) ─

    [Fact]
    public async Task DeleteSamlProvider_ProviderFromOtherOrg_Returns404()
    {
        // Create SAML provider in org A, try to delete using org B's token
        var (orgA, _, _) = await OrgAdminClientAsync();
        var projectA = await fixture.Seed.CreateProjectAsync(orgA.Id);

        // Create provider in org A
        var (orgB, orgBAdmin, clientB) = await OrgAdminClientAsync();
        var projectB = await fixture.Seed.CreateProjectAsync(orgB.Id);

        // Seed a SAML provider directly for projectA
        var provider = new SamlIdpConfig
        {
            Id        = Guid.NewGuid(),
            ProjectId = projectA.Id,
            EntityId  = "https://idp.orga.com",
            SsoUrl    = "https://idp.orga.com/sso",
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(provider);
        await fixture.Db.SaveChangesAsync();

        // Try to delete projectA's provider using orgB's client and projectB as the route param
        // The provider exists but provider.Project.OrgId (orgA) != OrgId (orgB) → 404
        var res = await clientB.DeleteAsync($"/org/projects/{projectA.Id}/saml-providers/{provider.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /org/userlists/{id}/export — list not found (line 859) ───────────

    [Fact]
    public async Task ExportUserList_NotFound_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/userlists/{Guid.NewGuid()}/export");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /org/userlists/{id}/users/{uid}/resend-invite — list not found (line 447) ─

    [Fact]
    public async Task ResendInvite_ListNotFound_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsync(
            $"/org/userlists/{Guid.NewGuid()}/users/{Guid.NewGuid()}/resend-invite",
            new StringContent(""));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /org/userlists/{id}/users/{uid}/resend-invite — user not found (line 450) ─

    [Fact]
    public async Task ResendInvite_UserNotFound_Returns404()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsync(
            $"/org/userlists/{list.Id}/users/{Guid.NewGuid()}/resend-invite",
            new StringContent(""));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /org/userlists/{id}/users/{uid} — user not found (line 596) ────

    [Fact]
    public async Task RemoveUser_UserNotFound_Returns404()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/userlists/{id}/users/{uid} — active=true covers line 540 TRUE branch ─

    [Fact]
    public async Task UpdateUser_ActiveTrue_SetsDisabledAtNull()
    {
        // Covers line 540: body.Active.Value ? null : DateTimeOffset.UtcNow (TRUE path → null)
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        // Pre-set DisabledAt so we can verify it gets cleared
        user.DisabledAt = DateTimeOffset.UtcNow.AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new
        {
            active = true   // TRUE → DisabledAt = null (line 540)
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.DisabledAt.Should().BeNull();
    }

    // ── PATCH /org/userlists/{id}/users/{uid} — email_verified=true covers line 547 TRUE branch ─

    [Fact]
    public async Task UpdateUser_EmailVerifiedTrue_SetsEmailVerifiedAt()
    {
        // Covers line 547: body.EmailVerified.Value ? DateTimeOffset.UtcNow : null (TRUE path)
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new
        {
            email_verified = true   // TRUE → EmailVerifiedAt = DateTimeOffset.UtcNow (line 547)
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.EmailVerifiedAt.Should().NotBeNull();
    }

    // ── GET /org/projects/{id} — project with LoginTheme set (line 141) ────────

    [Fact]
    public async Task GetProject_WithLoginTheme_StripSecretsReturnsNonNull()
    {
        // Covers line 141: StripSecretsFromTheme(LoginTheme) returns non-null (theme != null)
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.LoginTheme = new Dictionary<string, object> { ["color"] = "blue" };
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/org/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /org/projects/{id}/saml-providers — missing both metadataUrl and ssoUrl (line 796) ─

    [Fact]
    public async Task CreateSamlProvider_MissingBothUrls_ReturnsBadRequest()
    {
        // Covers line 796: both MetadataUrl and SsoUrl are empty
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id   = "https://idp.test.com"
            // no metadata_url, no sso_url
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("metadata_url_or_sso_url_required");
    }

    // ── PATCH /org/projects/{id}/saml-providers/{pid} — with MetadataUrl (line 827) ─

    [Fact]
    public async Task UpdateSamlProvider_WithMetadataUrl_Returns200()
    {
        // Covers line 827: body.MetadataUrl != null → provider.MetadataUrl = body.MetadataUrl
        var (org, _, client) = await OrgAdminClientAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var provider = new RediensIAM.Data.Entities.SamlIdpConfig
        {
            Id        = Guid.NewGuid(),
            ProjectId = project.Id,
            EntityId  = "https://idp.test.com",
            SsoUrl    = "https://idp.test.com/sso",
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(provider);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{provider.Id}", new
        {
            metadata_url = "https://idp.test.com/metadata"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /org/projects/{id}/saml-providers/{pid} — with DefaultRoleId (line 833) ─

    [Fact]
    public async Task UpdateSamlProvider_WithDefaultRoleId_Returns200()
    {
        // Covers line 833: body.DefaultRoleId.HasValue → provider.DefaultRoleId = body.DefaultRoleId
        var (org, _, client) = await OrgAdminClientAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "SamlRole");
        var provider = new RediensIAM.Data.Entities.SamlIdpConfig
        {
            Id        = Guid.NewGuid(),
            ProjectId = project.Id,
            EntityId  = "https://idp2.test.com",
            SsoUrl    = "https://idp2.test.com/sso",
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(provider);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{provider.Id}", new
        {
            default_role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /org/userlists/{id}/users/{uid} — user not found (line 482) ────

    [Fact]
    public async Task UpdateUserInList_UserNotFound_Returns404()
    {
        // Covers line 482: user == null → NotFound
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PatchAsJsonAsync(
            $"/org/userlists/{list.Id}/users/{Guid.NewGuid()}", new { active = true });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /org/smtp/test — actor not found (line 750) ─────────────────────

    [Fact]
    public async Task TestSmtp_ActorNotFound_ReturnsBadRequest()
    {
        // Covers line 750: actor == null → BadRequest "user_not_found"
        // Use a token with non-existent user ID
        var fakeUserId = Guid.NewGuid();
        var (org, _)   = await fixture.Seed.CreateOrgAsync();
        var token      = $"smtp-fake-{fakeUserId:N}";
        fixture.Hydra.RegisterToken(token, fakeUserId.ToString(), org.Id.ToString(), null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsync("/org/smtp/test", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("user_not_found");
    }

    // ── OrgId property — invalid Guid in claims (line 41 FALSE branch) ────────

    [Fact]
    public async Task GetOrgInfo_InvalidOrgIdClaim_Returns404()
    {
        // Covers line 41 FALSE: Guid.TryParse(Claims.OrgId) fails → OrgId = Guid.Empty
        // No org has Id = Guid.Empty, so the query returns null → 404
        var fakeUserId = Guid.NewGuid();
        var token      = $"badorgid-{fakeUserId:N}";
        fixture.Hydra.RegisterToken(token, fakeUserId.ToString(), "not-a-valid-guid", null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/admins/{id} — same ScopeId (line 644 AND condition FALSE) ──

    [Fact]
    public async Task UpdateOrgListManager_SameScopeId_Returns200()
    {
        // Covers line 644 uncovered condition: body.ScopeId != null && body.ScopeId == role.ScopeId
        // → whole AND is FALSE → no project-existence check → update succeeds
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target         = await fixture.Seed.CreateUserAsync(orgList.Id);
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // Seed an org role with a ScopeId (project-scoped)
        var role = new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            UserId    = target.Id,
            Role      = "org_admin",
            ScopeId   = project.Id,
            GrantedBy = admin.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.OrgRoles.Add(role);
        await fixture.Db.SaveChangesAsync();

        // PATCH with the same ScopeId → condition is FALSE → no project check → 200
        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            scope_id = project.Id   // same as role.ScopeId → body.ScopeId != role.ScopeId is FALSE
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
