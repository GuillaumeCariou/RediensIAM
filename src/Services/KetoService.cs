using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Exceptions;

namespace RediensIAM.Services;

/// <summary>One Ory Keto relation tuple, in the four fields this deployment ever writes.</summary>
public sealed record RelationTuple(string Namespace, string Object, string Relation, string Subject);

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

    /// <summary>
    /// Throws unless Keto actually removed the tuple.
    ///
    /// <para>
    /// The three delete calls discarded their response, so a revoke that Keto refused was reported
    /// as done: the DB row went away and the grant stayed live with nothing left to name it. Every
    /// "tuple-first, so it fails closed" comment in the controllers depended on this throwing.
    /// A 404 means the tuple is already gone, which is the state the caller asked for.
    /// </para>
    /// </summary>
    private static void EnsureDeleted(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteRelationTupleAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&object={Uri.EscapeDataString(objectId)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}";
        EnsureDeleted(await WriteClient.DeleteAsync(url));
    }

    public async Task DeleteAllProjectTuplesAsync(string projectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString("Projects")}&object={Uri.EscapeDataString(projectId)}";
        EnsureDeleted(await WriteClient.DeleteAsync(url));
    }

    /// <summary>
    /// Removes every tuple on an organisation object — the structural <c>org</c> relation AND all
    /// per-user management grants.
    ///
    /// Deleting an org used to drop only the structural tuple and the org_roles rows, leaving
    /// <c>Organisations:{orgId}#org_admin@user:{uid}</c> behind. That orphan still satisfies
    /// "is admin of some org" while no row remains to name which org, which is the first link in
    /// a chain that ends at the system service accounts.
    /// </summary>
    public async Task DeleteAllOrgTuplesAsync(string orgId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString(Roles.KetoOrgsNamespace)}&object={Uri.EscapeDataString(orgId)}";
        EnsureDeleted(await WriteClient.DeleteAsync(url));
    }

    /// <summary>
    /// Every tuple in <paramref name="namespaceName"/>, optionally narrowed to one relation.
    ///
    /// <para>
    /// Paged: Keto answers with at most <c>page_size</c> tuples and a <c>next_page_token</c>, and a
    /// reconciler that read only the first page would report every tuple beyond it as missing from
    /// Keto — i.e. would propose deleting live grants. The page cap is a hard stop rather than an
    /// endless loop, so a Keto that keeps handing back tokens cannot hang a background pass; the
    /// caller sees a short list, which <see cref="GrantReconciler"/> treats as a refusal to repair
    /// rather than as divergence.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RelationTuple>> ListRelationTuplesAsync(
        string namespaceName, string? relation = null, CancellationToken ct = default)
    {
        const int pageSize = 500;
        const int maxPages = 200;

        var tuples = new List<RelationTuple>();
        var token = "";
        for (var page = 0; page < maxPages; page++)
        {
            var url = $"{_readUrl}/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&page_size={pageSize}";
            if (!string.IsNullOrEmpty(relation)) url += $"&relation={Uri.EscapeDataString(relation)}";
            if (!string.IsNullOrEmpty(token)) url += $"&page_token={Uri.EscapeDataString(token)}";

            var resp = await ReadClient.GetAsync(url, ct);
            // Throws rather than returning what arrived: a partial list read as the state of the
            // store turns every unread grant into "missing from Keto", and the repair for that
            // class deletes rows. Every other read in this class fails soft because a failed check
            // is a denial; this one has to fail loud.
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
            if (result.TryGetProperty("relation_tuples", out var arr) && arr.ValueKind == JsonValueKind.Array)
                tuples.AddRange(arr.EnumerateArray().Select(ToTuple).OfType<RelationTuple>());

            token = result.TryGetProperty("next_page_token", out var next) ? next.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(token)) break;
        }
        return tuples;
    }

    private static RelationTuple? ToTuple(JsonElement t) =>
        t.ValueKind == JsonValueKind.Object
        && t.TryGetProperty("namespace", out var ns) && ns.GetString() is { } nsv
        && t.TryGetProperty("object", out var obj) && obj.GetString() is { } objv
        && t.TryGetProperty("relation", out var rel) && rel.GetString() is { } relv
        && t.TryGetProperty("subject_id", out var sub) && sub.GetString() is { } subv
            ? new RelationTuple(nsv, objv, relv, subv)
            : null;

    public async Task<bool> HasAnyRelationAsync(string namespaceName, string relation, string subjectId)
    {
        var url = $"{_readUrl}/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}&page_size=1";
        var resp = await ReadClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return false;
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.TryGetProperty("relation_tuples", out var tuples) && tuples.GetArrayLength() > 0;
    }

    // ── Role level resolution ─────────────────────────────────────────────────
    //
    // S-8. One question, one implementation, one store.
    //
    // There used to be three: LiveAuthorizationService resolved project_admin as "Keto manager of
    // some project OR an org_roles row anywhere", GetActorManagementLevelForOrgAsync as "an
    // org_roles row in this org", GetActorManagementLevelForProjectAsync as "Keto manager of this
    // project". Three answers to one question, two of them sourced from a store the other did not
    // consult, and no code path that could ever notice them disagreeing.
    //
    // Keto is now the single authority for every management level, because it is the store every
    // grant writes to *first* (AssignManagementRoleAsync, AssignOrgAdmin) — so a row without a
    // tuple is a failed grant, which fails closed, while a tuple without a row is a grant whose
    // bookkeeping lagged, which still works. org_roles keeps holding the scope, the display name
    // and the grant provenance; it is no longer consulted as an answer.

    /// <summary>
    /// Whether <paramref name="level"/> is granted to the actor within the given scope. The scopes
    /// are the ones the grant paths actually write, so the check reads the tuple the grant wrote.
    /// </summary>
    public async Task<bool> IsManagementLevelGrantedAsync(Guid actorId, ManagementLevel level, Guid? orgId, Guid? projectId)
    {
        var subject = $"user:{actorId}";
        return level switch
        {
            ManagementLevel.SuperAdmin => await CheckAsync(
                Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, subject),

            // An org_admin claim must name the org it applies to. Falling back to "admin of any
            // org" let an orphaned grant — one whose organisation had been deleted — satisfy the
            // check while carrying an empty org_id downstream.
            ManagementLevel.OrgAdmin => orgId is { } org
                && await CheckAsync(Roles.KetoOrgsNamespace, org.ToString(), Roles.KetoOrgAdminRelation, subject),

            // Both shapes AssignManagementRoleAsync writes: unscoped (org-wide project_admin) and
            // scoped to one project. Plus the Projects#manager relation, which is what
            // GetConsent reads to decide the role goes in the token at all.
            ManagementLevel.ProjectAdmin =>
                (projectId is { } proj && await CheckAsync(
                    Roles.KetoProjectsNamespace, proj.ToString(), Roles.KetoManagerRelation, subject))
                || (orgId is { } o && await CheckAsync(
                    Roles.KetoOrgsNamespace, o.ToString(), Roles.ProjectAdmin, subject))
                || (orgId is { } o2 && projectId is { } p2 && await CheckAsync(
                    Roles.KetoOrgsNamespace, o2.ToString(), Roles.ProjectAdmin, $"{subject}|project:{p2}")),

            _ => false,
        };
    }

    /// <summary>Most privileged level the actor holds in the given scope, or None.</summary>
    public async Task<ManagementLevel> ResolveManagementLevelAsync(Guid actorId, Guid? orgId, Guid? projectId)
    {
        foreach (var level in (ManagementLevel[])[ManagementLevel.SuperAdmin, ManagementLevel.OrgAdmin, ManagementLevel.ProjectAdmin])
            if (await IsManagementLevelGrantedAsync(actorId, level, orgId, projectId))
                return level;
        return ManagementLevel.None;
    }

    public Task<ManagementLevel> GetActorManagementLevelForProjectAsync(Guid actorId, Guid projectId, Guid orgId)
        => ResolveManagementLevelAsync(actorId, orgId, projectId);

    public Task<ManagementLevel> GetActorManagementLevelForOrgAsync(Guid actorId, Guid orgId)
        => ResolveManagementLevelAsync(actorId, orgId, null);

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
            await ValidateProjectAdminScopeAsync(actorId, orgId, role, scopeId);

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

    private async Task ValidateProjectAdminScopeAsync(Guid actorId, Guid orgId, string role, Guid? scopeId)
    {
        if (role != Roles.ProjectAdmin || scopeId == null)
            throw new ForbiddenException("project_manager can only assign project_manager roles");
        var actorScope = await db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == orgId && r.UserId == actorId && r.Role == Roles.ProjectAdmin);
        if (actorScope?.ScopeId != scopeId)
            throw new ForbiddenException("Cannot assign project_manager for a project outside your scope");
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

    /// <summary>
    /// Grants every role the project flags as a default. A project may flag none, in which case a
    /// new account starts with nothing.
    /// </summary>
    public async Task AssignDefaultRoleAsync(Project project, User user)
    {
        var roles = await db.Roles
            .Where(r => r.ProjectId == project.Id && r.IsDefault)
            .OrderBy(r => r.Rank)
            .ToListAsync();
        foreach (var role in roles) await GrantAsync(project, user, role);
    }

    /// <summary>
    /// One grant, tuple first and rolled back if the row does not take. Per role rather than per
    /// batch: a failure on the third of three must not revoke the two already granted, which are
    /// committed and correct.
    /// </summary>
    private async Task GrantAsync(Project project, User user, Role role)
    {
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
