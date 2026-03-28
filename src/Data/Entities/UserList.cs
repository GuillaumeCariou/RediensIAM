namespace RediensIAM.Data.Entities;

public class UserList
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? OrgId { get; set; }
    public bool Immovable { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Organisation? Organisation { get; set; }
    public ICollection<User> Users { get; set; } = [];
    public ICollection<ServiceAccount> ServiceAccounts { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
}
