using Bogus;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Infrastructure;

/// <summary>
/// Helper that creates test fixtures directly in the database.
/// Uses a unique GUID suffix on all names/emails to avoid conflicts between tests.
/// </summary>
public class SeedData
{
    private readonly RediensIamDbContext _db;
    private readonly HydraStub          _hydra;
    private readonly PasswordService    _pwd;

    // Shared Bogus faker for generating plausible-looking data
    private static readonly Faker Faker = new("en");

    public SeedData(RediensIamDbContext db, HydraStub hydra, PasswordService pwd)
    {
        _db    = db;
        _hydra = hydra;
        _pwd   = pwd;
    }

    // ── Unique helpers ────────────────────────────────────────────────────────

    public static string UniqueEmail()    => $"{Guid.NewGuid():N}@test.com";
    public static string UniqueSlug()     => Guid.NewGuid().ToString("N")[..12];
    public static string UniqueName()     => $"Test-{Guid.NewGuid().ToString("N")[..8]}";

    // ── Organisation ─────────────────────────────────────────────────────────

    public async Task<(Organisation org, UserList orgList)> CreateOrgAsync(string? name = null)
    {
        var slug = UniqueSlug();

        // Step 1: create the org list first (no OrgId yet — circular FK)
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"__org_{slug}__",
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.UserLists.Add(list);
        await _db.SaveChangesAsync();

        // Step 2: create the org pointing at the list
        var org = new Organisation
        {
            Id        = Guid.NewGuid(),
            Name      = name ?? UniqueName(),
            Slug      = slug,
            OrgListId = list.Id,
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Organisations.Add(org);
        await _db.SaveChangesAsync();

        // Step 3: back-fill OrgId on the list
        list.OrgId = org.Id;
        await _db.SaveChangesAsync();

        return (org, list);
    }

    // ── Project ───────────────────────────────────────────────────────────────

    public async Task<Project> CreateProjectAsync(Guid orgId, string? name = null)
    {
        var slug    = UniqueSlug();
        var project = new Project
        {
            Id              = Guid.NewGuid(),
            OrgId           = orgId,
            Name            = name ?? UniqueName(),
            Slug            = slug,
            HydraClientId   = $"project-{slug}",
            Active          = true,
            CreatedAt       = DateTimeOffset.UtcNow,
            UpdatedAt       = DateTimeOffset.UtcNow,
        };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return project;
    }

    // ── User List (movable) ───────────────────────────────────────────────────

    public async Task<UserList> CreateUserListAsync(Guid orgId, string? name = null)
    {
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = name ?? UniqueName(),
            Immovable = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.UserLists.Add(list);
        await _db.SaveChangesAsync();
        return list;
    }

    // ── User ──────────────────────────────────────────────────────────────────

    public async Task<User> CreateUserAsync(
        Guid   userListId,
        string? email    = null,
        string  password = "P@ssw0rd!Test",
        bool    active   = true)
    {
        email ??= UniqueEmail();
        var user = new User
        {
            Id             = Guid.NewGuid(),
            UserListId     = userListId,
            Email          = email,
            Username       = email.Split('@')[0],
            Discriminator  = Random.Shared.Next(1000, 9999).ToString(),
            PasswordHash   = _pwd.Hash(password),
            EmailVerified  = true,
            Active         = active,
            CreatedAt      = DateTimeOffset.UtcNow,
            UpdatedAt      = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    // ── Role ──────────────────────────────────────────────────────────────────

    public async Task<Role> CreateRoleAsync(Guid projectId, string? name = null, int rank = 100)
    {
        var role = new Role
        {
            Id        = Guid.NewGuid(),
            ProjectId = projectId,
            Name      = name ?? UniqueName(),
            Rank      = rank,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        return role;
    }

    // ── Bearer tokens ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a bearer token that the gateway middleware will accept as a super admin.
    /// Registers it with the Hydra stub so introspection works.
    /// </summary>
    public string SuperAdminToken(Guid userId)
    {
        var token = $"sa-{userId:N}";
        _hydra.RegisterToken(token, userId.ToString(), null, null, ["super_admin"]);
        return token;
    }

    /// <summary>Creates a bearer token accepted as org_admin for the given org.</summary>
    public string OrgAdminToken(Guid userId, Guid orgId)
    {
        var token = $"oa-{userId:N}-{orgId:N}";
        _hydra.RegisterToken(token, userId.ToString(), orgId.ToString(), null, ["org_admin"]);
        return token;
    }

    /// <summary>Creates a bearer token accepted as project_manager.</summary>
    public string ProjectManagerToken(Guid userId, Guid orgId, Guid projectId)
    {
        var token = $"pm-{userId:N}-{projectId:N}";
        _hydra.RegisterToken(token, userId.ToString(), orgId.ToString(), projectId.ToString(), ["project_admin"]);
        return token;
    }

    /// <summary>Creates a bearer token for a regular authenticated user (no management level).</summary>
    public string UserToken(Guid userId, Guid orgId, Guid projectId)
    {
        var token = $"usr-{userId:N}-{projectId:N}";
        _hydra.RegisterToken(token, userId.ToString(), orgId.ToString(), projectId.ToString(), []);
        return token;
    }

    // ── Service Account ───────────────────────────────────────────────────────

    public async Task<ServiceAccount> CreateServiceAccountAsync(Guid userListId, string? name = null)
    {
        var sa = new ServiceAccount
        {
            Id         = Guid.NewGuid(),
            UserListId = userListId,
            Name       = name ?? UniqueName(),
            Active     = true,
            CreatedAt  = DateTimeOffset.UtcNow,
        };
        _db.ServiceAccounts.Add(sa);
        await _db.SaveChangesAsync();
        return sa;
    }

    // ── Org Role (management) ─────────────────────────────────────────────────

    public async Task<OrgRole> CreateOrgRoleAsync(Guid orgId, Guid userId, string role, Guid? scopeId = null)
    {
        var orgRole = new OrgRole
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            UserId    = userId,
            Role      = role,
            ScopeId   = scopeId,
            GrantedBy = userId,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        _db.OrgRoles.Add(orgRole);
        await _db.SaveChangesAsync();
        return orgRole;
    }
}
