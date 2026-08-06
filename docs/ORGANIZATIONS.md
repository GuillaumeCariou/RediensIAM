# Organisations : appartenance de l'utilisateur

Comment un jeton apprend de quelle organisation vient son porteur, et pourquoi ce
n'est plus le projet qui le décide.

Statut : **livré**. `User.OrgId`, migration `UserOrganisationMembership`.

---

## 1. Le problème

Jusqu'ici l'organisation d'un jeton venait du **projet** :

```csharp
org_id = project.OrgId
```

C'est juste tant qu'un projet sert **un seul** locataire — le modèle d'origine, où
une organisation possède ses projets et chaque projet a sa page de connexion.

Ça cesse de l'être dès qu'un projet en sert plusieurs. Une console client unique,
une page de connexion, des employés de sociétés différentes derrière : tous
auraient porté l'organisation **propriétaire du projet**. L'isolation aurait
disparu au niveau du jeton — avant Keto, avant `IsInCallerScopeAsync`, avant tout
ce qui s'appuie dessus.

## 2. Pourquoi pas un projet par client

C'était la première réponse, et elle bute sur un fait mécanique : **un serveur de
ressources déclare un seul identifiant de locataire** à `/api/introspect`. Dix
clients, dix projets, une passerelle : neuf clients ne peuvent pas se connecter.

Le contourner demanderait une passerelle par client — c'est-à-dire un déploiement
par client. C'est exactement le mode self-hosted, où ce modèle reste naturel. En
mutualisé, il ne tient pas.

## 3. Ce que fait l'industrie

**Keycloak** — `organizations/intro.adoc` :

> *an organization represents these third parties […] It enables **multi-tenancy
> within a realm** so that users can have access to protected resources from a
> realm but with a more restricted and controlled context, that context being the
> organization to which they belong.*

Keycloak a ajouté les Organizations parce qu'un realm par client ne passe pas à
l'échelle. Un realm = une page de connexion = ce qu'est un projet ici.

**Ory** — `kratos/organizations/organizations.mdx` :

> *Organizations serve as a grouping mechanism for users **within a single Ory
> project**.*

Dans les deux cas l'organisation est une **appartenance de l'utilisateur**, pas
une propriété de la surface de connexion.

## 4. Ce qui a changé

Une colonne, une fonction, six appels.

```csharp
/// L'organisation de l'UTILISATEUR si elle est nommée, celle du PROJET sinon.
private static Guid EffectiveOrgId(User user, Project project) => user.OrgId ?? project.OrgId;
private static string SubjectFor(User user, Project project) => $"{EffectiveOrgId(user, project)}:{user.Id}";
```

Les six sites qui posaient `project.OrgId` — connexion par mot de passe,
inscription, connexion sociale, MFA — passent par elles.

⚠ **Le sujet et le contexte doivent nommer la même organisation.** Deux chemins
distincts les relisent — `ParseSubjectOrgId` sur le sujet Hydra, `CtxOrgId` sur le
contexte — et les désaccorder ferait diverger la portée pinée de celle du jeton.
C'est pourquoi une seule fonction produit les deux.

## 5. Le repli n'est pas une commodité

`user.OrgId == null` → l'organisation vient du projet, exactement comme avant.

Tout déploiement existant est dans ce cas : la colonne est nulle partout après
migration. **Aucun comportement ne change** pour ce qui existe — et c'est vérifié,
pas supposé : `Login_UserWithoutOrgId_CarriesTheProjectOrganisation`, plus les
1620 tests de la suite, inchangés.

## 6. Ce que ce n'est PAS

**Ce n'est pas `OrgRole`.** Celui-ci porte les rôles de *management* — `org_admin`,
`project_admin`. Un employé ordinaire n'en a aucun, et n'avait donc, avant ce
champ, **aucun lien vers son organisation**. C'était le manque réel.

**Ce n'est pas l'organisation de la liste.** `UserList.OrgId` dit qui possède la
liste. Dans un modèle à liste partagée, c'est l'exploitant — pas le client.

## 7. Le modèle que ça rend possible

```
Organisation Yandee
├── UserList « clients »          ← tous les utilisateurs de tous les clients
│     ├── projet yandee-client    ← le portail, UNE page de connexion
│     └── projet yandee-suite     ← la Suite, MÊME liste
│                                   (UserList.Projects est une collection)
├── UserList « yandee »
│     └── projet yandee-gestion   ← les opérateurs
│
└── Organisations ACME, BETA, …   ← groupement. Chaque utilisateur en nomme une.
```

Marie d'ACME se connecte sur la page unique. Son jeton porte `org_id = ACME`. Keto,
`IsInCallerScopeAsync` et l'en-tête `X-IAM-Org` des passerelles fonctionnent sans
modification.

## 8. La suppression d'une organisation

La clé étrangère est en `Restrict`, pas `Cascade`. Supprimer une organisation ne
doit pas effacer en silence les comptes de ses membres : c'est une décision qui se
prend explicitement, avec la liste sous les yeux.

## 9. Ce qui reste ouvert

**L'identity-first login** — demander le courriel avant le mot de passe pour router
vers le fournisseur SSO d'une organisation. **Pas nécessaire aujourd'hui** : la
recherche au login est déjà bornée à la liste du projet
(`u.UserListId == project.AssignedUserListId && u.Email == emailLower`), donc une
liste partagée trouve directement l'utilisateur.

Il le deviendra au premier client qui voudra son propre SAML : on ne peut plus
afficher un champ mot de passe avant de savoir qui c'est. Il faudra alors des
`domains` sur `Organisation`, et une résolution **par domaine de courriel** —
jamais par balayage des utilisateurs, qui serait un oracle d'énumération.
