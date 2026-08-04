using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// The last of the duplicated operations, and the two of them that were wrong rather than merely
/// written twice.
/// </summary>
[Collection("RediensIAM")]
public class SessionAndProjectParityTests(TestFixture fixture)
{
    /// <summary>
    /// Forcing a super-admin to log out revoked nothing.
    ///
    /// <para>
    /// A tenant user's Hydra subject is <c>"&lt;org&gt;:&lt;user&gt;"</c>; an administrator has no
    /// organisation, so theirs is the bare user id — that is what CompleteLoginAsync mints and what
    /// ParseSubjectUserId reads back. The force-logout route built the subject as
    /// <c>$"{user.UserList.OrgId?.ToString() ?? ""}:{id}"</c>, which for an administrator is
    /// <c>":&lt;guid&gt;"</c>: a subject no session was ever issued under.
    /// </para>
    ///
    /// <para>
    /// So the one account whose sessions most need ending on demand was the one account the button
    /// could not end. Hydra answered 204 to the revocation of a subject that does not exist, and
    /// the API answered "sessions_revoked". Three lines away in the same controller, the
    /// password-change path builds the subject correctly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ForcingLogoutOfAnAdministratorNamesTheSubjectTheirSessionsWereIssuedUnder()
    {
        var systemList = new UserList
        {
            Id = Guid.NewGuid(), Name = SeedData.UniqueName(), OrgId = null,
            Immovable = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var admin = await fixture.Seed.CreateUserAsync(systemList.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));
        fixture.Hydra.ResetLog();

        var res = await client.DeleteAsync($"/admin/users/{admin.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.SessionsRevokedFor(admin.Id.ToString()).Should().BeTrue(
            "an administrator's sessions are issued under the bare user id, not under \":<id>\"");
    }

    /// <summary>A tenant user keeps the subject their sessions really carry.</summary>
    [Fact]
    public async Task ForcingLogoutOfATenantUserStillNamesTheScopedSubject()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));
        fixture.Hydra.ResetLog();

        await client.DeleteAsync($"/admin/users/{user.Id}/sessions");

        fixture.Hydra.SessionsRevokedFor($"{org.Id}:{user.Id}").Should().BeTrue();
    }

    /// <summary>
    /// <c>/org/projects/{id}</c> deliberately admits a super-admin — the lookup reads
    /// <c>isSuperAdmin || p.OrgId == OrgId</c>. A super-admin's token carries no organisation, so
    /// <c>OrgId</c> is <c>Guid.Empty</c>, and the deletion was recorded against it: the tenant
    /// could not see in their own audit log that their project had been deleted. Every other
    /// operation on this object was already fixed to record the project's organisation; this one
    /// is where it is not a coincidence but a live path.
    /// </summary>
    [Fact]
    public async Task DeletingAProjectIsRecordedAgainstItsOwnOrganisationWhoeverDeletesIt()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var superAdmin = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await superAdmin.DeleteAsync($"/org/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var entry = fixture.Db.AuditLogs
            .Single(a => a.TargetId == project.Id.ToString() && a.Action == "project.deleted");
        entry.OrgId.Should().Be(org.Id,
            "an entry against Guid.Empty is one the tenant cannot read");
    }

    /// <summary>
    /// Assigning a user list to a project decides who may sign in to it. Neither scope recorded it.
    /// </summary>
    [Fact]
    public async Task AssigningAUserListIsRecordedFromBothScopes()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var orgClient = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        var sysClient = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        foreach (var (client, prefix) in new[] { (orgClient, "/org"), (sysClient, "/admin") })
        {
            var project = await fixture.Seed.CreateProjectAsync(org.Id);
            var list    = await fixture.Seed.CreateUserListAsync(org.Id);

            var res = await client.PutAsJsonAsync($"{prefix}/projects/{project.Id}/userlist",
                new { user_list_id = list.Id });

            res.StatusCode.Should().Be(HttpStatusCode.OK);
            fixture.Db.AuditLogs
                .Where(a => a.TargetId == project.Id.ToString() && a.Action == "project.userlist_assigned")
                .Should().NotBeEmpty($"{prefix} changed who may sign in to that project");
        }
    }

    /// <summary>And the removal, which is the same decision in the other direction.</summary>
    [Fact]
    public async Task UnassigningAUserListIsRecordedFromBothScopes()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var orgClient = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        var sysClient = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        foreach (var (client, prefix) in new[] { (orgClient, "/org"), (sysClient, "/admin") })
        {
            var project = await fixture.Seed.CreateProjectAsync(org.Id);

            var res = await client.DeleteAsync($"{prefix}/projects/{project.Id}/userlist");

            res.StatusCode.Should().Be(HttpStatusCode.OK);
            fixture.Db.AuditLogs
                .Where(a => a.TargetId == project.Id.ToString() && a.Action == "project.userlist_unassigned")
                .Should().NotBeEmpty($"{prefix} changed who may sign in to that project");
        }
    }
}
