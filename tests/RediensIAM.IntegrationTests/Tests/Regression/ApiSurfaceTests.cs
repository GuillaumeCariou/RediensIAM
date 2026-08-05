using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Step 19 — the API surface: audience binding (P-06), /api/authorize object scoping (P-05),
/// the MFA-disable guard, and /api/manage ↔ /admin parity.
///
/// Stays under Regression: it crosses Api (introspect/authorize), Project (the require_mfa
/// setting) and ManagedApi (the /api/manage ↔ /admin twin), and the three only make sense read
/// as one API-surface change.
/// </summary>
[Collection("RediensIAM")]
public class ApiSurfaceIntrospectionTests(TestFixture fixture)
{
    private static FormUrlEncodedContent Form(string token, string? projectId)
    {
        var fields = new List<KeyValuePair<string, string>> { new("token", token) };
        if (projectId != null) fields.Add(new("project_id", projectId));
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>A service account on the __system__ list — the deployment-scoped gateway credential.</summary>
    private async Task<HttpClient> SystemGatewayAsync()
    {
        var systemList = new UserList
        {
            Id = Guid.NewGuid(), Name = SeedData.UniqueName(), OrgId = null,
            Immovable = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var sa       = await fixture.Seed.CreateServiceAccountAsync(systemList.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "system-gateway", null, null);
        return fixture.ClientWithToken(raw);
    }

    private async Task<(Organisation Org, Project Project, UserList List)> TenantAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── P-06: tenant binding ───────────────────────────────────────────────

    /// <summary>
    /// The breaking half of the change. A resource server that declares no project id is refused
    /// outright rather than served the pre-1 answer, because being served is what let it believe
    /// a token from another tenant was its own.
    /// </summary>
    [Fact]
    public async Task Introspect_WithoutAudience_IsRefused()
    {
        var gateway  = await SystemGatewayAsync();
        var tenant   = await TenantAsync();
        var user     = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var token    = fixture.Seed.UserToken(user.Id, tenant.Org.Id, tenant.Project.Id);

        var res = await gateway.PostAsync("/api/introspect", Form(token, null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an undeclared project id is a defect in the caller, not a statement about the token");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_id_required");
        body.GetProperty("ver").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Authorize_WithoutAudience_IsRefused()
    {
        var gateway = await SystemGatewayAsync();
        var tenant  = await TenantAsync();

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token      = "anything",
            @namespace = Roles.KetoOrgsNamespace,
            @object    = tenant.Org.Id.ToString(),
            relation   = Roles.KetoOrgAdminRelation,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("project_id_required");
    }

    /// <summary>
    /// P-06 itself. One deployment-scoped credential, two tenants: declaring tenant A's project
    /// must not resolve tenant B's token. Before the fix both answered active:true and the
    /// resource server was expected to notice the project_id mismatch on its own.
    /// </summary>
    [Fact]
    public async Task Introspect_SystemGateway_ResolvesOnlyTheTenantItDeclares()
    {
        fixture.Keto.AllowAll();
        var gateway = await SystemGatewayAsync();
        var mine    = await TenantAsync();
        var theirs  = await TenantAsync();

        var theirUser  = await fixture.Seed.CreateUserAsync(theirs.List.Id);
        var theirToken = fixture.Seed.UserToken(theirUser.Id, theirs.Org.Id, theirs.Project.Id);

        var wrong = await (await gateway.PostAsync("/api/introspect", Form(theirToken, mine.Project.Id.ToString())))
            .Content.ReadFromJsonAsync<JsonElement>();
        wrong.GetProperty("active").GetBoolean().Should().BeFalse(
            "a deployment-scoped gateway credential must not resolve every tenant's token at once");
        wrong.GetProperty("ver").GetInt32().Should().Be(2);

        var right = await (await gateway.PostAsync("/api/introspect", Form(theirToken, theirs.Project.Id.ToString())))
            .Content.ReadFromJsonAsync<JsonElement>();
        right.GetProperty("active").GetBoolean().Should().BeTrue(
            "the same credential still serves the tenant it names");
        right.GetProperty("project_id").GetString().Should().Be(theirs.Project.Id.ToString());
    }

    /// <summary>
    /// The org id is the other accepted form — a gateway fronting a whole organisation
    /// rather than one application. It is still a tenant, so it still cannot name someone else's.
    /// </summary>
    [Fact]
    public async Task Introspect_OrganisationAudience_IsAccepted()
    {
        var gateway = await SystemGatewayAsync();
        var tenant  = await TenantAsync();
        var user    = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var token   = fixture.Seed.UserToken(user.Id, tenant.Org.Id, tenant.Project.Id);

        var body = await (await gateway.PostAsync("/api/introspect", Form(token, tenant.Org.Id.ToString())))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Introspect_AudienceMismatch_IsAudited()
    {
        var gateway = await SystemGatewayAsync();
        var mine    = await TenantAsync();
        var theirs  = await TenantAsync();
        var user    = await fixture.Seed.CreateUserAsync(theirs.List.Id);
        var token   = fixture.Seed.UserToken(user.Id, theirs.Org.Id, theirs.Project.Id);

        await gateway.PostAsync("/api/introspect", Form(token, mine.Project.Id.ToString()));

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "api.introspect.project_mismatch").Should().BeTrue(
            "probing tenants by project id must leave a trace");
    }

    // ── P-05: /api/authorize object scoping ──────────────────────────────────

    /// <summary>
    /// P-05. The subject is always the presented token's user, so the answer was never a forged
    /// decision — but an org-scoped gateway could walk another tenant's relation graph one bit
    /// per request. Keto is set to allow everything so the refusal can only come from the
    /// controller.
    /// </summary>
    [Fact]
    public async Task Authorize_ObjectInAnotherTenant_IsRefusedEvenWhenKetoWouldAllow()
    {
        fixture.Keto.AllowAll();
        var mine   = await TenantAsync();
        var theirs = await TenantAsync();

        var sa       = await fixture.Seed.CreateServiceAccountAsync(mine.List.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "gateway", null, null);
        var gateway  = fixture.ClientWithToken(raw);

        var user  = await fixture.Seed.CreateUserAsync(mine.List.Id);
        var token = $"p05-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), mine.Org.Id.ToString(), mine.Project.Id.ToString(), []);

        var foreign = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoProjectsNamespace,
            @object    = theirs.Project.Id.ToString(),
            relation   = Roles.KetoManagerRelation,
            project_id = mine.Project.Id,
        });
        (await foreign.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse(
                "the object belongs to another organisation; Keto must never be asked");

        var own = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoProjectsNamespace,
            @object    = mine.Project.Id.ToString(),
            relation   = Roles.KetoManagerRelation,
            project_id = mine.Project.Id,
        });
        (await own.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeTrue(
                "the caller's own objects still answer normally");

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "api.authorize.object_out_of_scope").Should().BeTrue();
    }

    /// <summary>
    /// The deployment-scoped credential is scoped by the project id it declared rather than left
    /// unscoped, which is where P-05 and P-06 meet: without the project id there is no tenant to
    /// scope the object to.
    /// </summary>
    /// <summary>
    /// The System namespace is refused to a deployment-scoped caller, not only to tenant ones.
    ///
    /// <para><b>This is defence in depth, not an exploit regression — and the distinction is the
    /// point.</b> Writing SECURITY.md turned up a fail-open in <c>IsObjectInScopeAsync</c>: with
    /// no org on the caller and none on the subject token it returned true outright, and the
    /// System refusal above it was conditioned on the caller having a scope. Reaching it needs a
    /// token carrying neither <c>org_id</c> nor <c>project_id</c>, which
    /// <c>IsBoundToAudienceAsync</c> can only admit through <c>subject.Audiences</c> — and
    /// nothing in this codebase populates <c>grant_access_token_audience</c>. P-06's tenant
    /// requirement, added in the same release, refuses that token shape upstream, so the
    /// fail-open is unreachable today.</para>
    ///
    /// <para>It is closed anyway, because it becomes live the moment OAuth2 audiences start
    /// being minted. This test pins the invariant so that change does not silently reopen it;
    /// it does not claim to reproduce a vulnerability, and it passed before the fix as well.</para>
    /// </summary>
    [Fact]
    public async Task Authorize_SystemNamespace_IsRefusedRegardlessOfCallerScope()
    {
        fixture.Keto.AllowAll();
        var gateway = await SystemGatewayAsync();
        var tenant  = await TenantAsync();

        var user  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var token = $"p05sys-sysns-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), tenant.Org.Id.ToString(), tenant.Project.Id.ToString(), []);

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoSystemNamespace,
            @object    = Roles.KetoSystemObject,
            relation   = Roles.SuperAdmin,
            project_id = tenant.Project.Id,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should()
            .BeFalse("Keto says yes — the refusal has to come from the controller, not the store");
    }

    /// <summary>
    /// The other half of the same fail-open, and unlike the System-namespace half this one is
    /// live. <c>IsObjectInScopeAsync</c> answered <i>true</i> whenever the caller had no
    /// organisation and the subject token named none either, on the strength of the namespace
    /// alone — no ownership was ever checked. Reaching it needs a token carrying neither
    /// <c>org_id</c> nor <c>project_id</c>, which <c>IsBoundToAudienceAsync</c> admits through
    /// <c>subject.Audiences</c>: a Hydra client with <c>grant_access_token_audience</c> set.
    /// RediensIAM never mints one, but Hydra honours one written into its client store
    /// directly, which is what this token shape represents.
    ///
    /// <para>Keto is told to allow everything, so a <c>true</c> here can only have come from the
    /// controller declining to ask the ownership question.</para>
    /// </summary>
    [Fact]
    public async Task Authorize_WithNoTenantOnEitherSide_IsRefusedInsteadOfPassedThrough()
    {
        fixture.Keto.AllowAll();
        var gateway = await SystemGatewayAsync();
        var tenant  = await TenantAsync();
        var user    = await fixture.Seed.CreateUserAsync(tenant.List.Id);

        const string audience = "https://resource.example.com";
        var token = $"p05-noscope-{user.Id:N}";
        fixture.Hydra.RegisterTokenForClient(token, user.Id.ToString(),
            orgId: null, projectId: null, roles: [], clientId: "audience-bound-client",
            audience: [audience]);

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoOrgsNamespace,
            @object    = tenant.Org.Id.ToString(),
            relation   = Roles.KetoOrgAdminRelation,
            project_id = audience,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse(
                "no tenant on either side means there is no owner to compare the object against, " +
                "and 'nobody owns this' is not 'you own this'");

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "api.authorize.object_out_of_scope").Should().BeTrue(
            "a refusal on this surface has to leave a trace, as every other one does");
    }

