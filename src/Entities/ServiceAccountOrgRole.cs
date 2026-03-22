namespace RediensIAM.Entities;

public class ServiceAccountOrgRole
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public ServiceAccount ServiceAccount { get; set; } = null!;
}
