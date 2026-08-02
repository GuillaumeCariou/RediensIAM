using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class SystemUserTests(TestFixture fixture)
{
    private async Task<(User targetUser, HttpClient client)> ScaffoldAsync()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token           = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var list            = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser      = await fixture.Seed.CreateUserAsync(list.Id);
        return (targetUser, fixture.ClientWithToken(token));
    }

    // ── GET /admin/users/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetUser_ExistingUser_Returns200()
    {
        var (user, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/admin/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(user.Id.ToString());
        body.GetProperty("email").GetString().Should().Be(user.Email);
    }

    [Fact]
    public async Task GetUser_NonExistent_Returns404()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/admin/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUser_Unauthenticated_Returns401Or403()
    {
        var (user, _) = await ScaffoldAsync();

        var res = await fixture.Client.GetAsync($"/admin/users/{user.Id}");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── PATCH /admin/users/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateUser_ChangeName_Returns200AndPersists()
    {
        var (user, client) = await ScaffoldAsync();
        var newName        = "Updated Name " + Guid.NewGuid().ToString("N")[..4];

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new
        {
            display_name = newName
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.DisplayName.Should().Be(newName);
    }

    [Fact]
    public async Task UpdateUser_ClearLock_ResetsLockAndFailedCount()
    {
        var (user, client) = await ScaffoldAsync();
        user.LockedUntil        = DateTimeOffset.UtcNow.AddHours(1);
        user.FailedLoginCount   = 5;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new
        {
            clear_lock = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.LockedUntil.Should().BeNull();
        updated.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateUser_Deactivate_SetsActiveToFalse()
    {
        var (user, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new
        {
            active = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Active.Should().BeFalse();
        updated.DisabledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateUser_NonExistent_Returns404()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{Guid.NewGuid()}", new
        {
            display_name = "ghost"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateUser_ClearDisplayName_SetsNull()
    {
        var (user, client) = await ScaffoldAsync();
        user.DisplayName = "Old Name";
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new { display_name = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task UpdateUser_SetPhone_PersistsValue()
    {
        var (user, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new { phone = "+1-555-0100" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().Be("+1-555-0100");
    }

    [Fact]
    public async Task UpdateUser_ClearPhone_SetsNull()
    {
        var (user, client) = await ScaffoldAsync();
        user.Phone = "+1-555-0100";
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/users/{user.Id}", new { phone = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().BeNull();
    }

    // ── DELETE /admin/users/{id}/sessions ─────────────────────────────────────

    [Fact]
    public async Task ForceLogout_ExistingUser_Returns200WithMessage()
    {
        var (user, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/admin/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("sessions_revoked");
    }

    [Fact]
    public async Task ForceLogout_NonExistentUser_Returns404()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/admin/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The console's user table reads four fields this projection never sent, so the Organisation
    /// and User List columns were always blank and <c>isLocked()</c> was permanently false — which
    /// hid the Locked badge and, with it, the only route to the Unlock action.
    /// </summary>
    [Fact]
    public async Task SearchUsers_ReturnsTheFieldsTheConsoleRenders()
    {
        var (user, client) = await ScaffoldAsync();
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await fixture.Db.SaveChangesAsync();

        var res  = await client.GetAsync($"/admin/users?q={Uri.EscapeDataString(user.Email)}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var row = body.EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == user.Email);

        foreach (var field in new[] { "display_name", "org_name", "user_list_name", "locked_until" })
            row.TryGetProperty(field, out _).Should().BeTrue($"the console renders {field}");

        row.GetProperty("locked_until").ValueKind.Should().NotBe(JsonValueKind.Null,
            "this account is locked, and the badge and the unlock action both key off it");
    }
}