    [Fact]
    public async Task Authorize_SystemGateway_ObjectIsScopedToTheSubjectsTenant()
    {
        fixture.Keto.AllowAll();
        var gateway = await SystemGatewayAsync();
        var mine    = await TenantAsync();
        var theirs  = await TenantAsync();

        var user  = await fixture.Seed.CreateUserAsync(mine.List.Id);
        var token = $"p05sys-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), mine.Org.Id.ToString(), mine.Project.Id.ToString(), []);

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoOrgsNamespace,
            @object    = theirs.Org.Id.ToString(),
            relation   = Roles.KetoOrgAdminRelation,
            project_id = mine.Project.Id,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse();
    }

    /// <summary>A namespace this deployment writes no objects into has no ownership to check.</summary>
    [Fact]
    public async Task Authorize_UnknownNamespace_FailsClosed()
    {
        fixture.Keto.AllowAll();
        var mine = await TenantAsync();

        var sa       = await fixture.Seed.CreateServiceAccountAsync(mine.List.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "gateway", null, null);
        var gateway  = fixture.ClientWithToken(raw);

        var user  = await fixture.Seed.CreateUserAsync(mine.List.Id);
        var token = $"p05ns-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), mine.Org.Id.ToString(), mine.Project.Id.ToString(), []);

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = "SomethingElse",
            @object    = mine.Project.Id.ToString(),
            relation   = "x",
            project_id = mine.Project.Id,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse();
    }
}

