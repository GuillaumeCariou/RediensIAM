using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

/// <summary>
/// The project-level login controls, for the flows that finish with a redirect: the social-login
/// callback and the SAML ACS.
///
/// <para>
/// Both used to call <c>hydra.AcceptLoginAsync</c> directly, so <c>Project.RequireMfa</c> and
/// <c>Project.IpAllowlist</c> — enforced on the password path — applied to exactly the users who
/// did not use the identity provider the tenant had configured. A tenant that switched either
/// control on had it apply to the wrong half of its users.
/// </para>
///
/// <para>
/// It lives here rather than in either controller because that is the shape of nearly every defect
/// this codebase has had: one route doing correctly what its sibling does not. One implementation,
/// two callers.
/// </para>
/// </summary>
public static class FederatedLoginGuard
{
    /// <summary>Session key marking a pending MFA enrolment; read by the setup endpoints.</summary>
    public const string MfaSetupRequiredKey = "mfa_setup_required";

    /// <summary>Whether the caller's address satisfies the project's allowlist.</summary>
    public static bool IsIpAllowed(Project project, string? clientIp)
    {
        if (project.IpAllowlist.Length == 0) return true;
        return System.Net.IPAddress.TryParse(clientIp, out var ip)
               && project.IpAllowlist.Any(cidr => IpInRange(ip, cidr));
    }

    /// <summary>
    /// Whether this login still owes a second factor, and whether the account has none at all.
    /// </summary>
    public static async Task<(bool Required, bool NeedsEnrolment)> RequiresFactorAsync(
        RediensIamDbContext db, User user, Project project)
    {
        var hasFactor = user.TotpEnabled || user.PhoneVerified
                        || await db.WebAuthnCredentials.AnyAsync(w => w.UserId == user.Id);
        var needsEnrolment = project.RequireMfa && !hasFactor;
        return (needsEnrolment || user.TotpEnabled || user.PhoneVerified, needsEnrolment);
    }

    /// <summary>
    /// Where the browser goes to finish the factor step. Mirrors the login SPA's own navigation
    /// after <c>requires_mfa</c> / <c>requires_mfa_setup</c>, so a federated login lands on the
    /// same two screens a password login does.
    /// </summary>
    public static string MfaRedirectPath(string loginChallenge, bool needsEnrolment) =>
        needsEnrolment
            ? $"/mfa-setup?login_challenge={Uri.EscapeDataString(loginChallenge)}"
            : $"/mfa?login_challenge={Uri.EscapeDataString(loginChallenge)}";

    /// <summary>Records the pending-MFA state the factor endpoints read.</summary>
    public static void SetMfaSession(
        ISession session, Guid userId, string loginChallenge, Guid projectId, Guid orgId, bool needsEnrolment)
    {
        session.SetString("mfa_pending_user", userId.ToString());
        session.SetString("mfa_pending_challenge", loginChallenge);
        session.SetString("mfa_pending_project", projectId.ToString());
        session.SetString("mfa_pending_org", orgId.ToString());
        if (needsEnrolment) session.SetString(MfaSetupRequiredKey, "true");
    }

    /// <summary>
    /// CIDR containment, matching the password path's own check. An unparseable entry answers
    /// false, which is why both write paths validate the allowlist before storing it — an
    /// unparseable CIDR would otherwise lock a tenant out of its own project.
    /// </summary>
    private static bool IpInRange(System.Net.IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (!System.Net.IPAddress.TryParse(parts[0], out var network)) return false;
        if (parts.Length == 1) return address.Equals(network);
        if (!int.TryParse(parts[1], out var prefix)) return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length) return false;
        if (prefix < 0 || prefix > addressBytes.Length * 8) return false;

        for (var i = 0; i < addressBytes.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = prefix >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - prefix));
            if ((addressBytes[i] & mask) != (networkBytes[i] & mask)) return false;
        }
        return true;
    }
}
