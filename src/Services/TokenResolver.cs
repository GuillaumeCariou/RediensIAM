using RediensIAM.Config;
using RediensIAM.Models;

namespace RediensIAM.Services;

/// <summary>
/// Turns a bearer credential into the claims it carries, whatever shape it is.
///
/// <para>
/// RediensIAM issues three: an OAuth2 access token minted by Hydra, a service-account personal
/// access token, and a delegated impersonation credential. Deciding which is one question, and it
/// used to be answered inside <c>IntrospectionController</c> — which is what dragged four
/// collaborators into that controller purely to answer it.
/// </para>
///
/// <para>
/// The order is not arbitrary. The two opaque shapes are recognised by prefix, in constant time,
/// <b>before</b> anything reaches <see cref="HydraService.ValidateJwtAsync"/> — handing an opaque
/// credential to Hydra would cost a network round trip to be told what the prefix already said.
/// </para>
/// </summary>
public class TokenResolver(
    PatService pats,
    HydraService hydra,
    ImpersonationService impersonation,
    AppConfig appConfig)
{
    public async Task<TokenClaims?> ResolveAsync(string token)
    {
        if (token.StartsWith(ImpersonationService.TokenPrefix, StringComparison.Ordinal))
        {
            var session = await impersonation.ResolveAsync(token);
            return session is null ? null : ImpersonationService.ClaimsFor(session);
        }

        if (token.StartsWith(appConfig.PatPrefix, StringComparison.Ordinal))
        {
            var pat = await pats.IntrospectAsync(token);
            return pat is { Active: true }
                ? new TokenClaims
                {
                    UserId = pat.Sub, OrgId = pat.OrgId, ProjectId = pat.ProjectId,
                    Roles = pat.Roles, IsServiceAccount = true,
                }
                : null;
        }

        return await hydra.ValidateJwtAsync(token);
    }
}