/// <summary>
/// The MFA-disable guard. Turning <c>require_mfa</c> on stays opt-in and unguarded; turning it
/// off on a project whose users have already enrolled a factor takes an explicit confirmation.
/// </summary>
[Collection("RediensIAM")]
public class ApiSurfaceMfaDowngradeTests(TestFixture fixture)
{
    /// <summary>A project with require_mfa on and one user holding a TOTP factor.</summary>
    private async Task<(Organisation Org, Project Project, HttpClient Admin)> ProtectedProjectAsync()
    {
        fixture.Keto.AllowAll();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa = true;

        var enrolled = await fixture.Seed.CreateUserAsync(list.Id);
        enrolled.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        return (org, project, fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id)));
    }

    [Fact]
    public async Task DisablingRequireMfa_WithEnrolledUsers_Is409WithTheCount()
    {
        var (_, project, admin) = await ProtectedProjectAsync();

        var res = await admin.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { require_mfa = false });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("mfa_downgrade_requires_confirmation");
        body.GetProperty("enrolled_user_count").GetInt32().Should().Be(1);
        body.GetProperty("confirm_with").GetString().Should().Be("confirm_mfa_downgrade");
        body.GetProperty("consequence").GetString().Should().NotBeNullOrWhiteSpace();

        await fixture.RefreshDbAsync();
        (await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id)).RequireMfa
            .Should().BeTrue("a refused downgrade must not half-apply");
    }

    [Fact]
    public async Task DisablingRequireMfa_WithConfirmation_ProceedsAndIsAudited()
    {
        var (_, project, admin) = await ProtectedProjectAsync();

        var res = await admin.PatchAsJsonAsync($"/admin/projects/{project.Id}",
            new { require_mfa = false, confirm_mfa_downgrade = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        (await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id)).RequireMfa.Should().BeFalse();
        fixture.Db.AuditLogs.Any(a => a.Action == "project.mfa_requirement_removed"
                                   && a.TargetId == project.Id.ToString())
            .Should().BeTrue("a machine credential may do this, so it must leave a record");
    }

    /// <summary>Nothing to protect, nothing to confirm — the 409 is not a tax on empty projects.</summary>
    [Fact]
    public async Task DisablingRequireMfa_WithNoEnrolledUsers_IsAllowed()
    {
        fixture.Keto.AllowAll();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa = true;
        await fixture.Seed.CreateUserAsync(list.Id);   // no factor
        await fixture.Db.SaveChangesAsync();

        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { require_mfa = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Enabling it is the safe direction and must stay a one-step call.</summary>
    [Fact]
    public async Task EnablingRequireMfa_IsUnguarded()
    {
        fixture.Keto.AllowAll();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client  = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { require_mfa = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        (await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id)).RequireMfa.Should().BeTrue();
    }

    /// <summary>
    /// The guard is shared, not per-controller. The org route reaches the same setting and had
    /// to be covered by the same rule — a guard on one of three write paths is not a guard.
    /// </summary>
    [Fact]
    public async Task DisablingRequireMfa_ViaTheOrgRoute_IsGuardedToo()
    {
        fixture.Keto.AllowAll();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa = true;
        var enrolled = await fixture.Seed.CreateUserAsync(list.Id);
        enrolled.PhoneVerified = true;
        await fixture.Db.SaveChangesAsync();

        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var refused = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new { require_mfa = false });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var confirmed = await client.PatchAsJsonAsync($"/org/projects/{project.Id}",
            new { require_mfa = false, confirm_mfa_downgrade = true });
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Parity means a machine credential can do this too — which is exactly why it is audited.
    /// </summary>
    [Fact]
    public async Task DisablingRequireMfa_ViaApiManage_BehavesIdentically()
    {
        var (_, project, admin) = await ProtectedProjectAsync();

        var refused = await admin.PatchAsJsonAsync($"/api/manage/projects/{project.Id}", new { require_mfa = false });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var confirmed = await admin.PatchAsJsonAsync($"/api/manage/projects/{project.Id}",
            new { require_mfa = false, confirm_mfa_downgrade = true });
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// /api/manage ↔ /admin parity. The point of the change is that there is no second
/// implementation to review: SystemAdminController carries both route prefixes, so every action
/// is the same method behind the same class-level RequireManagementLevel filter.
/// </summary>
[Collection("RediensIAM")]
public class ApiSurfaceManagedParityTests(TestFixture fixture)
{
    private const string AdminPrefix  = "/admin/";
    private const string ManagePrefix = "/api/manage/";

    private static IEnumerable<(string Pattern, string? Action)> Routes(TestFixture fixture) =>
        fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => ("/" + e.RoutePattern.RawText!.TrimStart('/'),
                          e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()
                              ?.ActionName));

    /// <summary>
    /// Parity, proved against the routing table rather than one endpoint at a time: every
    /// <c>/admin/*</c> controller action has an <c>/api/manage/*</c> twin resolving to the same
    /// action on the same controller. A duplicated handler could not satisfy this — which is the
    /// property worth pinning, because a duplicated handler is where an authorisation check gets
    /// left out.
    ///
    /// Routes with no controller action are excluded, and there is exactly one: <c>/admin/config</c>,
    /// the minimal-API bootstrap the admin SPA fetches before it has a token (issuer, client id,
    /// redirect uri). It is browser configuration, not a management endpoint, and mirroring it
    /// under a machine-credential prefix would mean nothing.
    /// </summary>
    [Fact]
    public void EveryAdminRoute_HasAManagedTwinOnTheSameAction()
    {
        var routes = Routes(fixture).Where(r => r.Action is not null).ToList();
        var admin  = routes.Where(r => r.Pattern.StartsWith(AdminPrefix, StringComparison.Ordinal)).ToList();

        admin.Should().NotBeEmpty("the admin surface must be discoverable at all");

        var managed = routes
            .Where(r => r.Pattern.StartsWith(ManagePrefix, StringComparison.Ordinal))
            .ToLookup(r => r.Pattern[ManagePrefix.Length..], r => r.Action);

        foreach (var (pattern, action) in admin)
        {
            var tail = pattern[AdminPrefix.Length..];
            managed[tail].Should().Contain(action,
                $"/admin/{tail} must also be reachable at /api/manage/{tail}, on the same action");
        }
    }

    /// <summary>
    /// The other half: the authorisation is one filter on one class, so it cannot differ between
    /// the two prefixes. Sampled across the tenant-lifecycle routes that /api/manage did not have
    /// before — suspend, unsuspend, delete, project update, roles.
    /// </summary>
    [Theory]
    [InlineData("organizations/{0}/suspend")]
    [InlineData("organizations/{0}/unsuspend")]
    [InlineData("projects/{1}/roles")]
    public async Task NewlyReachableManagedRoutes_RefuseANonSuperAdmin(string template)
    {
        fixture.Keto.AllowAll();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client  = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var path = ManagePrefix + string.Format(null, template, org.Id, project.Id);
        var res  = await client.PostAsJsonAsync(path, new { name = SeedData.UniqueName() });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an org_admin token must be refused on /api/manage exactly as it is on /admin");
    }

    [Fact]
    public async Task NewlyReachableManagedRoutes_RefuseAnUnauthenticatedCaller()
    {
        var res = await fixture.Client.PostAsJsonAsync(
            $"{ManagePrefix}organizations/{Guid.NewGuid()}/suspend", new { });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Tenant lifecycle, end to end on the machine-credential prefix ────────

    /// <summary>
    /// The half the brief called out first: suspend / unsuspend / delete an organisation and
    /// update a project, driven entirely through /api/manage.
    /// </summary>
    [Fact]
    public async Task TenantLifecycle_IsFullyDrivableFromApiManage()
    {
        fixture.Keto.AllowAll();
        var (_, sysList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(sysList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));

        var created = await client.PostAsJsonAsync($"{ManagePrefix}organizations",
            new { name = SeedData.UniqueName(), slug = SeedData.UniqueSlug() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var orgId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var projectRes = await client.PostAsJsonAsync($"{ManagePrefix}organizations/{orgId}/projects",
            new { name = SeedData.UniqueName(), slug = SeedData.UniqueSlug() });
        projectRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await projectRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await client.PatchAsJsonAsync($"{ManagePrefix}projects/{projectId}", new { name = "Renamed" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var role = await client.PostAsJsonAsync($"{ManagePrefix}projects/{projectId}/roles",
            new { name = "editor", rank = 50 });
        role.StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostAsJsonAsync($"{ManagePrefix}organizations/{orgId}/suspend", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        (await fixture.Db.Organisations.FirstAsync(o => o.Id == orgId)).Active.Should().BeFalse();

        (await client.PostAsJsonAsync($"{ManagePrefix}organizations/{orgId}/unsuspend", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        (await fixture.Db.Organisations.FirstAsync(o => o.Id == orgId)).Active.Should().BeTrue();

        (await client.DeleteAsync($"{ManagePrefix}organizations/{orgId}"))
            .StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
    }

    /// <summary>
    /// Every mutation on the machine-credential prefix must be attributable — the credential is
    /// not a person and there is no session to correlate afterwards.
    /// </summary>
    [Fact]
    public async Task Mutations_ViaApiManage_WriteAuditRows()
    {
        fixture.Keto.AllowAll();
        var (_, sysList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(sysList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));

        var created = await client.PostAsJsonAsync($"{ManagePrefix}organizations",
            new { name = SeedData.UniqueName(), slug = SeedData.UniqueSlug() });
        var orgId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await client.PostAsJsonAsync($"{ManagePrefix}organizations/{orgId}/suspend", new { });

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "org.created" && a.TargetId == orgId.ToString())
            .Should().BeTrue();
        fixture.Db.AuditLogs.Any(a => a.OrgId == orgId && a.ActorId == actor.Id && a.Action.StartsWith("org."))
            .Should().BeTrue();
    }

    /// <summary>
    /// Regression from folding the two controllers together: the duplicate-email check only
    /// existed on the ManagedApi copy, so /admin created two users with the same email in one
    /// list. Both prefixes now refuse it.
    /// </summary>
    [Fact]
    public async Task AddUserToList_DuplicateEmail_IsRefusedOnBothPrefixes()
    {
        fixture.Keto.AllowAll();
        var (org, sysList) = await fixture.Seed.CreateOrgAsync();
        var actor  = await fixture.Seed.CreateUserAsync(sysList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var list   = await fixture.Seed.CreateUserListAsync(org.Id);
        var email  = SeedData.UniqueEmail();

        (await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users",
            new { email, password = "P@ssw0rd!Dup1" })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users",
            new { email, password = "P@ssw0rd!Dup1" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.PostAsJsonAsync($"{ManagePrefix}userlists/{list.Id}/users",
            new { email, password = "P@ssw0rd!Dup1" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
