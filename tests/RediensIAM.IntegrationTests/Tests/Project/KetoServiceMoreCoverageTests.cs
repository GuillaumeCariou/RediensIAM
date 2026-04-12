using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Keto;

/// <summary>
/// Second batch of KetoService coverage tests, targeting branches not yet
/// exercised by KetoServiceCoverageTests.cs:
///
///   - GetActorManagementLevelForOrgAsync: OrgAdmin branch (line 79), None branch (line 82)
///   - AssignProjectRoleAsync: role-not-in-project (line 96), user-not-in-list (line 117)
///   - AssignManagementRoleAsync: no-management-rights (line 164), unknown-role (line 168),
///       insufficient-level (line 175), existing-role-update (lines 184-187)
///   - ValidateProjectAdminScopeAsync: success path — closing brace (line 219)
///   - RemoveManagementRoleAsync: no-rights (line 225), own-role (line 228),
///       SuperAdmin/OrgAdmin/ProjectAdmin switch branches + rank check (lines 235-241)
/// </summary>
[Collection("RediensIAM")]
public class KetoServiceMoreCoverageTests(TestFixture fixture)
{
    // ── GetActorManagementLevelForOrgAsync — OrgAdmin branch (line 79) ────────

    /// <summary>
    /// When Keto denies super_admin but allows org_admin, the method returns
    /// OrgAdmin (line 79). Triggered via DELETE /org/admins/{id}.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_ActorIsOrgAdminInKeto_Returns204()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor     = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target    = await fixture.Seed.CreateUserAsync(orgList.Id);
        var orgRole   = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "org_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        // Deny super_admin → falls through to org_admin check which AllowAll passes
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{orgRole.Id}");

        // actor=OrgAdmin(2), target=OrgAdmin(2) → 2 < 2 is false → proceeds to delete
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── GetActorManagementLevelForOrgAsync — None branch (line 82) ───────────

