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

    /// <summary>Hash of the previous row in this organisation's chain. Null at the start of a chain.</summary>
    public string? PrevHash { get; set; }

    /// <summary>
    /// SHA-256 over this row's contents and <see cref="PrevHash"/>. Written by
    /// <see cref="RediensIamDbContext.SaveChangesAsync"/>, never by a caller. See
    /// <see cref="AuditChain"/>.
    /// </summary>
    public string Hash { get; set; } = "";
}
