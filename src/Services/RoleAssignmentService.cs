using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public enum ManagementLevel { SuperAdmin = 1, OrgAdmin = 2, ProjectManager = 3, None = 99 }

public class RoleAssignmentService(RediensIamDbContext db, KetoService keto, AuditLogService audit)
{
    public async Task<ManagementLevel> GetActorManagementLevelForProjectAsync(Guid actorId, Guid projectId, Guid orgId)
    {
        if (await keto.CheckAsync("System", "rediensiam", "super_admin", $"user:{actorId}"))
            return ManagementLevel.SuperAdmin;
        if (await keto.CheckAsync("Organisations", orgId.ToString(), "org_admin", $"user:{actorId}"))
            return ManagementLevel.OrgAdmin;
        if (await keto.CheckAsync("Projects", projectId.ToString(), "manager", $"user:{actorId}"))
            return ManagementLevel.ProjectManager;
        return ManagementLevel.None;
    }

    public async Task<ManagementLevel> GetActorManagementLevelForOrgAsync(Guid actorId, Guid orgId)
    {
        if (await keto.CheckAsync("System", "rediensiam", "super_admin", $"user:{actorId}"))
            return ManagementLevel.SuperAdmin;
        if (await keto.CheckAsync("Organisations", orgId.ToString(), "org_admin", $"user:{actorId}"))
            return ManagementLevel.OrgAdmin;
        var pmRole = await db.OrgRoles.AnyAsync(r => r.OrgId == orgId && r.UserId == actorId && r.Role == "project_manager");
        if (pmRole) return ManagementLevel.ProjectManager;
        return ManagementLevel.None;
    }

    public async Task AssignProjectRoleAsync(Guid actorId, Guid targetUserId, Guid projectId, Guid roleId)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new NotFoundException("Project not found");

        var targetRole = await db.Roles.FindAsync(roleId)
            ?? throw new NotFoundException("Role not found");

        if (targetRole.ProjectId != projectId)
            throw new BadRequestException("Role does not belong to this project");

        var level = await GetActorManagementLevelForProjectAsync(actorId, projectId, project.OrgId);
        if (level == ManagementLevel.None)
            throw new ForbiddenException("No management rights over this project");

        if (level == ManagementLevel.ProjectManager)
        {
            var actorRoles = await db.UserProjectRoles.Include(r => r.Role)
                .Where(r => r.UserId == actorId && r.ProjectId == projectId).ToListAsync();
            if (actorRoles.Count > 0)
            {
                var actorMinRank = actorRoles.Min(r => r.Role.Rank);
                if (targetRole.Rank < actorMinRank)
                    throw new ForbiddenException($"Cannot assign role '{targetRole.Name}' (rank {targetRole.Rank}): your lowest rank is {actorMinRank}");
            }
        }

        var userInList = project.AssignedUserListId.HasValue
            && await db.Users.AnyAsync(u => u.Id == targetUserId && u.UserListId == project.AssignedUserListId);
        if (!userInList)
            throw new BadRequestException("User is not in this project's assigned UserList");

        var existing = await db.UserProjectRoles.FirstOrDefaultAsync(r =>
            r.UserId == targetUserId && r.ProjectId == projectId && r.RoleId == roleId);
        if (existing != null) return;

        db.UserProjectRoles.Add(new UserProjectRole
        {
            UserId = targetUserId, ProjectId = projectId, RoleId = roleId,
            GrantedBy = actorId, GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync("Projects", projectId.ToString(), $"role:{targetRole.Name}", $"user:{targetUserId}");
        await audit.RecordAsync(project.OrgId, projectId, actorId, "role.assigned",
            "user", targetUserId.ToString(), new() { ["role_name"] = targetRole.Name });
    }

    public async Task RemoveProjectRoleAsync(Guid actorId, Guid targetUserId, Guid projectId, Guid roleId)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new NotFoundException("Project not found");
        var level = await GetActorManagementLevelForProjectAsync(actorId, projectId, project.OrgId);
        if (level == ManagementLevel.None) throw new ForbiddenException("No management rights");

        var assignment = await db.UserProjectRoles.Include(r => r.Role)
            .FirstOrDefaultAsync(r => r.UserId == targetUserId && r.ProjectId == projectId && r.RoleId == roleId)
            ?? throw new NotFoundException("Role assignment not found");

        db.UserProjectRoles.Remove(assignment);
        await db.SaveChangesAsync();
        await keto.DeleteRelationTupleAsync("Projects", projectId.ToString(), $"role:{assignment.Role.Name}", $"user:{targetUserId}");
        await audit.RecordAsync(project.OrgId, projectId, actorId, "role.removed", "user", targetUserId.ToString());
    }

    public async Task AssignManagementRoleAsync(Guid actorId, Guid targetUserId, Guid orgId,
        string role, Guid? scopeId = null, string? displayName = null)
    {
        var actorLevel = await GetActorManagementLevelForOrgAsync(actorId, orgId);
        if (actorLevel == ManagementLevel.None)
            throw new ForbiddenException("No management rights over this organisation");

        var targetRank = role switch
        {
            "super_admin" => ManagementLevel.SuperAdmin,
            "org_admin" => ManagementLevel.OrgAdmin,
            "project_manager" => ManagementLevel.ProjectManager,
            _ => throw new BadRequestException($"Unknown management role: {role}")
        };

        if (targetRank < actorLevel)
            throw new ForbiddenException($"Cannot assign '{role}': insufficient management level");

        if (actorLevel == ManagementLevel.ProjectManager)
        {
            if (role != "project_manager" || scopeId == null)
                throw new ForbiddenException("project_manager can only assign project_manager roles");
            var actorScope = await db.OrgRoles.FirstOrDefaultAsync(r =>
                r.OrgId == orgId && r.UserId == actorId && r.Role == "project_manager");
            if (actorScope?.ScopeId != scopeId)
                throw new ForbiddenException("Cannot assign project_manager for a project outside your scope");
        }

        var existing = await db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == orgId && r.UserId == targetUserId && r.Role == role && r.ScopeId == scopeId);

        if (existing != null)
        {
            if (displayName != null) existing.DisplayName = displayName;
            await db.SaveChangesAsync();
            return;
        }

        db.OrgRoles.Add(new OrgRole
        {
            OrgId = orgId, UserId = targetUserId, Role = role,
            ScopeId = scopeId, DisplayName = displayName,
            GrantedBy = actorId, GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var ketoSubject = scopeId.HasValue ? $"user:{targetUserId}|project:{scopeId}" : $"user:{targetUserId}";
        await keto.WriteRelationTupleAsync("Organisations", orgId.ToString(), role, ketoSubject);
        await audit.RecordAsync(orgId, null, actorId, "role.management.assigned",
            "user", targetUserId.ToString(), new() { ["role"] = role });
    }
}
