namespace RediensIAM.Data.Entities;

public class Role
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Rank { get; set; } = 100;
    /// <summary>
    /// Granted to every new account of this role's project. A project may flag as many roles as it
    /// likes, or none — which is why the flag lives here and not as a single
    /// <c>projects.DefaultRoleId</c> foreign key, as it did until the set became plural.
    /// </summary>
    public bool IsDefault { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<UserProjectRole> UserProjectRoles { get; set; } = [];
}
