namespace RediensIAM.Services;

/// <summary>
/// Runs the two checks that only mean something if somebody actually runs them: the audit hash
/// chain (S-3) and the Keto/Postgres grant dual write (S-8).
///
/// <para>
/// Both had a verifier and no caller, which is not a control — it is a function that would have
/// noticed. This is the caller. It reports and never repairs: revoking a grant or deleting a row
/// unattended, on the strength of one background read, is a privilege change nobody asked for.
/// Repair is <c>POST /admin/grant-reconcile/repair</c>, run by an operator who has read the
/// finding. The findings themselves land in three places — the Prometheus gauges below (what an
/// alert rule watches), a warning log line, and the on-demand endpoints.
/// </para>
///
/// <para>
/// ponytail: one daily pass for both checks. Chain verification reads every audit row of every
/// organisation, so it is deliberately not hourly; the grant scan is cheap and would happily run
/// more often. Split the loop if the divergence window ever needs to be shorter than a day.
/// </para>
/// </summary>
public class IntegrityMonitorService(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityMonitorService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed pass must not kill the loop: the next one is the only thing that will
                // notice a chain break, and a monitor that dies on a transient Keto 503 is the
                // same as no monitor at all.
                logger.LogWarning(ex, "Integrity pass failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>One pass, exposed so an operator surface or a test can force one.</summary>
    public async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await VerifyChainsAsync(scope.ServiceProvider.GetRequiredService<AuditLogService>(), ct);
        await ScanGrantsAsync(scope.ServiceProvider.GetRequiredService<GrantReconciler>(), ct);
    }

    private async Task VerifyChainsAsync(AuditLogService audit, CancellationToken ct)
    {
        var statuses = await audit.VerifyAllChainsAsync(ct);

        IamMetrics.AuditChainBroken.Set(statuses.Count(s => !s.Status.Intact));
        IamMetrics.AuditChainUnverifiableRows.Set(statuses.Sum(s => s.Status.Unverifiable));

        foreach (var (orgId, status) in statuses.Where(s => !s.Status.Intact))
            logger.LogError(
                "Audit chain for organisation {OrgId} breaks at row {RowId}: that row is not what it was written as, "
                + "or a row before it is gone",
                orgId, status.FirstBreak);
    }

    private async Task ScanGrantsAsync(GrantReconciler reconciler, CancellationToken ct)
    {
        var report = await reconciler.ScanAsync(ct);

        IamMetrics.GrantDivergence.WithLabels("orphan_tuple").Set(report.OrphanTuples.Count);
        IamMetrics.GrantDivergence.WithLabels("orphan_row").Set(report.OrphanRows.Count);

        if (report.OrphanTuples.Count > 0)
            logger.LogError(
                "{Count} Keto grant(s) have no backing row — live privilege with no record of who granted it. "
                + "First: {Namespace}:{Object}#{Relation}@{Subject}",
                report.OrphanTuples.Count, report.OrphanTuples[0].Namespace, report.OrphanTuples[0].Object,
                report.OrphanTuples[0].Relation, report.OrphanTuples[0].Subject);

        if (report.OrphanRows.Count > 0)
            logger.LogWarning(
                "{Count} grant row(s) have no Keto tuple — dead as authorisation, still read by the consent path "
                + "for token scopes. First: {Namespace}:{Object}#{Relation}@{Subject}",
                report.OrphanRows.Count, report.OrphanRows[0].Namespace, report.OrphanRows[0].Object,
                report.OrphanRows[0].Relation, report.OrphanRows[0].Subject);
    }
}
