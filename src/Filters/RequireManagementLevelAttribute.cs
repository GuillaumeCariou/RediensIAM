using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RediensIAM.Config;
using RediensIAM.Middleware;

namespace RediensIAM.Filters;

/// <summary>
/// Restricts an action (or entire controller) to callers whose management level
/// is at least <paramref name="minimum"/>.
/// Levels: SuperAdmin=1, OrgAdmin=2, ProjectManager=3  (lower = more privileged).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public class RequireManagementLevelAttribute(ManagementLevel minimum) : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var claims = context.HttpContext.GetClaims();
        if (claims is null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "unauthorized" });
            return;
        }
        var level = claims.GetManagementLevel();
        if (level > minimum)
            context.Result = new ObjectResult(new { error = "forbidden" }) { StatusCode = 403 };
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
