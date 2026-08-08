using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;

namespace RediensIAM.Controllers;

/// <summary>
/// The console's Users page, served whole, written once for both scopes.
///
/// <para>
/// <c>/admin/users</c> and <c>/org/users</c> answer the same question over a different population,
/// and that is the ONLY difference between them: which tenant the search is confined to, and who
/// decided it. On the system scope the caller names it in <c>org_id</c>; on the organisation scope
/// it comes from the caller's TOKEN and the query parameter is never read — an org admin who could
/// name another organisation would be reading a tenant that is not theirs. Same mistake
/// <c>createUserList</c> made in the other direction, on the other side of the wire.
/// </para>
///
/// <para>
/// Everything after that decision — the filters, their refusals, the counts, the ordering, the
/// page — is stated here rather than twice. <see cref="UserListOperations"/> and
/// <see cref="ProjectOperations"/> exist for the same reason, and record what happened to the six
/// operations that were written twice: four of them had drifted.
/// </para>
///
/// <para>
/// Every filter is applied to the QUERY, before <c>Skip/Take</c>. Narrowing the fifty rows a page
/// already holds would answer "3 disabled accounts" for a deployment that has four hundred — a
/// count that is wrong in the one direction nobody checks.
/// </para>
///
/// <para>Indexes: the ordering and the address search ride <c>ix_users_email</c>; the list filter
/// rides the two <c>(UserListId, …)</c> uniques; the tenant filter rides <c>user_lists.OrgId</c>.
/// <c>signed_in</c> and <c>mfa</c> have no index of their own — they are low-selectivity predicates
/// evaluated over an already-bounded scan, and an index on a boolean would not be chosen
/// anyway.</para>
/// </summary>
public static class UserSearch
{
    public static readonly string[] StatusFilters   = ["active", "disabled", "locked"];
    public static readonly string[] MfaFilters      = ["yes", "no"];
    public static readonly string[] SignedInFilters = ["7d", "30d", "never"];

    /// <summary>The shortest query the search box will send. Below it the server refuses.</summary>
    public const int MinQueryLength = 3;

    /// <summary>
    /// What the page asked for.
    ///
    /// <para>
    /// <c>OrgId</c> est le locataire auquel la recherche est confinée, <c>null</c> pour tout le
    /// déploiement. ⚠ Sur la surface d'organisation, il vient du JETON de l'appelant et de nulle
    /// part ailleurs : c'est ce qui empêche un administrateur d'organisation d'en lire une autre.
    /// </para>
    /// </summary>
    public readonly record struct Criteria(
        string? Q, Guid? OrgId, Guid? UserListId, string? Status, string? Mfa, string? SignedIn,
        int Page, int PageSize);

