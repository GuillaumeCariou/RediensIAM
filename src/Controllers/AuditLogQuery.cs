using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Controllers;

/// <summary>
/// One page of the audit log. The three scopes differ only in which rows they may see.
///
/// <para>
/// Organisation, project and system each had their own copy — same clamping, same ordering, same
/// projection, three times. Nothing had drifted yet, which is the point of collapsing them now:
/// the projection is what an operator reads, and three places to add a column is three chances to
/// add it to two.
/// </para>
/// </summary>
public static class AuditLogQuery
{
    /// <summary>
    /// A page, newest first. <paramref name="where"/> is the scope — null means the whole
    /// deployment, which only the system routes may ask for.
    /// </summary>
    public static async Task<IActionResult> PageAsync(
        RediensIamDbContext db, Expression<Func<AuditLog, bool>>? where, int limit, int offset)
    {
        // Clamped, not validated: a page size is a display choice, and refusing 500 is less useful
        // than answering with 200. The floor matters more — limit 0 returns nothing forever.
        limit  = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var rows = db.AuditLogs.AsQueryable();
        if (where != null) rows = rows.Where(where);

        var logs = await rows
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new
            {
                l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId,
                l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata,
            })
            .ToListAsync();
        return new OkObjectResult(logs);
    }
}
