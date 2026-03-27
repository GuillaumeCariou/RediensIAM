using System.Text.Json;

namespace RediensIAM.Entities;

public class Project
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? HydraClientId { get; set; }
    public Guid? AssignedUserListId { get; set; }
    public Dictionary<string, object> LoginTheme { get; set; } = [];
    public string? LoginTemplate { get; set; }
    public bool RequireRoleToLogin { get; set; }
    public bool AllowSelfRegistration { get; set; }
    public string[] AllowedEmailDomains { get; set; } = [];
    public bool EmailVerificationEnabled { get; set; }
    public bool SmsVerificationEnabled { get; set; }
    public bool Active { get; set; } = true;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? DefaultRoleId { get; set; }
    public int MinPasswordLength { get; set; }
    public bool PasswordRequireUppercase { get; set; }
    public bool PasswordRequireLowercase { get; set; }
    public bool PasswordRequireDigit { get; set; }
    public bool PasswordRequireSpecial { get; set; }

    public Organisation Organisation { get; set; } = null!;
    public UserList? AssignedUserList { get; set; }
    public Role? DefaultRole { get; set; }
    public ICollection<Role> Roles { get; set; } = [];
    public ICollection<UserProjectRole> UserProjectRoles { get; set; } = [];
    public ICollection<ServiceAccountProjectRole> ServiceAccountProjectRoles { get; set; } = [];
}
