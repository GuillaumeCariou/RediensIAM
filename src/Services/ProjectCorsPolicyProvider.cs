using Microsoft.AspNetCore.Cors.Infrastructure;

namespace RediensIAM.Services;

/// <summary>
/// Builds the CORS policy per request from <see cref="ClientOriginsService"/>, so that CORS and the
/// CSP header answer to the same authority instead of each carrying its own list.
///
/// <para>
/// A policy registered through <c>AddCors</c> is fixed when the process starts, which is the whole
/// defect: it could only ever name <c>App__AdminSpaOrigin</c>, so every front added afterwards had
/// to be written into a values file by hand. <see cref="ICorsPolicyProvider"/> is asked per request
/// and may await, which is what lets the answer come from the registered clients — and lets it be
/// narrowed to one project when the request names one.
/// </para>
///
/// <para>
/// The policy name is ignored on purpose. There is one question here — may this origin talk to this
/// deployment — and giving it two names would be two ways to ask it.
/// </para>
/// </summary>
public sealed class ProjectCorsPolicyProvider(ClientOriginsService origins) : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var allowed = await origins.ForRequestAsync(context);
        // WithOrigins on an empty set allows nothing, which is the right way to fail.
        return new CorsPolicyBuilder()
            .WithOrigins(allowed)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .Build();
    }
}
