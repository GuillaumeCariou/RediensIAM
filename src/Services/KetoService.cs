using System.Text.Json;
using RediensIAM.Config;

namespace RediensIAM.Services;

public class KetoService(IHttpClientFactory http, AppConfig appConfig)
{
    private readonly string _readUrl = appConfig.KetoReadUrl;
    private readonly string _writeUrl = appConfig.KetoWriteUrl;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private HttpClient ReadClient => http.CreateClient("keto-read");
    private HttpClient WriteClient => http.CreateClient("keto-write");

    public async Task<bool> CheckAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var url = $"{_readUrl}/relation-tuples/check?namespace={Uri.EscapeDataString(namespaceName)}&object={Uri.EscapeDataString(objectId)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}";
        var resp = await ReadClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return false;
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("allowed").GetBoolean();
    }

    public async Task WriteRelationTupleAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var body = new[]
        {
            new
            {
                action = "insert",
                relation_tuple = new { @namespace = namespaceName, @object = objectId, relation, subject_id = subjectId }
            }
        };
        var resp = await WriteClient.PatchAsJsonAsync($"{_writeUrl}/admin/relation-tuples", body);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteRelationTupleAsync(string namespaceName, string objectId, string relation, string subjectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&object={Uri.EscapeDataString(objectId)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}";
        await WriteClient.DeleteAsync(url);
    }

    public async Task DeleteAllProjectTuplesAsync(string projectId)
    {
        var url = $"{_writeUrl}/admin/relation-tuples?namespace={Uri.EscapeDataString("Projects")}&object={Uri.EscapeDataString(projectId)}";
        await WriteClient.DeleteAsync(url);
    }

    public async Task<bool> HasAnyRelationAsync(string namespaceName, string relation, string subjectId)
    {
        var url = $"{_readUrl}/relation-tuples?namespace={Uri.EscapeDataString(namespaceName)}&relation={Uri.EscapeDataString(relation)}&subject_id={Uri.EscapeDataString(subjectId)}&page_size=1";
        var resp = await ReadClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return false;
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.TryGetProperty("relation_tuples", out var tuples) && tuples.GetArrayLength() > 0;
    }
}
