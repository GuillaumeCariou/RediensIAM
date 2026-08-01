using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;

namespace RediensIAM.Services;

/// <param name="OrphanTuples">
/// Grants Keto holds that no database row backs. <b>Live privilege with no provenance</b> — Keto
/// is the authority, so these authorise requests today, and nothing records who granted them, to
/// whom, or when.
/// </param>
/// <param name="OrphanRows">
/// Grants the database holds that Keto does not. Dead as authorisation, but not inert: the consent
/// path still reads <c>org_roles</c> to resolve scopes into a minted token.
/// </param>
/// <param name="TuplesRevoked">Orphan tuples deleted from Keto by a repair run.</param>
/// <param name="RowsRemoved">Orphan rows deleted from the database by a repair run.</param>
/// <param name="RepairRefused">
/// Why a repair declined to act, or null when it did not decline. Set when divergence is large
/// enough to mean a store-level failure rather than dropped writes.
/// </param>
public sealed record GrantReconcileReport(
    IReadOnlyList<RelationTuple> OrphanTuples,
    IReadOnlyList<RelationTuple> OrphanRows,
    int TuplesRevoked = 0,
    int RowsRemoved = 0,
    string? RepairRefused = null)
{
    public int Divergence => OrphanTuples.Count + OrphanRows.Count;
}

