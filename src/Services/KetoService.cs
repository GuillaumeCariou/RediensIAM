using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

public class KetoService(IHttpClientFactory http, AppConfig appConfig, RediensIamDbContext db, AuditLogService audit)
{
    private readonly string _readUrl = appConfig.KetoReadUrl;
    private readonly string _writeUrl = appConfig.KetoWriteUrl;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private HttpClient ReadClient  => http.CreateClient("keto-read");
    private HttpClient WriteClient => http.CreateClient("keto-write");

    // ── Relation tuples ───────────────────────────────────────────────────────

    public async Task<bool> CheckAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var url = $"{_readUrl}/relation-tuples/check?namespace={Uri.EscapeDataString(namespaceName)}&object={Uri.EscapeDataString(objectId)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}";
        var resp = await ReadClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return false;
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("allowed").GetBoolean();
    }

    public async Task WriteRelationTupleAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var body = new[]
        {
            new { action = "insert", relation_tuple = new { @namespace = namespaceName, @object = objectId, relation, subject_id = subjectId } }
        };
        var resp = await WriteClient.PatchAsJsonAsync($"{_writeUrl}/admin/relation-tuples", body);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteRelationTupleAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&object={Uri.EscapeDataString(objectId)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}";
        await WriteClient.DeleteAsync(url);
    }

    public async Task DeleteAllProjectTuplesAsync(string projectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString("Projects")}&object={Uri.EscapeDataString(projectId)}";
        await WriteClient.DeleteAsync(url);
    }

    public async Task<bool> HasAnyRelationAsync(string namespaceName, string relation, string subjectId)
    {
        var url = $"{_readUrl}/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}&page_size=1";
        var resp = await ReadClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return false;
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.TryGetProperty("relation_tuples", out var tuples) && tuples.GetArrayLength() > 0;
    }

    // ── Role level resolution ─────────────────────────────────────────────────

    public async Task<ManagementLevel> GetActorManagementLevelForProjectAsync(Guid actorId, Guid projectId, Guid orgId)
    {
        if (await CheckAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{actorId}"))
            return ManagementLevel.SuperAdmin;
        if (await CheckAsync(Roles.KetoOrgsNamespace, orgId.ToString(), Roles.KetoOrgAdminRelation, $"user:{actorId}"))
            return ManagementLevel.OrgAdmin;
        if (await CheckAsync(Roles.KetoProjectsNamespace, projectId.ToString(), Roles.KetoManagerRelation, $"user:{actorId}"))
            return ManagementLevel.ProjectAdmin;
        return ManagementLevel.None;
    }

    public async Task<ManagementLevel> GetActorManagementLevelForOrgAsync(Guid actorId, Guid orgId)
    {
        if (await CheckAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{actorId}"))
            return ManagementLevel.SuperAdmin;
        if (await CheckAsync(Roles.KetoOrgsNamespace, orgId.ToString(), Roles.KetoOrgAdminRelation, $"user:{actorId}"))
            return ManagementLevel.OrgAdmin;
        var pmRole = await db.OrgRoles.AnyAsync(r => r.OrgId == orgId && r.UserId == actorId && r.Role == Roles.ProjectAdmin);
        if (pmRole) return ManagementLevel.ProjectAdmin;
        return ManagementLevel.None;
    }

    // ── Role assignment ───────────────────────────────────────────────────────

    public async Task AssignProjectRoleAsync(Guid actorId, Guid targetUserId, Guid projectId, Guid roleId)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new NotFoundException("Project not found");

        var targetRole = await db.Roles.FindAsync(roleId)
            ?? throw new NotFoundException("Role not found");

        if (targetRole.ProjectId != projectId)
            throw new BadRequestException("Role does not belong to this project");

        var level = await GetActorManagementLevelForProjectAsync(actorId, projectId, project.OrgId);
        if (level == ManagementLevel.None)
            throw new ForbiddenException("No management rights over this project");

        if (level == ManagementLevel.ProjectAdmin)
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

        await WriteRelationTupleAsync(Roles.KetoProjectsNamespace, projectId.ToString(), $"role:{targetRole.Name}", $"user:{targetUserId}");
        try
        {
            db.UserProjectRoles.Add(new UserProjectRole
            {
                UserId = targetUserId, ProjectId = projectId, RoleId = roleId,
                GrantedBy = actorId, GrantedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            await DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, projectId.ToString(), $"role:{targetRole.Name}", $"user:{targetUserId}");
            throw;
        }
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

        await DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, projectId.ToString(), $"role:{assignment.Role.Name}", $"user:{targetUserId}");
        db.UserProjectRoles.Remove(assignment);
        await db.SaveChangesAsync();
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
            Roles.SuperAdmin     => ManagementLevel.SuperAdmin,
            Roles.OrgAdmin       => ManagementLevel.OrgAdmin,
            Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
            _ => throw new BadRequestException($"Unknown management role: {role}")
        };

        if (targetRank < actorLevel)
            throw new ForbiddenException($"Cannot assign '{role}': insufficient management level");

        if (actorLevel == ManagementLevel.ProjectAdmin)
        {
            if (role != Roles.ProjectAdmin || scopeId == null)
                throw new ForbiddenException("project_manager can only assign project_manager roles");
            var actorScope = await db.OrgRoles.FirstOrDefaultAsync(r =>
                r.OrgId == orgId && r.UserId == actorId && r.Role == Roles.ProjectAdmin);
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

        var ketoSubject = scopeId.HasValue ? $"user:{targetUserId}|project:{scopeId}" : $"user:{targetUserId}";
        await WriteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role, ketoSubject);
        try
        {
            db.OrgRoles.Add(new OrgRole
            {
                OrgId = orgId, UserId = targetUserId, Role = role,
                ScopeId = scopeId, DisplayName = displayName,
                GrantedBy = actorId, GrantedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            await DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role, ketoSubject);
            throw;
        }
        await audit.RecordAsync(orgId, null, actorId, "role.management.assigned",
            "user", targetUserId.ToString(), new() { ["role"] = role });
    }

    public async Task RemoveManagementRoleAsync(Guid actorId, Guid orgRoleId, Guid orgId)
    {
        var actorLevel = await GetActorManagementLevelForOrgAsync(actorId, orgId);
        if (actorLevel == ManagementLevel.None)
            throw new ForbiddenException("No management rights over this organisation");

        if (actorId == (await db.OrgRoles.FindAsync(orgRoleId))?.UserId)
            throw new ForbiddenException("Cannot remove your own management role");

        var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == orgRoleId && r.OrgId == orgId)
            ?? throw new NotFoundException("Role assignment not found");

        var targetRank = role.Role switch
        {
            Roles.SuperAdmin     => ManagementLevel.SuperAdmin,
            Roles.OrgAdmin       => ManagementLevel.OrgAdmin,
            Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
            _ => ManagementLevel.None
        };
        if (targetRank < actorLevel)
            throw new ForbiddenException($"Cannot remove '{role.Role}': insufficient management level");

        var ketoSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, ketoSubject);
        db.OrgRoles.Remove(role);
        await db.SaveChangesAsync();
        await audit.RecordAsync(orgId, null, actorId, "role.management.removed",
            "user", role.UserId.ToString(), new() { ["role"] = role.Role });
    }

    public async Task AssignDefaultRoleAsync(Project project, User user)
    {
        if (project.DefaultRoleId == null) return;
        var role = await db.Roles.FindAsync(project.DefaultRoleId.Value);
        if (role == null || role.ProjectId != project.Id) return;
        var already = await db.UserProjectRoles.AnyAsync(r =>
            r.UserId == user.Id && r.ProjectId == project.Id && r.RoleId == role.Id);
        if (already) return;
        await WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), $"role:{role.Name}", $"user:{user.Id}");
        try
        {
            db.UserProjectRoles.Add(new UserProjectRole
            {
                UserId = user.Id, ProjectId = project.Id, RoleId = role.Id,
                GrantedBy = user.Id, GrantedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            await DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), $"role:{role.Name}", $"user:{user.Id}");
            throw;
        }
    }
}
