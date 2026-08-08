namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// <c>GET /org/users</c> — la même recherche que <c>/admin/users</c>, confinée au locataire.
///
/// <para>
/// Les deux routes partagent <c>UserSearch</c>, donc les filtres eux-mêmes sont déjà tenus par
/// <c>UserSearchFilterTests</c>. Ce qui est vérifié ici est ce que le partage ne peut PAS garantir :
/// la portée. Un administrateur d'organisation trouve les comptes de ses listes et aucun autre, et
/// aucun paramètre de requête ne l'élargit — la faute que <c>createUserList</c> a commise dans
/// l'autre sens.
/// </para>
///
/// <para>
/// La base est partagée par la collection : sans le confinement, chaque compte de chaque fixture
/// remonterait, ce qui est exactement ce que la première assertion interdit.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class OrgUserSearchTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, HttpClient client)> OrgAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        return (org, list, fixture.ClientWithToken(token));
    }

    private static async Task<JsonElement> BodyAsync(HttpClient client, string query)
    {
        var res = await client.GetAsync(query);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int Total(JsonElement body) => body.GetProperty("total").GetInt32();

    private static string[] Emails(JsonElement body) =>
        [.. body.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!)];

    private async Task PatchAsync(User user, Action<User> change)
    {
        var tracked = await fixture.Db.Users.FirstAsync(u => u.Id == user.Id);
        change(tracked);
        await fixture.Db.SaveChangesAsync();
    }

    // ── La portée ─────────────────────────────────────────────────────────────

    /// <summary>
    /// L'assertion qui compte. Aucun paramètre n'est envoyé : la seule chose qui borne la réponse
    /// est le <c>org_id</c> du jeton, et le compte d'un autre locataire n'y est pas.
    /// </summary>
    [Fact]
    public async Task FindsItsOwnListsAccounts_AndNoOtherTenants()
    {
        var (org, list, client) = await OrgAdminAsync();
        var mine     = await fixture.Seed.CreateUserAsync(list.Id);
        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        var stranger = await fixture.Seed.CreateUserAsync(otherList.Id);

        var body = await BodyAsync(client, "/org/users");

        Emails(body).Should().Contain(mine.Email).And.NotContain(stranger.Email);
        Total(body).Should().Be(await fixture.Db.Users.CountAsync(u => u.UserList.OrgId == org.Id));
    }

    /// <summary>
    /// <c>org_id</c> n'est pas lié par cette route, donc rien ne peut l'honorer. Un administrateur
    /// qui pourrait nommer une autre organisation lirait des comptes qui ne sont pas les siens.
    /// </summary>
    [Fact]
    public async Task OrgId_PointingAtAnotherTenant_WidensNothing()
    {
        var (org, list, client) = await OrgAdminAsync();
        var mine = await fixture.Seed.CreateUserAsync(list.Id);
        var (otherOrg, otherList) = await fixture.Seed.CreateOrgAsync();
        var stranger = await fixture.Seed.CreateUserAsync(otherList.Id);

        var body = await BodyAsync(client, $"/org/users?org_id={otherOrg.Id}");

        Emails(body).Should().Contain(mine.Email).And.NotContain(stranger.Email);
        Total(body).Should().Be(await fixture.Db.Users.CountAsync(u => u.UserList.OrgId == org.Id));
    }

    /// <summary>
    /// Une liste d'un autre locataire ne devient pas lisible parce qu'elle est nommée : le
    /// confinement est appliqué en plus du filtre, pas à sa place.
    /// </summary>
    [Fact]
    public async Task UserListId_OfAnotherTenant_MatchesNothing()
    {
        var (_, _, client) = await OrgAdminAsync();
        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateUserAsync(otherList.Id);

        var body = await BodyAsync(client, $"/org/users?user_list_id={otherList.Id}");

        Total(body).Should().Be(0);
    }

    /// <summary>Confinée à un locataire, la réponse en compte 0 ou 1 — jamais celui d'à côté.</summary>
    [Fact]
    public async Task Tenants_CountsTheOneTenantTheScopeAllows()
    {
        var (_, list, client) = await OrgAdminAsync();
        await fixture.Seed.CreateUserAsync(list.Id);
        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        await fixture.Seed.CreateUserAsync(otherList.Id);

        var body = await BodyAsync(client, "/org/users");

        body.GetProperty("tenants").GetInt32().Should().Be(1);
    }

    // ── Les critères ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Q_MatchesTheAddress_TheNameAndTheIdAlike()
    {
        var (_, list, client) = await OrgAdminAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var needle = $"Zaz{Guid.NewGuid():N}"[..12];
        await PatchAsync(user, u => u.DisplayName = $"{needle} Lefevre");

        var byName = await BodyAsync(client, $"/org/users?q={needle}");
        var byId   = await BodyAsync(client, $"/org/users?q={user.Id}");

        Emails(byName).Should().ContainSingle().Which.Should().Be(user.Email);
        Emails(byId).Should().ContainSingle().Which.Should().Be(user.Email);
    }

    [Fact]
    public async Task Q_StillRefusesTwoCharacters()
    {
        var (_, _, client) = await OrgAdminAsync();

        var res = await client.GetAsync("/org/users?q=ab");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("query_too_short");
    }

    [Fact]
    public async Task UserListId_KeepsOnlyThatListsAccounts()
    {
        var (org, list, client) = await OrgAdminAsync();
        var inList    = await fixture.Seed.CreateUserAsync(list.Id);
        var other     = await fixture.Seed.CreateUserListAsync(org.Id);
        var elsewhere = await fixture.Seed.CreateUserAsync(other.Id);

        var body = await BodyAsync(client, $"/org/users?user_list_id={list.Id}");

        Total(body).Should().Be(1);
        Emails(body).Should().Contain(inList.Email).And.NotContain(elsewhere.Email);
    }

    [Fact]
    public async Task Status_Disabled_KeepsOnlyTheDisabledOnes()
    {
        var (_, list, client) = await OrgAdminAsync();
        await fixture.Seed.CreateUserAsync(list.Id);
        var off = await fixture.Seed.CreateUserAsync(list.Id, active: false);

        var body = await BodyAsync(client, "/org/users?status=disabled");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(off.Email);
    }

    /// <summary>
    /// « Actif » veut dire ce qu'un opérateur entend par là : activé ET pas en train de purger un
    /// verrouillage. Le compte que quelqu'un cherche est justement celui-là.
    /// </summary>
    [Fact]
    public async Task Status_Active_ExcludesAnAccountServingALockout()
    {
        var (_, list, client) = await OrgAdminAsync();
        var fine   = await fixture.Seed.CreateUserAsync(list.Id);
        var locked = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(locked, u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10));

        var active = await BodyAsync(client, "/org/users?status=active");
        var shut   = await BodyAsync(client, "/org/users?status=locked");

        Emails(active).Should().Contain(fine.Email).And.NotContain(locked.Email);
        Emails(shut).Should().ContainSingle().Which.Should().Be(locked.Email);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task Mfa_SplitsOnEitherFactor(string want)
    {
        var (_, list, client) = await OrgAdminAsync();
        var totp = await fixture.Seed.CreateUserAsync(list.Id);
        var key  = await fixture.Seed.CreateUserAsync(list.Id);
        var none = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(totp, u => u.TotpEnabled = true);
        await PatchAsync(key,  u => u.WebAuthnEnabled = true);

        var body = await BodyAsync(client, $"/org/users?mfa={want}");

        if (want == "yes")
            Emails(body).Should().Contain([totp.Email, key.Email]).And.NotContain(none.Email);
        else
            Emails(body).Should().Contain(none.Email).And.NotContain(totp.Email).And.NotContain(key.Email);
    }

    [Fact]
    public async Task SignedIn_CutsWhereItClaimsTo()
    {
        var (_, list, client) = await OrgAdminAsync();
        var never  = await fixture.Seed.CreateUserAsync(list.Id);
        var recent = await fixture.Seed.CreateUserAsync(list.Id);
        var older  = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(recent, u => u.LastLoginAt = DateTimeOffset.UtcNow.AddDays(-2));
        await PatchAsync(older,  u => u.LastLoginAt = DateTimeOffset.UtcNow.AddDays(-20));

        var week  = await BodyAsync(client, "/org/users?signed_in=7d");
        var month = await BodyAsync(client, "/org/users?signed_in=30d");
        var none  = await BodyAsync(client, "/org/users?signed_in=never");

        Emails(week).Should().Contain(recent.Email).And.NotContain(older.Email);
        Emails(month).Should().Contain([recent.Email, older.Email]);
        Emails(none).Should().Contain(never.Email).And.NotContain(recent.Email);
    }

    /// <summary>
    /// Un filtre inconnu est refusé ici aussi. Abandonné, il rendrait la population entière du
    /// locataire sous l'étiquette d'une population restreinte — la page aurait l'air juste.
    /// </summary>
    [Theory]
    [InlineData("status=enabled")]
    [InlineData("mfa=totp")]
    [InlineData("signed_in=today")]
    public async Task UnknownFilterValue_IsRefused_NotIgnored(string pair)
    {
        var (_, _, client) = await OrgAdminAsync();

        var res = await client.GetAsync($"/org/users?{pair}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_filter");
        body.GetProperty("filter").GetString().Should().Be(pair.Split('=')[0]);
    }

    // ── La pagination ─────────────────────────────────────────────────────────

    /// <summary>
    /// La page est une fenêtre sur l'ensemble filtré, jamais l'inverse : <c>total</c> ne bouge pas
    /// d'une page à l'autre, les pages ne se recouvrent pas, et ensemble elles font le tout.
    /// </summary>
    [Fact]
    public async Task PaginationUnderAFilter_WindowsTheFilteredSet()
    {
        var (_, list, client) = await OrgAdminAsync();
        for (var i = 0; i < 3; i++)
            await fixture.Seed.CreateUserAsync(list.Id, active: false);
        await fixture.Seed.CreateUserAsync(list.Id);

        var first  = await BodyAsync(client, "/org/users?status=disabled&page=1&pageSize=2");
        var second = await BodyAsync(client, "/org/users?status=disabled&page=2&pageSize=2");

        Total(first).Should().Be(3);
        Total(second).Should().Be(3, "le total compte l'ensemble filtré, pas la page");
        Emails(first).Should().HaveCount(2);
        Emails(second).Should().HaveCount(1);
        Emails(first).Should().NotIntersectWith(Emails(second));
        second.GetProperty("page").GetInt32().Should().Be(2);
        second.GetProperty("page_size").GetInt32().Should().Be(2);
    }
}
