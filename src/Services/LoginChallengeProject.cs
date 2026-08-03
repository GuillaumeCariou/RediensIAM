namespace RediensIAM.Services;

/// <summary>
/// Resolves which project a Hydra login challenge belongs to.
///
/// The authority is <c>client.metadata.project_id</c>, written by RediensIAM when the OAuth2
/// client is created. The <c>project_id</c> carried in <c>oidc_context.extra</c> or in the
/// authorize URL is caller-controlled and is used only to detect a mismatch — never as a source.
///
/// Treating the request value as a source is what let any tenant name another tenant's project
/// on its own login challenge: it leaked login configuration, and via the social/SAML start
/// endpoints it produced an authorization code for a foreign tenant's user on the caller's own
/// OAuth2 client.
/// </summary>
public static class LoginChallengeProject
{
    public enum Resolution { Ok, Missing, Mismatch }

    private const string ProjectIdKey = "project_id";
    private const string OrgIdKey = "org_id";

    /// <summary>
    /// The organisation the challenge's OAuth2 client is registered to, from the same
    /// server-authored <c>client.metadata</c> as <see cref="Resolve"/>. Written beside
    /// <c>project_id</c> at project creation; there is no caller-supplied counterpart to
    /// cross-check, and none is accepted.
    ///
    /// <para>
    /// This is what lets a login publish its RLS tenant scope before it reads anything at all —
    /// see <c>AuthController.PinScopeToChallengeAsync</c>. Null for the admin console client, for
    /// a non-project client, and for any project client registered before <c>org_id</c> was
    /// recorded; callers must fall back to the organisation on the project row.
    /// </para>
    /// </summary>
    public static Guid? ResolveOrgOrNull(HydraLoginRequest req) =>
        Guid.TryParse(req.Client?.Metadata?.GetValueOrDefault(OrgIdKey)?.ToString(), out var orgId)
        && orgId != Guid.Empty
            ? orgId
            : null;

    /// <summary>
    /// Returns <see cref="Resolution.Ok"/> and the registered project id, or the reason the
    /// challenge cannot be bound to a project. Fails closed: a client with no registered
    /// project resolves to <see cref="Resolution.Missing"/>.
    /// </summary>
    public static Resolution Resolve(HydraLoginRequest req, out string? projectId)
    {
        projectId = null;

        var registered = req.Client?.Metadata?.GetValueOrDefault(ProjectIdKey)?.ToString();
        if (string.IsNullOrWhiteSpace(registered)) return Resolution.Missing;

        var requested = ExtractRequested(req);
        if (requested != null && !string.Equals(requested, registered, StringComparison.OrdinalIgnoreCase))
            return Resolution.Mismatch;

        projectId = registered;
        return Resolution.Ok;
    }

    /// <summary>Convenience wrapper: anything other than <see cref="Resolution.Ok"/> yields null.</summary>
    public static string? ResolveOrNull(HydraLoginRequest req) =>
        Resolve(req, out var projectId) == Resolution.Ok ? projectId : null;

    /// <summary>The caller-supplied project_id — cross-check input only.</summary>
    private static string? ExtractRequested(HydraLoginRequest req)
    {
        var extra = req.OidcContext?.Extra;
        if (extra?.TryGetValue(ProjectIdKey, out var v) == true && v?.ToString() is { Length: > 0 } fromCtx)
            return fromCtx;

        if (!Uri.TryCreate(req.RequestUrl ?? "", UriKind.Absolute, out var parsed)) return null;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(parsed.Query);
        return query.TryGetValue(ProjectIdKey, out var fromUrl) && fromUrl.Count > 0
            ? fromUrl[0]
            : null;
    }
}
