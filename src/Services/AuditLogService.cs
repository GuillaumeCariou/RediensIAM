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
}
