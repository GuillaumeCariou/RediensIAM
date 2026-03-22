namespace RediensIAM.Models;

public record TokenClaims
{
    public required string UserId { get; init; }
    public required string OrgId { get; init; }
    public required string ProjectId { get; init; }
    public required List<string> Roles { get; init; }
    public bool IsServiceAccount { get; init; }
    public bool IsImpersonation { get; init; }

    // Strips the "orgId:userId" compound format used in Hydra subjects
    public Guid ParsedUserId
    {
        get
        {
            var raw = UserId.Contains(':') ? UserId.Split(':')[1] : UserId;
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
    bool IsServiceAccount = false);

public record IntrospectRequest(string Token);
