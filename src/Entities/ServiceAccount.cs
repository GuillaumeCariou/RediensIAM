namespace RediensIAM.Entities;

public class ServiceAccount
{
    public Guid Id { get; set; }
    public Guid UserListId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HydraClientId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastUsedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public UserList UserList { get; set; } = null!;
    public ICollection<PersonalAccessToken> PersonalAccessTokens { get; set; } = [];
    public ICollection<ServiceAccountProjectRole> ProjectRoles { get; set; } = [];
}
