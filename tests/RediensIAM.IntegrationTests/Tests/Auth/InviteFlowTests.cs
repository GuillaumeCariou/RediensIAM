using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// B1: User Invitation Flow.
/// </summary>
[Collection("RediensIAM")]
public class InviteFlowTests(TestFixture fixture)
{
    // ── Org endpoint: invite (no password) ───────────────────────────────────

    [Fact]
    public async Task OrgAddUser_NoPassword_CreatesInactiveUserAndSendsInvite()
    {
        fixture.EmailStub.SentInvites.Clear();

        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var email = SeedData.UniqueEmail();
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeTrue();

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == email);
        user.Should().NotBeNull();
        user!.Active.Should().BeFalse();

        fixture.EmailStub.SentInvites.Should().ContainSingle(i => i.To == email);
    }

    [Fact]
    public async Task OrgAddUser_WithPassword_CreatesActiveUserNoInvite()
    {
        fixture.EmailStub.SentInvites.Clear();

        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var email = SeedData.UniqueEmail();
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = "P@ssw0rd!Test" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeFalse();

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == email);
        user!.Active.Should().BeTrue();
        fixture.EmailStub.SentInvites.Should().BeEmpty();
    }

    // ── Invite complete ───────────────────────────────────────────────────────

    [Fact]
    public async Task InviteComplete_ValidToken_ActivatesUserSetsPassword()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        fixture.EmailStub.SentInvites.Clear();
        var email = SeedData.UniqueEmail();
        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = (string?)null });

        var invite = fixture.EmailStub.SentInvites.First(i => i.To == email);
        var inviteToken = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(new Uri(invite.InviteUrl).Query)["token"].ToString();

        var res = await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
            new { token = inviteToken, password = "NewP@ss1!" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("invite_accepted");

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == email);
        user!.Active.Should().BeTrue();
        user.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task InviteComplete_InvalidToken_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
            new { token = "not-a-real-token", password = "NewP@ss1!" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InviteComplete_TokenUsedTwice_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        fixture.EmailStub.SentInvites.Clear();
        var email = SeedData.UniqueEmail();
        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = (string?)null });

        var invite = fixture.EmailStub.SentInvites.First(i => i.To == email);
        var inviteToken = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(new Uri(invite.InviteUrl).Query)["token"].ToString();

        await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
            new { token = inviteToken, password = "NewP@ss1!" });

        var res2 = await fixture.Client.PostAsJsonAsync("/auth/invite/complete",
            new { token = inviteToken, password = "AnotherP@ss1!" });

        res2.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── List users: invite_pending flag ──────────────────────────────────────

    [Fact]
    public async Task ListUsers_InvitePendingFlag_ReflectsInviteState()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        fixture.EmailStub.SentInvites.Clear();
        var invitedEmail = SeedData.UniqueEmail();
        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email = invitedEmail, password = (string?)null });

        var directEmail = SeedData.UniqueEmail();
        await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email = directEmail, password = "P@ssw0rd!Test" });

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await res.Content.ReadFromJsonAsync<JsonElement[]>();

        var invited = users!.FirstOrDefault(u => u.GetProperty("email").GetString() == invitedEmail);
        invited.Should().NotBeNull("invited user should appear in list");
        invited.GetProperty("invite_pending").GetBoolean().Should().BeTrue();

        var direct = users.FirstOrDefault(u => u.GetProperty("email").GetString() == directEmail);
        direct.Should().NotBeNull("direct user should appear in list");
        direct.GetProperty("invite_pending").GetBoolean().Should().BeFalse();
    }

    // ── Resend invite ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResendInvite_InactiveUser_SendsNewInvite()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        fixture.EmailStub.SentInvites.Clear();
        var email = SeedData.UniqueEmail();
        var createRes = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users",
            new { email, password = (string?)null });
        var created  = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var userId   = created.GetProperty("id").GetString();

        fixture.EmailStub.SentInvites.Clear();

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users/{userId}/resend-invite", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("invite_resent");
        fixture.EmailStub.SentInvites.Should().ContainSingle(i => i.To == email);
    }

    [Fact]
    public async Task ResendInvite_ActiveUser_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}/resend-invite", new { });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Admin endpoint: invite ────────────────────────────────────────────────

    [Fact]
    public async Task AdminAddUser_NoPassword_CreatesInactiveUserAndSendsInvite()
    {
        fixture.EmailStub.SentInvites.Clear();

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var (org2, _)    = await fixture.Seed.CreateOrgAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org2.Id);
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var email = SeedData.UniqueEmail();
        var res = await client.PostAsJsonAsync($"/admin/userlists/{list.Id}/users",
            new { email, password = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invite_pending").GetBoolean().Should().BeTrue();

        fixture.EmailStub.SentInvites.Should().ContainSingle(i => i.To == email);

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == email);
        user!.Active.Should().BeFalse();
    }
}
