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
    /// <c>k{keyId}:{hex}</c> — HMAC-SHA256 over this row's contents and <see cref="PrevHash"/>,
    /// under the deployment's audit-chain key. Written by
    /// <see cref="RediensIamDbContext.SaveChangesAsync"/>, never by a caller. A bare 64-hex value
    /// with no <c>k…:</c> prefix is a row from before the chain was keyed, and an empty value one
    /// from before the chain existed; see <see cref="AuditChain"/> for what each is worth.
    /// </summary>
    public string Hash { get; set; } = "";
}
