using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Middleware;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// S-1, S-8 and S-3 from <c>SECURITY-AUDIT-LOG.md</c> step 03 — the three
/// structural findings, each of which exists because a class of defect had no representation in
/// the code that could fail.
/// </summary>
[Collection("RediensIAM")]
public class StructuralDebtTests(TestFixture fixture)
{
    // ── S-1: a claimed level is not a granted one ────────────────────────────

    /// <summary>
    /// The whole finding in one assertion. <c>ServiceAccountController.Level</c> read a management
    /// level straight off <c>ext.roles</c> and used it to decide access; it compiled, it read
    /// correctly, and it was R-22.
    ///
    /// This started life asserting a runtime throw. It asserts reachability instead because the
    /// guard moved to the compiler: <c>GetManagementLevel</c> was first made private and has since
    /// been deleted outright, so the mistake is no longer expressible and there is no call left to
    /// throw from. A test cannot assert "this does not compile", so it asserts the property that
    /// makes it so — the absence of a public reader — and it fails the moment anyone reintroduces
    /// one, which is the regression worth catching.
    /// </summary>
    [Fact]
    public void TheCallersClaimedManagementLevel_IsNotPubliclyReadable()
    {
        typeof(ClaimsExtensions)
            .GetMethod("GetManagementLevel", BindingFlags.Public | BindingFlags.Static)
            .Should().BeNull(
                "a public reader of the caller's claimed level is a ManagementLevel that compiles " +
                "everywhere and is verified nowhere — that is R-22 and P-01");
    }

    /// <summary>
    /// The legitimate reading survives under a name that says what it is — introspection asks what
    /// somebody *else's* token asserts, a question about the token rather than about the caller's
    /// authority — and it is <c>internal</c>, so it is reachable from the code that needs it and
    /// from nowhere else. That the reading still produces the right answer is covered end-to-end by
    /// the introspection API tests; what is asserted here is the visibility, which is the part a
    /// refactor can quietly widen.
    /// </summary>
    [Fact]
    public void TheDeliberateReaderOfAPresentedToken_IsInternalNotPublic()
    {
        var reader = typeof(GrantedLevel).GetMethod("ClaimedLevel",
            BindingFlags.NonPublic | BindingFlags.Static);

        reader.Should().NotBeNull("introspection must still be able to read a presented token");
        reader!.IsAssembly.Should().BeTrue(
            "a public one is the same trap under a better name — callers outside this assembly " +
            "have no business reading a claimed level at all");
    }

    /// <summary>
    /// The type-level half. <c>GrantedLevel</c> is the answer every access decision should take,
    /// and no code outside the type can produce one — the only constructor is private and the only
    /// caller of it is <c>ResolveAsync</c>, which goes through <c>LiveAuthorizationService</c>.
    /// Widening the constructor to <c>internal</c> would put a forgeable "verified" level back in
    /// reach of every controller in the assembly, so that is what this asserts against.
    /// </summary>
    [Fact]
    public void NothingOutsideGrantedLevel_CanConstructOne()
    {
        typeof(GrantedLevel)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().OnlyContain(c => c.IsPrivate,
                "a GrantedLevel that any type can mint is a ManagementLevel with a nicer name");
    }

    // ── S-8: one authorisation question, one answer ──────────────────────────

