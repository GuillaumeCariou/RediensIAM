using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// User lists and the users in them, written once for both scopes.
///
/// <para>
/// Six operations, each written twice, four of which had drifted. One of the four reaches outside
/// this deployment: <c>user.created</c>, <c>user.invited</c> and <c>user.deleted</c> are
/// subscribable webhook events — <see cref="WebhookService"/> lists them and
/// <see cref="AuditLogService"/> dispatches on the action string it records. The system route
/// recorded <c>userlist.user_added</c> and <c>userlist.user_removed</c> instead, which are on no
/// subscription list, so a tenant whose integration watches for new users never heard about the
/// ones a super-admin created in their own list. The audit query missed them for the same reason.
/// </para>
///
/// <para>
/// The super-admin grant is a genuinely different event and keeps its own entry — but it is now
/// recorded <i>in addition to</i> the user event rather than instead of it.
/// </para>
/// </summary>
public static class UserListOperations
{
    /// <summary>Audit metadata key, written from four places and spelled once.</summary>
    private const string UserListIdKey = "user_list_id";

    private const string KindInvite = "invite";

    /// <summary>One shape for what a caller may say when putting a user in a list.</summary>
    public record NewUser(string Email, string? Password = null, string? Username = null, bool? EmailVerified = null);

    /// <summary>
    /// The list itself. Both scopes answer with the same fields: one carried
    /// <c>assigned_projects</c> and the other <c>org_id</c> and <c>created_at</c>, so the console
    /// rendered a different resource depending on which route it had reached.
    /// </summary>
    public static async Task<IActionResult> ReadAsync(RediensIamDbContext db, UserList list)
    {
        var assignedProjects = await db.Projects
            .Where(p => p.AssignedUserListId == list.Id)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();

        return new OkObjectResult(new
        {
            list.Id, list.Name, list.OrgId, list.Immovable, list.CreatedAt,
            org_name          = list.Organisation?.Name,
            user_count        = await db.Users.CountAsync(u => u.UserListId == list.Id),
            assigned_projects = assignedProjects,
        });
    }

