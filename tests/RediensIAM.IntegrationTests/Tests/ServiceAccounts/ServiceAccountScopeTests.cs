using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

/// <summary>
/// Which service accounts each management level sees, when some of them live on a <b>project's</b>
/// user list rather than the organisation's.
///
/// <para>
/// This is the shape the reported defect is about: a <c>ServiceAccount</c> has no
/// <c>ProjectId</c>. It inherits its context from its <c>UserList</c>, and its project scope — if
/// it has one — lives in <c>ServiceAccountRole.ProjectId</c>. So "the service accounts of a
/// project" is not a column anyone can filter on; it is the project's assigned user list.
/// </para>
///
/// <para>
/// Two things follow, and both are asserted here rather than argued. An org admin must see the
/// accounts on their projects' lists — those lists belong to their organisation, and
/// <c>AssignUserListAsync</c> refuses to attach a list from any other one. A project admin must
/// see their own project's list and nothing else, including nothing from a sibling project in the
/// same organisation.
/// </para>
///
/// <para>
/// The listing and <c>CanAccessAsync</c> answer the same question twice — once for the collection,
/// once per object. A level that can list an account it cannot open, or open one it cannot list,
/// is the two answers having drifted apart, so each level checks both.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ServiceAccountScopeTests(TestFixture fixture)
{
    /// <summary>
    /// One organisation, two projects, each with its own assigned user list and one service
    /// account on it — plus one account on the organisation's own list. Enough to tell "scoped to
    /// my project" apart from "scoped to my organisation" and from "everything".
    /// </summary>
    private async Task<Scenario> ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();

        var projectA = await fixture.Seed.CreateProjectAsync(org.Id);
        var listA    = await fixture.Seed.CreateUserListAsync(org.Id);
        projectA.AssignedUserListId = listA.Id;

        var projectB = await fixture.Seed.CreateProjectAsync(org.Id);
        var listB    = await fixture.Seed.CreateUserListAsync(org.Id);
        projectB.AssignedUserListId = listB.Id;

        await fixture.Db.SaveChangesAsync();

        var saOrg = await fixture.Seed.CreateServiceAccountAsync(orgList.Id, "org-wide-bot");
        var saA   = await fixture.Seed.CreateServiceAccountAsync(listA.Id, "project-a-bot");
        var saB   = await fixture.Seed.CreateServiceAccountAsync(listB.Id, "project-b-bot");

        fixture.Keto.AllowAll();
        return new Scenario(org, projectA, projectB, saOrg, saA, saB);
    }

    private sealed record Scenario(
        Organisation Org, Project ProjectA, Project ProjectB,
        ServiceAccount SaOrg, ServiceAccount SaA, ServiceAccount SaB);

    private static async Task<string[]> NamesAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.EnumerateArray().Select(sa => sa.GetProperty("name").GetString()!)];
    }

    // ── OrgAdmin ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported symptom, as an assertion: an org admin listing service accounts must get the
    /// ones on their projects' lists too, not only the organisation's own list. Both projects'
    /// lists carry this organisation's <c>OrgId</c> — <c>AssignUserListAsync</c> cannot attach a
    /// list that does not — so all three belong to this tenant.
    /// </summary>
    [Fact]
    public async Task OrgAdmin_SeesOrganisationAndProjectServiceAccounts()
    {
        var s      = await ScaffoldAsync();
        var admin  = await fixture.Seed.CreateUserAsync(s.Org.OrgListId);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, s.Org.Id));

        var names = await NamesAsync(await client.GetAsync("/service-accounts"));

        names.Should().Contain("org-wide-bot")
            .And.Contain("project-a-bot", "a project's list belongs to the organisation that owns the project")
            .And.Contain("project-b-bot");
    }

    /// <summary>
    /// The per-object answer must agree with the listing: what an org admin can list, they can
    /// open. This is the half that <c>CanAccessAsync</c> owns.
    /// </summary>
    [Fact]
    public async Task OrgAdmin_CanOpenAProjectServiceAccount()
    {
        var s      = await ScaffoldAsync();
        var admin  = await fixture.Seed.CreateUserAsync(s.Org.OrgListId);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, s.Org.Id));

        var res = await client.GetAsync($"/service-accounts/{s.SaA.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// And the boundary still holds: another organisation's account is not listed. The tenant
    /// boundary is what the filter is for; widening it to fix the project case would trade one
    /// defect for a worse one.
    /// </summary>
    [Fact]
    public async Task OrgAdmin_DoesNotSeeAnotherOrganisationsServiceAccounts()
    {
        var s = await ScaffoldAsync();
        var (otherOrg, otherOrgList) = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateServiceAccountAsync(otherOrgList.Id, "other-tenant-bot");

        var admin  = await fixture.Seed.CreateUserAsync(s.Org.OrgListId);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, s.Org.Id));

        var names = await NamesAsync(await client.GetAsync("/service-accounts"));

        names.Should().NotContain("other-tenant-bot");
        otherOrg.Id.Should().NotBe(s.Org.Id);
    }

    // ── ProjectAdmin ──────────────────────────────────────────────────────────

    /// <summary>
    /// A project admin sees their project's list, and neither the organisation's own list nor a
    /// sibling project's — the narrowest of the three scopes, and the one the console's project
    /// page has to reproduce.
    /// </summary>
    [Fact]
    public async Task ProjectAdmin_SeesOnlyTheirOwnProjectsServiceAccounts()
    {
        var s       = await ScaffoldAsync();
        var manager = await fixture.Seed.CreateUserAsync(s.ProjectA.AssignedUserListId!.Value);
        var client  = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(manager.Id, s.Org.Id, s.ProjectA.Id));

        var names = await NamesAsync(await client.GetAsync("/service-accounts"));

        names.Should().Contain("project-a-bot")
            .And.NotContain("project-b-bot", "a sibling project in the same organisation is still another project")
            .And.NotContain("org-wide-bot");
    }

    /// <summary>
    /// The per-object half, on the account the listing refused: a project admin opening a sibling
    /// project's account gets 404, the same answer as for an account that does not exist. Listing
    /// and access agree.
    /// </summary>
    [Fact]
    public async Task ProjectAdmin_CannotOpenASiblingProjectsServiceAccount()
    {
        var s       = await ScaffoldAsync();
        var manager = await fixture.Seed.CreateUserAsync(s.ProjectA.AssignedUserListId!.Value);
        var client  = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(manager.Id, s.Org.Id, s.ProjectA.Id));

        var mine     = await client.GetAsync($"/service-accounts/{s.SaA.Id}");
        var sibling  = await client.GetAsync($"/service-accounts/{s.SaB.Id}");

        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        sibling.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── SuperAdmin ────────────────────────────────────────────────────────────

    /// <summary>
    /// A super-admin sees every account, on every list, in every organisation — including the ones
    /// on project lists. The console's <c>SystemServiceAccounts</c> page narrows this to the
    /// <c>__system__</c> list for display; that is a page filter, and this asserts that the filter
    /// is a choice the console makes rather than a limit the API imposes.
    /// </summary>
    [Fact]
    public async Task SuperAdmin_SeesEveryServiceAccountIncludingProjectOnes()
    {
        var s = await ScaffoldAsync();
        var (_, otherOrgList) = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateServiceAccountAsync(otherOrgList.Id, "other-tenant-bot");

        var root   = await fixture.Seed.CreateUserAsync(s.Org.OrgListId);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(root.Id));

        var names = await NamesAsync(await client.GetAsync("/service-accounts"));

        names.Should().Contain("org-wide-bot")
            .And.Contain("project-a-bot")
            .And.Contain("project-b-bot")
            .And.Contain("other-tenant-bot");
    }
}
