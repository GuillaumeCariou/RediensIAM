using System.Text.Json.Serialization;
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
#pragma warning disable S107 // what this controller depends on, listed; the bundle that hid the count only forwarded
public class OrgController(
    RediensIamDbContext db,
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    IEmailService emailService,
    IDistributedCache cache,
    LiveAuthorizationService live,
    AppConfig appConfig,
    ILogger<OrgController> logger) : ControllerBase
#pragma warning restore S107
{
    private static readonly string[] _hydraGrantTypes    = ["authorization_code", "refresh_token"];
    private static readonly string[] _hydraResponseTypes = ["code"];

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
        // -1 means "reset to global default". Anything below the floor is refused rather than
        // clamped: silently keeping more data than an admin asked for would be its own surprise,
        // and 0/negative made the retention sweep delete the org's entire history within 24 h.
        if (body.AuditRetentionDays is { } days && days != -1 && days < AppConfig.MinAuditRetentionDays)
            return BadRequest(new { error = "audit_retention_too_short", minimum = AppConfig.MinAuditRetentionDays });
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
            // The console shows the assigned user list by name and the creation date; without them
            // the User List column read "None" for every project that had one.
            .Select(p => new
            {
                p.Id, p.Name, p.Slug, p.Active, p.AssignedUserListId, p.RequireRoleToLogin, p.CreatedAt,
                AssignedUserListName = p.AssignedUserList != null ? p.AssignedUserList.Name : null,
            })
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
            RequireRoleToLogin = body.RequireRoleToLogin ?? false,
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
                post_logout_redirect_uris = body.PostLogoutRedirectUris ?? [],
                // Hydra's own CORS, carried by the client rather than by the chart — see
                // ClientOriginsService.CorsOriginsFor. Without it the SPA's token call is blocked
                // until someone edits apps/iam/values.yaml and restarts the pod.
                allowed_cors_origins = ClientOriginsService.CorsOriginsFor(body.RedirectUris, body.PostLogoutRedirectUris),
                grant_types = _hydraGrantTypes,
                response_types = _hydraResponseTypes,
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

        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), "org", $"{Roles.KetoOrgsNamespace}:{orgId}");
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
        project.LoginTheme = TotpEncryption.StripSecretsFromTheme(project.LoginTheme) ?? project.LoginTheme;
        return Ok(await ProjectUpdate.WithRedirectUrisAsync(hydra, project));
    }

    [HttpPatch("projects/{id}")]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] ProjectUpdateRequest body)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
        if (project == null) return NotFound();
        if (await ProjectUpdate.ApplyAsync(db, hydra, audit, appConfig, ActorId, project, body) is { } err) return err;
        await ProjectUpdate.SaveAndAuditAsync(db, audit, ActorId, project);
        return Ok(new { project.Id, project.Name });
    }

    // ── Scopes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Les deux routes de scopes étaient les seules de ce contrôleur sans échappatoire super-admin :
    /// <c>UpdateProject</c> et <c>DeleteProject</c> en ont une, celles-ci filtraient sur
    /// <c>OrgId</c> seul. Le jeton d'un super-admin n'en nomme aucune, donc il recevait un 404 —
    /// indiscernable d'un projet inexistant — sur un projet parfaitement réel.
    /// </summary>
    private async Task<Project?> FindProjectForScopesAsync(Guid id)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        return await db.Projects.FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
    }

    [HttpGet("projects/{id}/scopes")]
    public async Task<IActionResult> GetProjectScopes(Guid id)
    {
        var project = await FindProjectForScopesAsync(id);
        if (project == null) return NotFound();
        return ProjectOperations.ReadScopes(project);
    }

    [HttpPut("projects/{id}/scopes")]
    public async Task<IActionResult> UpdateProjectScopes(Guid id, [FromBody] UpdateScopesRequest body)
    {
        var project = await FindProjectForScopesAsync(id);
        if (project == null) return NotFound();

        return await ProjectOperations.UpdateScopesAsync(db, hydra, audit, logger, ActorId, project, body.Scopes);
    }

    [HttpDelete("projects/{id}")]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        var isSuperAdmin = Claims.Roles.Contains(Roles.SuperAdmin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && (isSuperAdmin || p.OrgId == OrgId));
        if (project == null) return NotFound();
        return await ProjectOperations.DeleteAsync(db, hydra, keto, audit, logger, ActorId, project);
    }

    [HttpPut("projects/{id}/userlist")]
    public async Task<IActionResult> AssignUserList(Guid id, [FromBody] AssignUserListRequest body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return await ProjectOperations.AssignUserListAsync(db, audit, ActorId, project, body.UserListId);
    }

    [HttpDelete("projects/{id}/userlist")]
    public async Task<IActionResult> UnassignUserList(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return await ProjectOperations.UnassignUserListAsync(db, audit, ActorId, project);
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
        return await UserListOperations.CreateAsync(db, audit, ActorId, body.Name, OrgId, "/org/userlists");
    }

    [HttpGet("userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
        var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null || ul.OrgId != OrgId) return NotFound();
        return await UserListOperations.ReadAsync(db, ul);
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
        // La suppression cascade sur les comptes de la liste : c'est la seule opération de cette
        // surface qui détruisait des utilisateurs sans laisser d'entrée au journal.
        await audit.RecordAsync(ul.OrgId, null, ActorId, "userlist.deleted", "userlist", id.ToString(),
            new() { ["name"] = ul.Name });
        return NoContent();
    }

    [HttpPost("userlists/{id}/cleanup")]
    public async Task<IActionResult> CleanupUserList(Guid id, [FromBody] OrgCleanupRequest body)
    {
        var orgId = OrgId;
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == orgId)) return NotFound();

        var projectIds   = await db.Projects.Where(p => p.AssignedUserListId == id).Select(p => p.Id).ToListAsync();
        var allUserIds   = await db.Users.Where(u => u.UserListId == id).Select(u => u.Id).ToHashSetAsync();
        var orphanedRoles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => projectIds.Contains(r.ProjectId) && !allUserIds.Contains(r.UserId)).ToListAsync();

        var inactiveUsers = await FindInactiveUsersAsync(id, body);

        var dryRun        = body.DryRun ?? true;
        var removeOrphaned = body.RemoveOrphanedRoles ?? true;
        var removeInactive = body.RemoveInactiveUsers ?? false;

        if (!dryRun)
            await ApplyCleanupChangesAsync(id, orphanedRoles, inactiveUsers, removeOrphaned, removeInactive);

        return Ok(new
        {
            dry_run                = dryRun,
            orphaned_roles_found   = orphanedRoles.Count,
            orphaned_roles_removed = dryRun || !removeOrphaned ? 0 : orphanedRoles.Count,
            inactive_users_found   = inactiveUsers.Count,
            inactive_users_removed = dryRun || !removeInactive ? 0 : inactiveUsers.Count,
        });
    }

    private async Task<List<User>> FindInactiveUsersAsync(Guid listId, OrgCleanupRequest body)
    {
        if (!(body.RemoveInactiveUsers ?? false)) return [];
        var cutoff = DateTimeOffset.UtcNow.AddDays(-(body.InactiveThresholdDays ?? 90));
        return await db.Users
            .Where(u => u.UserListId == listId && (u.LastLoginAt == null || u.LastLoginAt < cutoff))
            .ToListAsync();
    }

    private async Task ApplyCleanupChangesAsync(
        Guid listId, List<UserProjectRole> orphanedRoles, List<User> inactiveUsers,
        bool removeOrphaned, bool removeInactive)
    {
        if (removeOrphaned)
        {
            db.UserProjectRoles.RemoveRange(orphanedRoles);
            foreach (var r in orphanedRoles)
                await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, r.ProjectId.ToString(), $"role:{r.Role.Name}", $"user:{r.UserId}");
        }
        if (removeInactive)
        {
            foreach (var u in inactiveUsers)
                await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, listId.ToString(), "member", $"user:{u.Id}");
            db.Users.RemoveRange(inactiveUsers);
        }
        await db.SaveChangesAsync();
    }

    [HttpGet("userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == OrgId)) return NotFound();
        return await UserListOperations.ListUsersAsync(db, id);
    }

    [HttpPost("userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] UserListOperations.NewUser body)
    {
        var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null || ul.OrgId != OrgId) return NotFound();
        // `OrgId` de l'appelant, JAMAIS `body.OrgId` : un administrateur d'organisation qui
        // pourrait nommer une autre organisation créerait des comptes dont les jetons
        // revendiquent un locataire qui n'est pas le sien.
        return await UserListOperations.AddUserAsync(
            new UserListDeps(db, keto, audit, passwords, emailService, appConfig), ActorId, ul, body,
            "/org/userlists", OrgId);
    }

    [HttpPost("userlists/{id}/users/{uid}/resend-invite")]
    public async Task<IActionResult> ResendInvite(Guid id, Guid uid)
    {
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        if (user.Active) return BadRequest(new { error = "user_already_active" });

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
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(appConfig.InviteExpiryHours),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var inviteUrl = appConfig.InviteUrl(raw);
        var orgName   = ul.Organisation?.Name ?? "the organization";
        await emailService.SendInviteAsync(user.Email, inviteUrl, orgName, OrgId);
        await audit.RecordAsync(OrgId, null, ActorId, "user.invite_resent", "user", uid.ToString());
        return Ok(new { message = "invite_resent" });
    }

    [HttpPatch("userlists/{id}/users/{uid}")]
    public async Task<IActionResult> UpdateUser(Guid id, Guid uid, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        return await UserHelpers.ApplyAdminUpdateAsync(db, hydra, audit, passwords, ActorId, user, body);
    }

    /// <summary>
    /// La page Users du locataire : la même recherche que <c>GET /admin/users</c>, confinée à
    /// l'organisation de l'appelant.
    ///
    /// <para>
    /// Elle passe par <see cref="UserSearch"/>, comme la surface système. La portée est la seule
    /// différence, et c'est le contrôle : le locataire vient du JETON. <c>org_id</c> n'est pas lié
    /// ici, donc rien ne peut l'honorer — un administrateur d'organisation qui pourrait en nommer
    /// une autre lirait des comptes qui ne sont pas les siens. Même faute que <c>createUserList</c>
    /// dans l'autre sens.
    /// </para>
    ///
    /// <para>
    /// <c>tenants</c> reste dans l'enveloppe et reste calculé : confiné à un locataire il vaut 0 ou
    /// 1, ce qui est vrai des deux côtés, et une valeur en dur serait une seconde chose à tenir en
    /// accord avec la première.
    /// </para>
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> SearchOrgUsers(
        [FromQuery] string? q,
        [FromQuery] Guid? user_list_id,
        [FromQuery] string? status,
        [FromQuery] string? mfa,
        [FromQuery] string? signed_in,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => await UserSearch.RunAsync(db,
            new UserSearch.Criteria(q, OrgId, user_list_id, status, mfa, signed_in, page, pageSize));

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
        return await UserHelpers.ApplyAdminUpdateAsync(db, hydra, audit, passwords, ActorId, user, body);
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
        var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null || ul.OrgId != OrgId) return NotFound();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        return await UserListOperations.RemoveUserAsync(db, keto, audit, ActorId, ul, user);
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
        await live.InvalidateAsync(body.UserId);
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

        // This endpoint used to bypass KetoService.AssignManagementRoleAsync entirely, so the
        // management-level checks it performs (can the actor grant this rank?) were skipped and
        // any string could be written as a Keto relation.
        if (body.Role != null && !SystemAdminController.KnownManagementRoles.Contains(body.Role))
            return BadRequest(new { error = "unknown_role", allowed = SystemAdminController.KnownManagementRoles });

        var actorLevel = await keto.GetActorManagementLevelForOrgAsync(ActorId, orgId);
        var targetLevel = ManagementLevelForRole(body.Role ?? role.Role);
        if (actorLevel == ManagementLevel.None || targetLevel < actorLevel)
            return StatusCode(403, new { error = "insufficient_management_level" });

        if (await ValidateScopeIsInOrgAsync(body.ScopeId, role.ScopeId, orgId) is { } scopeErr)
            return scopeErr;

        var oldSubject = KetoSubject(role.UserId, role.ScopeId);
        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, oldSubject);

        if (body.Role != null) role.Role = body.Role;
        if (body.ScopeId != null) role.ScopeId = body.ScopeId;

        // Seul project_admin porte une portée : org_admin et super_admin valent sur toute
        // l'organisation. `ScopeId` n'étant qu'affectable, jamais effaçable, promouvoir un
        // project_admin en org_admin gardait son ancienne portée — et le tuple réécrit plus bas
        // partait sur le sujet `user:…|project:…` sous la relation `org_admin`. Keto répondait donc
        // « oui » à un org_admin dont le grant nomme un projet, et « non » au sujet non scopé que
        // toute vérification d'organisation interroge.
        if (role.Role != Roles.ProjectAdmin) role.ScopeId = null;

        await db.SaveChangesAsync();

        var newSubject = KetoSubject(role.UserId, role.ScopeId);
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, newSubject);
        await live.InvalidateAsync(role.UserId);
        await audit.RecordAsync(orgId, null, ActorId, "role.management.assigned", "user", role.UserId.ToString(),
            new() { ["role"] = role.Role });

        return Ok(new { role.Id, role.Role, role.ScopeId });
    }

    private static ManagementLevel ManagementLevelForRole(string roleName) => roleName switch
    {
        Roles.SuperAdmin   => ManagementLevel.SuperAdmin,
        Roles.OrgAdmin     => ManagementLevel.OrgAdmin,
        Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
        _                  => ManagementLevel.None,
    };

    /// <summary>
    /// Rejects a scope change that points at a project outside this org. Unchanged (or absent)
    /// scopes are not re-checked, exactly as before — the tuple they produce is the one already
    /// stored.
    /// </summary>
    private async Task<IActionResult?> ValidateScopeIsInOrgAsync(Guid? newScopeId, Guid? currentScopeId, Guid orgId)
    {
        if (newScopeId != null && newScopeId != currentScopeId)
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == newScopeId && p.OrgId == orgId);
            if (!projectExists) return BadRequest(new { error = "project_not_in_org" });
        }
        return null;
    }

    /// <summary>The Keto subject string for a management grant — scoped to a project when the grant is.</summary>
    private static string KetoSubject(Guid userId, Guid? scopeId)
        => scopeId.HasValue ? $"user:{userId}|project:{scopeId}" : $"user:{userId}";

    [HttpDelete("admins/{id}")]
    public async Task<IActionResult> RemoveOrgListManager(Guid id)
    {
        
        var removed = await db.OrgRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OrgId == OrgId);
        await keto.RemoveManagementRoleAsync(ActorId, id, OrgId);
        if (removed != null) await live.InvalidateAsync(removed.UserId);
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
        if (await SmtpEndpointValidator.ValidateAsync(body.Host, body.Port, body.StartTls) is { } smtpErr)
            return BadRequest(new { error = smtpErr });

        var orgId  = OrgId;
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == orgId);
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
                    ? TotpEncryption.Encrypt(appConfig.SmtpEncKey, System.Text.Encoding.UTF8.GetBytes(body.Password))
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
                config.PasswordEnc = TotpEncryption.Encrypt(appConfig.SmtpEncKey, System.Text.Encoding.UTF8.GetBytes(body.Password));
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
            await emailService.SendOtpAsync(actor.Email, "TEST-MESSAGE", "smtp_test", OrgId);
            return Ok(new { message = "test_email_sent", to = actor.Email });
        }
        catch (Exception ex)
        {
            // No exception text on the wire. The message distinguishes "connection refused" from
            // "no route" from an SMTP banner, which turns this endpoint into a probe oracle for
            // whatever the pod can reach. It stays in the log, where only an operator sees it.
            logger.LogWarning(ex, "SMTP test failed for org {OrgId}", OrgId);
            return BadRequest(new { error = "smtp_test_failed" });
        }
    }

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var orgId = OrgId;
        return await AuditLogQuery.PageAsync(db, l => l.OrgId == orgId, limit, offset);
    }

    // ── SAML IdP configs ──────────────────────────────────────────────────────

    [HttpGet("projects/{id}/saml-providers")]
    public async Task<IActionResult> ListSamlProviders(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return await ProjectOperations.ListSamlProvidersAsync(db, id);
    }

    [HttpPost("projects/{id}/saml-providers")]
    public async Task<IActionResult> CreateSamlProvider(Guid id, [FromBody] SamlProviderInput body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return await SamlProviderOperations.CreateAsync(db, audit, ActorId, project, body);
    }

    [HttpPatch("projects/{id}/saml-providers/{pid}")]
    public async Task<IActionResult> UpdateSamlProvider(Guid id, Guid pid, [FromBody] SamlProviderInput body)
    {
        var provider = await SamlProviderOperations.FindAsync(db, id, pid);
        if (provider == null || provider.Project.OrgId != OrgId) return NotFound();
        return await SamlProviderOperations.UpdateAsync(db, audit, ActorId, provider, body);
    }

    [HttpDelete("projects/{id}/saml-providers/{pid}")]
    public async Task<IActionResult> DeleteSamlProvider(Guid id, Guid pid)
    {
        var provider = await SamlProviderOperations.FindAsync(db, id, pid);
        if (provider == null || provider.Project.OrgId != OrgId) return NotFound();
        return await SamlProviderOperations.DeleteAsync(db, audit, ActorId, provider);
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

    private static string CsvEscape(string? value) => CsvWriter.Escape(value);
}

    // post_logout_redirect_uris is not optional decoration: Hydra rejects any logout whose
    // target the client has not whitelisted, and the browser SDK this repo ships always sends
    // one. A project registered without it can be signed into and not out of.
public record CreateProjectRequest(string Name, string Slug, bool? RequireRoleToLogin, string[]? RedirectUris,
    string[]? PostLogoutRedirectUris = null);
public record UpdateScopesRequest(string[] Scopes);
public record UpdateOrgSettingsRequest(int? AuditRetentionDays);
public record AssignUserListRequest([property: JsonRequired] Guid UserListId);
public record CreateUserListRequest(string Name);
public record CreateUserRequest(string Email, string? Password, string? Username);
public record UpdateUserRequest(string? Email, string? Username, string? DisplayName, string? Phone, bool? Active, bool? EmailVerified, bool? ClearLock, string? NewPassword);
public record OrgCleanupRequest(bool? RemoveOrphanedRoles, bool? RemoveInactiveUsers, int? InactiveThresholdDays, bool? DryRun);
public record OrgAssignManagerRequest([property: JsonRequired] Guid UserId, string Role, Guid? ScopeId);
public record OrgUpdateManagerRequest(string? Role, Guid? ScopeId);
public record UpsertSmtpRequest(string Host, [property: JsonRequired] int Port, [property: JsonRequired] bool StartTls, string? Username, string? Password, string FromAddress, string FromName);
