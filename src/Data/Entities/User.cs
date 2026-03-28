namespace RediensIAM.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public Guid UserListId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Discriminator { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Phone { get; set; }
    public bool PhoneVerified { get; set; }
    public bool TotpEnabled { get; set; }
    public string? TotpSecret { get; set; }
    public bool WebAuthnEnabled { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public UserList UserList { get; set; } = null!;
    public ICollection<UserProjectRole> ProjectRoles { get; set; } = [];
    public ICollection<OrgRole> OrgRoles { get; set; } = [];
    public ICollection<WebAuthnCredential> WebAuthnCredentials { get; set; } = [];
    public ICollection<BackupCode> BackupCodes { get; set; } = [];
    public ICollection<EmailToken> EmailTokens { get; set; } = [];
    public ICollection<UserSocialAccount> SocialAccounts { get; set; } = [];
}
