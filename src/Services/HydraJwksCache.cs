using System.Text.Json;
using RediensIAM.Models;

namespace RediensIAM.Services;

// Validates tokens via Hydra's admin introspection endpoint (port 4445).
// This avoids fetching JWKS from the public port (4444) which may not be
// reachable pod-to-pod depending on network configuration.
public class HydraJwksCache(IHttpClientFactory http, IConfiguration config)
{
    private readonly string _introspectUrl = config["Hydra:AdminUrl"] + "/admin/oauth2/introspect";

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<TokenClaims?> ValidateJwtAsync(string token)
    {
        var client = http.CreateClient("hydra-admin");
        var form = new FormUrlEncodedContent([new("token", token)]);
        var resp = await client.PostAsync(_introspectUrl, form);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadFromJsonAsync<IntrospectResult>(_json);
        if (body is not { Active: true }) return null;

        // Custom consent session claims land in `ext`
        var ext = body.Ext;
        var userId = ext?.GetString("user_id") ?? body.Sub ?? "";
        var orgId = ext?.GetString("org_id") ?? "";
        var projectId = ext?.GetString("project_id") ?? "";
        var roles = ext?.GetRoles("roles") ?? [];

        return new TokenClaims
        {
            UserId = userId,
            OrgId = orgId,
            ProjectId = projectId,
            Roles = roles,
            IsServiceAccount = false
        };
    }

    private record IntrospectResult(
        bool Active,
        string? Sub,
        [property: System.Text.Json.Serialization.JsonPropertyName("ext")]
        ExtClaims? Ext
    );

    private class ExtClaims : Dictionary<string, JsonElement>
    {
        public string? GetString(string key) =>
            TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public List<string> GetRoles(string key)
        {
            if (!TryGetValue(key, out var v)) return [];
            if (v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList();
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = v.GetString() ?? "";
                if (raw.StartsWith('['))
                    try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; } catch { }
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return [];
        }
    }
}
