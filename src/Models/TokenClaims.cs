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
