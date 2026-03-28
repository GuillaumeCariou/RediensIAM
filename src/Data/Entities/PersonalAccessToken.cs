namespace RediensIAM.Data.Entities;

public class PersonalAccessToken
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ServiceAccount ServiceAccount { get; set; } = null!;
}
