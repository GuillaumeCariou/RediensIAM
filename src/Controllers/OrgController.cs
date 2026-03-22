using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Exceptions;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class OrgController(
    RediensIamDbContext db,
    HydraAdminService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    RoleAssignmentService roleService) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid OrgId => Guid.Parse(Claims.OrgId);

    private async Task RequireOrgAdminAsync()
    {
        var level = await roleService.GetActorManagementLevelForOrgAsync(ActorId, OrgId);
        if (level > Services.ManagementLevel.OrgAdmin) throw new ForbiddenException("org_admin required");
    }
    private Guid ActorId
    {
        get
        {
            var id = Claims.UserId.Contains(':') ? Claims.UserId.Split(':')[1] : Claims.UserId;
            return Guid.Parse(id);
        }
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    [HttpGet("/org/projects")]
    public async Task<IActionResult> ListProjects([FromQuery] Guid? org_id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        Guid orgId;
        if (Guid.TryParse(claims.OrgId, out var claimsOrgId))
            orgId = claimsOrgId;
        else if (org_id.HasValue && claims.Roles.Contains("super_admin"))
            orgId = org_id.Value;
        else
            return Forbid();
        var projects = await db.Projects
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active, p.AssignedUserListId, p.RequireRoleToLogin })
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("/org/projects")]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var orgId = Guid.Parse(claims.OrgId);
        var actorId = Guid.Parse(claims.UserId.Contains(':') ? claims.UserId.Split(':')[1] : claims.UserId);

        var project = new Project
        {
            OrgId = orgId, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin,
            Active = true, CreatedBy = actorId,
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
        catch { /* log but don't fail — can retry */ }

        await keto.WriteRelationTupleAsync("Projects", project.Id.ToString(), "org", $"Organisations:{orgId}");
        await db.SaveChangesAsync();
        await audit.RecordAsync(orgId, project.Id, actorId, "project.created", "project", project.Id.ToString());
        return Created($"/org/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpGet("/org/projects/{id}")]
    public async Task<IActionResult> GetProject(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == Guid.Parse(claims.OrgId));
        if (project == null) return NotFound();
        return Ok(project);
    }

    [HttpPatch("/org/projects/{id}")]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] UpdateProjectRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == Guid.Parse(claims.OrgId));
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue) project.RequireRoleToLogin = body.RequireRoleToLogin.Value;
        if (body.LoginTheme != null) project.LoginTheme = body.LoginTheme;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    [HttpDelete("/org/projects/{id}")]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = Guid.Parse(claims.UserId.Contains(':') ? claims.UserId.Split(':')[1] : claims.UserId);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == Guid.Parse(claims.OrgId));
        if (project == null) return NotFound();

        if (project.HydraClientId != null)
        {
            try { await hydra.DeleteOAuth2ClientAsync(project.HydraClientId); } catch { }
        }
        await keto.DeleteAllProjectTuplesAsync(id.ToString());
        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, id, actorId, "project.deleted", "project", id.ToString());
        return NoContent();
    }

    [HttpPost("/org/projects/{id}/assign-userlist")]
    public async Task<IActionResult> AssignUserList(Guid id, [FromBody] AssignUserListRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var orgId = Guid.Parse(claims.OrgId);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == orgId);
        if (project == null) return NotFound();
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == orgId);
        if (list == null) return BadRequest(new { error = "userlist_not_in_org" });
        project.AssignedUserListId = body.UserListId;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.AssignedUserListId });
    }

    [HttpDelete("/org/projects/{id}/assign-userlist")]
    public async Task<IActionResult> UnassignUserList(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == Guid.Parse(claims.OrgId));
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("/org/userlists")]
    public async Task<IActionResult> ListUserLists()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var lists = await db.UserLists
            .Where(ul => ul.OrgId == Guid.Parse(claims.OrgId) && !ul.Immovable)
            .Select(ul => new { ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt })
            .ToListAsync();
        return Ok(lists);
    }

    [HttpPost("/org/userlists")]
    public async Task<IActionResult> CreateUserList([FromBody] CreateUserListRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var ul = new UserList
        {
            Name = body.Name, OrgId = Guid.Parse(claims.OrgId), Immovable = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/org/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    [HttpGet("/org/userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var ul = await db.UserLists.Include(ul => ul.Users)
            .FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == Guid.Parse(claims.OrgId));
        if (ul == null) return NotFound();
        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id)
            .Select(p => new { p.Id, p.Name }).ToListAsync();
        return Ok(new { ul.Id, ul.Name, ul.Immovable, user_count = ul.Users.Count, assigned_projects = assignedProjects });
    }

    [HttpDelete("/org/userlists/{id}")]
    public async Task<IActionResult> DeleteUserList(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == Guid.Parse(claims.OrgId));
        if (ul == null) return NotFound();
        if (ul.Immovable) return BadRequest(new { error = "cannot_delete_immovable" });
        var isAssigned = await db.Projects.AnyAsync(p => p.AssignedUserListId == id);
        if (isAssigned) return BadRequest(new { error = "userlist_is_assigned_to_project" });
        db.UserLists.Remove(ul);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("/org/userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var ul = await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == Guid.Parse(claims.OrgId));
        if (!ul) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("/org/userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] CreateUserRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == Guid.Parse(claims.OrgId));
        if (ul == null) return NotFound();

        var discriminator = Random.Shared.Next(1000, 9999).ToString();
        var user = new User
        {
            UserListId = id, Username = body.Username ?? body.Email.Split('@')[0],
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = passwords.Hash(body.Password),
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync("UserLists", id.ToString(), "member", $"user:{user.Id}");
        return Created($"/org/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email
        });
    }

    [HttpPatch("/org/userlists/{id}/users/{uid}")]
    public async Task<IActionResult> UpdateUser(Guid id, Guid uid, [FromBody] UpdateUserRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id
            && u.UserList.OrgId == Guid.Parse(claims.OrgId));
        if (user == null) return NotFound();
        if (body.DisplayName != null) user.DisplayName = body.DisplayName;
        if (body.Active.HasValue) { user.Active = body.Active.Value; if (!body.Active.Value) user.DisabledAt = DateTimeOffset.UtcNow; }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { user.Id, user.Active, user.DisplayName });
    }

    [HttpDelete("/org/userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUser(Guid id, Guid uid)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = Guid.Parse(claims.UserId.Contains(':') ? claims.UserId.Split(':')[1] : claims.UserId);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id
            && u.UserList.OrgId == Guid.Parse(claims.OrgId));
        if (user == null) return NotFound();

        await keto.DeleteRelationTupleAsync("UserLists", id.ToString(), "member", $"user:{uid}");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        await audit.RecordAsync(Guid.Parse(claims.OrgId), null, actorId, "user.removed", "user", uid.ToString());
        return NoContent();
    }

    [HttpGet("/org/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var orgId = Guid.Parse(claims.OrgId);
        var logs = await db.AuditLogs
            .Where(l => l.OrgId == orgId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new {
                l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId,
                l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt
            })
            .ToListAsync();
        return Ok(logs);
    }
}

public record CreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record UpdateProjectRequest(string? Name, bool? RequireRoleToLogin, Dictionary<string, object>? LoginTheme);
public record AssignUserListRequest(Guid UserListId);
public record CreateUserListRequest(string Name);
public record CreateUserRequest(string Email, string Password, string? Username);
public record UpdateUserRequest(string? DisplayName, bool? Active);
