namespace RediensIAM.Entities;

public class ServiceAccountProjectRole
{
    public Guid Id { get; set; }
    public Guid SaId { get; set; }
    public Guid ProjectId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public Guid? GrantedBy { get; set; }

    public ServiceAccount ServiceAccount { get; set; } = null!;
    public Project Project { get; set; } = null!;
}
