using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// The SuperAdmin management surface.
///
/// It is served under two prefixes on purpose. <c>/admin</c> is what the admin SPA calls;
/// <c>/api/manage</c> is the machine-to-machine name, reachable with a SuperAdmin PAT or a
/// client_credentials token. They are the *same actions on the same class*, not two
/// implementations — which is the whole point: <c>ManagedApiController</c> used to re-implement
/// seven of them, and a re-implementation is exactly where an authorisation check goes missing.
/// Every route below therefore passes through one <see cref="RequireManagementLevelAttribute"/>,
/// i.e. one live Keto re-check, whichever prefix the caller used.
/// </summary>
[ApiController]
[Route("admin")]
[Route("api/manage")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
#pragma warning disable S107 // what this controller depends on, listed; the bundle that hid the count only forwarded
public class SystemAdminController(
    RediensIamDbContext db,
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    IEmailService emailService,
    IDistributedCache cache,
    LiveAuthorizationService live,
    KeyRotationService keyRotation,
    GrantReconciler grantReconciler,
    AppConfig appConfig,
    ILogger<SystemAdminController> logger) : ControllerBase
#pragma warning restore S107
{
    private static readonly string[] OAuth2GrantTypes   = ["authorization_code", "refresh_token"];
    private static readonly string[] OAuth2ResponseTypes = ["code"];
    private const string AuditOrg = "organisation";

    // The two columns OrganisationConfiguration declares, restated so an over-long value is a
    // named 400 rather than a DbUpdateException. Changing one of these without the other is the
    // drift that puts the 500 back.
    private const int MaxOrgNameLength = 200;
    private const int MaxOrgSlugLength = 100;

    /// <summary>Management role names accepted by the role-assignment endpoints.</summary>
    internal static readonly string[] KnownManagementRoles = Roles.Management;

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid GetActorId() => Claims.ParsedUserId;

    // ── Key rotation (S-10) ───────────────────────────────────────────────────

    /// <summary>
    /// Progress of a root-key rotation. <c>totalPending</c> reaching 0 is the only signal that
    /// the retired key may be removed from <c>Security:EncryptionKeys</c>.
    /// </summary>
    [HttpGet("key-rotation")]
    public async Task<IActionResult> KeyRotationStatus(CancellationToken ct)
        => Ok(await keyRotation.GetStatusAsync(ct));

    /// <summary>
    /// Re-encrypts every stored ciphertext under the active key. Idempotent and safe to re-run;
    /// encryption is already lazy on write, but cold rows (a TOTP secret written once at
    /// enrolment) never migrate on their own.
    /// </summary>
    [HttpPost("key-rotation/reencrypt")]
    public async Task<IActionResult> KeyRotationReEncrypt(CancellationToken ct)
    {
        var status = await keyRotation.ReEncryptAsync(ct);
        await audit.RecordAsync(null, null, GetActorId(), "system.key_rotation.reencrypt",
            "encryption_key", status.ActiveKeyId.ToString());
        return Ok(status);
    }

    // ── Integrity: audit chain (S-3) and the grant dual write (S-8) ───────────

    /// <summary>
    /// Verifies every organisation's audit hash chain, plus the deployment-wide one.
    ///
    /// <para>
    /// <c>intact</c> means no broken link. <c>fullyVerified</c> is the stronger claim and the one
    /// worth reading: every surviving row recomputed under a key this deployment holds.
    /// <c>unverifiable</c> counts the rows that predate the chain or its keying, or that were
    /// written under a retired key — rows nothing can vouch for, reported rather than passed off
    /// as valid.
    /// </para>
    ///
    /// <para>Also runs unattended once a day; see <c>IntegrityMonitorService</c>.</para>
    /// </summary>
    [HttpGet("audit-chain")]
    public async Task<IActionResult> AuditChainStatus(CancellationToken ct)
    {
        var statuses = await audit.VerifyAllChainsAsync(ct);
        return Ok(new
        {
            chains = statuses.Select(s => new
            {
                org_id = s.OrgId,
                first_break = s.Status.FirstBreak,
                verified = s.Status.Verified,
                unverifiable = s.Status.Unverifiable,
                intact = s.Status.Intact,
                fully_verified = s.Status.FullyVerified,
            }),
            broken = statuses.Count(s => !s.Status.Intact),
        });
    }

    /// <summary>
    /// Reports grants the Keto tuple store and <c>org_roles</c>/<c>user_project_roles</c> disagree
    /// about. Read-only. <c>orphanTuples</c> is the class that matters: live privilege with no
    /// record of who granted it.
    /// </summary>
    [HttpGet("grant-reconcile")]
    public async Task<IActionResult> GrantReconcileScan(CancellationToken ct)
        => Ok(await grantReconciler.ScanAsync(ct));

    /// <summary>
    /// Repairs the divergence: revokes tuples with no backing row, deletes rows with no tuple.
    /// Never creates a tuple from a row — that would make the database a source of authority
    /// again. Refuses when divergence is large enough to mean a store-level failure.
    /// </summary>
    [HttpPost("grant-reconcile/repair")]
    public async Task<IActionResult> GrantReconcileRepair(CancellationToken ct)
        => Ok(await grantReconciler.RepairAsync(GetActorId(), ct));

    // ── Organisations ─────────────────────────────────────────────────────────

    [HttpGet("organizations")]
    public async Task<IActionResult> ListOrgs()
    {
var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt }).ToListAsync();
        return Ok(orgs);
    }

    [HttpGet("organizations/{id}")]
    public async Task<IActionResult> GetOrg(Guid id)
    {
        var org = await db.Organisations
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt, o.OrgListId, o.CreatedBy })
            .FirstOrDefaultAsync();
        if (org == null) return NotFound();
        return Ok(org);
    }

    /// <summary>
    /// Creates an organisation and the user list it owns.
    ///
    /// <para>
    /// Every refusal below is stated before anything is written, and the writes that follow are one
    /// transaction. Both halves matter and neither substitutes for the other:
    /// </para>
    /// <list type="bullet">
    ///   <item>Validating first is what turns the ordinary case — a second customer whose name
    ///   derives the same slug — from a <c>DbUpdateException</c> and a 500 into a 409 naming the
    ///   one field the caller can change. The same reasoning, and the same defect, as the unique
    ///   index on (UserListId, Email) in <see cref="UserListOperations"/>.</item>
    ///   <item>The transaction is what covers the failures nobody predicted. The list used to be
    ///   committed before the organisation existed, so any error on the second write left an
    ///   <c>Immovable</c> list with a null <c>OrgId</c> that nothing referenced and
    ///   <c>DeleteOrg</c> could never reach — invisible, and one more per retry.</item>
    /// </list>
    /// </summary>
    [HttpPost("organizations")]
    public async Task<IActionResult> CreateOrg([FromBody] CreateOrgRequest body)
    {
        var actorId = GetActorId();

        var name = body.Name?.Trim();
        var slug = body.Slug?.Trim();

        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "name_required" });
        if (string.IsNullOrEmpty(slug))
            return BadRequest(new { error = "slug_required" });

        // The columns are 200 and 100. Left to the database these are a third and fourth way to
        // get a 500 through the same door as the duplicate slug.
        if (name.Length > MaxOrgNameLength)
            return BadRequest(new { error = "name_too_long", max_length = MaxOrgNameLength });
        if (slug.Length > MaxOrgSlugLength)
            return BadRequest(new { error = "slug_too_long", max_length = MaxOrgSlugLength });

        if (await db.Organisations.AnyAsync(o => o.Slug == slug))
            return Conflict(new { error = "slug_already_exists" });

        Organisation org;
        UserList orgList;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            orgList = new UserList { Name = $"{name} Org List", Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
            db.UserLists.Add(orgList);
            await db.SaveChangesAsync();

            org = new Organisation
            {
                Name = name, Slug = slug, OrgListId = orgList.Id,
                Active = true, CreatedBy = actorId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();

            orgList.OrgId = org.Id;
            await db.SaveChangesAsync();

            await tx.CommitAsync();
        }

        // Outside the transaction, and after it: Keto and the audit log are other systems, and a
        // database transaction cannot roll either of them back.
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, org.Id.ToString(), "org", $"{Roles.KetoSystemNamespace}:{Roles.KetoSystemObject}");
        await audit.RecordAsync(org.Id, null, actorId, "org.created", AuditOrg, org.Id.ToString());
        return Created($"/admin/organizations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    [HttpPatch("organizations/{id}")]
    public async Task<IActionResult> UpdateOrg(Guid id, [FromBody] UpdateOrgRequest body)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        if (body.AuditRetentionDays is { } days && days != -1 && days < AppConfig.MinAuditRetentionDays)
            return BadRequest(new { error = "audit_retention_too_short", minimum = AppConfig.MinAuditRetentionDays });
        if (body.Name != null) org.Name = body.Name;
        if (body.AuditRetentionDays.HasValue) org.AuditRetentionDays = body.AuditRetentionDays == -1 ? null : body.AuditRetentionDays;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.updated", AuditOrg, id.ToString());
        return Ok(new { org.Id, org.Name, org.AuditRetentionDays });
    }

    [HttpPost("organizations/{id}/suspend")]
    public async Task<IActionResult> SuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = false; org.SuspendedAt = DateTimeOffset.UtcNow; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.suspended", AuditOrg, id.ToString());
        // Active is only consulted at login, so suspension used to leave every session already
        // issued working until its token expired — the suspended tenant kept full API access for
        // the token lifetime at every resource server. DeleteOrg already revokes this way, and
        // suspension is the same revocation with the rows left in place.
        await RevokeOrgSessionsAsync(id);
        return Ok(new { message = "org_suspended" });
    }

    /// <summary>
    /// Revokes the Hydra sessions of everyone an organisation's suspension must reach.
    /// Best-effort per subject: one unreachable subject must not leave the rest signed in.
    /// </summary>
    private async Task RevokeOrgSessionsAsync(Guid orgId)
    {
        var memberIds = await db.Users
            .Where(u => u.UserList.OrgId == orgId)
            .Select(u => u.Id)
            .ToListAsync();
        // Administering an org does not require belonging to it — AssignOrgAdmin takes any user
        // id — so the membership query alone misses every grant held by a system-list user.
        var adminIds = await db.OrgRoles
            .Where(r => r.OrgId == orgId)
            .Select(r => r.UserId)
            .ToListAsync();

        // Two subject shapes, not one. `{orgId}:{userId}` is the tenant session; the bare user id
        // is the management-console session (LiveAuthorizationService.InvalidateAsync says so
        // explicitly). Revoking only the first suspended the org's end users and left its own
        // administrators working on /org/* — the party suspension most needs to stop.
        var subjects = memberIds
            .SelectMany(id => new[] { $"{orgId}:{id}", id.ToString() })
            .Concat(adminIds.Select(id => id.ToString()))
            .Distinct();
        foreach (var subject in subjects)
        {
            try { await hydra.RevokeSessionsAsync(subject); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to revoke Hydra sessions for subject {Subject} in org {OrgId}", subject, orgId); }
        }
    }

    [HttpPost("organizations/{id}/unsuspend")]
    public async Task<IActionResult> UnsuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = true; org.SuspendedAt = null; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.unsuspended", AuditOrg, id.ToString());
        return Ok(new { message = "org_unsuspended" });
    }

    [HttpDelete("organizations/{id}")]
    public async Task<IActionResult> DeleteOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var projects = await db.Projects.Where(p => p.OrgId == id).ToListAsync();
        foreach (var p in projects)
        {
            if (p.HydraClientId != null)
            {
                try { await hydra.DeleteOAuth2ClientAsync(p.HydraClientId); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete Hydra client {ClientId} during org {OrgId} deletion", p.HydraClientId, id); }
            }
            await keto.DeleteAllProjectTuplesAsync(p.Id.ToString());
        }
        db.Projects.RemoveRange(projects);

        var orgRoles = await db.OrgRoles.Where(r => r.OrgId == id).ToListAsync();
        db.OrgRoles.RemoveRange(orgRoles);

        // Projecting to ids keeps UserList out of EF's change tracker. Materialising the entities
        // instead triggers EF's own cascade, which fails on the org ↔ list circular dependency.
        var listIds = await db.UserLists.Where(ul => ul.OrgId == id).Select(ul => ul.Id).ToListAsync();
        foreach (var listId in listIds)
        {
            var users = await db.Users.Where(u => u.UserListId == listId).ToListAsync();
            foreach (var userId in users.Select(u => u.Id))
            {
                await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, listId.ToString(), "member", $"user:{userId}");
                try { await hydra.RevokeSessionsAsync($"{id}:{userId}"); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to revoke Hydra sessions for user {UserId} during org {OrgId} deletion", userId, id); }
            }
            db.Users.RemoveRange(users);
        }
        // Save the user deletions before tackling the org+lists circular FK.
        await db.SaveChangesAsync();

        // Delete the whole object, not just the structural relation: per-user org_admin grants
        // written by AssignOrgAdmin/AssignManagementRoleAsync live on this same object and would
        // otherwise survive the org, becoming an "admin of some org that no longer exists" grant.
        await keto.DeleteAllOrgTuplesAsync(id.ToString());

        // Use raw SQL so PostgreSQL handles the circular FK (org.OrgListId ↔ list.OrgId)
        // in a single statement where both sides are deleted atomically.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM organisations WHERE \"Id\" = {0}", id);
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!string.IsNullOrEmpty(q) && q.Length < 3)
            return BadRequest(new { error = "query_too_short", min_length = 3 });

        // Clamped like every other paged endpoint here: page=0 produced Skip(-50), which Postgres
        // rejects outright, and an unbounded pageSize serialised the whole table.
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.Users.AsQueryable();
        if (!string.IsNullOrEmpty(q))
            query = query.Where(u => u.Email.Contains(q) || u.Username.Contains(q));
        var users = await query
            .Include(u => u.UserList)
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            // display_name, org_name, user_list_name and locked_until are what the console's user
            // table renders. Without locked_until its isLocked() was permanently false, so the
            // Locked badge never appeared and the Unlock action it gates was unreachable.
            .Select(u => new
            {
                u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active,
                u.UserListId, u.LastLoginAt, u.LockedUntil,
                OrgId        = u.UserList.OrgId,
                OrgName      = u.UserList.Organisation != null ? u.UserList.Organisation.Name : null,
                UserListName = u.UserList.Name,
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
var user = await db.Users
            .Include(u => u.UserList).ThenInclude(ul => ul.Organisation)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgRoles = await db.OrgRoles
            .Where(r => r.UserId == id)
            .Select(r => new { r.Role, r.OrgId, r.ScopeId })
            .ToListAsync();
        var isSystemAdmin = user.UserList.OrgId == null && user.UserList.Immovable;
        return Ok(new
        {
            user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName,
            user.Phone, user.Active, user.EmailVerified, user.PhoneVerified,
            user.TotpEnabled, user.WebAuthnEnabled,
            user.LockedUntil, user.FailedLoginCount,
            user.LastLoginAt, user.CreatedAt, user.UpdatedAt,
            user_list_id = user.UserListId,
            org_id       = user.UserList.OrgId,
            org_name     = user.UserList.Organisation?.Name,
            is_system_admin = isSystemAdmin,
            roles = orgRoles
        });
    }

    [HttpPatch("users/{id}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        return await UserHelpers.ApplyAdminUpdateAsync(db, hydra, audit, passwords, GetActorId(), user, body);
    }

    [HttpPost("users/{id}/unlock")]
    public async Task<IActionResult> UnlockUser(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        user.LockedUntil      = null;
        user.FailedLoginCount = 0;
        user.UpdatedAt        = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.unlocked", "user", id.ToString());
        return Ok(new { user.Id, message = "user_unlocked" });
    }

    [HttpGet("users/{id}/sessions")]
    public async Task<IActionResult> ListSessions(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgId = user.UserList.OrgId?.ToString() ?? "";
        var sessions = await hydra.ListConsentSessionsAsync($"{orgId}:{id}");
        return Ok(sessions.Select(s => new
        {
            client_id   = s.ConsentRequest?.Client?.ClientId,
            client_name = s.ConsentRequest?.Client?.ClientName,
            scopes      = s.GrantedScopes,
            created_at  = s.ConsentRequest?.RequestedAt,
            expires_at  = s.ExpiresAt
        }));
    }

    [HttpDelete("users/{id}/sessions")]
    public async Task<IActionResult> ForceLogout(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        return await UserHelpers.ForceLogoutAsync(hydra, audit, GetActorId(), user);
    }


    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("userlists")]
    public async Task<IActionResult> ListAllUserLists([FromQuery] Guid? org_id)
    {
var query = db.UserLists.AsQueryable();
        if (org_id.HasValue) query = query.Where(ul => ul.OrgId == org_id);
        var lists = await query
            .Select(ul => new {
                ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt,
                OrgName = ul.Organisation != null ? ul.Organisation.Name : null
            }).ToListAsync();
        return Ok(lists);
    }

    [HttpGet("userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null) return NotFound();
        return await UserListOperations.ReadAsync(db, ul);
    }

    [HttpGet("userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id)) return NotFound();
        return await UserListOperations.ListUsersAsync(db, id);
    }

    [HttpPost("userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] UserListOperations.NewUser body)
    {
        var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null) return NotFound();
        // Surface système : l'organisation est nommée explicitement. C'est le seul endroit où
        // elle peut l'être, et c'est ce qui permet de peupler une liste PARTAGÉE entre plusieurs
        // locataires — le modèle décrit dans docs/ORGANIZATIONS.md.
        return await UserListOperations.AddUserAsync(
            new UserListDeps(db, keto, audit, passwords, emailService, appConfig), GetActorId(), ul, body,
            "/admin/userlists", body.OrgId);
    }

    [HttpDelete("userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUserFromList(Guid id, Guid uid)
    {
var ul = await UserListOperations.FindAsync(db, id);
        if (ul == null) return NotFound();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        return await UserListOperations.RemoveUserAsync(db, keto, audit, GetActorId(), ul, user);
    }

    [HttpPost("userlists")]
    public async Task<IActionResult> AdminCreateUserList([FromBody] AdminCreateUserListRequest body)
    {
return await UserListOperations.CreateAsync(db, audit, GetActorId(), body.Name, body.OrgId, "/admin/userlists");
    }

    /// <summary>
    /// Le pendant système de <c>DELETE /org/userlists/{id}</c>, qui n'existait pas : une liste
    /// pouvait être créée depuis la console système et n'en être jamais retirée, y compris celles
    /// sans organisation, que la route d'organisation ne voit pas.
    ///
    /// <para>
    /// Mêmes deux refus que la route d'organisation, et pour les mêmes raisons : la liste
    /// immuable porte l'administration du déploiement, et une liste encore assignée à un projet
    /// laisserait ce projet sans population plutôt que sans liste.
    /// </para>
    /// </summary>
    [HttpDelete("userlists/{id}")]
    public async Task<IActionResult> AdminDeleteUserList(Guid id)
    {
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id);
        if (ul == null) return NotFound();
        if (ul.Immovable) return BadRequest(new { error = "cannot_delete_immovable" });
        if (await db.Projects.AnyAsync(p => p.AssignedUserListId == id))
            return BadRequest(new { error = "userlist_is_assigned_to_project" });

        db.UserLists.Remove(ul);
        await db.SaveChangesAsync();
        await audit.RecordAsync(ul.OrgId, null, GetActorId(), "userlist.deleted", "userlist", id.ToString(),
            new() { ["name"] = ul.Name });
        return NoContent();
    }

    // ── Org Admins ────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/admins")]
    public async Task<IActionResult> ListOrgAdmins(Guid id)
    {
var orgRoles = await db.OrgRoles.Where(r => r.OrgId == id).Include(r => r.User).ToListAsync();
        var projectIds = orgRoles.Where(r => r.ScopeId.HasValue).Select(r => r.ScopeId!.Value).Distinct().ToList();
        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(orgRoles.Select(r => new
        {
            r.Id, r.OrgId, r.UserId, r.Role, r.ScopeId, r.GrantedAt,
            user_name  = $"{r.User.Username}#{r.User.Discriminator}",
            user_email = r.User.Email,
            scope_name = r.ScopeId.HasValue && projects.TryGetValue(r.ScopeId.Value, out var p) ? p.Name : null
        }));
    }

    [HttpPost("organizations/{id}/admins")]
    public async Task<IActionResult> AssignOrgAdmin(Guid id, [FromBody] AssignOrgAdminRequest body)
    {
        // Role is written verbatim into OrgRoles.Role AND used as a Keto relation name.
        // An arbitrary string would pollute the permission graph with a relation nothing checks.
        if (!KnownManagementRoles.Contains(body.Role))
            return BadRequest(new { error = "unknown_role", allowed = KnownManagementRoles });

        // super_admin is deployment-wide and lives at System:rediensiam#super_admin, written by
        // adding the user to the __system__ list. Accepting it here wrote an org-scoped tuple no
        // policy reads plus an org_roles row nothing resolves: the console then showed a
        // super_admin who had no super_admin. /org/admins already refuses it; so does this now.
        if (body.Role == Roles.SuperAdmin)
            return StatusCode(403, new { error = "cannot_grant_super_admin" });

        var existing = await db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == id && r.UserId == body.UserId && r.Role == body.Role && r.ScopeId == body.ScopeId);
        if (existing != null) return Ok(new { existing.Id });
        var role = new OrgRole
        {
            OrgId = id, UserId = body.UserId, Role = body.Role,
            ScopeId = body.ScopeId, GrantedBy = GetActorId(), GrantedAt = DateTimeOffset.UtcNow
        };
        // Tuple first, row second, tuple removed if the row fails — the order
        // KetoService.AssignManagementRoleAsync established. Row-first left a committed grant with
        // no tuple behind it when Keto rejected the write.
        var ketoSubject = body.ScopeId.HasValue ? $"user:{body.UserId}|project:{body.ScopeId}" : $"user:{body.UserId}";
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), body.Role, ketoSubject);
        try
        {
            db.OrgRoles.Add(role);
            await db.SaveChangesAsync();
        }
        catch
        {
            await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), body.Role, ketoSubject);
            throw;
        }
        await live.InvalidateAsync(body.UserId);
        await audit.RecordAsync(id, null, GetActorId(), "role.management.assigned", "user", body.UserId.ToString(),
            new() { ["role"] = body.Role });
        return Created($"/admin/organizations/{id}/admins/{role.Id}", new { role.Id });
    }

    [HttpDelete("organizations/{id}/admins/{roleId}")]
    public async Task<IActionResult> RemoveOrgAdmin(Guid id, Guid roleId)
    {
var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.OrgId == id);
        if (role == null) return NotFound();
        // Tuple first. Row-first meant a failing Keto delete left the tuple with no row naming it —
        // R-01's "admin of some org" orphan, and the fail-open direction. Tuple-first fails closed:
        // the grant stops working and the row is the evidence that it should be cleaned up.
        var ketoSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), role.Role, ketoSubject);
        db.OrgRoles.Remove(role);
        await db.SaveChangesAsync();
        await live.InvalidateAsync(role.UserId);
        await audit.RecordAsync(id, null, GetActorId(), "role.management.removed", "user", role.UserId.ToString(),
            new() { ["role"] = role.Role });
        return NoContent();
    }


    // ── Projects (admin scope) ────────────────────────────────────────────────

    [HttpGet("projects")]
    public async Task<IActionResult> AdminListAllProjects()
    {
var projects = await db.Projects
            .Join(db.Organisations, p => p.OrgId, o => o.Id,
                (p, o) => new {
                    p.Id, p.Name, p.Slug, p.Active, p.OrgId,
                    // The list whose members may sign in here. A consumer that provisions accounts
                    // for a project has to know it, and the only other way to learn it was to read
                    // every user list and look for one whose assigned_projects names this project —
                    // a scan to recover something the project already holds. Left out, a caller
                    // pins the id in its own configuration and silently keeps pointing at the old
                    // list the day the assignment changes: accounts land somewhere nobody can sign
                    // in through, and the symptom is "invalid credentials" on a valid account.
                    p.AssignedUserListId,
                    OrgName = o.Name, p.HydraClientId, p.CreatedAt
                })
            .OrderBy(p => p.OrgName).ThenBy(p => p.Name)
            .ToListAsync();
        return Ok(projects);
    }


    /// <summary>Projects of one organisation. Kept from the old ManagedApiController surface.</summary>
    [HttpGet("organizations/{id}/projects")]
    public async Task<IActionResult> AdminListOrgProjects(Guid id)
    {
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        var projects = await db.Projects
            .Where(p => p.OrgId == id)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active, p.OrgId, p.AssignedUserListId, p.HydraClientId, p.CreatedAt })
            .ToListAsync();
        return Ok(projects);
    }

    /// <summary>
    /// One project, by id.
    ///
    /// <para>
    /// It did not exist, and <c>AdminCreateProject</c> has been answering
    /// <c>Location: /admin/projects/{id}</c> since it was written — a header pointing at a 404. A
    /// caller holding a project id could only find it again by listing every project of every
    /// organisation.
    /// </para>
    /// </summary>
    [HttpGet("projects/{id}")]
    public async Task<IActionResult> AdminGetProject(Guid id)
    {
        var project = await db.Projects
            .Where(p => p.Id == id)
            .Join(db.Organisations, p => p.OrgId, o => o.Id,
                (p, o) => new {
                    p.Id, p.Name, p.Slug, p.Active, p.OrgId, p.AssignedUserListId,
                    OrgName = o.Name, p.HydraClientId, p.RequireRoleToLogin, p.CreatedAt, p.UpdatedAt
                })
            .FirstOrDefaultAsync();
        return project == null ? NotFound() : Ok(project);
    }

    [HttpPost("organizations/{id}/projects")]
    public async Task<IActionResult> AdminCreateProject(Guid id, [FromBody] AdminCreateProjectRequest body)
    {
var actorId = GetActorId();
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        var project = new Project
        {
            OrgId = id, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin ?? false,
            Active = true, CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        try
        {
            await hydra.CreateOAuth2ClientAsync(new
            {
                client_id    = $"client_{project.Id}",
                client_name  = $"Project: {project.Name}",
                redirect_uris = body.RedirectUris ?? [],
                post_logout_redirect_uris = body.PostLogoutRedirectUris ?? [],
                allowed_cors_origins = ClientOriginsService.CorsOriginsFor(body.RedirectUris, body.PostLogoutRedirectUris),
                grant_types  = OAuth2GrantTypes,
                response_types = OAuth2ResponseTypes,
                scope        = "openid profile offline_access",
                token_endpoint_auth_method = "none",
                metadata     = new { project_id = project.Id.ToString(), org_id = id.ToString() }
            });
            project.HydraClientId = $"client_{project.Id}";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hydra client creation failed for project {ProjectId} — rolling back", project.Id);
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return StatusCode(502, new { error = "hydra_unavailable", detail = ex.Message });
        }
        await keto.WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), "org", $"{Roles.KetoOrgsNamespace}:{id}");
        await audit.RecordAsync(id, project.Id, actorId, "project.created", "project", project.Id.ToString());
        return Created($"/admin/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpPatch("projects/{id}")]
    public async Task<IActionResult> AdminUpdateProject(Guid id, [FromBody] ProjectUpdateRequest body)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        if (await ProjectUpdate.ApplyAsync(db, hydra, audit, appConfig, GetActorId(), project, body) is { } err) return err;
        await ProjectUpdate.SaveAndAuditAsync(db, audit, GetActorId(), project);
        return Ok(new { project.Id, project.Name });
    }

    [HttpGet("projects/{id}/scopes")]
    public async Task<IActionResult> AdminGetProjectScopes(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return ProjectOperations.ReadScopes(project);
    }

    [HttpPut("projects/{id}/scopes")]
    public async Task<IActionResult> AdminUpdateProjectScopes(Guid id, [FromBody] UpdateScopesRequest body)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();

        return await ProjectOperations.UpdateScopesAsync(db, hydra, audit, logger, GetActorId(), project, body.Scopes);
    }

    [HttpDelete("projects/{id}")]
    public async Task<IActionResult> AdminDeleteProject(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return await ProjectOperations.DeleteAsync(db, hydra, keto, audit, logger, GetActorId(), project);
    }

    [HttpPut("projects/{id}/userlist")]
    public async Task<IActionResult> AdminAssignUserList(Guid id, [FromBody] AdminAssignUserListRequest body)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return await ProjectOperations.AssignUserListAsync(db, audit, GetActorId(), project, body.UserListId);
    }

    [HttpDelete("projects/{id}/userlist")]
    public async Task<IActionResult> AdminUnassignUserList(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return await ProjectOperations.UnassignUserListAsync(db, audit, GetActorId(), project);
    }

    [HttpGet("projects/{id}/stats")]
    public async Task<IActionResult> AdminGetProjectStats(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();

        // A project with no user list assigned has no users, which is a fact about the project and
        // not a missing resource. Answering 404 made the console log an error on every freshly
        // created project — the state every project is in for its first few minutes.
        if (project.AssignedUserListId == null)
            return Ok(new { total_users = 0, active_users = 0, users_by_role = Array.Empty<object>() });

        var totalUsers  = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId);
        var activeUsers = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId && u.Active);
        var usersByRole = await db.UserProjectRoles
            .Include(r => r.Role)
            .Where(r => r.ProjectId == id)
            .GroupBy(r => new { r.RoleId, r.Role.Name })
            .Select(g => new { role_id = g.Key.RoleId, role_name = g.Key.Name, count = g.Count() })
            .ToListAsync();

        return Ok(new { total_users = totalUsers, active_users = activeUsers, users_by_role = usersByRole });
    }

    // ── Roles (admin scope) ───────────────────────────────────────────────────

    [HttpGet("projects/{id}/roles")]
    public async Task<IActionResult> AdminListRoles(Guid id)
    {
        // 404 sur projet inconnu, comme ProjectController.ListRoles. Sans ce test la route rendait
        // `[]`, que la console affiche en « No roles defined yet » : un id erroné dans l'URL était
        // indiscernable d'un projet sans rôle.
        if (!await db.Projects.AnyAsync(p => p.Id == id)) return NotFound();

        var roles = await db.Roles
            .Where(r => r.ProjectId == id)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank })
            .ToListAsync();
        return Ok(roles);
    }

    [HttpPost("projects/{id}/roles")]
    public async Task<IActionResult> AdminCreateRole(Guid id, [FromBody] AdminCreateRoleRequest body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (project == null) return NotFound();
        return await ProjectOperations.CreateRoleAsync(db, audit, GetActorId(), project.Id, project.OrgId,
            new ProjectOperations.NewRole(body.Name, body.Description, body.Rank), $"/admin/projects/{id}/roles");
    }

    [HttpDelete("projects/{id}/roles/{rid}")]
    public async Task<IActionResult> AdminDeleteRole(Guid id, Guid rid)
    {
var role = await db.Roles
            .Include(r => r.UserProjectRoles)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == id);
        if (role == null) return NotFound();

        // ProjectController.DeleteRole walks the holders and drops a tuple each; removing the row
        // here only cascaded the UserProjectRoles, leaving every holder's grant live in Keto.
        // The holder ids, not the assignment rows: nothing below reads any other column, and the
        // list is materialised before the delete so the cascade cannot pull it out from under us.
        foreach (var userId in role.UserProjectRoles.Select(a => a.UserId).ToList())
        {
            try
            {
                // `role:{nom}` et `user:{id}`, pas les valeurs nues : c'est la forme que KetoService
                // écrit à l'assignation et que ProjectController.DeleteRole supprime. Passer le nom
                // et l'id bruts visait un tuple qui n'existe pas — la ligne partait, le grant Keto
                // restait, et un rôle supprimé depuis la portée système laissait ses porteurs
                // autorisés.
                await keto.DeleteRelationTupleAsync(
                    Roles.KetoProjectsNamespace, id.ToString(), $"role:{role.Name}", $"user:{userId}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Keto tuple cleanup failed for role {RoleId} user {UserId}", rid, userId);
            }
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        var project = await db.Projects.FindAsync(id);
        await audit.RecordAsync(project?.OrgId, id, GetActorId(), "role.deleted", "role", rid.ToString(),
            new() { ["name"] = role.Name });
        return NoContent();
    }

    // ── Email overview ────────────────────────────────────────────────────────

    [HttpGet("email/overview")]
    public async Task<IActionResult> GetEmailOverview()
    {
        var globalConfigured = !string.IsNullOrEmpty(appConfig.SmtpHost);

        var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .OrderBy(o => o.Name)
            .Select(o => new
            {
                o.Id,
                o.Name,
                o.Slug,
                SmtpConfig = db.OrgSmtpConfigs
                    .Where(c => c.OrgId == o.Id)
                    .Select(c => new { c.Host, c.Port, c.StartTls, c.FromAddress, c.FromName, c.UpdatedAt })
                    .FirstOrDefault(),
                ProjectOverrides = db.Projects
                    .Where(p => p.OrgId == o.Id && p.EmailFromName != null)
                    .Select(p => new { p.Id, p.Name, p.EmailFromName })
                    .ToList(),
            })
            .ToListAsync();

        return Ok(new
        {
            global_smtp = new
            {
                configured   = globalConfigured,
                host         = appConfig.SmtpHost,
                port         = appConfig.SmtpPort,
                start_tls    = appConfig.SmtpStartTls,
                from_address = appConfig.SmtpFromAddress,
                from_name    = appConfig.SmtpFromName,
            },
            orgs = orgs.Select(o => new
            {
                o.Id,
                o.Name,
                o.Slug,
                smtp_configured  = o.SmtpConfig != null,
                smtp_host        = o.SmtpConfig?.Host,
                smtp_port        = o.SmtpConfig?.Port,
                smtp_from_address = o.SmtpConfig?.FromAddress,
                smtp_from_name   = o.SmtpConfig?.FromName,
                smtp_updated_at  = o.SmtpConfig?.UpdatedAt,
                project_overrides = o.ProjectOverrides,
            }),
        });
    }

    // ── Org SMTP ──────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/smtp")]
    public async Task<IActionResult> GetOrgSmtp(Guid id)
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
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

    [HttpPut("organizations/{id}/smtp")]
    public async Task<IActionResult> UpsertOrgSmtp(Guid id, [FromBody] AdminUpsertSmtpRequest body)
    {
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        if (await SmtpEndpointValidator.ValidateAsync(body.Host, body.Port, body.StartTls) is { } smtpErr)
            return BadRequest(new { error = smtpErr });

        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null)
        {
            config = new OrgSmtpConfig
            {
                OrgId       = id,
                Host        = body.Host,
                Port        = body.Port,
                StartTls    = body.StartTls,
                Username    = body.Username,
                PasswordEnc = body.Password != null
                    ? TotpEncryption.Encrypt(appConfig.SmtpEncKey, Encoding.UTF8.GetBytes(body.Password))
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
                config.PasswordEnc = TotpEncryption.Encrypt(appConfig.SmtpEncKey, Encoding.UTF8.GetBytes(body.Password));
            config.FromAddress = body.FromAddress;
            config.FromName    = body.FromName;
            config.UpdatedAt   = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "smtp_config_saved" });
    }

    [HttpDelete("organizations/{id}/smtp")]
    public async Task<IActionResult> DeleteOrgSmtp(Guid id)
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null) return NoContent();
        db.OrgSmtpConfigs.Remove(config);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("organizations/{id}/smtp/test")]
    public async Task<IActionResult> TestOrgSmtp(Guid id)
    {
        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == Claims.ParsedUserId);
        if (actor == null) return BadRequest(new { error = "user_not_found" });
        try
        {
            await emailService.SendOtpAsync(actor.Email, "TEST-MESSAGE", "smtp_test", id);
            return Ok(new { message = "test_email_sent", to = actor.Email });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP test failed for org {OrgId}", id);
            return BadRequest(new { error = "smtp_test_failed", detail = ex.Message });
        }
    }

    // ── Audit + Metrics ────────────────────────────────────────────────────────

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        return await AuditLogQuery.PageAsync(db, null, limit, offset);
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics()
    {
        // logins_by_hour has been declared in the console's Metrics interface all along, and nothing
        // ever sent it: the chart under "Login activity · last 24h" drew a sine wave, identical on
        // every deployment and every reload, beside a real count. The audit log already records
        // every outcome, so the panel can simply be told the truth.
        // Aligned to the hour, or the label lies a second time: a bucket starting at 13:35 that
        // prints "13:00" puts a 13:10 login in yesterday's bar.
        var now       = DateTimeOffset.UtcNow;
        var thisHour  = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var since     = thisHour.AddHours(-23);
        var loginEvents = await db.AuditLogs
            .Where(l => l.CreatedAt >= since
                        && (l.Action == "user.login.success" || l.Action == "user.login.failure"))
            .Select(l => new { l.CreatedAt, Success = l.Action == "user.login.success" })
            .ToListAsync();

        // Buckets run [start, start+1h) and carry the label of their own start. Labelling a bucket
        // with the hour it ends in put every event one bar to the right of when it happened.
        var loginsByHour = Enumerable.Range(0, 24)
            .Select(offset =>
            {
                var start = since.AddHours(offset);
                var end   = start.AddHours(1);
                var bucket = loginEvents.Where(e => e.CreatedAt >= start && e.CreatedAt < end);
                return new
                {
                    hour      = start.ToString("yyyy-MM-ddTHH:00:00Z"),
                    succeeded = bucket.Count(e => e.Success),
                    failed    = bucket.Count(e => !e.Success),
                };
            })
            .ToList();

        return Ok(new
        {
            org_count    = await db.Organisations.CountAsync(),
            active_users = await db.Users.CountAsync(u => u.Active),
            project_count = await db.Projects.CountAsync(),
            logins_by_hour = loginsByHour,
        });
    }

    [HttpGet("hydra/clients")]
    public async Task<IActionResult> ListHydraClients()
    {
var clients = await hydra.ListOAuth2ClientsAsync();
        return Ok(clients);
    }

    // "sa_" marks a service-account client and "client_" a project client; both prefixes carry
    // authorisation meaning elsewhere (IntrospectionController.IsServiceAccountCaller, the
    // project lookup by HydraClientId), so a hand-made client must never claim one.
    private static readonly string[] ReservedClientIdPrefixes = [Roles.ServiceAccountClientPrefix, "client_"];

    private static bool IsValidClientId(string id) =>
        id.Length is > 0 and <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && !ReservedClientIdPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

    [HttpPost("hydra/clients")]
    public async Task<IActionResult> CreateHydraClient([FromBody] CreateHydraClientRequest body)
    {
        var payload = new Dictionary<string, object?>
        {
            ["client_name"] = body.ClientName,
            ["grant_types"] = body.GrantTypes,
            ["redirect_uris"] = body.RedirectUris,
            ["post_logout_redirect_uris"] = body.PostLogoutRedirectUris ?? [],
            ["allowed_cors_origins"] = ClientOriginsService.CorsOriginsFor(body.RedirectUris, body.PostLogoutRedirectUris),
            ["scope"] = body.Scope ?? "openid profile offline_access",
            ["token_endpoint_auth_method"] = body.GrantTypes.Contains("client_credentials") ? "private_key_jwt" : "none",
        };

        // Without an explicit id Hydra mints a random UUID, so every integrator has to capture
        // it per environment. This endpoint is SuperAdmin-only, so letting the caller pin a
        // stable id costs nothing — but it must not smuggle odd bytes into URLs and logs
        // (allowlist), nor silently hijack an existing client (conflict check).
        if (body.ClientId is not null)
        {
            if (!IsValidClientId(body.ClientId))
                return BadRequest(new { error = "invalid_client_id" });
            if (await hydra.GetOAuth2ClientAsync(body.ClientId) is not null)
                return Conflict(new { error = "client_id_taken" });
            payload["client_id"] = body.ClientId;
        }

        var client = await hydra.CreateOAuth2ClientAsync(payload);
        await audit.RecordAsync(null, null, GetActorId(), "oauth2_client.created", "oauth2_client", body.ClientName);
        return Ok(client);
    }

    [HttpGet("hydra/clients/{id}")]
    public async Task<IActionResult> GetHydraClient(string id)
    {
var client = await hydra.GetOAuth2ClientAsync(id);
        if (client == null) return NotFound();
        return Ok(client);
    }

    [HttpDelete("hydra/clients/{id}")]
    public async Task<IActionResult> DeleteHydraClient(string id)
    {
await hydra.DeleteOAuth2ClientAsync(id);
        await audit.RecordAsync(null, null, GetActorId(), "oauth2_client.deleted", "oauth2_client", id);
        return NoContent();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/export/users")]
    public async Task<IActionResult> AdminExportUsers(Guid id, [FromQuery] string format = "csv")
    {
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var rateLimitKey = $"export_rl:{GetActorId()}:admin:users:{id}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(id, null, GetActorId(), "export.users", AuditOrg, id.ToString(),
            new Dictionary<string, object> { ["format"] = format });

        var userListIds = await db.UserLists.Where(ul => ul.OrgId == id).Select(ul => ul.Id).ToListAsync();
        var users = db.Users
            .Where(u => userListIds.Contains(u.UserListId))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Email, u.Username, u.DisplayName, u.Phone, u.Active, u.EmailVerified, u.TotpEnabled, u.UserListId, u.LastLoginAt, u.CreatedAt });

        if (format == "json")
        {
            var data = await users.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=users-org-{id}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=users-org-{id}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,email,username,display_name,phone,active,email_verified,totp_enabled,user_list_id,last_login_at,created_at\n");
        await foreach (var u in users.AsAsyncEnumerable())
            await Response.WriteAsync($"{u.Id},{AdminCsvEscape(u.Email)},{AdminCsvEscape(u.Username)},{AdminCsvEscape(u.DisplayName)},{AdminCsvEscape(u.Phone)},{u.Active},{u.EmailVerified},{u.TotpEnabled},{u.UserListId},{u.LastLoginAt:O},{u.CreatedAt:O}\n");
        return Empty;
    }

    /// <summary>
    /// System-wide audit-log export (all organisations, plus system-level entries with no org).
    /// The admin console offers this on its global audit-log page; no route existed for it, so
    /// the download button 404'd.
    /// </summary>
    [HttpGet("audit-log/export")]
    public async Task<IActionResult> AdminExportAllAuditLog(
        [FromQuery] string format = "csv",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var rateLimitKey = $"export_rl:{GetActorId()}:admin:auditlog:all";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(null, null, GetActorId(), "export.audit_log", "system", "all",
            new Dictionary<string, object> { ["format"] = format, ["scope"] = "system" });

        var query = db.AuditLogs
            .Where(l => from == null || l.CreatedAt >= from)
            .Where(l => to == null || l.CreatedAt <= to)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt });

        if (format == "json")
        {
            var data = await query.ToListAsync();
            Response.Headers.ContentDisposition = "attachment; filename=audit-log-system.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = "attachment; filename=audit-log-system.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,action,org_id,project_id,actor_id,target_type,target_id,ip_address,created_at\n");
        await foreach (var l in query.AsAsyncEnumerable())
            await Response.WriteAsync($"{l.Id},{AdminCsvEscape(l.Action)},{l.OrgId},{l.ProjectId},{l.ActorId},{AdminCsvEscape(l.TargetType)},{AdminCsvEscape(l.TargetId)},{AdminCsvEscape(l.IpAddress)},{l.CreatedAt:O}\n");
        return Empty;
    }

    [HttpGet("organizations/{id}/export/audit-log")]
    public async Task<IActionResult> AdminExportAuditLog(
        Guid id,
        [FromQuery] string format = "csv",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var rateLimitKey = $"export_rl:{GetActorId()}:admin:auditlog:{id}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(id, null, GetActorId(), "export.audit_log", AuditOrg, id.ToString(),
            new Dictionary<string, object> { ["format"] = format, ["from"] = from?.ToString("O") ?? "", ["to"] = to?.ToString("O") ?? "" });

        var query = db.AuditLogs
            .Where(l => l.OrgId == id)
            .Where(l => from == null || l.CreatedAt >= from)
            .Where(l => to == null || l.CreatedAt <= to)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { l.Id, l.Action, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt });

        if (format == "json")
        {
            var data = await query.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{id}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{id}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,action,project_id,actor_id,target_type,target_id,ip_address,created_at\n");
        await foreach (var l in query.AsAsyncEnumerable())
            await Response.WriteAsync($"{l.Id},{AdminCsvEscape(l.Action)},{l.ProjectId},{l.ActorId},{AdminCsvEscape(l.TargetType)},{AdminCsvEscape(l.TargetId)},{AdminCsvEscape(l.IpAddress)},{l.CreatedAt:O}\n");
        return Empty;
    }

    private static string AdminCsvEscape(string? value) => CsvWriter.Escape(value);

    // ── SAML IdP management (admin) ───────────────────────────────────────────

    [HttpGet("projects/{id}/saml-providers")]
    public async Task<IActionResult> AdminListSamlProviders(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return await ProjectOperations.ListSamlProvidersAsync(db, id);
    }

    [HttpPost("projects/{id}/saml-providers")]
    public async Task<IActionResult> AdminCreateSamlProvider(Guid id, [FromBody] SamlProviderInput req)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return await SamlProviderOperations.CreateAsync(db, audit, GetActorId(), project, req);
    }

    [HttpPatch("projects/{projectId}/saml-providers/{providerId}")]
    public async Task<IActionResult> AdminUpdateSamlProvider(Guid projectId, Guid providerId, [FromBody] SamlProviderInput req)
    {
        var provider = await SamlProviderOperations.FindAsync(db, projectId, providerId);
        if (provider == null) return NotFound();
        return await SamlProviderOperations.UpdateAsync(db, audit, GetActorId(), provider, req);
    }

    [HttpDelete("projects/{projectId}/saml-providers/{providerId}")]
    public async Task<IActionResult> AdminDeleteSamlProvider(Guid projectId, Guid providerId)
    {
        var provider = await SamlProviderOperations.FindAsync(db, projectId, providerId);
        if (provider == null) return NotFound();
        return await SamlProviderOperations.DeleteAsync(db, audit, GetActorId(), provider);
    }

}

