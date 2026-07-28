using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

/// <summary>Why a candidate password was refused. <see cref="Ok"/> means it passed.</summary>
public enum PasswordPolicyResult
{
    Ok,
    TooShort,
    RequiresUppercase,
    RequiresLowercase,
    RequiresDigit,
    RequiresSpecial,
    Breached,
}

/// <summary>
/// Single evaluation point for a project's password policy.
///
/// Registration, admin-driven creation, invite completion, password reset and self-service
/// password change all write a password hash — every one of them must run the same rules.
/// They previously did not: <c>AccountController.ChangePassword</c> hardcoded a minimum of 8
/// characters and skipped the breach check, so a user could downgrade below their tenant's
/// policy simply by changing their password after signing up.
/// </summary>
public sealed class PasswordPolicyService(BreachCheckService breachCheck)
{
    /// <summary>Absolute floor applied when no project context is available.</summary>
    public const int AbsoluteMinimumLength = 8;

    public async Task<(PasswordPolicyResult Result, int BreachCount)> EvaluateAsync(
        Project? project, string password)
    {
        var minLength = Math.Max(project?.MinPasswordLength ?? 0, AbsoluteMinimumLength);
        if (password.Length < minLength) return (PasswordPolicyResult.TooShort, 0);

        if (project is null) return (PasswordPolicyResult.Ok, 0);

        if (project.PasswordRequireUppercase && !password.Any(char.IsUpper))
            return (PasswordPolicyResult.RequiresUppercase, 0);
        if (project.PasswordRequireLowercase && !password.Any(char.IsLower))
            return (PasswordPolicyResult.RequiresLowercase, 0);
        if (project.PasswordRequireDigit && !password.Any(char.IsDigit))
            return (PasswordPolicyResult.RequiresDigit, 0);
        if (project.PasswordRequireSpecial && !password.Any(c => !char.IsLetterOrDigit(c)))
            return (PasswordPolicyResult.RequiresSpecial, 0);

        if (project.CheckBreachedPasswords)
        {
            var count = await breachCheck.GetBreachCountAsync(password);
            if (count > 0) return (PasswordPolicyResult.Breached, count);
        }

        return (PasswordPolicyResult.Ok, 0);
    }

    /// <summary>The minimum length actually enforced for a project, for error payloads.</summary>
    public static int EffectiveMinimumLength(Project? project) =>
        Math.Max(project?.MinPasswordLength ?? 0, AbsoluteMinimumLength);

    /// <summary>Stable error code for API responses. Matches the existing wire contract.</summary>
    public static string ErrorCode(PasswordPolicyResult result) => result switch
    {
        PasswordPolicyResult.TooShort          => "password_too_short",
        PasswordPolicyResult.RequiresUppercase => "password_requires_uppercase",
        PasswordPolicyResult.RequiresLowercase => "password_requires_lowercase",
        PasswordPolicyResult.RequiresDigit     => "password_requires_digit",
        PasswordPolicyResult.RequiresSpecial   => "password_requires_special",
        PasswordPolicyResult.Breached          => "password_breached",
        _                                      => "ok",
    };
}
