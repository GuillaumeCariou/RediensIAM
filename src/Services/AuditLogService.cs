using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

public class AuditLogService(IServiceScopeFactory scopeFactory, IHttpContextAccessor http, WebhookService webhookService)
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

        // Prometheus
        IamMetrics.AuditEvents.WithLabels(action).Inc();

        // Fire-and-forget webhook dispatch for supported event types
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
    /// Walks one organisation's hash chain and returns the id of the first row that does not link
    /// (S-3). Null means every surviving row is exactly as it was written and none has been
    /// removed from the middle.
    ///
    /// <para>
    /// This is detection, not prevention: the application holds the credentials that could rewrite
    /// the table, so nothing at this layer can stop a determined writer. What it can do is make
    /// the result inconsistent, which is the difference between an audit log and a list of rows.
    /// Run it from an operator surface or on a schedule; the answer is only useful if somebody
    /// looks at it.
    /// </para>
    /// </summary>
    public async Task<long?> VerifyChainAsync(Guid? orgId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return AuditChain.FirstBreak(rows);
    }
}