    /// <summary>Loads a list with the organisation the read and the scope check both need.</summary>
    public static Task<UserList?> FindAsync(RediensIamDbContext db, Guid id) =>
        db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id);

    /// <summary>
    /// The users in a list. <c>invite_pending</c> existed on the organisation scope only, so a
    /// super-admin could not tell an invited user from an active one — which is the state they are
    /// most often asked to look into.
    /// </summary>
    public static async Task<IActionResult> ListUsersAsync(RediensIamDbContext db, Guid listId)
    {
        var users = await db.Users
            .Where(u => u.UserListId == listId)
            .OrderBy(u => u.Username).ThenBy(u => u.Discriminator)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();

        var pending = await db.EmailTokens
            .Where(t => t.Kind == KindInvite && t.UsedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow
                        && db.Users.Any(u => u.Id == t.UserId && u.UserListId == listId))
            .Select(t => t.UserId)
            .ToHashSetAsync();

        return new OkObjectResult(users.Select(u => new
        {
            u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt,
            invite_pending = pending.Contains(u.Id),
        }));
    }

    /// <summary>
    /// Creating a list was recorded on the system scope and nowhere on the organisation scope. A
    /// list decides who may sign in to every project it is assigned to; that is not a change to
    /// leave unrecorded on either route.
    /// </summary>
    public static async Task<IActionResult> CreateAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId, string name, Guid? orgId, string locationPrefix)
    {
        var list = new UserList { Name = name, OrgId = orgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(list);
        await db.SaveChangesAsync();

        await audit.RecordAsync(orgId, null, actorId, "userlist.created", "userlist", list.Id.ToString());
        return new CreatedResult($"{locationPrefix}/{list.Id}", new { list.Id, list.Name });
    }

    /// <summary>
    /// Adds a user, or invites one when no password is given.
    ///
    /// <para>
    /// The action recorded is <c>user.created</c> or <c>user.invited</c> from both scopes — the
    /// names a tenant can actually subscribe to. Membership of the immovable system list still
    /// grants deployment-wide administration and still gets its own entry beside the user one.
    /// </para>
    /// </summary>
    public static async Task<IActionResult> AddUserAsync(
        UserListDeps deps, Guid actorId, UserList list, NewUser body, string locationPrefix)
    {
        var (db, keto, audit, passwords, emailService, appConfig) = deps;

        // Without this the unique index on (UserListId, Email) surfaces as a DbUpdateException and
        // a 500, which tells the caller nothing about the one thing they can fix.
        var email = body.Email.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.UserListId == list.Id && u.Email == email))
            return new ConflictObjectResult(new { error = "email_already_exists" });

        if (UserHelpers.PasswordFloorError(body.Password) is { } floorErr) return floorErr;

        var username      = body.Username ?? body.Email.Split('@')[0];
        var discriminator = await UserHelpers.GenerateDiscriminatorAsync(db, list.Id, username);
        var isInvite      = string.IsNullOrEmpty(body.Password);
        var emailVerified = body.EmailVerified ?? false;

        var user = new User
        {
            UserListId      = list.Id,
            Username        = username,
            Discriminator   = discriminator,
            Email           = email,
            PasswordHash    = isInvite ? null : passwords.Hash(body.Password!),
            EmailVerified   = emailVerified,
            EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null,
            Active          = !isInvite,
            CreatedAt       = DateTimeOffset.UtcNow,
            UpdatedAt       = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, list.Id.ToString(), "member", $"user:{user.Id}");
        if (GrantsSuperAdmin(list))
            await keto.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");

        // No organisation filter: a project assigned to this list is in this list's organisation by
        // construction, so filtering by the caller's added nothing but a way to disagree.
        foreach (var project in await db.Projects.Where(p => p.AssignedUserListId == list.Id).ToListAsync())
            await keto.AssignDefaultRoleAsync(project, user);

        if (isInvite) await SendInviteAsync(db, emailService, appConfig, list, user);

        await audit.RecordAsync(list.OrgId, null, actorId,
            isInvite ? "user.invited" : "user.created", "user", user.Id.ToString(),
            new() { [UserListIdKey] = list.Id.ToString() });
        if (GrantsSuperAdmin(list))
            await audit.RecordAsync(null, null, actorId,
                "user.super_admin_granted", "user", user.Id.ToString(),
                new() { [UserListIdKey] = list.Id.ToString() });

        return new CreatedResult($"{locationPrefix}/{list.Id}/users/{user.Id}", new
        {
            user.Id,
            username = $"{user.Username}#{user.Discriminator}",
            user.Email,
            invite_pending = isInvite,
        });
    }

    /// <summary>Removes a user from a list, recording the event a subscriber can be subscribed to.</summary>
    public static async Task<IActionResult> RemoveUserAsync(
        RediensIamDbContext db, KetoService keto, AuditLogService audit,
        Guid actorId, UserList list, User user)
    {
        await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, list.Id.ToString(), "member", $"user:{user.Id}");
        if (GrantsSuperAdmin(list))
            await keto.DeleteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");

        var userId = user.Id;
        db.Users.Remove(user);
        await db.SaveChangesAsync();

        await audit.RecordAsync(list.OrgId, null, actorId, "user.deleted", "user", userId.ToString(),
            new() { [UserListIdKey] = list.Id.ToString() });
        if (GrantsSuperAdmin(list))
            await audit.RecordAsync(null, null, actorId,
                "user.super_admin_revoked", "user", userId.ToString(),
                new() { [UserListIdKey] = list.Id.ToString() });

        return new NoContentResult();
    }

    /// <summary>
    /// The <c>__system__</c> list: no organisation and immovable. Membership of it is
    /// <c>System:rediensiam#super_admin</c> — deployment-wide administration — which is why it gets
    /// an audit entry of its own on top of the user one.
    /// </summary>
    private static bool GrantsSuperAdmin(UserList list) => list.OrgId == null && list.Immovable;

    private static async Task SendInviteAsync(
        RediensIamDbContext db, IEmailService emailService, AppConfig appConfig, UserList list, User user)
    {
        var raw  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        db.EmailTokens.Add(new EmailToken
        {
            UserId    = user.Id,
            Kind      = KindInvite,
            TokenHash = hash,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(appConfig.InviteExpiryHours),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await emailService.SendInviteAsync(
            user.Email, appConfig.InviteUrl(raw), list.Organisation?.Name ?? "the organization", list.OrgId);
    }
}

/// <summary>
/// The collaborators <see cref="UserListOperations.AddUserAsync"/> needs, as one value.
///
/// <para>
/// Adding a user touches six services, and passing them positionally put ten arguments on one
/// call — the point where a reader checks a signature by counting commas. The six travel together
/// and never vary independently, so they are one thing; what stays positional is what actually
/// differs between the two call sites.
/// </para>
/// </summary>
public record UserListDeps(
    RediensIamDbContext Db,
    KetoService Keto,
    AuditLogService Audit,
    PasswordService Passwords,
    IEmailService EmailService,
    AppConfig AppConfig);
