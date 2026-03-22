namespace RediensIAM.Entities;

public class UserProjectRole
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public User User { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
