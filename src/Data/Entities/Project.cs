namespace RediensIAM.Data.Entities;

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
    // Opt-in, by product decision: forcing a second factor on every new tenant is a UX call that
    // belongs to whoever owns the tenant. RediensIAM's own admin surface is governed separately
    // and not by this flag: the first administrator signs in without a factor and every one after
    // that must enrol, which is derived from the deployment's state rather than configured.
    // Breached-password checking below stays on by default: it costs the user nothing.
    public bool RequireMfa { get; set; }
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
    public string? EmailFromName { get; set; }
    public string[] IpAllowlist { get; set; } = [];
    public bool CheckBreachedPasswords { get; set; } = true;
    public string[] AllowedScopes { get; set; } = [];

    // Not columns: the redirect URIs live in Hydra, which is the registry that enforces them. They
    // are carried here so a project reads as one object — a console that has to fetch them from a
    // second place to edit them is a console that will forget to.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string[] RedirectUris { get; set; } = [];
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string[] PostLogoutRedirectUris { get; set; } = [];

    public Organisation Organisation { get; set; } = null!;
    public UserList? AssignedUserList { get; set; }
    public Role? DefaultRole { get; set; }
    public ICollection<Role> Roles { get; set; } = [];
    public ICollection<UserProjectRole> UserProjectRoles { get; set; } = [];
}
