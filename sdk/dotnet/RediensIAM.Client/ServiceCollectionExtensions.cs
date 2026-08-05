using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RediensIAM.Client;

public static class RediensIamServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RediensIamClient"/>.
    ///
    /// <code>
    /// builder.Services.AddRediensIam(o =>
    /// {
    ///     o.BaseUrl             = "https://auth.example.com";
    ///     o.ServiceAccountToken = builder.Configuration["RediensIAM:Token"]!;
    ///     // The tenant this service serves. Required — see RediensIamOptions.ProjectId.
    ///     o.ProjectId           = builder.Configuration["RediensIAM:ProjectId"]!;
    /// });
    /// </code>
    /// </summary>
    /// <exception cref="ArgumentException">The configured options are missing or unusable.</exception>
    public static IServiceCollection AddRediensIam(
        this IServiceCollection services, Action<RediensIamOptions> configure)
    {
        var options = new RediensIamOptions();
        configure(options);

        // Same checks the client itself runs, done here so the failure lands at startup with the
        // registration in the stack trace rather than at the first resolve.
        options.Validated();

        services.AddSingleton(options);
        services.AddMemoryCache();
        services.AddHttpClient<RediensIamClient>(client =>
        {
            // The trailing separator matters: a relative request URI resolves against the last path
            // segment, so a base of "https://host/iam" would send "introspect" to
            // "https://host/introspect" — the segment is dropped, silently, and the call 404s
            // somewhere the caller never configured.
            //
            // UriBuilder rather than string surgery: it parses the URL, so a base carrying a query
            // or a fragment gets the separator in the path where it belongs instead of appended to
            // the end of the whole string.
            var baseUri = new UriBuilder(options.BaseUrl);
            if (!baseUri.Path.EndsWith('/')) baseUri.Path += '/';
            client.BaseAddress = baseUri.Uri;
            client.Timeout     = options.Timeout;
        });

        return services;
    }

    /// <summary>
    /// Adds a "RediensIAM" authentication scheme that validates the incoming bearer token by
    /// introspection and turns the answer into a <see cref="ClaimsPrincipal"/>. Use it when the
    /// service wants ordinary <c>[Authorize]</c> semantics rather than calling the client itself.
    ///
    /// <code>
    /// builder.Services.AddAuthentication(RediensIamDefaults.Scheme).AddRediensIam();
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddRediensIam(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, RediensIamAuthenticationHandler>(
            RediensIamDefaults.Scheme, _ => { });
}

public static class RediensIamDefaults
{
    public const string Scheme = "RediensIAM";

    /// <summary>Claim carrying the tenant. Present on every authenticated principal that has one.</summary>
    public const string OrgIdClaim = "org_id";

    public const string ProjectIdClaim = "project_id";
}

/// <summary>
/// Validates the request's bearer token through RediensIAM on every request.
///
/// Deliberately not a JWT-signature handler: a valid signature only proves the token was issued,
/// not that it is still honoured. Roles revoked, accounts disabled and organisations suspended
/// after issuance are invisible to signature checks.
/// </summary>
public sealed class RediensIamAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    RediensIamClient client) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();

        TokenInfo info;
        try
        {
            info = await client.IntrospectAsync(token, Context.RequestAborted);
        }
        catch (Exception ex)
        {
            // Fail the request rather than letting it through unauthenticated: an IAM outage
            // must not become an authorisation bypass.
            return AuthenticateResult.Fail(ex);
        }

        if (!info.Active) return AuthenticateResult.Fail("Token is not active.");

        var claims = new List<Claim>();
        if (info.UserId is { Length: > 0 }) claims.Add(new Claim(ClaimTypes.NameIdentifier, info.UserId));
        if (info.OrgId is { Length: > 0 }) claims.Add(new Claim(RediensIamDefaults.OrgIdClaim, info.OrgId));
        if (info.ProjectId is { Length: > 0 }) claims.Add(new Claim(RediensIamDefaults.ProjectIdClaim, info.ProjectId));
        claims.AddRange(info.Roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity  = new ClaimsIdentity(claims, RediensIamDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, RediensIamDefaults.Scheme));
    }
}
