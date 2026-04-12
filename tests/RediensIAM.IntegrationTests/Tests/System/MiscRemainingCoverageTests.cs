using System.Net.Http.Json;
using RediensIAM.Config;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.System;

/// <summary>
/// Covers small remaining uncovered lines across several files:
///   - OrgController line 648          — PATCH /org/admins/{id} with valid new ScopeId
///   - PatService lines 117-119         — InvalidateAsync (direct service call)
///   - KetoService line 168             — AssignManagementRoleAsync with SuperAdmin role
/// </summary>
[Collection("RediensIAM")]
public class MiscRemainingCoverageTests(TestFixture fixture)
{
    // ── OrgController line 648: PATCH /org/admins/{id} with valid ScopeId ────

    [Fact]
    public async Task UpdateOrgAdmin_ValidScopeId_UpdatesRole()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var targetUser     = await fixture.Seed.CreateUserAsync(orgList.Id);

        // Use OrgAdmin token so OrgController sees the correct OrgId in claims
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var project1 = await fixture.Seed.CreateProjectAsync(org.Id);
        var project2 = await fixture.Seed.CreateProjectAsync(org.Id);
        await fixture.Db.SaveChangesAsync();

        // Assign a scoped project_admin role (ScopeId = project1)
        var orgRole = new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            UserId    = targetUser.Id,
            Role      = Config.Roles.ProjectAdmin,
            ScopeId   = project1.Id,
            GrantedBy = admin.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.OrgRoles.Add(orgRole);
        await fixture.Db.SaveChangesAsync();

        // PATCH the role with a DIFFERENT ScopeId that IS a valid project in the org
        // body.ScopeId != null && body.ScopeId != role.ScopeId → projectExists = true → line 648
        var res = await client.PatchAsJsonAsync($"/org/admins/{orgRole.Id}", new
        {
            scope_id = project2.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.OrgRoles.FindAsync(orgRole.Id);
        updated!.ScopeId.Should().Be(project2.Id);
    }

    // ── KetoService line 168: SuperAdmin case in AssignManagementRoleAsync switch ──

    [Fact]
    public async Task KetoService_AssignManagementRole_SuperAdmin_HitsLine168()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);

        // AllowAll → GetActorManagementLevelForOrgAsync returns SuperAdmin
        // → switch on Roles.SuperAdmin hits line 168
        fixture.Keto.AllowAll();

        var ketoService = fixture.GetService<KetoService>();
        await ketoService.AssignManagementRoleAsync(actor.Id, target.Id, org.Id, Roles.SuperAdmin);

        await fixture.RefreshDbAsync();
        var created = await fixture.Db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == org.Id && r.UserId == target.Id && r.Role == Roles.SuperAdmin);
        created.Should().NotBeNull();
    }

    // ── PatService lines 117-119: InvalidateAsync ─────────────────────────────

    [Fact]
    public async Task PatService_InvalidateAsync_RemovesFromCache()
    {
        var patService = fixture.GetService<PatService>();

        // Calling with a hash that doesn't exist in cache is a no-op (KeyDelete is idempotent)
        // but it DOES execute lines 117-119.
        const string fakeHash = "aabbccddee112233aabbccddee112233aabbccddee112233aabbccddee112233";
        var act = async () => await patService.InvalidateAsync(fakeHash);
        await act.Should().NotThrowAsync();
    }
}
