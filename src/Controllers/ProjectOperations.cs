using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Operations on a project that both the organisation scope and the system scope offer.
///
/// <para>
/// The two scopes differ in exactly two things: who may call the route, and how the project is
/// found — the caller's own organisation, or an id named in the URL. Everything after that was
/// written twice, and what is written twice drifts. It had:
/// </para>
///
/// <list type="bullet">
/// <item>the SAML provider list came back without <c>updated_at</c> and with no <c>ORDER BY</c> on
/// the organisation scope, so a tenant admin saw fewer fields than a super-admin looking at the
/// same providers, in whatever order the query plan produced that day;</item>
/// <item>the scope-change audit entry was written against the caller's organisation on one side
/// and the project's on the other. Equivalent only because the organisation route filters the
/// lookup by that same id — a coincidence, not a rule.</item>
/// </list>
///
/// <para>
/// The controller still resolves the project, because that is the part that is genuinely
/// per-scope. What happens to it afterwards is stated once. Same shape as
/// <see cref="ProjectUpdate"/>.
/// </para>
/// </summary>
public static class ProjectOperations
{
    /// <summary>The scopes every project has, whatever it adds to them.</summary>
    public static readonly string[] BuiltInScopes = ["openid", "profile", "offline_access"];

    // Bounded: a scope list is operator input, and an unbounded backtracking match on it is a
    // denial of service with extra steps.
    private static readonly System.Text.RegularExpressions.Regex ScopeName =
        new(@"^[a-z0-9_:.-]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static IActionResult ReadScopes(Project project) =>
        new OkObjectResult(new { custom_scopes = project.AllowedScopes, built_in = BuiltInScopes });

    public static async Task<IActionResult> UpdateScopesAsync(
        RediensIamDbContext db, HydraService hydra, AuditLogService audit, ILogger logger,
        Guid actorId, Project project, string[] scopes)
    {
        var invalid = scopes.Where(s => !ScopeName.IsMatch(s)).ToArray();
        if (invalid.Length > 0) return new BadRequestObjectResult(new { error = "invalid_scope_names", invalid });

        project.AllowedScopes = scopes;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (project.HydraClientId != null)
        {
            // Warn rather than fail: the scopes are stored, and a Hydra that is briefly unreachable
            // must not lose the operator's change. The reconciler catches the drift.
            try { await hydra.UpdateOAuth2ClientScopeAsync(project.HydraClientId, project.AllowedScopes); }
            catch (Exception ex) { logger.LogWarning(ex, "Hydra scope update failed for project {ProjectId}", project.Id); }
        }

        // project.OrgId, never the caller's: a row written against another organisation — or none —
        // lands on a chain the tenant cannot read, so they could not see that a super-admin changed
        // their scopes for them.
        await audit.RecordAsync(project.OrgId, project.Id, actorId, "project.scopes_updated", "project", project.Id.ToString());
        return new OkObjectResult(new { project.Id, custom_scopes = project.AllowedScopes });
    }

    public static async Task<IActionResult> UnassignUserListAsync(RediensIamDbContext db, Project project)
    {
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new OkObjectResult(new { project.Id, message = "userlist_unassigned" });
    }

    /// <summary>
    /// Ordered, and complete. Without the ORDER BY the row order is whatever the plan produced,
    /// which is not the same as stable; <c>updated_at</c> was missing from one of the two copies.
    /// </summary>
    public static async Task<IActionResult> ListSamlProvidersAsync(RediensIamDbContext db, Guid projectId)
    {
        var providers = await db.SamlIdpConfigs
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id, x.EntityId, x.MetadataUrl, x.SsoUrl,
                x.EmailAttributeName, x.DisplayNameAttributeName,
                x.JitProvisioning, x.DefaultRoleId, x.Active,
                x.CreatedAt, x.UpdatedAt,
            })
            .ToListAsync();
        return new OkObjectResult(providers);
    }
}