    /// <summary>
    /// <c>LiveAuthorizationService</c> and <c>KetoService</c> each carried their own resolution of
    /// "what management level does this actor hold", reading different stores. With Keto refusing
    /// the subject outright, the old <c>LiveAuthorizationService</c> still said yes — its
    /// project_admin branch asked a *list* endpoint and fell back to any <c>org_roles</c> row
    /// anywhere. Two authorities, one of which the other could not see.
    /// </summary>
    [Fact]
    public async Task WhenTheAuthorisationStoreRefusesAnActor_BothResolversRefuse()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);

        // A project_admin row in this very organisation: the fallback that used to answer "yes".
        await fixture.Seed.CreateOrgRoleAsync(org.Id, user.Id, Roles.ProjectAdmin, project.Id);

        fixture.Keto.DenySubject($"user:{user.Id}");
        try
        {
            var keto = fixture.GetService<KetoService>();
            var live = fixture.GetService<LiveAuthorizationService>();
            var claims = new TokenClaims
            {
                UserId = user.Id.ToString(), OrgId = org.Id.ToString(),
                ProjectId = project.Id.ToString(), Roles = [Roles.ProjectAdmin],
            };

            (await keto.GetActorManagementLevelForOrgAsync(user.Id, org.Id))
                .Should().Be(ManagementLevel.None);
            (await live.IsStillGrantedAsync(claims, ManagementLevel.ProjectAdmin))
                .Should().BeFalse("the Keto tuple is the single authority and it says no");
        }
        finally
        {
            fixture.Keto.AllowAll();
        }
    }

    /// <summary>
    /// R-22 residual 3: "project_admin somewhere" used to satisfy "project_admin here". The claim
    /// now has to name a scope, and an actor whose only grant is a row in another organisation is
    /// no longer authorised by it.
    /// </summary>
    [Fact]
    public async Task AProjectAdminGrantInAnotherOrganisation_DoesNotAuthoriseThisOne()
    {
        await fixture.FlushCacheAsync();
        var (home, _)  = await fixture.Seed.CreateOrgAsync();
        var list       = await fixture.Seed.CreateUserListAsync(home.Id);
        var user       = await fixture.Seed.CreateUserAsync(list.Id);
        var otherProj  = await fixture.Seed.CreateProjectAsync(home.Id);
        await fixture.Seed.CreateOrgRoleAsync(home.Id, user.Id, Roles.ProjectAdmin, otherProj.Id);

        // The token carries no org and no project — the shape a stale or forged claim has.
        var unscoped = new TokenClaims
        {
            UserId = user.Id.ToString(), OrgId = "", ProjectId = "", Roles = [Roles.ProjectAdmin],
        };

        (await fixture.GetService<LiveAuthorizationService>()
            .IsStillGrantedAsync(unscoped, ManagementLevel.ProjectAdmin))
            .Should().BeFalse("a management claim that names no scope grants nothing");
    }

    // ── S-3: audit coverage and tamper-evidence ──────────────────────────────

    /// <summary>
    /// T-N2 was seven security-relevant mutations that no <c>RecordAsync</c> call site covered.
    /// The record is now written by the save itself: this test overwrites a TOTP secret straight
    /// through the DbContext, with no controller and no audit call anywhere in the path.
    /// </summary>
    [Fact]
    public async Task OverwritingAnAuthenticationFactor_IsAuditedWithNobodyCallingTheAuditService()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);

        user.TotpSecret  = "ciphertext";
        user.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var row = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.TargetId == user.Id.ToString() && a.Action == "entity.users.credential_changed")
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        row.Should().NotBeNull();
        row!.Metadata["properties"].ToString().Should().Contain(nameof(User.TotpSecret));
    }

    /// <summary>Rows link to their predecessor, and an edit to any of them breaks the link.</summary>
    [Fact]
    public async Task EditingAnAuditRowBehindTheApplicationsBack_IsDetectable()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit    = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.chain.one");
        await audit.RecordAsync(org.Id, null, null, "test.chain.two");
        await audit.RecordAsync(org.Id, null, null, "test.chain.three");

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().BeNull("nothing has been touched yet");

        var middle = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == org.Id && a.Action == "test.chain.two")
            .Select(a => a.Id).SingleAsync();

        await fixture.Db.Database.ExecuteSqlRawAsync(
            "UPDATE audit_log SET \"Action\" = 'test.chain.rewritten' WHERE \"Id\" = {0}", middle);

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().Be(middle);
    }

    /// <summary>A row removed from the middle leaves its successor pointing at nothing.</summary>
    [Fact]
    public async Task DeletingAnAuditRowFromTheMiddle_IsDetectable()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit    = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.delete.one");
        await audit.RecordAsync(org.Id, null, null, "test.delete.two");
        await audit.RecordAsync(org.Id, null, null, "test.delete.three");

        var ids = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == org.Id).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();

        await fixture.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM audit_log WHERE \"Id\" = {0}", ids[1]);

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().Be(ids[2],
            "the row after the deleted one names a predecessor that no longer exists");
    }

    /// <summary>The application refuses to be the instrument of its own log's rewriting.</summary>
    [Fact]
    public async Task RewritingAnAuditRowThroughTheApplication_IsRefused()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        await fixture.GetService<AuditLogService>().RecordAsync(org.Id, null, null, "test.appendonly");

        using var scope = fixture.Services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
        var row = await db.AuditLogs.Where(a => a.OrgId == org.Id).OrderByDescending(a => a.Id).FirstAsync();
        row.Action = "something.else";

        var save = async () => await db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }
}
