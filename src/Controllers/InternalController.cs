using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class InternalController(RediensIamDbContext db, PatIntrospectionService patService) : ControllerBase
{
    [HttpPost("/internal/tokens/introspect")]
    public async Task<IActionResult> Introspect([FromBody] IntrospectRequest body)
    {
        var result = await patService.IntrospectAsync(body.Token);
        if (result == null) return Ok(new { active = false });
        return Ok(result);
    }

    [HttpGet("/internal/projects/{id}")]
    public async Task<IActionResult> GetProject(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return Ok(new
        {
            project.Id, project.Name, project.Slug, project.OrgId,
            project.AssignedUserListId, project.RequireRoleToLogin,
            project.Active, project.LoginTheme
        });
    }

    [HttpGet("/internal/users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();
        return Ok(new
        {
            user.Id, user.Username, user.Discriminator, user.Email,
            user.DisplayName, user.Active, user.UserListId
        });
    }

    [HttpGet("/internal/organisations/{id}/active-users")]
    public async Task<IActionResult> GetActiveUserCount(Guid id)
    {
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        var count = await db.Users
            .Where(u => u.UserList.OrgId == id && u.Active)
            .CountAsync();
        return Ok(new { org_id = id, active_user_count = count });
    }
}
