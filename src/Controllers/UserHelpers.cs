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
}
