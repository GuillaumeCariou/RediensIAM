namespace RediensIAM.Models;

public record TokenClaims
{
    public required string UserId { get; init; }
    public required string OrgId { get; init; }
    public required string ProjectId { get; init; }
    public required List<string> Roles { get; init; }
    public bool IsServiceAccount { get; init; }

    /// <summary>
    /// OAuth2 client the token was issued to (<c>client_id</c> from introspection).
    /// Empty for personal access tokens, which are not OAuth2 tokens.
    /// </summary>
    public string ClientId { get; init; } = "";

    /// <summary>Audiences the token was minted for (<c>aud</c>). Often empty — Hydra only sets it when requested.</summary>
    public List<string> Audiences { get; init; } = [];

    /// <summary>
    /// Who is acting for whom (RFC 8693 §4.1). Null on every ordinary token, which is the whole
    /// point: a consumer that cannot tell a delegated request from a genuine one is the one thing
    /// this must never allow.
    /// </summary>
    public ActorClaim? Act { get; init; }

    // Strips the "orgId:userId" compound format used in Hydra subjects.
    // Split into exactly two parts and take the last: a subject with extra colons used to
    // silently yield the middle segment instead of failing.
    public Guid ParsedUserId
    {
        get
        {
            var parts = UserId.Split(':', 2);
            var raw = parts.Length == 2 ? parts[1] : UserId;
            return Guid.TryParse(raw, out var g) ? g : Guid.Empty;
        }
    }
}

/// <summary>
/// The actor behind a delegated token: the operator, the level they held when the session opened,
/// the mode the session was issued in, and the session id that revokes it.
///
/// <para>
/// <c>Mode</c> is a claim, and a claim enforces nothing on its own — the enforcement point is the
/// consuming gateway, which refuses mutating verbs while it reads <c>read</c>. It is carried here
/// so that every enforcement point sees the same value, rather than each deriving its own.
/// </para>
/// </summary>
public record ActorClaim(string Sub, string Level, string Mode, string Session);

public record IntrospectionResponse(
    bool Active,
    string Sub,
    string OrgId,
    string ProjectId,
    List<string> Roles,
    bool IsServiceAccount = false,
    // Carried so the cached-introspection path can re-check expiry without a DB round-trip.
    DateTimeOffset? ExpiresAt = null);

public record IntrospectRequest(string Token);
