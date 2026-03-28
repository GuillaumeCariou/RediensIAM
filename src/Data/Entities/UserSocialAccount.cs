namespace RediensIAM.Data.Entities;

public class UserSocialAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Provider key: "google", "github", "gitlab", "facebook", or the custom OIDC provider id.</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>The subject/user-id returned by the provider.</summary>
    public string ProviderUserId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset LinkedAt { get; set; }

    public User User { get; set; } = null!;
}
