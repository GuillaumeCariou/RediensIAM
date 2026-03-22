namespace RediensIAM.Entities;

public class Role
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Rank { get; set; } = 100;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<UserProjectRole> UserProjectRoles { get; set; } = [];
}
