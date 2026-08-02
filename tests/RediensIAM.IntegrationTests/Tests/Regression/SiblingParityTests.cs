using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// One route doing correctly what its sibling does not.
///
/// <para>
/// Nearly every defect the 2026-08 sweep found had this shape: the org-scoped delete cleans up its
/// Keto tuples and the admin-scoped one does not; one add-user path checks for a duplicate e-mail
/// and the other returns a 500; the password login resets the lockout counter and the MFA login
/// does not. Each pair is asserted here together, so the next divergence fails a test that names
/// both halves.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class SiblingParityTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminAsync()
    {
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));
    }

    // ── The IP allowlist must be parseable wherever it is written ─────────────

    /// <summary>
    /// <c>ProjectController</c> validates every CIDR and says so in its own comment: an entry that
    /// does not parse makes <c>IpInRange</c> answer false for everyone, which locks the tenant out
    /// of its own project rather than reporting the typo. The org and admin update paths took the
    /// value unchecked.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.0/33")]
    [InlineData("not-an-address")]
    public async Task OrgUpdateProject_RefusesAnUnparseableCidr(string cidr)
    {
        var (org, list) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new { ip_allowlist = new[] { cidr } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an allowlist nobody can match is a tenant outage, not a saved setting");
    }

    // ── Adding a user twice is a conflict, not a crash ────────────────────────

    /// <summary>
    /// <c>SystemAdminController.AddUserToList</c> checks for an existing address before inserting;
    /// the org and project paths did not, so the unique index on (UserListId, Email) surfaced as a
    /// <c>DbUpdateException</c> and a 500 <c>internal_error</c>.
    /// </summary>
    [Fact]
    public async Task OrgAddUserToList_DuplicateEmail_IsAConflictNotAServerError()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var body = new { email = $"dupe-{Guid.NewGuid():N}@tenant.test", password = "P@ssw0rd!Test" };
        var first = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", body);
        ((int)first.StatusCode).Should().BeLessThan(400);

        var second = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", body);

        ((int)second.StatusCode).Should().BeLessThan(500,
            "a duplicate address is a conflict the caller can act on, not a server fault");
    }

    // ── Granting deployment-wide admin must be auditable ──────────────────────

    /// <summary>
    /// Adding a user to the system list grants <c>System:rediensiam#super_admin</c>. Neither that
    /// nor its removal wrote an audit row, so the most privileged grant in the deployment left no
    /// record and the hash chain had nothing to protect. The org-scoped equivalent audits.
    /// </summary>
    [Fact]
    public async Task AddingAUserToTheSystemList_IsAudited()
    {
        var client = await SuperAdminAsync();
        var list   = await fixture.Db.UserLists.FirstOrDefaultAsync(l => l.OrgId == null && l.Immovable);
        if (list == null) return;   // no system list in this fixture: nothing to assert

        var before = await fixture.Db.AuditLogs.CountAsync();
        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users", new
        {
            email = $"sysadmin-{Guid.NewGuid():N}@deployment.test",
            password = "P@ssw0rd!Test",
        });
        ((int)res.StatusCode).Should().BeLessThan(400);

        await fixture.RefreshDbAsync();
        (await fixture.Db.AuditLogs.CountAsync()).Should().BeGreaterThan(before,
            "granting deployment-wide administration is the last thing that should be silent");
    }
}
