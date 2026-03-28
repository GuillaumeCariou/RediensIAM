using System.Net;

namespace RediensIAM.Data.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public Guid? OrgId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
