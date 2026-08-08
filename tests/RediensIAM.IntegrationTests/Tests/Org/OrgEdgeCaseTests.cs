using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Data.Entities;

namespace RediensIAM.IntegrationTests.Tests.Org;

// ── from OrgBranchCoverageTests.cs ───────────────────────────────

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

        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id    = "https://idp.example.com/saml",
            sso_url      = "https://idp.example.com/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetGuid();

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
            sso_url   = "https://idp.example.com/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /org/admins — scope pointing to deleted/missing project (line 644) ─

    [Fact]
    public async Task ListOrgAdmins_RoleWithMissingProject_ReturnsScopeNameNull()
    {
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
            display_name   = "",      // "" clears the field rather than leaving it unchanged
            phone          = "",      // "" clears the field rather than leaving it unchanged
            active         = false,   // stamps DisabledAt
            email_verified = false,   // clears EmailVerifiedAt
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
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
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
        var userId = Guid.NewGuid();
        var fakeOrgId = Guid.NewGuid();
        var token = $"fake-{userId:N}";
        fixture.Hydra.RegisterToken(token, userId.ToString(), fakeOrgId.ToString(), null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/org/info");

        // The claim names a well-formed org that simply does not exist: the live check passes
        // (Keto allows) and the controller finds no row.
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
        var (orgA, _, _) = await OrgAdminClientAsync();
        var projectA = await fixture.Seed.CreateProjectAsync(orgA.Id);

        var (orgB, orgBAdmin, clientB) = await OrgAdminClientAsync();
        var projectB = await fixture.Seed.CreateProjectAsync(orgB.Id);

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

        // Org B's admin naming org A's project and provider directly. The row exists, so the 404
        // has to come from provider.Project.OrgId (orgA) != the caller's OrgId (orgB) — not from
        // a missing row.
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
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        // Pre-set DisabledAt so we can verify it gets cleared
        user.DisabledAt = DateTimeOffset.UtcNow.AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new
        {
            active = true
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
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new
        {
            email_verified = true
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
    public async Task GetOrgInfo_InvalidOrgIdClaim_Returns403()
    {
        // An org_admin claim whose org_id does not parse is refused outright: the live check no
        // longer degrades to "admin of any org" (R-01, link 3).
        var fakeUserId = Guid.NewGuid();
        var token      = $"badorgid-{fakeUserId:N}";
        fixture.Hydra.RegisterToken(token, fakeUserId.ToString(), "not-a-valid-guid", null, ["org_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── PATCH /org/admins/{id} — same ScopeId (line 644 AND condition FALSE) ──

    /// <summary>
    /// Re-sending the scope a role already carries must not be treated as a scope change: the
    /// project-existence check is skipped, so the update succeeds without a second lookup. Guards
    /// against a regression that would make an unchanged PATCH fail whenever the scoped project
    /// has since been renamed or is momentarily unreadable.
    /// </summary>
    [Fact]
    public async Task UpdateOrgListManager_SameScopeId_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target         = await fixture.Seed.CreateUserAsync(orgList.Id);
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

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

        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            scope_id = role.ScopeId
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ── from OrgCoverageTests.cs ─────────────────────────────────────

/// <summary>
/// Covers OrgController lines not hit by existing test files:
///   - GET /org/userlists/{id}/users/{uid}/sessions (lines 553-565)
///   - DELETE /org/userlists/{id}/users/{uid}/sessions (lines 569-577)
///   - POST /org/smtp/test failure path (lines 756-759)
///   - GET /org/userlists/{id}/export rate-limit (line 862)
///   - GET /org/userlists/{id}/export CSV format (line 884)
/// </summary>
[Collection("RediensIAM")]
public class OrgCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, User user, HttpClient client)> ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        return (org, list, user, client);
    }

    // ── GET /org/userlists/{id}/users/{uid}/sessions ──────────────────────────

    [Fact]
    public async Task ListUserSessions_ExistingUser_Returns200WithArray()
    {
        var (_, list, user, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListUserSessions_NonExistentUser_Returns404()
    {
        var (_, list, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /org/userlists/{id}/users/{uid}/sessions ───────────────────────

    [Fact]
    public async Task RevokeUserSessions_ExistingUser_Returns200()
    {
        var (_, list, user, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("sessions_revoked");
    }

    [Fact]
    public async Task RevokeUserSessions_NonExistentUser_Returns404()
    {
        var (_, list, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /org/smtp/test — failure path (lines 756-759) ───────────────────

    [Fact]
    public async Task TestSmtp_WhenEmailServiceThrows_Returns400WithSmtpTestFailed()
    {
        var (_, _, _, client) = await ScaffoldAsync();

        fixture.EmailStub.ThrowOnNextSend = new InvalidOperationException("Connection refused");

        var res = await client.PostAsync("/org/smtp/test", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("smtp_test_failed");
    }

    // ── GET /org/userlists/{id}/export — rate limit ───────────────────────────

    [Fact]
    public async Task ExportUserList_SecondCallInWindow_Returns429()
    {
        var (_, list, _, client) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var first = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── GET /org/userlists/{id}/export — CSV with special chars ──────────────

    [Fact]
    public async Task ExportUserList_UserWithCommaInDisplayName_ReturnsCsvWithQuoting()
    {
        var (_, list, user, client) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        user.DisplayName = "Smith, John";
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await res.Content.ReadAsStringAsync();
        csv.Should().Contain("\"Smith, John\"");
    }
}

// ── from OrgExtendedTests.cs ─────────────────────────────────────

/// <summary>
/// Covers OrgController endpoints that were not exercised by the original test files:
///   - PATCH /org/settings
///   - PUT /org/smtp (update path)
///   - DELETE /org/smtp
///   - POST /org/smtp/test
///   - GET/PATCH /org/users/{uid}
///   - PATCH /org/userlists/{id}/users/{uid}
///   - GET/PUT /org/projects/{id}/scopes
///   - GET /org/audit-log
///   - GET /org/audit-log/export (JSON + CSV)
///   - GET /org/userlists/{id}/export?format=json
///   - POST/PATCH/DELETE /org/projects/{id}/saml-providers
/// </summary>
[Collection("RediensIAM")]
public class OrgExtendedTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PATCH /org/settings ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateOrgSettings_SetRetentionDays_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = 90 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("audit_retention_days").GetInt32().Should().Be(90);
    }

    [Fact]
    public async Task UpdateOrgSettings_ResetRetentionDays_Returns200WithNull()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        // -1 means "reset to global default" (stored as null)
        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = -1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("audit_retention_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── PUT /org/smtp — UPDATE path (config already exists) ──────────────────

    [Fact]
    public async Task UpdateSmtp_ExistingConfig_OverwritesAndReturns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.v1.com", port = 587, start_tls = true,
            username = "u@v1.com", password = "p1",
            from_address = "no@v1.com", from_name = "V1"
        });

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.v2.com", port = 465, start_tls = false,
            username = "u@v2.com", password = "p2",
            from_address = "no@v2.com", from_name = "V2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRes = await client.GetAsync("/org/smtp");
        var body   = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("host").GetString().Should().Be("smtp.v2.com");
        body.GetProperty("port").GetInt32().Should().Be(465);
    }

    // ── DELETE /org/smtp ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSmtp_ExistingConfig_Returns204()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.del.com", port = 587, start_tls = true,
            username = "u@del.com", from_address = "no@del.com", from_name = "Del"
        });

        var res = await client.DeleteAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSmtp_NoConfig_Returns204()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.DeleteAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /org/smtp/test ───────────────────────────────────────────────────

    [Fact]
    public async Task TestSmtp_AuthenticatedActor_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsync("/org/smtp/test", null);

        // StubEmailService never throws → 200
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("test_email_sent");
    }

    // ── GET /org/users/{uid} ──────────────────────────────────────────────────

    [Fact]
    public async Task GetOrgUser_ExistingUser_Returns200WithOrgRoles()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/org/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(user.Id.ToString());
        body.TryGetProperty("roles", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetOrgUser_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/users/{uid} ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateOrgUser_ChangeDisplayName_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { display_name = "Updated Name" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("display_name").GetString().Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateOrgUser_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{Guid.NewGuid()}", new { display_name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOrgUser_SetPhone_PersistsValue()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { phone = "+1-555-0200" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().Be("+1-555-0200");
    }

    [Fact]
    public async Task UpdateOrgUser_ClearPhone_SetsNull()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);
        user.Phone = "+1-555-0200";
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { phone = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().BeNull();
    }

    // ── PATCH /org/userlists/{id}/users/{uid} ─────────────────────────────────

    [Fact]
    public async Task UpdateUserListUser_ChangeActive_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new { active = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── GET /org/projects/{id}/scopes ─────────────────────────────────────────

    [Fact]
    public async Task GetProjectScopes_ExistingProject_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/org/projects/{project.Id}/scopes");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("custom_scopes", out _).Should().BeTrue();
        body.TryGetProperty("built_in", out _).Should().BeTrue();
    }

    // ── PUT /org/projects/{id}/scopes ─────────────────────────────────────────

    [Fact]
    public async Task UpdateProjectScopes_ValidScopes_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/scopes",
            new { scopes = new[] { "read:data", "write:data" } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("custom_scopes").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task UpdateProjectScopes_InvalidScopeName_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/scopes",
            new { scopes = new[] { "INVALID SCOPE!" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_scope_names");
    }

    // ── GET /org/audit-log ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_OrgAdmin_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/org/audit-log?limit=10");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── GET /org/audit-log/export ─────────────────────────────────────────────

    [Fact]
    public async Task ExportAuditLog_JsonFormat_Returns200WithAttachment()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync("/org/audit-log/export?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task ExportAuditLog_CsvFormat_Returns200WithCsv()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync("/org/audit-log/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        var content = await res.Content.ReadAsStringAsync();
        content.Should().StartWith("id,action,");
    }

    [Fact]
    public async Task ExportAuditLog_RateLimited_Returns429()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        // The export limit is one request per window, so the first call consumes the whole budget.
        await client.GetAsync("/org/audit-log/export?format=csv");
        var res = await client.GetAsync("/org/audit-log/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ExportAuditLog_WithDateRange_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        var to   = DateTimeOffset.UtcNow.ToString("O");

        var res = await client.GetAsync(
            $"/org/audit-log/export?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /org/userlists/{id}/export?format=json ────────────────────────────

    [Fact]
    public async Task ExportUserList_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/export?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // ── POST /org/projects/{id}/saml-providers ────────────────────────────────

    [Fact]
    public async Task CreateSamlProvider_ValidRequest_Returns201()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/entity",
            sso_url   = "https://idp.example.com/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSamlProvider_MissingEntityId_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "",
            sso_url   = "https://idp.example.com/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("entity_id_required");
    }

    [Fact]
    public async Task CreateSamlProvider_MissingUrlConfig_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/entity"
            // neither metadata_url nor sso_url
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("metadata_url_or_sso_url_required");
    }

    // ── PATCH /org/projects/{id}/saml-providers/{pid} ─────────────────────────

    [Fact]
    public async Task UpdateSamlProvider_ChangeSsoUrl_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.update.com",
            sso_url   = "https://idp.update.com/sso/v1",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });
        var providerId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{providerId}",
            new { sso_url = "https://idp.update.com/sso/v2", active = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── DELETE /org/projects/{id}/saml-providers/{pid} ────────────────────────

    [Fact]
    public async Task DeleteSamlProvider_ExistingProvider_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.delete.com",
            sso_url   = "https://idp.delete.com/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nc3R1Yg==\n-----END CERTIFICATE-----"
        });
        var providerId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var res = await client.DeleteAsync($"/org/projects/{project.Id}/saml-providers/{providerId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

// ── from OrgMoreCoverageTests.cs ─────────────────────────────────

/// <summary>
/// Covers OrgController lines not yet hit by existing test files:
///   - PATCH /org/projects/{id}          — clear_default_role = true (line 173)
///   - DELETE /org/projects/{id}         — Hydra client delete failure (line 238)
///   - DELETE /org/userlists/{id}        — assigned to project → 400 (line 311)
///   - POST /org/userlists/{id}/users    — list assigned to project → assigns default role (line 414)
///   - POST /org/userlists/{id}/cleanup  — dry_run=false, orphaned roles removed (lines 335, 360-365, 372)
///   - POST /org/userlists/{id}/cleanup  — dry_run=false, inactive users removed (lines 350-353, 366-370, 372)
///   - PATCH /org/users/{uid}            — email_verified=false (lines 546-547)
///   - PATCH /org/admins/{id}            — new ScopeId not in org → 400 (lines 645-648)
/// </summary>
[Collection("RediensIAM")]
public class OrgMoreCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PATCH /org/projects/{id} — clear_default_role = true (line 173) ──────

    [Fact]
    public async Task UpdateProject_ClearDefaultRole_SetsRoleToNull()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "Starter");
        role.IsDefault = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeFalse();
    }

    // ── DELETE /org/projects/{id} — Hydra client delete failure (line 238) ───

    [Fact]
    public async Task DeleteProject_HydraClientDeleteFails_StillReturns204()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);
        try
        {
            var res = await client.DeleteAsync($"/org/projects/{project.Id}");
            res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            fixture.Hydra.RestoreClientCreation();
        }
    }

    // ── DELETE /org/userlists/{id} — assigned to project (line 311) ──────────

    [Fact]
    public async Task DeleteUserList_WhenAssignedToProject_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("userlist_is_assigned_to_project");
    }

    // ── POST /org/userlists/{id}/users — list assigned to project (line 414) ─

    [Fact]
    public async Task AddUserToList_WithProjectAssigned_AssignsDefaultRole()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        // Adding a user to a list that's assigned to a project triggers keto.AssignDefaultRoleAsync
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssword1!Long"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── POST /org/userlists/{id}/cleanup — dry_run=false removes orphaned roles (lines 335, 360-365, 372) ─

    [Fact]
    public async Task CleanupUserList_DryRunFalse_WithOrphanedRoles_RemovesThem()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        var orphanUser = await fixture.Seed.CreateUserAsync(otherList.Id);
        var role       = await fixture.Seed.CreateRoleAsync(project.Id, "Orphan");

        // Seed an orphaned UserProjectRole directly — user is not in 'list'
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id        = Guid.NewGuid(),
            UserId    = orphanUser.Id,
            ProjectId = project.Id,
            RoleId    = role.Id,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run               = false,
            remove_orphaned_roles = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeFalse();
        body.GetProperty("orphaned_roles_removed").GetInt32().Should().Be(1);
    }

    // ── POST /org/userlists/{id}/cleanup — dry_run=false removes inactive users (lines 350-353, 366-370, 372) ─

    [Fact]
    public async Task CleanupUserList_DryRunFalse_WithInactiveUsers_RemovesThem()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        // New users have LastLoginAt = null → always inactive
        var inactiveUser = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run               = false,
            remove_inactive_users = true,
            inactive_threshold_days = 0    // threshold = today → all users with null LastLoginAt qualify
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeFalse();
        body.GetProperty("inactive_users_removed").GetInt32().Should().BeGreaterThan(0);
    }

    // ── PATCH /org/users/{uid} — email_verified = false (lines 546-547) ──────

    [Fact]
    public async Task UpdateOrgUser_SetEmailVerifiedFalse_ClearsVerifiedAt()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.EmailVerified   = true;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new
        {
            email_verified = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.FindAsync(user.Id);
        reloaded!.EmailVerified.Should().BeFalse();
        reloaded.EmailVerifiedAt.Should().BeNull();
    }

    // ── PATCH /org/admins/{id} — ScopeId not in org → 400 (lines 645-648) ───

    [Fact]
    public async Task UpdateOrgAdmin_WithInvalidScopeId_Returns400()
    {
        var (org, admin, client) = await OrgAdminClientAsync();
        var (otherOrg, otherList) = await fixture.Seed.CreateOrgAsync();
        var targetUser = await fixture.Seed.CreateUserAsync(otherList.Id);

        // Create an OrgRole with no ScopeId so we can try to patch it with an invalid scope
        var role = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            scope_id = Guid.NewGuid()   // project not in this org
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_in_org");
    }
}

// ── from OrgProjectCoverageTests.cs ──────────────────────────────

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
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeTrue();
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
