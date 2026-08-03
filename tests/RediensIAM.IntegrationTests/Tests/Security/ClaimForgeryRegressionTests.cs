using RediensIAM.Config;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// R-23 + T-N3 — the claim-forgery chain.
///
/// Project role names were stored verbatim and copied straight into <c>ext.roles</c> at consent.
/// A project_admin — the lowest management tier — could create a role literally named
/// <c>super_admin</c>, assign it, and have RediensIAM sign an access token asserting
/// platform-administrator authority to every resource server that validates locally against JWKS.
///
/// Two independent defects, and the chain needs both closed:
///   R-23  the name was never validated → reserve the management names at creation;
///   T-N3  tenant role names carried no tenant qualifier, so tenant A's "admin" and tenant B's
///         "admin" were the same string at every consumer → emit them as {project_id}/{name}.
/// </summary>
[Collection("RediensIAM")]
public class ClaimForgeryRegressionTests(TestFixture fixture)
{
    private async Task<(Organisation Org, Project Project, UserList List)> CreateTenantAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── R-23: reserved role names ────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.OrgAdmin)]
    [InlineData(Roles.ProjectAdmin)]
    [InlineData("SUPER_ADMIN")]
    public async Task CreateProjectRole_NamedAfterAManagementRole_IsRefused(string name)
    {
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PostAsJsonAsync("/project/roles", new { name });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("role_name_reserved");
        (await fixture.Db.Roles.AnyAsync(r => r.ProjectId == tenant.Project.Id && r.Name == name))
            .Should().BeFalse("a tenant role must never be able to carry a management role's name");
    }

    /// <summary>The admin surface creates project roles too, and had the same gap.</summary>
    [Fact]
    public async Task AdminCreateProjectRole_NamedAfterAManagementRole_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PostAsJsonAsync(
            $"/admin/projects/{tenant.Project.Id}/roles", new { name = Roles.SuperAdmin });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>'/' separates the project scope from the name in the claim — it cannot appear in one.</summary>
    [Fact]
    public async Task CreateProjectRole_ContainingTheNamespaceSeparator_IsRefused()
    {
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PostAsJsonAsync("/project/roles",
            new { name = $"{Guid.NewGuid()}/admin" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateProjectRole_WithAnOrdinaryName_StillSucceeds()
    {
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PostAsJsonAsync("/project/roles", new { name = "editor" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── T-N3: the role claim is namespaced by project ────────────────────────

    /// <summary>
    /// The claim RediensIAM actually signs. A bare "admin" would be indistinguishable from every
    /// other tenant's "admin" in a resource server's ClaimsPrincipal.
    /// </summary>
    [Fact]
    public async Task Consent_EmitsTenantRolesQualifiedByProject()
    {
        var tenant = await CreateTenantAsync();
        var user   = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var role   = await fixture.Seed.CreateRoleAsync(tenant.Project.Id, "admin");
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id = Guid.NewGuid(), UserId = user.Id, ProjectId = tenant.Project.Id,
            RoleId = role.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(),
            tenant.Project.HydraClientId, tenant.Project.Id.ToString(), tenant.Org.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        ((int)res.StatusCode).Should().BeLessThan(400);
        var body = fixture.Hydra.AcceptedConsentBody(challenge);
        body.Should().NotBeNull();
        body.Should().Contain($"{tenant.Project.Id}/admin",
            "tenant role names must be qualified by the project that defined them");
        body.Should().NotContain("\"admin\"",
            "a bare tenant role name in ext.roles has no tenant boundary at any consumer");
    }
}