    /// <summary>
    /// <c>q</c> matches what the search box promises: the address, the username, the display name,
    /// or — when the text parses as one — the account id itself. It used to promise all four and
    /// match the first two.
    ///
    /// <para>An unknown value for <c>status</c>, <c>mfa</c> or <c>signed_in</c> is refused rather
    /// than ignored: a filter silently dropped returns the unfiltered population under the label of
    /// a filtered one.</para>
    /// </summary>
    public static async Task<IActionResult> RunAsync(RediensIamDbContext db, Criteria c)
    {
        if (!string.IsNullOrEmpty(c.Q) && c.Q.Length < MinQueryLength)
            return new BadRequestObjectResult(new { error = "query_too_short", min_length = MinQueryLength });
        if (Rejected(c.Status,   StatusFilters,   "status",    out var bad)) return bad;
        if (Rejected(c.Mfa,      MfaFilters,      "mfa",       out bad))     return bad;
        if (Rejected(c.SignedIn, SignedInFilters, "signed_in", out bad))     return bad;

        // Clamped like every other paged endpoint here: page=0 produced Skip(-50), which Postgres
        // rejects outright, and an unbounded pageSize serialised the whole table.
        var page     = Math.Max(1, c.Page);
        var pageSize = Math.Clamp(c.PageSize, 1, 200);

        var now   = DateTimeOffset.UtcNow;
        var query = db.Users.AsQueryable();

        if (!string.IsNullOrEmpty(c.Q))
        {
            var q    = c.Q;
            var asId = Guid.TryParse(q, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(u =>
                u.Email.Contains(q) || u.Username.Contains(q) ||
                (u.DisplayName != null && u.DisplayName.Contains(q)) ||
                (asId != null && u.Id == asId));
        }
        if (c.OrgId is { } org)       query = query.Where(u => u.UserList.OrgId == org);
        if (c.UserListId is { } list) query = query.Where(u => u.UserListId == list);

        query = c.Status switch
        {
            // "Active" is the state an operator means by it: enabled AND not sitting out a lockout.
            "active"   => query.Where(u => u.Active && (u.LockedUntil == null || u.LockedUntil <= now)),
            "disabled" => query.Where(u => !u.Active),
            "locked"   => query.Where(u => u.LockedUntil != null && u.LockedUntil > now),
            _          => query,
        };
        // A second factor is the two flags on the account. Backup codes are deliberately not one:
        // they exist only alongside TOTP, so counting them would report a factor nobody enrolled.
        query = c.Mfa switch
        {
            "yes" => query.Where(u => u.TotpEnabled || u.WebAuthnEnabled),
            "no"  => query.Where(u => !u.TotpEnabled && !u.WebAuthnEnabled),
            _     => query,
        };
        query = c.SignedIn switch
        {
            "7d"    => query.Where(u => u.LastLoginAt != null && u.LastLoginAt >= now.AddDays(-7)),
            "30d"   => query.Where(u => u.LastLoginAt != null && u.LastLoginAt >= now.AddDays(-30)),
            "never" => query.Where(u => u.LastLoginAt == null),
            _       => query,
        };

        // The three numbers the page's "Showing" banner names, over the FILTERED set — which is
        // what makes it read as "3 matches in 3 lists" once something is typed, and as the
        // deployment's own totals when nothing is. __system__ has no organisation, so it is a list
        // without a tenant and must not inflate the tenant count.
        //
        // `tenants` is computed the same way on both scopes rather than hard-coded to 1 on the
        // organisation one. Confined to a single tenant it can only come out 0 or 1, which is the
        // truth either way — and a branch here would be a second thing to keep in agreement.
        var total   = await query.CountAsync();
        var lists   = await query.Select(u => u.UserListId).Distinct().CountAsync();
        var tenants = await query.Where(u => u.UserList.OrgId != null)
                                 .Select(u => u.UserList.OrgId).Distinct().CountAsync();

        var users = await query
            // Email alone is unique per LIST, not per deployment: two lists holding the same address
            // gave the sort no total order, and a row could appear on both page 1 and page 2.
            .OrderBy(u => u.Email).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            // display_name, org_name, user_list_name and locked_until are what the console's user
            // table renders. Without locked_until its isLocked() was permanently false, so the
            // Locked badge never appeared and the Unlock action it gates was unreachable.
            .Select(u => new
            {
                u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active,
                u.UserListId, u.LastLoginAt, u.LockedUntil, u.TotpEnabled, u.WebAuthnEnabled,
                OrgId        = u.UserList.OrgId,
                OrgName      = u.UserList.Organisation != null ? u.UserList.Organisation.Name : null,
                UserListName = u.UserList.Name,
                // Bounded by the page, and read from the grant table rather than from a token: a
                // project may flag several roles as default, so a single "the default one" would
                // be a guess. Named per project because the emitted claim is qualified too.
                Roles = db.UserProjectRoles
                    .Where(r => r.UserId == u.Id)
                    .Select(r => new { r.RoleId, r.Role.Name, r.Role.ProjectId, ProjectName = r.Role.Project.Name })
                    .ToList(),
            })
            .ToListAsync();

        return new OkObjectResult(new
        {
            Users = users, Total = total, Lists = lists, Tenants = tenants, Page = page, PageSize = pageSize,
        });
    }

    /// <summary>A filter value the query cannot mean. Empty passes; anything else must be known.</summary>
    private static bool Rejected(string? value, string[] allowed, string name, out IActionResult refusal)
    {
        if (string.IsNullOrEmpty(value) || allowed.Contains(value))
        {
            refusal = null!;
            return false;
        }
        refusal = new BadRequestObjectResult(new { error = "invalid_filter", filter = name, allowed });
        return true;
    }
}
