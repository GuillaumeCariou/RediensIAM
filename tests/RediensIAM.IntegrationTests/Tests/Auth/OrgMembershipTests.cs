using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// L'organisation que porte un jeton vient de l'UTILISATEUR quand il en nomme une, du PROJET
/// sinon.
///
/// Pourquoi ce champ existe. Jusqu'ici l'organisation venait toujours du projet. C'est juste tant
/// qu'un projet sert un seul locataire — le modèle d'origine. Ça cesse de l'être dès qu'un projet
/// en sert plusieurs : une console client unique, une page de connexion, des employés de sociétés
/// différentes derrière. Tous auraient porté l'organisation propriétaire du projet, et l'isolation
/// aurait disparu au niveau du jeton — avant Keto, avant IsInCallerScopeAsync, avant tout ce qui
/// s'appuie dessus.
///
/// C'est le motif que Keycloak nomme « Organizations » (multi-tenancy within a realm) et qu'Ory
/// décrit comme « a grouping mechanism for users within a single project ».
/// </summary>
[Collection("RediensIAM")]
public class OrgMembershipTests(TestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    /// <param name="memberOf">L'organisation dont l'utilisateur est membre, ou null pour le repli.</param>
    private async Task<(Project project, User user, Organisation projectOrg)> ScaffoldAsync(
        Func<Organisation, Task<Organisation?>>? memberOf = null, string password = "P@ssw0rd!Test")
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);

        project.AssignedUserListId = list.Id;
        project.RequireMfa         = false;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id, password: password);
        if (memberOf is not null)
        {
            user.OrgId = (await memberOf(org))?.Id;
            await fixture.Db.SaveChangesAsync();
        }
        return (project, user, org);
    }

    private async Task<string> LoginAsync(Project project, User user, string password = "P@ssw0rd!Test")
    {
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), project.OrgId.ToString());
        var res = await fixture.Client.PostAsJsonAsync("/auth/login",
            new { email = user.Email, password, login_challenge = challenge });
        res.EnsureSuccessStatusCode();
        return challenge;
    }

    [Fact]
    public async Task Login_UserWithoutOrgId_CarriesTheProjectOrganisation()
    {
        // Le comportement HISTORIQUE. Tout déploiement existant est dans ce cas : la colonne est
        // nulle partout, et rien ne doit changer pour lui. C'est ce que garantit le repli.
        var (project, user, projectOrg) = await ScaffoldAsync();
        var challenge = await LoginAsync(project, user);

        var body = fixture.Hydra.AcceptedLoginBody(challenge);
        Assert.NotNull(body);
        Assert.Contains($"\"{projectOrg.Id}:{user.Id}\"", body);
        Assert.Contains(projectOrg.Id.ToString(), body);
    }

    [Fact]
    public async Task Login_UserOfAnotherOrganisation_CarriesHisOwn_NotTheProjectOne()
    {
        // Le cas qui motive tout : un projet PARTAGÉ. L'utilisateur se connecte sur la page du
        // projet de Yandee, mais il est membre d'ACME. Son jeton doit dire ACME.
        Organisation? acme = null;
        var (project, user, projectOrg) = await ScaffoldAsync(async _ =>
        {
            var created = await fixture.Seed.CreateOrgAsync();
            acme = created.Item1;
            return acme;
        });
        Assert.NotNull(acme);
        Assert.NotEqual(acme!.Id, projectOrg.Id);

        var challenge = await LoginAsync(project, user);
        var body = fixture.Hydra.AcceptedLoginBody(challenge);
        Assert.NotNull(body);

        // Le SUJET et le CONTEXTE doivent nommer la même organisation : deux chemins distincts
        // les relisent — ParseSubjectOrgId et CtxOrgId — et les désaccorder ferait diverger la
        // portée pinée de celle du jeton.
        Assert.Contains($"\"{acme.Id}:{user.Id}\"", body);
        Assert.DoesNotContain($"\"{projectOrg.Id}:{user.Id}\"", body);
    }

    [Fact]
    public async Task Login_TwoUsersOfDifferentOrganisations_OnTheSameProject_AreNotConfused()
    {
        // La propriété qui rend une console client unique possible : deux employés de sociétés
        // différentes, une seule page de connexion, deux organisations dans les jetons.
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa = false;
        await fixture.Db.SaveChangesAsync();

        var acme = (await fixture.Seed.CreateOrgAsync()).Item1;
        var beta = (await fixture.Seed.CreateOrgAsync()).Item1;
        var marie = await fixture.Seed.CreateUserAsync(list.Id, password: "P@ssw0rd!Test");
        var paul  = await fixture.Seed.CreateUserAsync(list.Id, password: "P@ssw0rd!Test");
        marie.OrgId = acme.Id;
        paul.OrgId  = beta.Id;
        await fixture.Db.SaveChangesAsync();

        var cMarie = await LoginAsync(project, marie);
        var cPaul  = await LoginAsync(project, paul);

        Assert.Contains($"\"{acme.Id}:{marie.Id}\"", fixture.Hydra.AcceptedLoginBody(cMarie)!);
        Assert.Contains($"\"{beta.Id}:{paul.Id}\"",  fixture.Hydra.AcceptedLoginBody(cPaul)!);
    }
}
