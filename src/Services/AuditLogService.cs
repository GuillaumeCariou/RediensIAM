using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

public class AuditLogService(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor http,
    WebhookService webhookService,
    AppConfig appConfig)
{
    public async Task RecordAsync(
        Guid? orgId, Guid? projectId, Guid? actorId, string action,
        string? targetType = null, string? targetId = null,
        Dictionary<string, object>? metadata = null)
    {
        var ctx = http.HttpContext;

        // Own scope, own DbContext. Writing the audit row through the request's DbContext meant
        // SaveChangesAsync() here also committed whatever the caller had pending — including
        // half-applied changes it had not decided to persist yet.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

        db.AuditLogs.Add(new AuditLog
        {
            OrgId = orgId,
            ProjectId = projectId,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Metadata = metadata ?? [],
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = ctx?.Request.Headers.UserAgent.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IamMetrics.AuditEvents.WithLabels(action).Inc();

        if (WebhookEvents.All.Contains(action))
        {
            _ = webhookService.DispatchAsync(action, new
            {
                org_id     = orgId,
                project_id = projectId,
                actor_id   = actorId,
                target_type = targetType,
                target_id  = targetId,
                metadata
            }, orgId, projectId);
        }
    }

    /// <summary>
    /// Walks one organisation's hash chain (S-3) and reports what can honestly be said about it:
    /// where the first broken link is, how many rows were recomputed under a key this deployment
    /// holds, and how many it cannot vouch for at all.
    ///
    /// <para>
    /// <b>An intact chain is not the same as a verified one.</b> Rows written before the chain was
    /// keyed carry an unkeyed digest that anyone with database write access can recompute, so they
    /// walk cleanly whatever was done to them. They are counted as unverifiable rather than
    /// reported as valid, because reporting them as valid is the lie this control exists to avoid.
    /// <see cref="AuditChainStatus.FullyVerified"/> is the flag that means what "no break" used to
    /// pretend to mean.
    /// </para>
    ///
    /// <para>
    /// This is detection, not prevention: the application holds the credentials that could rewrite
    /// the table, so nothing at this layer can stop a determined writer. What keying adds is that
    /// a rewrite can no longer be made to <i>verify</i>. Callers: <c>IntegrityMonitorService</c>
    /// once a day, and <c>GET /admin/audit-chain</c> on demand.
    /// </para>
    /// </summary>
    public async Task<AuditChainStatus> VerifyChainAsync(Guid? orgId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return AuditChain.Verify(appConfig.AuditChainKey, rows);
    }

    /// <summary>
    /// Every chain in the deployment: one per organisation, plus the <c>OrgId IS NULL</c> chain
    /// that carries deployment-wide rows (reported with a null organisation id).
    /// </summary>
    public async Task<IReadOnlyList<(Guid? OrgId, AuditChainStatus Status)>> VerifyAllChainsAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
        var orgIds = await db.Organisations.AsNoTracking().Select(o => (Guid?)o.Id).ToListAsync(cancellationToken);

        var results = new List<(Guid? OrgId, AuditChainStatus Status)>();
        foreach (var orgId in orgIds.Append(null))
            results.Add((orgId, await VerifyChainAsync(orgId, cancellationToken)));
        return results;
    }
}
