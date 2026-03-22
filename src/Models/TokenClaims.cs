namespace RediensIAM.Models;

public record TokenClaims
{
    public required string UserId { get; init; }
    public required string OrgId { get; init; }
    public required string ProjectId { get; init; }
    public required List<string> Roles { get; init; }
    public bool IsServiceAccount { get; init; }
}

public record IntrospectionResponse(
    bool Active,
    string Sub,
    string OrgId,
    string ProjectId,
    List<string> Roles,
    bool IsServiceAccount = false);

public record IntrospectRequest(string Token);
