using System.Net.Http.Json;
using System.Text.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// A user list, and the users in it, read and written through either scope.
///
/// <para>
/// Six operations, each written twice. Four had drifted, and one of them reaches outside this
/// deployment entirely:
/// </para>
///
/// <list type="bullet">
/// <item><b>Adding or removing a user through the system scope fired no webhook.</b>
/// <c>user.created</c>, <c>user.invited</c> and <c>user.deleted</c> are subscribable events —
/// WebhookService.WebhookEvents lists them, and AuditLogService dispatches on the action string.
/// The system route recorded <c>userlist.user_added</c> and <c>userlist.user_removed</c>, which
/// are on no subscription list, so a tenant whose integration watches for new users simply never
/// heard about the ones a super-admin created in their list. The audit query missed them too.</item>
/// <item>Creating a user list from the organisation scope was recorded nowhere; from the system
/// scope it was.</item>
/// <item><c>GET /userlists/{id}</c> answered with a different set of fields per scope — one
/// carried <c>assigned_projects</c>, the other <c>org_id</c> and <c>created_at</c>.</item>
/// <item><c>GET /userlists/{id}/users</c> reported <c>invite_pending</c> on one scope only, so a
/// super-admin could not tell an invited user from an active one.</item>
/// </list>
/// </summary>
[Collection("RediensIAM")]
public class UserListParityTests(TestFixture fixture)
{
    private async Task<(HttpClient Org, HttpClient System, Guid OrgId, Guid ListId)> BothScopesAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        return (fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id)),
                fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id)),
                org.Id, list.Id);
    }

    private static object NewUser() => new
    {
        email = $"parity-{Guid.NewGuid():N}@test.local",
        password = "Correct-Horse-Battery-9",
    };

    private List<string> ActionsFor(string userId) =>
        [.. fixture.Db.AuditLogs.Where(a => a.TargetId == userId).Select(a => a.Action)];

    // ── The one that leaves the deployment ────────────────────────────────────

    /// <summary>
    /// A tenant subscribes to <c>user.created</c>. AuditLogService dispatches a webhook when the
    /// action it records is on that list — so an action the system route invented instead reaches
    /// no subscriber at all.
    /// </summary>
    [Fact]
    public async Task AddingAUserRecordsTheSubscribableEventFromBothScopes()
    {
        var (orgClient, sysClient, _, listId) = await BothScopesAsync();

        var fromOrg = await (await orgClient.PostAsJsonAsync($"/org/userlists/{listId}/users", NewUser()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.PostAsJsonAsync($"/admin/userlists/{listId}/users", NewUser()))
            .Content.ReadFromJsonAsync<JsonElement>();

        ActionsFor(fromOrg.GetProperty("id").GetString()!).Should().Contain("user.created");
        ActionsFor(fromSystem.GetProperty("id").GetString()!).Should().Contain("user.created",
            "a tenant's integration watches for user.created, and hears nothing about a name it was never told");
    }

    [Fact]
    public async Task RemovingAUserRecordsTheSubscribableEventFromBothScopes()
    {
        var (orgClient, sysClient, _, listId) = await BothScopesAsync();

        foreach (var (client, prefix) in new[] { (orgClient, "/org"), (sysClient, "/admin") })
        {
            var created = await (await client.PostAsJsonAsync($"{prefix}/userlists/{listId}/users", NewUser()))
                .Content.ReadFromJsonAsync<JsonElement>();
            var userId = created.GetProperty("id").GetString()!;

            await client.DeleteAsync($"{prefix}/userlists/{listId}/users/{userId}");

            ActionsFor(userId).Should().Contain("user.deleted",
                $"{prefix} has to record the event a subscriber can actually be subscribed to");
        }
    }

    /// <summary>
    /// An invitation is its own subscribable event, and it too was named differently per scope.
    /// </summary>
    [Fact]
    public async Task InvitingAUserRecordsTheSubscribableEventFromBothScopes()
    {
        var (orgClient, sysClient, _, listId) = await BothScopesAsync();
        object invite() => new { email = $"invite-{Guid.NewGuid():N}@test.local" };

        var fromOrg = await (await orgClient.PostAsJsonAsync($"/org/userlists/{listId}/users", invite()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.PostAsJsonAsync($"/admin/userlists/{listId}/users", invite()))
            .Content.ReadFromJsonAsync<JsonElement>();

        ActionsFor(fromOrg.GetProperty("id").GetString()!).Should().Contain("user.invited");
        ActionsFor(fromSystem.GetProperty("id").GetString()!).Should().Contain("user.invited");
    }

    // ── Recorded at all ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatingAUserListIsRecordedFromBothScopes()
    {
        var (orgClient, sysClient, orgId, _) = await BothScopesAsync();

        var fromOrg = await (await orgClient.PostAsJsonAsync("/org/userlists",
            new { name = $"org-{Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.PostAsJsonAsync("/admin/userlists",
            new { name = $"sys-{Guid.NewGuid():N}", org_id = orgId })).Content.ReadFromJsonAsync<JsonElement>();

        foreach (var created in new[] { fromOrg, fromSystem })
        {
            var id = created.GetProperty("id").GetString()!;
            fixture.Db.AuditLogs.Where(a => a.TargetId == id).Select(a => a.Action)
                .Should().Contain("userlist.created",
                    "a list decides who may sign in to the projects it is assigned to");
        }
    }

    // ── Same resource, same shape ─────────────────────────────────────────────

    private static string[] FieldsOf(JsonElement obj) =>
        [.. obj.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task ReadingAUserListGivesTheSameFieldsInBothScopes()
    {
        var (orgClient, sysClient, _, listId) = await BothScopesAsync();

        var fromOrg = await (await orgClient.GetAsync($"/org/userlists/{listId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.GetAsync($"/admin/userlists/{listId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        FieldsOf(fromOrg).Should().Equal(FieldsOf(fromSystem));
    }

    /// <summary>
    /// Without it a super-admin cannot tell an invited user from an active one, which is exactly
    /// the state they are most often asked to look into.
    /// </summary>
    [Fact]
    public async Task ListingUsersReportsPendingInvitesInBothScopes()
    {
        var (orgClient, sysClient, _, listId) = await BothScopesAsync();
        await orgClient.PostAsJsonAsync($"/org/userlists/{listId}/users",
            new { email = $"pending-{Guid.NewGuid():N}@test.local" });

        var fromOrg = await (await orgClient.GetAsync($"/org/userlists/{listId}/users"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.GetAsync($"/admin/userlists/{listId}/users"))
            .Content.ReadFromJsonAsync<JsonElement>();

        FieldsOf(fromSystem.EnumerateArray().First()).Should().Equal(FieldsOf(fromOrg.EnumerateArray().First()));
        fromSystem.EnumerateArray().Should().Contain(u => u.GetProperty("invite_pending").GetBoolean());
    }
}