/// <summary>
/// Reconciles the grant dual write (S-8's remaining half).
///
/// <para>
/// Every grant is written twice — a Keto tuple first, a row second, with a compensating tuple
/// delete in the <c>catch</c>. That is best effort, not a transaction: a process killed between
/// the two writes, or a compensating delete that itself fails, leaves the stores disagreeing and
/// nothing has ever looked. This looks, and repairs the cases where the safe answer is not a
/// judgement call.
/// </para>
///
/// <para>
/// <b>The two divergence classes are not symmetric, and the direction is the whole design.</b>
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>Tuple with no row.</b> Keto is the authority, so this grant is live — someone can act on it
/// right now — while the row that would say who granted it does not exist. Since the row is
/// written second, a tuple without one is by definition a grant that never completed; and if it
/// was not a dropped write, it is a tuple someone put there directly, which is worse. Repair
/// deletes the tuple. Revoking is the recoverable direction: an admin can re-grant, whereas
/// privilege left standing cannot be un-exercised.
/// </item>
/// <item>
/// <b>Row with no tuple.</b> Repair deletes the row. It deliberately does <b>not</b> write the
/// missing tuple, and that asymmetry is the point: creating a tuple from a row would make
/// <c>org_roles</c> a source of authority again — the exact coupling S-8 removed — and would hand
/// anyone with database write access an escalation path, since an inserted row would be promoted
/// into a real grant by the next reconciler pass. Authority only ever converges downward.
/// </item>
/// </list>
///
/// <para>
/// <b>What it deliberately does not touch.</b> Only tuples whose shape a dual write produces are
/// compared: management relations on <c>Organisations</c>, and <c>role:*</c> on <c>Projects</c>.
/// The bootstrap super admin (<c>System:rediensiam#super_admin</c>) has no backing row <i>by
/// design</i>, user-list membership and the structural project relations likewise; a reconciler
/// that compared those would report them as orphan tuples and then delete the deployment's only
/// super admin.
/// </para>
/// </summary>
public class GrantReconciler(
    RediensIamDbContext db,
    KetoService keto,
    AuditLogService audit,
    ILogger<GrantReconciler> logger)
{
    /// <summary>
    /// Above this much divergence a repair refuses and reports instead.
    ///
    /// Dropped writes are rare and individual; hundreds of divergent grants means a store-level
    /// event — a Keto restored from an old backup, a half-migrated database — and in that state
    /// both repairs are destructive: deleting rows discards the provenance of grants that ought to
    /// be re-created, and deleting tuples revokes an organisation's whole admin set at once.
    ///
    /// ponytail: one flat bound, no per-class or per-org shaping. If a deployment ever legitimately
    /// exceeds it, the answer is an operator who has looked at the report, not a bigger number.
    /// </summary>
    public const int MaxRepairsPerRun = 100;

    /// <summary>
    /// Reads both stores and reports what disagrees. Never writes.
    ///
    /// <para>
    /// Keto is read <i>first</i>, the database second, and the order is deliberate: a grant in
    /// flight during the scan has written its tuple and not yet its row, so reading the database
    /// afterwards gives it the best chance of being seen complete. It narrows the window rather
    /// than closing it, which is why repair re-checks each item rather than trusting this list.
    /// </para>
    /// </summary>
    public async Task<GrantReconcileReport> ScanAsync(CancellationToken ct = default)
    {
        var ketoGrants = await KetoGrantsAsync(ct);
        var dbGrants = await DatabaseGrantsAsync(ct);
        var dbKeys = dbGrants.Select(g => g.Key).ToHashSet();

        return new GrantReconcileReport(
            OrphanTuples: [.. ketoGrants.Where(t => !dbKeys.Contains(t))],
            OrphanRows: [.. dbGrants.Where(g => !ketoGrants.Contains(g.Key)).Select(g => g.Key)]);
    }

    /// <summary>
    /// Scans, then repairs what is unambiguous: revokes orphan tuples, removes orphan rows. Each
    /// item is re-checked against the other store immediately before it is acted on, so a grant
    /// that completed between the scan and the repair is left alone.
    /// </summary>
    public async Task<GrantReconcileReport> RepairAsync(Guid? actorId, CancellationToken ct = default)
    {
        var report = await ScanAsync(ct);
        if (report.Divergence == 0) return report;

        if (report.Divergence > MaxRepairsPerRun)
        {
            logger.LogError(
                "Grant reconciliation found {Divergence} divergent grants, above the {Max} repair bound — refusing to repair",
                report.Divergence, MaxRepairsPerRun);
            return report with
            {
                RepairRefused =
                    $"{report.Divergence} divergent grants exceeds the {MaxRepairsPerRun} bound. That is a " +
                    "store-level failure, not dropped writes, and both repairs are destructive in that state. " +
                    "Investigate before repairing.",
            };
        }

        var revoked = await RevokeOrphanTuplesAsync(report.OrphanTuples, actorId, ct);
        var removed = await RemoveOrphanRowsAsync(report.OrphanRows, actorId, ct);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Grant reconciliation repaired {Revoked} orphan tuple(s) and {Removed} orphan row(s)",
                revoked, removed);

        return report with { TuplesRevoked = revoked, RowsRemoved = removed };
    }

    // ── Repairs ───────────────────────────────────────────────────────────────

    private async Task<int> RevokeOrphanTuplesAsync(
        IReadOnlyList<RelationTuple> orphans, Guid? actorId, CancellationToken ct)
    {
        if (orphans.Count == 0) return 0;

        // Re-read rather than reuse the scan's snapshot: this is the second look that lets a grant
        // which completed mid-scan keep its tuple.
        var dbKeys = (await DatabaseGrantsAsync(ct)).Select(g => g.Key).ToHashSet();

        var revoked = 0;
        foreach (var t in orphans.Where(t => !dbKeys.Contains(t)))
        {
            await keto.DeleteRelationTupleAsync(t.Namespace, t.Object, t.Relation, t.Subject);
            await AuditRepairAsync("system.grant_reconcile.tuple_revoked", t, actorId);
            revoked++;
        }
        return revoked;
    }

    private async Task<int> RemoveOrphanRowsAsync(
        IReadOnlyList<RelationTuple> orphans, Guid? actorId, CancellationToken ct)
    {
        if (orphans.Count == 0) return 0;

        // Tracked, unlike every other read here: these entities are about to be deleted, and a
        // no-tracking copy of a row the caller's context already holds collides with it on attach.
        var rowsByKey = (await DatabaseGrantsAsync(ct, tracked: true)).ToLookup(g => g.Key, g => g.Row);
        var removed = 0;
        foreach (var t in orphans)
        {
            // A fresh authoritative check, not the scan's list: if the tuple is there after all,
            // the grant is live and its row is bookkeeping, not garbage.
            if (await keto.CheckAsync(t.Namespace, t.Object, t.Relation, t.Subject)) continue;

            foreach (var row in rowsByKey[t])
            {
                db.Remove(row);
                removed++;
            }
            await AuditRepairAsync("system.grant_reconcile.row_removed", t, actorId);
        }

        if (removed > 0) await db.SaveChangesAsync(ct);
        return removed;
    }

    private Task AuditRepairAsync(string action, RelationTuple t, Guid? actorId)
    {
        // Only an Organisations tuple names its organisation in the object; a Projects tuple names
        // a project, and resolving the org from it is a lookup this does not need to make — the
        // tuple itself is in the metadata either way.
        var orgId = t.Namespace == Roles.KetoOrgsNamespace && Guid.TryParse(t.Object, out var id) ? id : (Guid?)null;
        return audit.RecordAsync(orgId, null, actorId, action, "relation_tuple", $"{t.Namespace}:{t.Object}",
            new Dictionary<string, object>
            {
                ["namespace"] = t.Namespace,
                ["object"] = t.Object,
                ["relation"] = t.Relation,
                ["subject"] = t.Subject,
            });
    }

    // ── The two stores, in one shape ──────────────────────────────────────────

    private async Task<HashSet<RelationTuple>> KetoGrantsAsync(CancellationToken ct)
    {
        var tuples = new HashSet<RelationTuple>();
        foreach (var relation in Roles.Management)
            foreach (var t in await keto.ListRelationTuplesAsync(Roles.KetoOrgsNamespace, relation, ct))
                tuples.Add(t);

        // Project roles are one relation per tenant-chosen role name, so they cannot be listed by
        // relation. Everything else in the namespace is structural and out of scope.
        var projectTuples = await keto.ListRelationTuplesAsync(Roles.KetoProjectsNamespace, null, ct);
        foreach (var t in projectTuples.Where(t => t.Relation.StartsWith(ProjectRoleRelationPrefix, StringComparison.Ordinal)))
            tuples.Add(t);

        return tuples;
    }

    private const string ProjectRoleRelationPrefix = "role:";

    /// <summary>
    /// Each backing row projected into the tuple the grant path would have written for it. Rows
    /// carrying a role name outside <see cref="Roles.Management"/> are left out: no write path
    /// produces a tuple for one, so comparing them would manufacture divergence.
    /// </summary>
    private async Task<List<(RelationTuple Key, object Row)>> DatabaseGrantsAsync(
        CancellationToken ct, bool tracked = false)
    {
        var orgRoles = await Query(db.OrgRoles, tracked)
            .Where(r => Roles.Management.Contains(r.Role))
            .ToListAsync(ct);

        var projectRoles = await Query(db.UserProjectRoles, tracked)
            .Select(r => new { Row = r, r.Role.Name })
            .ToListAsync(ct);

        return
        [
            .. orgRoles.Select(r => (
                new RelationTuple(Roles.KetoOrgsNamespace, r.OrgId.ToString(), r.Role, KetoSubject(r.UserId, r.ScopeId)),
                (object)r)),
            .. projectRoles.Select(r => (
                new RelationTuple(Roles.KetoProjectsNamespace, r.Row.ProjectId.ToString(),
                    ProjectRoleRelationPrefix + r.Name, $"user:{r.Row.UserId}"),
                (object)r.Row)),
        ];
    }

    private static IQueryable<T> Query<T>(DbSet<T> set, bool tracked) where T : class =>
        tracked ? set : set.AsNoTracking();

    /// <summary>The subject form <c>KetoService.AssignManagementRoleAsync</c> writes.</summary>
    private static string KetoSubject(Guid userId, Guid? scopeId) =>
        scopeId.HasValue ? $"user:{userId}|project:{scopeId}" : $"user:{userId}";
}
