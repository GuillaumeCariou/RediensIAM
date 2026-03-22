using RediensIAM.Data;
using RediensIAM.Entities;

namespace RediensIAM.Services;

public class AuditLogService(RediensIamDbContext db, IHttpContextAccessor http)
{
    public async Task RecordAsync(
        Guid? orgId, Guid? projectId, Guid? actorId, string action,
        string? targetType = null, string? targetId = null,
        Dictionary<string, object>? metadata = null)
    {
        var ctx = http.HttpContext;
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
    }
}
