using RediensIAM.Config;

namespace RediensIAM.Services;

/// <summary>
/// A management level that has been re-verified against the authorisation store for the request
/// that carries it — as opposed to a <see cref="ManagementLevel"/>, which is just a value and says
/// nothing about where it came from.
///
/// <para>
/// The defect this type exists to make unrepresentable: <c>ext.roles</c> is a snapshot taken when
/// the token was minted, and reading it produced the <i>same</i> <see cref="ManagementLevel"/> type
/// that <see cref="LiveAuthorizationService"/> produces after a live check. A controller that
/// forgot the live check therefore compiled, read correctly at review, and was wrong (R-22, P-01).
/// </para>
///
/// <para>
/// The constructor is <c>private</c> and the only code that can reach it is
/// <see cref="ResolveAsync"/>, which is lexically inside this type. Nothing else in the assembly —
/// controller, filter or service — can mint one; it can only be handed one that was already
/// verified. <c>HttpContext.GetGrantedLevel()</c> is how a request reads it.
/// </para>
/// </summary>
public readonly struct GrantedLevel : IEquatable<GrantedLevel>
{
    /// <summary>The verified level. Never <see cref="ManagementLevel.None"/>.</summary>
    public ManagementLevel Value { get; }

    private GrantedLevel(ManagementLevel value) => Value = value;

    /// <summary>
    /// True when the verified level is at least as privileged as <paramref name="required"/>.
    /// Levels run SuperAdmin=1 &lt; OrgAdmin=2 &lt; ProjectAdmin=3, so "at least" is "&lt;=".
    /// </summary>
    public bool IsAtLeast(ManagementLevel required) => Value <= required;

    /// <summary>
    /// The sole producer of a <see cref="GrantedLevel"/>: reads the level the token claims, then
    /// asks <see cref="LiveAuthorizationService"/> whether it is still granted. Returns
    /// <c>null</c> — never a level — when the claim is absent or no longer real.
    /// </summary>
    internal static async Task<GrantedLevel?> ResolveAsync(TokenClaims claims, LiveAuthorizationService live)
    {
        var claimed = ClaimedLevel(claims);
        if (claimed == ManagementLevel.None) return null;
        return await live.IsStillGrantedAsync(claims, claimed) ? new GrantedLevel(claimed) : null;
    }

    /// <summary>
    /// The level the token <i>asserts</i>, with no verification whatsoever. Deliberately named for
    /// what it is. Legitimate uses are the two questions that are genuinely about the token rather
    /// than about what its bearer may do: the gate in <see cref="Filters.RequireManagementLevelAttribute"/>
    /// that decides which level to re-check, and the introspection response strip, which is asked
    /// about somebody else's token.
    /// </summary>
    internal static ManagementLevel ClaimedLevel(TokenClaims claims)
    {
        if (claims.Roles.Contains(Roles.SuperAdmin))   return ManagementLevel.SuperAdmin;
        if (claims.Roles.Contains(Roles.OrgAdmin))     return ManagementLevel.OrgAdmin;
        if (claims.Roles.Contains(Roles.ProjectAdmin)) return ManagementLevel.ProjectAdmin;
        return ManagementLevel.None;
    }

    public bool Equals(GrantedLevel other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is GrantedLevel other && Equals(other);
    public override int GetHashCode() => (int)Value;
    public override string ToString() => Value.ToString();
    public static bool operator ==(GrantedLevel left, GrantedLevel right) => left.Equals(right);
    public static bool operator !=(GrantedLevel left, GrantedLevel right) => !left.Equals(right);
}