    /// <summary>
    /// When Keto denies both super_admin and org_admin checks, and the actor
    /// has no ProjectAdmin OrgRole in DB, the method returns None (line 82) and
    /// RemoveManagementRoleAsync throws ForbiddenException → 403.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_ActorHasNoManagementRights_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var orgRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "org_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        // Deny both → no DB role → ManagementLevel.None
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{orgRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── AssignProjectRoleAsync — role not in project (line 96) ───────────────

    /// <summary>
    /// Providing a roleId that belongs to a different project triggers the
    /// "Role does not belong to this project" check (line 96) → 400.
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_RoleFromDifferentProject_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var otherProject = await fixture.Seed.CreateProjectAsync(org.Id);
        var roleInOtherProject = await fixture.Seed.CreateRoleAsync(otherProject.Id, "OtherRole", rank: 10);

        var manager    = await fixture.Seed.CreateUserAsync(list.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);
        var token      = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles",
            new { role_id = roleInOtherProject.Id });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Role does not belong to this project");
    }

    // ── AssignProjectRoleAsync — user not in project list (line 117) ──────────

    /// <summary>
    /// When the target user is not in the project's assigned UserList, the
    /// service throws BadRequestException (line 117) → 400.
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_UserNotInProjectList_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "Tester", rank: 50);
        var manager = await fixture.Seed.CreateUserAsync(list.Id);

        // Target user is in a DIFFERENT list, not the project's assigned list
        var otherList = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(otherList.Id);

        var token  = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles",
            new { role_id = role.Id });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("User is not in this project's assigned UserList");
    }

    // ── AssignManagementRoleAsync — no management rights (line 164) ───────────

    /// <summary>
    /// Actor whose Keto checks all deny and has no DB OrgRole → ManagementLevel.None
    /// → ForbiddenException at line 164 → 403.
    /// </summary>
    [Fact]
    public async Task AssignOrgAdmin_ActorHasNoRights_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        // Deny all Keto checks → ManagementLevel.None
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = target.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── AssignManagementRoleAsync — unknown role (line 168) ───────────────────

    /// <summary>
    /// Actor is OrgAdmin in Keto → management level check passes, but role string
    /// is not recognized by the switch expression → BadRequestException (line 168).
    /// </summary>
    [Fact]
    public async Task AssignOrgAdmin_UnknownRole_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        // Deny super_admin → OrgAdmin branch passes → unknown role triggers switch default
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = target.Id,
            role    = "unknown_role"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── AssignManagementRoleAsync — insufficient level (line 175) ────────────

    /// <summary>
    /// A ProjectAdmin (level 3) cannot assign OrgAdmin (level 2) because
    /// OrgAdmin(2) &lt; ProjectAdmin(3) → ForbiddenException (line 175) → 403.
    /// </summary>
    [Fact]
    public async Task AssignOrgAdmin_ProjectAdminActorAssignsHigherRank_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        // Actor has a ProjectAdmin OrgRole in DB
        await fixture.Seed.CreateOrgRoleAsync(org.Id, actor.Id, "project_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        // Deny super_admin and org_admin → DB check returns ProjectAdmin (level 3)
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        // "org_admin" rank=2 < ProjectAdmin rank=3 → insufficient level
        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = target.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("insufficient management level");
    }

    // ── AssignManagementRoleAsync — existing role update path (lines 184-187) ─

    /// <summary>
    /// When the same (user, org, role, scope) combination already exists,
    /// AssignManagementRoleAsync takes the existing-role branch (lines 184-187):
    /// updates DisplayName and saves without touching Keto.
    /// </summary>
    [Fact]
    public async Task AssignOrgAdmin_ExistingRole_UpdatesDisplayName_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        // Pre-seed the role so the duplicate path fires
        await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "org_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id      = target.Id,
            role         = "org_admin",
            display_name = "Updated Name"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── ValidateProjectAdminScopeAsync — success path (line 219) ─────────────

    /// <summary>
    /// ProjectAdmin actor assigns project_admin for exactly their scoped project.
    /// ValidateProjectAdminScopeAsync passes (line 219) and the role is assigned.
    /// </summary>
    [Fact]
    public async Task AssignOrgAdmin_ProjectAdminAssignsSameScope_Succeeds()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target  = await fixture.Seed.CreateUserAsync(orgList.Id);

        // Actor's OrgRole is scoped to project
        await fixture.Seed.CreateOrgRoleAsync(org.Id, actor.Id, "project_admin", project.Id);

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        // scope_id = same project → ValidateProjectAdminScopeAsync succeeds
        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id  = target.Id,
            role     = "project_admin",
            scope_id = project.Id
        });

        // Assignment succeeds
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── RemoveManagementRoleAsync — actor removes own role (line 228) ─────────

    /// <summary>
    /// When the actor tries to delete their own OrgRole, RemoveManagementRoleAsync
    /// throws ForbiddenException at line 228 → 403.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_ActorRemovesOwnRole_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var ownRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, actor.Id, "org_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{ownRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("Cannot remove your own management role");
    }

    // ── RemoveManagementRoleAsync — SuperAdmin role rank check (lines 235, 241) ─

    /// <summary>
    /// OrgAdmin actor (level 2) trying to remove a SuperAdmin org role (rank 1).
    /// SuperAdmin(1) &lt; OrgAdmin(2) → ForbiddenException at line 241 → 403.
    /// Also covers the `Roles.SuperAdmin => ManagementLevel.SuperAdmin` switch branch (line 235).
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_OrgAdminRemovesSuperAdminRole_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor    = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target   = await fixture.Seed.CreateUserAsync(orgList.Id);
        // Seed a "super_admin" OrgRole for the target (DB only — Keto is stubbed)
        var superAdminRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "super_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{superAdminRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("insufficient management level");
    }

    // ── RemoveManagementRoleAsync — OrgAdmin role (line 237) ─────────────────

    /// <summary>
    /// OrgAdmin actor removes another user's OrgAdmin role.
    /// Switch branch `Roles.OrgAdmin => ManagementLevel.OrgAdmin` (line 237) is reached.
    /// OrgAdmin(2) not &lt; OrgAdmin(2) → rank check passes → 204.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_OrgAdminRoleTarget_Returns204()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var orgRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "org_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{orgRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── RemoveManagementRoleAsync — ProjectAdmin role (line 237) ─────────────

    /// <summary>
    /// OrgAdmin actor removes a ProjectAdmin org role.
    /// Switch branch `Roles.ProjectAdmin => ManagementLevel.ProjectAdmin` (line 237).
    /// ProjectAdmin(3) not &lt; OrgAdmin(2) → rank check passes → 204.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_ProjectAdminRoleTarget_Returns204()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var orgRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "project_admin");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{orgRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── RemoveManagementRoleAsync — unknown role default branch (line 238) ────

    /// <summary>
    /// An OrgRole with a custom/unknown role string hits the `_ => ManagementLevel.None`
    /// switch branch (line 238). None(99) is not &lt; OrgAdmin(2) → rank check passes → 204.
    /// </summary>
    [Fact]
    public async Task RemoveOrgAdmin_CustomRoleTarget_DefaultSwitchBranch_Returns204()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target  = await fixture.Seed.CreateUserAsync(orgList.Id);
        // "legacy_role" matches no case in the switch → default `_ => ManagementLevel.None(99)`
        var orgRole = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, "legacy_role");

        var token  = fixture.Seed.OrgAdminToken(actor.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{actor.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{orgRole.Id}");

        // None(99) not < OrgAdmin(2) → no rank throw → proceeds and deletes
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── AssignProjectRoleAsync — ProjectAdmin rank check closing brace (line 111) ─

    /// <summary>
    /// ProjectAdmin actor has existing roles, but target role rank >= actor's min rank
    /// so the ForbiddenException is NOT thrown. The `if (actorRoles.Count > 0)` body
    /// executes without throwing → covering the closing brace at line 111.
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_ProjectAdmin_TargetRoleEqualRank_Succeeds()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var manager    = await fixture.Seed.CreateUserAsync(list.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);

        // Actor's own role — rank 50; target role also rank 50 → 50 < 50 is false → no throw
        var actorRole  = await fixture.Seed.CreateRoleAsync(project.Id, "ManagerRole2", rank: 50);
        var targetRole = await fixture.Seed.CreateRoleAsync(project.Id, "SameRankRole", rank: 50);

        // Seed actor's UserProjectRole so actorRoles.Count > 0
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id        = Guid.NewGuid(),
            UserId    = manager.Id,
            ProjectId = project.Id,
            RoleId    = actorRole.Id,
            GrantedBy = manager.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var token = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{manager.Id}");
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles",
            new { role_id = targetRole.Id });

        // Rank check passes → assignment succeeds
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
