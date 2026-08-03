using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Guards the one project setting whose removal is silent: <c>require_mfa</c>.
///
/// Turning it <b>on</b> is opt-in and stays opt-in — that default was set deliberately and is not
/// this guard's business. Turning it <b>off</b> is the dangerous direction: every user who
/// enrolled a factor because the project demanded one keeps that factor in the database, but it
/// stops gating their logins. A stolen password is enough again, and nothing tells anybody. The
/// setting reads exactly the same afterwards whether the project ever had enrolled users or not.
///
/// So the first attempt is refused with <b>409</b> and the number of users it would affect, and a
/// second attempt carrying <c>confirm_mfa_downgrade: true</c> proceeds and writes an audit row.
/// The confirmation is deliberately not a header or a query flag: it travels in the same body as
/// the change it authorises, so it cannot be replayed onto a different request.
///
/// Three controllers reach this setting (<c>/admin</c>+<c>/api/manage</c>, <c>/org</c>,
/// <c>/project</c>). The guard lives here, once, rather than in each of them — a per-caller copy
/// is how the third path ends up without one.
/// </summary>
internal static class MfaDowngradeGuard
{
    internal const string AuditAction = "project.mfa_requirement_removed";
    internal const string ConfirmationField = "confirm_mfa_downgrade";

    /// <summary>
    /// Returns the response to send back, or null when the update may proceed. Call it
    /// <b>before</b> applying <c>require_mfa</c> to the entity — it reads the stored value to
    /// decide whether this is a downgrade.
    /// </summary>
    internal static async Task<IActionResult?> CheckAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId,
        Project project, bool? requireMfa, bool? confirmed)
    {
        if (!project.RequireMfa || requireMfa != false) return null;

        var enrolled = await EnrolledUserCountAsync(db, project.AssignedUserListId);
        if (enrolled == 0) return null;

        if (confirmed != true)
            return new ConflictObjectResult(new
            {
                error               = "mfa_downgrade_requires_confirmation",
                enrolled_user_count = enrolled,
                consequence         = "Disabling require_mfa stops enrolled second factors from gating logins for "
                                    + $"{enrolled} user(s) in this project. Their factors are not deleted, but a "
                                    + "stolen password alone becomes sufficient to sign in. Users are not notified.",
                confirm_with        = ConfirmationField,
            });

        await audit.RecordAsync(project.OrgId, project.Id, actorId, AuditAction, "project", project.Id.ToString(),
            new Dictionary<string, object> { ["enrolled_user_count"] = enrolled });
        return null;
    }

    /// <summary>
    /// Users of the project's assigned list who currently hold a factor. Same three factor kinds
    /// <c>AccountController.HasAnyFactorAsync</c> counts — TOTP, a verified phone, or a WebAuthn
    /// credential — so the number quoted in the 409 is the number of people actually protected.
    /// A project with no assigned user list has no such users and no downgrade to warn about.
    /// </summary>
    private static async Task<int> EnrolledUserCountAsync(RediensIamDbContext db, Guid? userListId)
    {
        if (userListId is null) return 0;
        return await db.Users.CountAsync(u =>
            u.UserListId == userListId
            && (u.TotpEnabled || u.PhoneVerified
                || db.WebAuthnCredentials.Any(c => c.UserId == u.Id)));
    }
}
