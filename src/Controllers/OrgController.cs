using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Exceptions;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[Route("org")]
[RequireManagementLevel(ManagementLevel.OrgAdmin)]
public class OrgController(
    RediensIamDbContext db,
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    AppConfig appConfig,
    IEmailService emailService,
    IDistributedCache cache,
    ILogger<OrgController> logger) : ControllerBase
{
    private static readonly string[] BuiltInScopes = ["openid", "profile", "offline_access"];
    private const string KindInvite      = "invite";
    private const string AuditOrg        = "organisation";

    private TokenClaims Claims => HttpContext.GetClaims() ?? throw new UnauthorizedException("Not authenticated");
    private Guid OrgId   => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;
    private Guid ActorId => Claims.ParsedUserId;

    // ── Organisation ──────────────────────────────────────────────────────────

    [HttpGet("info")]
    public async Task<IActionResult> GetOrgInfo()
    {
        var org = await db.Organisations
            .Where(o => o.Id == OrgId)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt, o.OrgListId, o.CreatedBy, o.AuditRetentionDays })
            .FirstOrDefaultAsync();
        if (org == null) return NotFound();
        return Ok(org);
    }

    [HttpPatch("settings")]
    public async Task<IActionResult> UpdateOrgSettings([FromBody] UpdateOrgSettingsRequest body)
    {
        var org = await db.Organisations.FindAsync(OrgId);
        if (org == null) return NotFound();
        // -1 means "reset to global default"
        if (body.AuditRetentionDays.HasValue)
            org.AuditRetentionDays = body.AuditRetentionDays == -1 ? null : body.AuditRetentionDays;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "org.settings_updated", AuditOrg, OrgId.ToString());
        return Ok(new { org.Id, org.AuditRetentionDays });
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    [HttpGet("projects")]
    public async Task<IActionResult> ListProjects([FromQuery] Guid? org_id)
    {
        Guid orgId;
        if (Guid.TryParse(Claims.OrgId, out var claimsOrgId))
            orgId = claimsOrgId;
        else if (org_id.HasValue && Claims.Roles.Contains(Roles.SuperAdmin))
            orgId = org_id.Value;
        else
            throw new ForbiddenException("No org context");
        var projects = await db.Projects
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active, p.AssignedUserListId, p.RequireRoleToLogin })
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("projects")]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest body)
    {
        var orgId = OrgId;
        var project = new Project
        {
            OrgId = orgId, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin,
            Active = true, CreatedBy = ActorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        try
        {
            await hydra.CreateOAuth2ClientAsync(new
            {
                client_id = $"client_{project.Id}",
                client_name = $"Project: {project.Name}",
                redirect_uris = body.RedirectUris ?? [],
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                scope = "openid profile offline_access",
                token_endpoint_auth_method = "none",
                metadata = new { project_id = project.Id.ToString(), org_id = orgId.ToString() }
            });
            project.HydraClientId = $"client_{project.Id}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hydra client creation failed for project {ProjectId} — rolling back", project.Id);
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return StatusCode(502, new { error = "hydra_unavailable", detail = ex.Message });
        }

        await keto.WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), "org", $"{Roles.KetoOrgsNamespace}:{orgId}");
        await db.SaveChangesAsync();
        await audit.RecordAsync(orgId, project.Id, ActorId, "project.created", "project", project.Id.ToString());
        return Created($"/org/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpGet("projects/{id}")]
    public async Task<IActionResult> GetProject(Guid id)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
        if (project == null) return NotFound();
        // Strip client secrets before exposing to caller
        project.LoginTheme = TotpEncryption.StripSecretsFromTheme(project.LoginTheme) ?? project.LoginTheme;
        return Ok(project);
    }

    [HttpPatch("projects/{id}")]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] UpdateProjectRequest body)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue) project.RequireRoleToLogin = body.RequireRoleToLogin.Value;
        if (body.RequireMfa.HasValue) project.RequireMfa = body.RequireMfa.Value;
        if (body.AllowSelfRegistration.HasValue) project.AllowSelfRegistration = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue) project.SmsVerificationEnabled = body.SmsVerificationEnabled.Value;
        if (body.Active.HasValue) project.Active = body.Active.Value;
        if (body.AllowedEmailDomains != null) project.AllowedEmailDomains = body.AllowedEmailDomains;
        if (body.ClearDefaultRole == true)
            project.DefaultRoleId = null;
        else if (body.DefaultRoleId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == body.DefaultRoleId && r.ProjectId == id);
            if (role == null) return BadRequest(new { error = "invalid_default_role" });
            project.DefaultRoleId = body.DefaultRoleId;
        }
        if (body.LoginTheme != null)
        {
            var encKey = Convert.FromHexString(appConfig.TotpSecretEncryptionKey);
            project.LoginTheme = TotpEncryption.EncryptProviderSecretsInTheme(
                body.LoginTheme, project.LoginTheme, encKey)!;
        }
        if (body.ClearEmailFromName == true)
            project.EmailFromName = null;
        else if (body.EmailFromName != null)
            project.EmailFromName = body.EmailFromName;
        if (body.IpAllowlist != null) project.IpAllowlist = body.IpAllowlist;
        if (body.CheckBreachedPasswords.HasValue) project.CheckBreachedPasswords = body.CheckBreachedPasswords.Value;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    // ── Scopes ────────────────────────────────────────────────────────────────

    [HttpGet("projects/{id}/scopes")]
    public async Task<IActionResult> GetProjectScopes(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return Ok(new { custom_scopes = project.AllowedScopes, built_in = BuiltInScopes });
    }

    [HttpPut("projects/{id}/scopes")]
    public async Task<IActionResult> UpdateProjectScopes(Guid id, [FromBody] UpdateScopesRequest body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();

        var invalid = body.Scopes.Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-z0-9_:.-]+$")).ToArray();
        if (invalid.Length > 0) return BadRequest(new { error = "invalid_scope_names", invalid });

        project.AllowedScopes = body.Scopes;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (project.HydraClientId != null)
        {
            try { await hydra.UpdateOAuth2ClientScopeAsync(project.HydraClientId, project.AllowedScopes); }
            catch (Exception ex) { logger.LogWarning(ex, "Hydra scope update failed for project {ProjectId}", id); }
        }

        await audit.RecordAsync(OrgId, id, ActorId, "project.scopes_updated", "project", id.ToString());
        return Ok(new { project.Id, custom_scopes = project.AllowedScopes });
    }

    [HttpDelete("projects/{id}")]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
        if (project == null) return NotFound();
        if (project.HydraClientId != null)
        {
            try { await hydra.DeleteOAuth2ClientAsync(project.HydraClientId); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete Hydra client {ClientId}", project.HydraClientId); }
        }
        await keto.DeleteAllProjectTuplesAsync(id.ToString());
        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, id, ActorId, "project.deleted", "project", id.ToString());
        return NoContent();
    }

    [HttpPut("projects/{id}/userlist")]
    public async Task<IActionResult> AssignUserList(Guid id, [FromBody] AssignUserListRequest body)
    {
        var orgId = OrgId;
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == orgId);
        if (project == null) return NotFound();
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == orgId);
        if (list == null) return BadRequest(new { error = "userlist_not_in_org" });
        project.AssignedUserListId = body.UserListId;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.AssignedUserListId });
    }

    [HttpDelete("projects/{id}/userlist")]
    public async Task<IActionResult> UnassignUserList(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("userlists")]
    public async Task<IActionResult> ListUserLists()
    {
        var lists = await db.UserLists
            .Where(ul => ul.OrgId == OrgId && !ul.Immovable)
            .Select(ul => new { ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt })
            .ToListAsync();
        return Ok(lists);
    }

    [HttpPost("userlists")]
    public async Task<IActionResult> CreateUserList([FromBody] CreateUserListRequest body)
    {
        var ul = new UserList { Name = body.Name, OrgId = OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/org/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    [HttpGet("userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
        var ul = await db.UserLists.Include(ul => ul.Users)
            .FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();
        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id)
            .Select(p => new { p.Id, p.Name }).ToListAsync();
        return Ok(new { ul.Id, ul.Name, ul.Immovable, user_count = ul.Users.Count, assigned_projects = assignedProjects });
    }

    [HttpDelete("userlists/{id}")]
    public async Task<IActionResult> DeleteUserList(Guid id)
    {
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();
        if (ul.Immovable) return BadRequest(new { error = "cannot_delete_immovable" });
        if (await db.Projects.AnyAsync(p => p.AssignedUserListId == id))
            return BadRequest(new { error = "userlist_is_assigned_to_project" });
        db.UserLists.Remove(ul);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("userlists/{id}/cleanup")]
    public async Task<IActionResult> CleanupUserList(Guid id, [FromBody] OrgCleanupRequest body)
    {
        var orgId = OrgId;
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == orgId)) return NotFound();

        var projectIds = await db.Projects.Where(p => p.AssignedUserListId == id).Select(p => p.Id).ToListAsync();
        var allUserIds = await db.Users.Where(u => u.UserListId == id).Select(u => u.Id).ToHashSetAsync();
        var orphanedRoles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => projectIds.Contains(r.ProjectId) && !allUserIds.Contains(r.UserId)).ToListAsync();

        var inactiveUsers = new List<User>();
        if (body.RemoveInactiveUsers)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-body.InactiveThresholdDays);
            inactiveUsers = await db.Users
                .Where(u => u.UserListId == id && (u.LastLoginAt == null || u.LastLoginAt < cutoff))
                .ToListAsync();
        }

        if (!body.DryRun)
        {
            if (body.RemoveOrphanedRoles)
            {
                db.UserProjectRoles.RemoveRange(orphanedRoles);
                foreach (var r in orphanedRoles)
                    await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, r.ProjectId.ToString(), $"role:{r.Role.Name}", $"user:{r.UserId}");
            }
            if (body.RemoveInactiveUsers)
            {
                foreach (var u in inactiveUsers)
                    await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{u.Id}");
                db.Users.RemoveRange(inactiveUsers);
            }
            await db.SaveChangesAsync();
        }

        return Ok(new
        {
            dry_run = body.DryRun,
            orphaned_roles_found = orphanedRoles.Count,
            orphaned_roles_removed = body.DryRun || !body.RemoveOrphanedRoles ? 0 : orphanedRoles.Count,
            inactive_users_found = inactiveUsers.Count,
            inactive_users_removed = body.DryRun || !body.RemoveInactiveUsers ? 0 : inactiveUsers.Count,
        });
    }

    [HttpGet("userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == OrgId)) return NotFound();
        var userIds = await db.Users.Where(u => u.UserListId == id).Select(u => u.Id).ToListAsync();
        var pendingInvites = await db.EmailTokens
            .Where(t => userIds.Contains(t.UserId) && t.Kind == KindInvite && t.UsedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(t => t.UserId).ToHashSetAsync();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users.Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt, invite_pending = pendingInvites.Contains(u.Id) }));
    }

    [HttpPost("userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] CreateUserRequest body)
    {
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();

        var username = body.Username ?? body.Email.Split('@')[0];
        string discriminator;
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
        while (await db.Users.AnyAsync(u => u.UserListId == id && u.Username == username && u.Discriminator == discriminator));

        var isInvite = string.IsNullOrEmpty(body.Password);
        var user = new User
        {
            UserListId = id, Username = username,
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = isInvite ? null : passwords.Hash(body.Password!),
            Active = !isInvite, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{user.Id}");
        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id && p.OrgId == OrgId).ToListAsync();
        foreach (var project in assignedProjects)
            await keto.AssignDefaultRoleAsync(project, user);

        string? inviteUrl = null;
        if (isInvite)
        {
            var raw  = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
            db.EmailTokens.Add(new EmailToken
            {
                UserId    = user.Id,
                Kind      = KindInvite,
                TokenHash = hash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(appConfig.InviteExpiryHours),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            inviteUrl = $"{appConfig.PublicUrl}/auth/invite/complete?token={Uri.EscapeDataString(raw)}";
            var orgName = ul.Organisation?.Name ?? "the organization";
            await emailService.SendInviteAsync(user.Email, inviteUrl, orgName);
        }

        await audit.RecordAsync(OrgId, null, ActorId, isInvite ? "user.invited" : "user.created", "user", user.Id.ToString());
        return Created($"/org/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email,
            invite_pending = isInvite
        });
    }

    [HttpPost("userlists/{id}/users/{uid}/resend-invite")]
    public async Task<IActionResult> ResendInvite(Guid id, Guid uid)
    {
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        if (user.Active) return BadRequest(new { error = "user_already_active" });

        // Expire any existing invite tokens
        var existing = await db.EmailTokens
            .Where(t => t.UserId == uid && t.Kind == KindInvite && t.UsedAt == null)
            .ToListAsync();
        foreach (var t in existing) t.ExpiresAt = DateTimeOffset.UtcNow;

        var raw  = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        db.EmailTokens.Add(new EmailToken
        {
            UserId    = user.Id,
            Kind      = KindInvite,
            TokenHash = hash,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(72),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var inviteUrl = $"{appConfig.PublicUrl}/auth/invite/complete?token={Uri.EscapeDataString(raw)}";
        var orgName   = ul.Organisation?.Name ?? "the organization";
        await emailService.SendInviteAsync(user.Email, inviteUrl, orgName);
        await audit.RecordAsync(OrgId, null, ActorId, "user.invite_resent", "user", uid.ToString());
        return Ok(new { message = "invite_resent" });
    }

    [HttpPatch("userlists/{id}/users/{uid}")]
    public async Task<IActionResult> UpdateUser(Guid id, Guid uid, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        return await ApplyUserUpdate(user, body);
    }

    [HttpGet("users/{uid}")]
    public async Task<IActionResult> GetOrgUser(Guid uid)
    {
        var user = await db.Users
            .Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == uid && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        var orgRoles = await db.OrgRoles
            .Where(r => r.UserId == uid && r.OrgId == OrgId)
            .Select(r => new { r.Role, r.OrgId, r.ScopeId })
            .ToListAsync();
        return Ok(new {
            user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName,
            user.Phone, user.Active, user.EmailVerified,
            user.LockedUntil, user.FailedLoginCount,
            user.LastLoginAt, user.CreatedAt, user.UpdatedAt,
            roles = orgRoles
        });
    }

    [HttpPatch("users/{uid}")]
    public async Task<IActionResult> UpdateOrgUser(Guid uid, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == uid && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        return await ApplyUserUpdate(user, body);
    }

    private async Task<IActionResult> ApplyUserUpdate(User user, UpdateUserRequest body)
    {
        if (body.Email != null) { user.Email = body.Email.ToLowerInvariant(); user.EmailVerified = false; user.EmailVerifiedAt = null; }
        if (body.Username != null) user.Username = body.Username;
        if (body.DisplayName != null) user.DisplayName = body.DisplayName == "" ? null : body.DisplayName;
        if (body.Phone != null) user.Phone = body.Phone == "" ? null : body.Phone;
        if (body.Active.HasValue) { user.Active = body.Active.Value; user.DisabledAt = body.Active.Value ? null : DateTimeOffset.UtcNow; }
        if (body.EmailVerified.HasValue) { user.EmailVerified = body.EmailVerified.Value; user.EmailVerifiedAt = body.EmailVerified.Value ? DateTimeOffset.UtcNow : null; }
        if (body.ClearLock == true) { user.LockedUntil = null; user.FailedLoginCount = 0; }
        if (!string.IsNullOrEmpty(body.NewPassword)) user.PasswordHash = passwords.Hash(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "user.updated", "user", user.Id.ToString());
        return Ok(new { user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName, user.Phone, user.Active, user.EmailVerified, user.LockedUntil, user.FailedLoginCount });
    }

    [HttpGet("userlists/{id}/users/{uid}/sessions")]
    public async Task<IActionResult> ListUserSessions(Guid id, Guid uid)
    {
        var user = await db.Users.Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        var subject = $"{OrgId}:{uid}";
        var sessions = await hydra.ListConsentSessionsAsync(subject);
        return Ok(sessions.Select(s => new
        {
            client_id   = s.ConsentRequest?.Client?.ClientId,
            client_name = s.ConsentRequest?.Client?.ClientName,
            scopes      = s.GrantedScopes,
            created_at  = s.ConsentRequest?.RequestedAt,
            expires_at  = s.ExpiresAt
        }));
    }

    [HttpDelete("userlists/{id}/users/{uid}/sessions")]
    public async Task<IActionResult> RevokeUserSessions(Guid id, Guid uid)
    {
        var user = await db.Users.Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        await hydra.RevokeAllConsentSessionsAsync($"{OrgId}:{uid}");
        await audit.RecordAsync(OrgId, null, ActorId, "session.revoked", "user", uid.ToString());
        return Ok(new { message = "sessions_revoked" });
    }

    [HttpPost("userlists/{id}/users/{uid}/unlock")]
    public async Task<IActionResult> UnlockUser(Guid id, Guid uid)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        user.LockedUntil      = null;
        user.FailedLoginCount = 0;
        user.UpdatedAt        = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "user.unlocked", "user", uid.ToString());
        return Ok(new { user.Id, message = "user_unlocked" });
    }

    [HttpDelete("userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUser(Guid id, Guid uid)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{uid}");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "user.removed", "user", uid.ToString());
        return NoContent();
    }

    // ── Org-list managers ─────────────────────────────────────────────────────
    // These endpoints let an org admin manage who has management roles within their org.
    // Keto tuples are written/deleted via KetoService to keep auth in sync.

    [HttpGet("admins")]
    public async Task<IActionResult> ListOrgListManagers()
    {
        var orgId = OrgId;
        var roles = await db.OrgRoles.Where(r => r.OrgId == orgId).Include(r => r.User).ToListAsync();
        var projectIds = roles.Where(r => r.ScopeId.HasValue).Select(r => r.ScopeId!.Value).Distinct().ToList();
        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(roles.Select(r => new
        {
            r.Id, r.OrgId, r.UserId, r.Role, r.ScopeId, r.GrantedAt,
            user_name  = $"{r.User.Username}#{r.User.Discriminator}",
            user_email = r.User.Email,
            scope_name = r.ScopeId.HasValue && projects.TryGetValue(r.ScopeId.Value, out var p) ? p.Name : null
        }));
    }

    [HttpPost("admins")]
    public async Task<IActionResult> AssignOrgListManager([FromBody] OrgAssignManagerRequest body)
    {
        if (body.Role == Roles.SuperAdmin) return StatusCode(403, new { error = "cannot_grant_super_admin" });
        
        await keto.AssignManagementRoleAsync(ActorId, body.UserId, OrgId, body.Role, body.ScopeId);
        return Ok(new { message = "role_assigned" });
    }

    [HttpPatch("admins/{id}")]
    public async Task<IActionResult> UpdateOrgListManager(Guid id, [FromBody] OrgUpdateManagerRequest body)
    {
        var orgId = OrgId;
        var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId);
        if (role == null) return NotFound();
        if (role.UserId == ActorId) return StatusCode(403, new { error = "cannot_modify_own_role" });

        if (body.Role != null && body.Role == Roles.SuperAdmin)
            return StatusCode(403, new { error = "cannot_grant_super_admin" });

        if (body.ScopeId != null && body.ScopeId != role.ScopeId)
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == body.ScopeId && p.OrgId == orgId);
            if (!projectExists) return BadRequest(new { error = "project_not_in_org" });
        }

        // Delete old Keto tuple before updating
        var oldSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, oldSubject);

        if (body.Role != null) role.Role = body.Role;
        if (body.ScopeId != null) role.ScopeId = body.ScopeId;
        await db.SaveChangesAsync();

        // Write new Keto tuple
        var newSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, newSubject);

        return Ok(new { role.Id, role.Role, role.ScopeId });
    }

    [HttpDelete("admins/{id}")]
    public async Task<IActionResult> RemoveOrgListManager(Guid id)
    {
        
        await keto.RemoveManagementRoleAsync(ActorId, id, OrgId);
        return NoContent();
    }

    // ── SMTP ──────────────────────────────────────────────────────────────────

    [HttpGet("smtp")]
    public async Task<IActionResult> GetSmtp()
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == OrgId);
        if (config == null) return Ok(new { configured = false });
        return Ok(new
        {
            configured   = true,
            config.Host,
            config.Port,
            config.StartTls,
            config.Username,
            config.FromAddress,
            config.FromName,
            config.UpdatedAt,
        });
    }

    [HttpPut("smtp")]
    public async Task<IActionResult> UpsertSmtp([FromBody] UpsertSmtpRequest body)
    {
        var orgId  = OrgId;
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == orgId);
        var key    = Convert.FromHexString(appConfig.TotpSecretEncryptionKey);

        if (config == null)
        {
            config = new OrgSmtpConfig
            {
                OrgId       = orgId,
                Host        = body.Host,
                Port        = body.Port,
                StartTls    = body.StartTls,
                Username    = body.Username,
                PasswordEnc = body.Password != null
                    ? TotpEncryption.Encrypt(key, System.Text.Encoding.UTF8.GetBytes(body.Password))
                    : null,
                FromAddress = body.FromAddress,
                FromName    = body.FromName,
                CreatedAt   = DateTimeOffset.UtcNow,
                UpdatedAt   = DateTimeOffset.UtcNow,
            };
            db.OrgSmtpConfigs.Add(config);
        }
        else
        {
            config.Host        = body.Host;
            config.Port        = body.Port;
            config.StartTls    = body.StartTls;
            config.Username    = body.Username;
            if (body.Password != null)
                config.PasswordEnc = TotpEncryption.Encrypt(key, System.Text.Encoding.UTF8.GetBytes(body.Password));
            config.FromAddress = body.FromAddress;
            config.FromName    = body.FromName;
            config.UpdatedAt   = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "smtp_config_saved" });
    }

    [HttpDelete("smtp")]
    public async Task<IActionResult> DeleteSmtp()
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == OrgId);
        if (config == null) return NoContent();
        db.OrgSmtpConfigs.Remove(config);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("smtp/test")]
    public async Task<IActionResult> TestSmtp()
    {
        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == ActorId);
        if (actor == null) return BadRequest(new { error = "user_not_found" });
        try
        {
            await emailService.SendOtpAsync(actor.Email, "123456", "registration", OrgId);
            return Ok(new { message = "test_email_sent", to = actor.Email });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP test failed for org {OrgId}", OrgId);
            return BadRequest(new { error = "smtp_test_failed", detail = ex.Message });
        }
    }

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var orgId = OrgId;
        var logs = await db.AuditLogs
            .Where(l => l.OrgId == orgId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata })
            .ToListAsync();
        return Ok(logs);
    }

    // ── SAML IdP configs ──────────────────────────────────────────────────────

    [HttpGet("projects/{id}/saml-providers")]
    public async Task<IActionResult> ListSamlProviders(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        var providers = await db.SamlIdpConfigs
            .Where(x => x.ProjectId == id)
            .Select(x => new { x.Id, x.EntityId, x.MetadataUrl, x.SsoUrl, x.EmailAttributeName, x.DisplayNameAttributeName, x.JitProvisioning, x.DefaultRoleId, x.Active, x.CreatedAt })
            .ToListAsync();
        return Ok(providers);
    }

    [HttpPost("projects/{id}/saml-providers")]
    public async Task<IActionResult> CreateSamlProvider(Guid id, [FromBody] CreateSamlProviderRequest body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        if (string.IsNullOrEmpty(body.EntityId)) return BadRequest(new { error = "entity_id_required" });
        if (string.IsNullOrEmpty(body.MetadataUrl) && string.IsNullOrEmpty(body.SsoUrl))
            return BadRequest(new { error = "metadata_url_or_sso_url_required" });

        var provider = new SamlIdpConfig
        {
            ProjectId                 = id,
            EntityId                  = body.EntityId,
            MetadataUrl               = body.MetadataUrl,
            SsoUrl                    = body.SsoUrl,
            CertificatePem            = body.CertificatePem,
            EmailAttributeName        = body.EmailAttributeName ?? "email",
            DisplayNameAttributeName  = body.DisplayNameAttributeName,
            JitProvisioning           = body.JitProvisioning ?? true,
            DefaultRoleId             = body.DefaultRoleId,
            Active                    = true,
            CreatedAt                 = DateTimeOffset.UtcNow,
            UpdatedAt                 = DateTimeOffset.UtcNow
        };
        db.SamlIdpConfigs.Add(provider);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, id, ActorId, "saml_provider.created", "saml_provider", provider.Id.ToString());
        return Created($"/org/projects/{id}/saml-providers/{provider.Id}", new { provider.Id, provider.EntityId });
    }

    [HttpPatch("projects/{id}/saml-providers/{pid}")]
    public async Task<IActionResult> UpdateSamlProvider(Guid id, Guid pid, [FromBody] UpdateSamlProviderRequest body)
    {
        var provider = await db.SamlIdpConfigs.FirstOrDefaultAsync(x => x.Id == pid && x.ProjectId == id);
        if (provider == null || provider.Project.OrgId != OrgId) return NotFound();
        if (body.EntityId != null) provider.EntityId = body.EntityId;
        if (body.MetadataUrl != null) provider.MetadataUrl = body.MetadataUrl;
        if (body.SsoUrl != null) provider.SsoUrl = body.SsoUrl;
        if (body.CertificatePem != null) provider.CertificatePem = body.CertificatePem;
        if (body.EmailAttributeName != null) provider.EmailAttributeName = body.EmailAttributeName;
        if (body.DisplayNameAttributeName != null) provider.DisplayNameAttributeName = body.DisplayNameAttributeName;
        if (body.JitProvisioning.HasValue) provider.JitProvisioning = body.JitProvisioning.Value;
        if (body.DefaultRoleId.HasValue) provider.DefaultRoleId = body.DefaultRoleId;
        if (body.Active.HasValue) provider.Active = body.Active.Value;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { provider.Id, provider.EntityId, provider.Active });
    }

    [HttpDelete("projects/{id}/saml-providers/{pid}")]
    public async Task<IActionResult> DeleteSamlProvider(Guid id, Guid pid)
    {
        var provider = await db.SamlIdpConfigs
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == pid && x.ProjectId == id);
        if (provider == null || provider.Project.OrgId != OrgId) return NotFound();
        db.SamlIdpConfigs.Remove(provider);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, id, ActorId, "saml_provider.deleted", "saml_provider", pid.ToString());
        return NoContent();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("userlists/{id}/export")]
    public async Task<IActionResult> ExportUserList(Guid id, [FromQuery] string format = "csv")
    {
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (list == null) return NotFound();

        var rateLimitKey = $"export_rl:{ActorId}:userlist:{id}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(OrgId, null, ActorId, "export.users", "userlist", id.ToString(),
            new Dictionary<string, object> { ["format"] = format });

        var users = db.Users
            .Where(u => u.UserListId == id)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Email, u.Username, u.DisplayName, u.Phone, u.Active, u.EmailVerified, u.TotpEnabled, u.LastLoginAt, u.CreatedAt });

        if (format == "json")
        {
            var data = await users.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=users-{id}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=users-{id}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,email,username,display_name,phone,active,email_verified,totp_enabled,last_login_at,created_at\n");
        await foreach (var u in users.AsAsyncEnumerable())
            await Response.WriteAsync($"{u.Id},{CsvEscape(u.Email)},{CsvEscape(u.Username)},{CsvEscape(u.DisplayName)},{CsvEscape(u.Phone)},{u.Active},{u.EmailVerified},{u.TotpEnabled},{u.LastLoginAt:O},{u.CreatedAt:O}\n");
        return Empty;
    }

    [HttpGet("audit-log/export")]
    public async Task<IActionResult> ExportAuditLog(
        [FromQuery] string format = "csv",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var rateLimitKey = $"export_rl:{ActorId}:auditlog:{OrgId}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(OrgId, null, ActorId, "export.audit_log", AuditOrg, OrgId.ToString(),
            new Dictionary<string, object> { ["format"] = format, ["from"] = from?.ToString("O") ?? "", ["to"] = to?.ToString("O") ?? "" });

        var orgId = OrgId;
        var query = db.AuditLogs
            .Where(l => l.OrgId == orgId)
            .Where(l => from == null || l.CreatedAt >= from)
            .Where(l => to == null || l.CreatedAt <= to)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { l.Id, l.Action, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt });

        if (format == "json")
        {
            var data = await query.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{orgId}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{orgId}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,action,project_id,actor_id,target_type,target_id,ip_address,created_at\n");
        await foreach (var l in query.AsAsyncEnumerable())
            await Response.WriteAsync($"{l.Id},{CsvEscape(l.Action)},{l.ProjectId},{l.ActorId},{CsvEscape(l.TargetType)},{CsvEscape(l.TargetId)},{CsvEscape(l.IpAddress)},{l.CreatedAt:O}\n");
        return Empty;
    }

    private static string CsvEscape(string? value)
    {
        if (value == null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

public record CreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record UpdateProjectRequest(
    string? Name,
    bool? RequireRoleToLogin,
    bool? RequireMfa,
    bool? AllowSelfRegistration,
    bool? EmailVerificationEnabled,
    bool? SmsVerificationEnabled,
    bool? Active,
    string[]? AllowedEmailDomains,
    Guid? DefaultRoleId,
    bool? ClearDefaultRole,
    Dictionary<string, object>? LoginTheme,
    string? EmailFromName,
    bool? ClearEmailFromName,
    string[]? IpAllowlist,
    bool? CheckBreachedPasswords);
public record UpdateScopesRequest(string[] Scopes);
public record CreateSamlProviderRequest(string EntityId, string? MetadataUrl, string? SsoUrl, string? CertificatePem, string? EmailAttributeName, string? DisplayNameAttributeName, bool? JitProvisioning, Guid? DefaultRoleId);
public record UpdateSamlProviderRequest(string? EntityId, string? MetadataUrl, string? SsoUrl, string? CertificatePem, string? EmailAttributeName, string? DisplayNameAttributeName, bool? JitProvisioning, Guid? DefaultRoleId, bool? Active);
public record UpdateOrgSettingsRequest(int? AuditRetentionDays);
public record AssignUserListRequest(Guid UserListId);
public record CreateUserListRequest(string Name);
public record CreateUserRequest(string Email, string? Password, string? Username);
public record UpdateUserRequest(string? Email, string? Username, string? DisplayName, string? Phone, bool? Active, bool? EmailVerified, bool? ClearLock, string? NewPassword);
public record OrgCleanupRequest(bool RemoveOrphanedRoles = true, bool RemoveInactiveUsers = false, int InactiveThresholdDays = 90, bool DryRun = true);
public record OrgAssignManagerRequest(Guid UserId, string Role, Guid? ScopeId);
public record OrgUpdateManagerRequest(string? Role, Guid? ScopeId);
public record UpsertSmtpRequest(string Host, int Port, bool StartTls, string? Username, string? Password, string FromAddress, string FromName);
