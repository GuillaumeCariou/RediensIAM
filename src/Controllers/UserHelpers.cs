using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

internal static class UserHelpers
{
    internal static void ApplyUpdate(User user, UpdateUserRequest body, PasswordService passwords)
    {
        if (body.Email != null) { user.Email = body.Email.ToLowerInvariant(); user.EmailVerified = false; user.EmailVerifiedAt = null; }
        if (body.Username != null) user.Username = body.Username;
        if (body.DisplayName != null) user.DisplayName = body.DisplayName == "" ? null : body.DisplayName;
        if (body.Phone != null) user.Phone = body.Phone == "" ? null : body.Phone;
        if (body.Active.HasValue) { user.Active = body.Active.Value; user.DisabledAt = body.Active.Value ? null : DateTimeOffset.UtcNow; }
        if (body.EmailVerified.HasValue) { user.EmailVerified = body.EmailVerified.Value; user.EmailVerifiedAt = body.EmailVerified.Value ? DateTimeOffset.UtcNow : null; }
        if (body.ClearLock == true) { user.LockedUntil = null; user.FailedLoginCount = 0; }
        if (!string.IsNullOrEmpty(body.NewPassword)) user.PasswordHash = passwords.Hash(body.NewPassword);
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
