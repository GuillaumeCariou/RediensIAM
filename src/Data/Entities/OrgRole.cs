namespace RediensIAM.Data.Entities;

public class OrgRole
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid? ScopeId { get; set; }
    public string? DisplayName { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
    public User User { get; set; } = null!;
}