// ── Request records ───────────────────────────────────────────────────────────
/// <summary>
/// Nullable on purpose, and not because either field is optional. Declared non-nullable, a body
/// without <c>slug</c> binds to null anyway and the refusal comes from the NOT NULL constraint as a
/// 500. Admitting the null here is what lets <c>CreateOrg</c> answer <c>slug_required</c> — the
/// caller relays a 4xx and translates everything else to 502, so the difference is whether its
/// operator reads "that name is taken" or "service unavailable".
/// </summary>
public record CreateOrgRequest(string? Name, string? Slug);
public record UpdateOrgRequest(string? Name, int? AuditRetentionDays);
public record AdminCreateUserRequest(string Email, string? Password, string? Username, bool? EmailVerified);
public record AssignOrgAdminRequest([property: System.Text.Json.Serialization.JsonRequired] Guid UserId, string Role, Guid? ScopeId);
public record AdminCreateUserListRequest(string Name, [property: System.Text.Json.Serialization.JsonRequired] Guid OrgId);
public record AdminCreateProjectRequest(string Name, string Slug, bool? RequireRoleToLogin, string[]? RedirectUris,
    string[]? PostLogoutRedirectUris = null);
public record AdminAssignUserListRequest([property: System.Text.Json.Serialization.JsonRequired] Guid UserListId);
public record AdminCreateRoleRequest(string Name, string? Description, int? Rank);
public record CreateHydraClientRequest(string ClientName, string[] GrantTypes, string[] RedirectUris, string? Scope,
    string? ClientId = null, string[]? PostLogoutRedirectUris = null);
public record AdminUpsertSmtpRequest(string Host, [property: System.Text.Json.Serialization.JsonRequired] int Port, [property: System.Text.Json.Serialization.JsonRequired] bool StartTls, string? Username, string? Password, string FromAddress, string FromName);
