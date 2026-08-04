using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

internal static class UserHelpers
{
    /// <summary>
    /// The absolute password floor for the admin-driven create/update paths, which have no
    /// project to read a policy from and so ran no length check at all. An account seeded below
    /// the floor keeps that password indefinitely — nothing re-evaluates it after the write.
    /// A null or empty password is an invite: no hash is written, so there is nothing to check.
    /// </summary>
    internal static BadRequestObjectResult? PasswordFloorError(string? password) =>
        string.IsNullOrEmpty(password) || password.Length >= PasswordPolicyService.AbsoluteMinimumLength
            ? null
            : new BadRequestObjectResult(new
            {
                error      = "password_too_short",
                min_length = PasswordPolicyService.AbsoluteMinimumLength,
            });

    /// <summary>
    /// Applies the update to <paramref name="user"/>. Reports what the caller must follow up on:
    /// a password rotation and a deactivation both invalidate every session already issued.
    ///
    /// <c>Active</c> is consulted at login only, so before this a deactivated account kept full
    /// API access at every resource server until its token expired.
    /// </summary>
    internal static (bool PasswordChanged, bool Deactivated) ApplyUpdate(User user, UpdateUserRequest body, PasswordService passwords)
    {
        if (body.Email != null)          ApplyEmail(user, body.Email);
        if (body.Username != null)       user.Username    = body.Username;
        if (body.DisplayName != null)    user.DisplayName = body.DisplayName == "" ? null : body.DisplayName;
        if (body.Phone != null)          user.Phone       = body.Phone == "" ? null : body.Phone;
        if (body.Active.HasValue)        ApplyActive(user, body.Active.Value);
        if (body.EmailVerified.HasValue) ApplyEmailVerified(user, body.EmailVerified.Value);
        if (body.ClearLock == true)    { user.LockedUntil = null; user.FailedLoginCount = 0; }
        var passwordChanged = !string.IsNullOrEmpty(body.NewPassword);
        if (passwordChanged) user.PasswordHash = passwords.Hash(body.NewPassword!);
        return (passwordChanged, body.Active == false);
    }

    private static void ApplyEmail(User user, string email)
    {
        user.Email = email.ToLowerInvariant();
        user.EmailVerified = false;
        user.EmailVerifiedAt = null;
    }

    private static void ApplyActive(User user, bool active)
    {
        user.Active = active;
        user.DisabledAt = active ? null : DateTimeOffset.UtcNow;
    }

    private static void ApplyEmailVerified(User user, bool verified)
    {
        user.EmailVerified = verified;
        user.EmailVerifiedAt = verified ? DateTimeOffset.UtcNow : null;
    }

    internal static async Task<string> GenerateDiscriminatorAsync(RediensIamDbContext db, Guid userListId, string username)
    {
        var existing = await db.Users
            .Where(u => u.UserListId == userListId && u.Username == username)
            .Select(u => u.Discriminator)
            .ToListAsync();
        var max = existing.Count > 0
            ? existing.Select(d => int.TryParse(d, out var n) ? n : 0).Max()
            : 999;
        var next = max + 1;
        if (next > 9999) throw new InvalidOperationException("discriminator_space_exhausted");
        return next.ToString("D4");
    }

    /// <summary>
    /// Applies an administrator's changes to a user, revokes what the change invalidates, and
    /// records it. Both scopes call this: the organisation route and the system route differed
    /// only in which organisation they attributed the audit entry to — the caller's on one side,
    /// the user's own on the other. They were equal only because the organisation route's lookup
    /// filters on that same id, which is a coincidence rather than a rule. The user's own is the
    /// one that is always right: an entry on another chain is one the tenant cannot read.
    /// </summary>
    internal static async Task<IActionResult> ApplyAdminUpdateAsync(
        RediensIamDbContext db, HydraService hydra, AuditLogService audit, PasswordService passwords,
        Guid actorId, User user, UpdateUserRequest body)
    {
        if (PasswordFloorError(body.NewPassword) is { } floorErr) return floorErr;

        var (passwordChanged, deactivated) = ApplyUpdate(user, body, passwords);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (passwordChanged || deactivated)
            await hydra.RevokeSessionsAsync(HydraSubject(user));

        var orgId = user.UserList.OrgId;
        if (passwordChanged)
            await audit.RecordAsync(orgId, null, actorId, "user.password_reset_by_admin", "user", user.Id.ToString());
        if (deactivated)
            await audit.RecordAsync(orgId, null, actorId, "user.deactivated", "user", user.Id.ToString());
        await audit.RecordAsync(orgId, null, actorId, "user.updated", "user", user.Id.ToString());

        return new OkObjectResult(new
        {
            user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName,
            user.Phone, user.Active, user.EmailVerified, user.LockedUntil, user.FailedLoginCount,
        });
    }

    /// <summary>
    /// The subject Hydra issued this user's sessions under: <c>"&lt;org&gt;:&lt;user&gt;"</c> for a
    /// tenant user, the bare id for an administrator, who has no organisation. It is what
    /// CompleteLoginAsync mints and what ParseSubjectUserId reads back.
    ///
    /// <para>
    /// Stated once because the force-logout route built it differently — <c>OrgId?.ToString() ?? ""</c>
    /// interpolated into <c>"{org}:{id}"</c>, giving <c>":&lt;guid&gt;"</c> for an administrator.
    /// No session was ever issued under that, so Hydra answered 204 to a revocation that revoked
    /// nothing and the API reported success. The one account whose sessions most need ending on
    /// demand was the one account the button could not end.
    /// </para>
    /// </summary>
    internal static string HydraSubject(User user) =>
        user.UserList.OrgId.HasValue ? $"{user.UserList.OrgId}:{user.Id}" : user.Id.ToString();

    /// <summary>Ends every session this user holds, and records it.</summary>
    internal static async Task<IActionResult> ForceLogoutAsync(
        HydraService hydra, AuditLogService audit, Guid actorId, User user, Guid? projectId = null)
    {
        await hydra.RevokeSessionsAsync(HydraSubject(user));
        await audit.RecordAsync(user.UserList.OrgId, projectId, actorId,
            "session.revoked", "user", user.Id.ToString());
        return new OkObjectResult(new { message = "sessions_revoked" });
    }
}
