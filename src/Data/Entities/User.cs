namespace RediensIAM.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public Guid UserListId { get; set; }

    /// <summary>
    /// L'organisation dont cet utilisateur est membre, quand elle diffère de celle qui possède
    /// sa liste.
    ///
    /// Jusqu'ici l'organisation d'un jeton venait du PROJET (<c>project.OrgId</c>). C'est juste
    /// tant qu'un projet sert un seul locataire — le modèle d'origine. Ça cesse de l'être dès
    /// qu'un projet en sert plusieurs : une console client unique, une page de connexion, et des
    /// employés de sociétés différentes derrière. Tous auraient porté l'organisation propriétaire
    /// du projet, et l'isolation aurait disparu au niveau du jeton, avant même d'atteindre Keto.
    ///
    /// C'est le motif que Keycloak nomme <i>Organizations</i> — « multi-tenancy within a realm » —
    /// et qu'Ory décrit comme « a grouping mechanism for users within a single project ». Dans les
    /// deux cas l'organisation est une APPARTENANCE de l'utilisateur, pas une propriété de la
    /// surface de connexion.
    ///
    /// <c>null</c> = comportement historique, l'organisation vient du projet. Aucun déploiement
    /// existant ne change de comportement : c'est ce que garantit le repli, pas une intention.
    ///
    /// ⚠ Ce n'est PAS <c>OrgRole</c>. Celui-ci porte les rôles de MANAGEMENT (org_admin,
    /// project_admin) ; un employé ordinaire n'en a aucun et n'avait donc, avant ce champ,
    /// aucun lien vers son organisation.
    /// </summary>
    public Guid? OrgId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Discriminator { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string? PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public string? Phone { get; set; }
    public bool PhoneVerified { get; set; }
    public bool TotpEnabled { get; set; }
    public string? TotpSecret { get; set; }
    public bool WebAuthnEnabled { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public bool NewDeviceAlertsEnabled { get; set; } = true;
    public Dictionary<string, object> Metadata { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public UserList UserList { get; set; } = null!;
    public Organisation? Organisation { get; set; }
    public ICollection<UserProjectRole> ProjectRoles { get; set; } = [];
    public ICollection<OrgRole> OrgRoles { get; set; } = [];
    public ICollection<WebAuthnCredential> WebAuthnCredentials { get; set; } = [];
    public ICollection<BackupCode> BackupCodes { get; set; } = [];
    public ICollection<EmailToken> EmailTokens { get; set; } = [];
    public ICollection<UserSocialAccount> SocialAccounts { get; set; } = [];
}
