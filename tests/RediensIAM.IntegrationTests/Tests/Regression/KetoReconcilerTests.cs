using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// S-8's remaining half — the grant dual write had a compensating delete and no reconciler.
///
/// A grant is a Keto tuple plus a database row, written in that order, with the tuple deleted again
/// in the <c>catch</c>. A process killed between the two writes, or a compensating delete that
/// itself fails, leaves the two stores disagreeing, and until now nothing looked. The two ways they
/// can disagree are not the same failure and do not have the same safe answer, which is what these
/// tests pin:
///
///   - a tuple with no row is <b>live privilege with no provenance</b> → revoke it;
///   - a row with no tuple is a dead grant that still feeds token scopes → delete it, and
///     <b>never</b> create the tuple, because that would make the database a source of authority.
///
/// Its own database. The reconciler compares every grant in the deployment and its repair deletes
/// things; pointed at the collection's shared database it would act on rows every other test in
/// the collection seeded.
/// </summary>
[Collection("RediensIAM")]
public class KetoReconcilerTests(TestFixture fixture) : IAsyncLifetime
{
    private string _dbName = "";
    private RediensIamDbContext _db = null!;
    private SeedData _seed = null!;
    private GrantReconciler _reconciler = null!;

    public async Task InitializeAsync()
    {
        _dbName = "reconcile_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString) { Database = _dbName }.ToString();
        var appConfig = fixture.GetService<AppConfig>();
        _db = new RediensIamDbContext(
            new DbContextOptionsBuilder<RediensIamDbContext>().UseNpgsql(cs).Options, appConfig);
        await _db.Database.MigrateAsync();
        _seed = new SeedData(_db, fixture.Hydra, fixture.GetService<PasswordService>());

        _reconciler = new GrantReconciler(
            _db, fixture.GetService<KetoService>(), fixture.GetService<AuditLogService>(),
            NullLogger<GrantReconciler>.Instance);

        fixture.Keto.AllowAll();
        NoTuplesAnywhere();
        fixture.Keto.ResetWriteRequests();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        fixture.Keto.AllowAll();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.PostgresConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Class 1: a tuple Keto holds that no row backs ────────────────────────

    /// <summary>
    /// The dangerous direction. Keto is the authority, so this grant authorises requests right now
    /// — and nothing records who granted it, to whom, or when. It is what a process killed between
    /// the tuple write and the row write leaves behind, and equally what someone writing straight
    /// to the tuple store leaves behind.
    /// </summary>
    [Fact]
    public async Task ATupleWithNoBackingRow_IsReportedAsAnOrphanTuple()
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var stranger = Guid.NewGuid();
        var orphan = new RelationTuple(Roles.KetoOrgsNamespace, org.Id.ToString(), Roles.OrgAdmin, $"user:{stranger}");
        fixture.Keto.SetTuples(Roles.KetoOrgsNamespace, orphan);

        var report = await _reconciler.ScanAsync();

        report.OrphanTuples.Should().Contain(orphan);
        report.OrphanRows.Should().NotContain(orphan);
    }

    /// <summary>Repair revokes it — the recoverable direction, since an admin can re-grant.</summary>
    [Fact]
    public async Task RepairingAnOrphanTuple_DeletesItFromKeto()
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var stranger = Guid.NewGuid();
        fixture.Keto.SetTuples(Roles.KetoOrgsNamespace,
            new RelationTuple(Roles.KetoOrgsNamespace, org.Id.ToString(), Roles.OrgAdmin, $"user:{stranger}"));
        fixture.Keto.ResetWriteRequests();

        var report = await _reconciler.RepairAsync(actorId: null);

