using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RediensIAM.Config;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Filters;

/// <summary>
/// Restricts an action (or entire controller) to callers whose management level
/// is at least <paramref name="minimum"/>.
/// Levels: SuperAdmin=1, OrgAdmin=2, ProjectAdmin=3  (lower = more privileged).
///
/// The level is read from the token AND re-verified against Keto on every request
/// (see <see cref="LiveAuthorizationService"/>). Authorising from <c>ext.roles</c> alone left
/// a revoked role effective until the token expired.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public class RequireManagementLevelAttribute(ManagementLevel minimum) : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var claims = context.HttpContext.GetClaims();
        if (claims is null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "unauthorized" });
            return;
        }

        var level = claims.GetManagementLevel();
        if (level > minimum)
        {
            context.Result = new ObjectResult(new { error = "forbidden" }) { StatusCode = 403 };
            return;
        }

        var live = context.HttpContext.RequestServices.GetRequiredService<LiveAuthorizationService>();
        if (!await live.IsStillGrantedAsync(claims, level))
        {
            context.Result = new ObjectResult(new { error = "forbidden", detail = "role_no_longer_granted" })
            {
                StatusCode = 403,
            };
            return;
        }

        await next();
    }
}
