using System.Text.Json;

namespace RediensIAM.Entities;

public class Organisation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public Guid OrgListId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? SuspendedAt { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public UserList OrgList { get; set; } = null!;
    public ICollection<UserList> UserLists { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
    public ICollection<OrgRole> OrgRoles { get; set; } = [];
}