        report.TuplesRevoked.Should().Be(1);
        fixture.Keto.WriteRequests.Should().Contain(r =>
            r.Method == "DELETE" && r.Url.Contains(stranger.ToString(), StringComparison.Ordinal),
            "the repair has to reach Keto, not just say it did");
    }

    // ── Class 2: a row no tuple backs ────────────────────────────────────────

    /// <summary>
    /// Dead as authorisation — Keto answers no — but not inert: the consent path still reads
    /// <c>org_roles</c> to resolve scopes into a minted token, so a row nobody can act on can
    /// still put scopes in one.
    /// </summary>
    [Fact]
    public async Task ARowWithNoTuple_IsReportedAsAnOrphanRow()
    {
        var grant = await SeedManagementGrantAsync();

        var report = await _reconciler.ScanAsync();

        report.OrphanRows.Should().Contain(grant);
        report.OrphanTuples.Should().BeEmpty();
    }

    /// <summary>
    /// The re-check that makes the repair safe to run. Removal writes the tuple delete first and
    /// the row delete second, so a removal caught mid-flight looks exactly like an orphan row —
    /// and so does a grant whose tuple the scan simply missed. Before deleting anything the
    /// reconciler asks Keto again, and a grant that turns out to be live keeps its row.
    /// </summary>
    [Fact]
    public async Task AnOrphanRowWhoseTupleTurnsUpOnTheRecheck_IsLeftAlone()
    {
        var grant = await SeedManagementGrantAsync();

        // The list endpoint says the tuple is absent; the check endpoint (default: allow) says it
        // is there. The check is the authoritative one, and it wins.
        var report = await _reconciler.RepairAsync(actorId: null);

        report.OrphanRows.Should().Contain(grant);
        report.RowsRemoved.Should().Be(0);
        (await _db.OrgRoles.AsNoTracking().CountAsync()).Should().Be(1, "the row survives a live grant");
    }

    /// <summary>…and when Keto confirms the grant is gone, the row goes with it.</summary>
    [Fact]
    public async Task RepairingAnOrphanRowKetoConfirmsIsGone_DeletesTheRow()
    {
        var grant = await SeedManagementGrantAsync();
        fixture.Keto.DenyCheck(grant.Namespace, grant.Object, grant.Relation, grant.Subject);

        var report = await _reconciler.RepairAsync(actorId: null);

        report.RowsRemoved.Should().Be(1);
        (await _db.OrgRoles.AsNoTracking().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The direction claim, asserted rather than asserted-in-prose: repairing an orphan row must
    /// never write the missing tuple. Creating authority from a database row is the coupling S-8
    /// removed, and it would hand anyone with write access to <c>org_roles</c> an escalation path
    /// — insert a row, wait for the reconciler to promote it into a real grant.
    /// </summary>
    [Fact]
    public async Task RepairingAnOrphanRow_NeverCreatesTheTuple()
    {
        var grant = await SeedManagementGrantAsync();
        fixture.Keto.DenyCheck(grant.Namespace, grant.Object, grant.Relation, grant.Subject);
        fixture.Keto.ResetWriteRequests();

        await _reconciler.RepairAsync(actorId: null);

        fixture.Keto.WriteRequests.Should().NotContain(r => r.Method == "PATCH",
            "a tuple insert is the one repair this must never make");
    }

    // ── Grants that agree, and tuples that are nobody's business ─────────────

    [Fact]
    public async Task AGrantPresentInBothStores_IsNotDivergence()
    {
        var grant = await SeedManagementGrantAsync();
        fixture.Keto.SetTuples(Roles.KetoOrgsNamespace, grant);

        var report = await _reconciler.ScanAsync();

        report.Divergence.Should().Be(0);
    }

    /// <summary>
    /// The tuples that have no backing row <b>by design</b>. The bootstrap super admin is one
    /// (<c>System:rediensiam#super_admin</c>, written by Program.cs with no row at all), the
    /// structural project relations are others. A reconciler that compared them would report the
    /// deployment's only super admin as an orphan and then, on repair, revoke it.
    /// </summary>
    [Fact]
    public async Task TuplesWithNoBackingTableByDesign_AreNeverReportedAsOrphans()
    {
        var bootstrap = new RelationTuple(
            Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{Guid.NewGuid()}");
        var structural = new RelationTuple(
            Roles.KetoProjectsNamespace, Guid.NewGuid().ToString(), Roles.KetoManagerRelation, $"user:{Guid.NewGuid()}");
        fixture.Keto.SetTuples(Roles.KetoSystemNamespace, bootstrap);
        fixture.Keto.SetTuples(Roles.KetoProjectsNamespace, structural);

        var report = await _reconciler.ScanAsync();

        report.OrphanTuples.Should().NotContain(bootstrap);
        report.OrphanTuples.Should().NotContain(structural,
            "only role:* relations on Projects come from a dual write");
    }

    /// <summary>A tenant project role diverges the same way, and is found the same way.</summary>
    [Fact]
    public async Task AProjectRoleRowWithNoTuple_IsReportedAsAnOrphanRow()
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var list = await _seed.CreateUserListAsync(org.Id);
        var user = await _seed.CreateUserAsync(list.Id);
        var project = await _seed.CreateProjectAsync(org.Id);
        var role = await _seed.CreateRoleAsync(project.Id, "editor");
        _db.UserProjectRoles.Add(new UserProjectRole
        {
            UserId = user.Id, ProjectId = project.Id, RoleId = role.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var report = await _reconciler.ScanAsync();

        report.OrphanRows.Should().Contain(new RelationTuple(
            Roles.KetoProjectsNamespace, project.Id.ToString(), "role:editor", $"user:{user.Id}"));
    }

    // ── The bound ────────────────────────────────────────────────────────────

    /// <summary>
    /// Divergence at this scale is not dropped writes — it is a Keto restored from an old backup,
    /// or a half-migrated database. Both repairs are destructive in that state: deleting rows
    /// discards the provenance of grants that ought to be re-created, deleting tuples revokes an
    /// organisation's whole admin set at once. So the repair declines and says why.
    /// </summary>
    [Fact]
    public async Task DivergenceAboveTheRepairBound_RefusesToRepairAtAll()
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var list = await _seed.CreateUserListAsync(org.Id);
        var user = await _seed.CreateUserAsync(list.Id);
        for (var i = 0; i <= GrantReconciler.MaxRepairsPerRun; i++)
            _db.OrgRoles.Add(new OrgRole
            {
                OrgId = org.Id, UserId = user.Id, Role = Roles.ProjectAdmin,
                ScopeId = Guid.NewGuid(), GrantedAt = DateTimeOffset.UtcNow,
            });
        await _db.SaveChangesAsync();

        var report = await _reconciler.RepairAsync(actorId: null);

        report.RepairRefused.Should().NotBeNull();
        report.RowsRemoved.Should().Be(0);
        report.TuplesRevoked.Should().Be(0);
        (await _db.OrgRoles.AsNoTracking().CountAsync())
            .Should().Be(GrantReconciler.MaxRepairsPerRun + 1, "nothing was deleted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>An <c>org_roles</c> row and the tuple the grant path would have written for it.</summary>
    private async Task<RelationTuple> SeedManagementGrantAsync()
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var list = await _seed.CreateUserListAsync(org.Id);
        var user = await _seed.CreateUserAsync(list.Id);
        await _seed.CreateOrgRoleAsync(org.Id, user.Id, Roles.OrgAdmin);
        return new RelationTuple(Roles.KetoOrgsNamespace, org.Id.ToString(), Roles.OrgAdmin, $"user:{user.Id}");
    }

    private void NoTuplesAnywhere()
    {
        fixture.Keto.SetTuples(Roles.KetoOrgsNamespace);
        fixture.Keto.SetTuples(Roles.KetoProjectsNamespace);
        fixture.Keto.SetTuples(Roles.KetoSystemNamespace);
    }
}
