# Chantier : la surface backend que la console n'atteint pas

Document unique de ce chantier. Tout ce qui a été trouvé, décidé, fait et reste à faire est ici.
À supprimer quand la dernière case est cochée.

Dernière mise à jour : 2026-08-07.

---

## 1. Ce qui a déclenché ce chantier

Trois bugs signalés depuis la console :

1. **Créer un rôle rendait 400, sans rien afficher.** `ProjectRoles.handleCreate` n'avait pas de
   `catch` : la promesse partait en rejet non attrapé (`Uncaught (in promise) API error 400` dans
   les devtools), la boîte de dialogue restait ouverte, inchangée. Le champ nom minuscule la saisie
   et remplace les espaces par des soulignés — « Super Admin » arrive en `super_admin`, qui est
   réservé et refusé. Refus juste, invisible.
2. **Un nom de rôle en double rendait 500.** L'index unique est `(ProjectId, Name)` ; sans test
   d'existence, le doublon partait en `DbUpdateException` que rien n'attrape.
3. **Aucun bouton pour supprimer une user list.** `DELETE /org/userlists/{id}` existait,
   `api.ts` n'avait aucune fonction pour l'appeler, et `/admin/userlists/{id}` n'avait pas de
   DELETE du tout.

Ces trois-là sont **corrigés** (§4). Le troisième a ouvert la vraie question : combien d'autres
routes le backend expose-t-il sans que la console ne les appelle ?

---

## 2. Le motif de fond

> **La console a été écrite pour le super admin, et la portée organisation a été laissée derrière.**

Une opération existe en `/org` **et** en `/admin`, la console n'en câble qu'une, et le rôle servi
par l'autre se retrouve sans porte. Constaté huit fois à ce jour :

| Opération | Portée câblée | Portée orpheline | Conséquence |
|---|---|---|---|
| Supprimer une user list | `/org` | `/admin` | super admin sans porte (corrigé) |
| Assigner une user list | `/admin` | `/org` | contourné dans la page |
| Gérer SAML | `/admin` | `/org` | **org_admin sans porte** |
| Modifier un fournisseur SAML | — | les deux | **modification impossible** |
| Ajouter un membre à une liste | `/admin` | `/org` | org_admin sans porte |
| Éditer un membre | `/admin` | `/org` | org_admin sans porte |
| Éditer un projet | — | les deux | **édition impossible** |
| Modifier un rôle | — | `/project` | **modification impossible** |

Chaque lot doit se demander *pour quel rôle* la porte manque, pas seulement quelle route est
absente.

---

## 3. Méthode d'audit, et son défaut corrigé

194 routes extraites des attributs `[Route]` / `[Http*]` de `src/Controllers/*.cs`, comparées aux
appels `apiFetch` de `frontend/admin/src/**` (hors tests).

**Le premier audit comparait des chemins, pas des couples (verbe, chemin)** — un `PATCH` était donc
masqué par le `DELETE` qui partage son URL. Il annonçait 26 routes manquantes. Refait sur
(verbe, chemin), ternaires résolus par verbe : **38 lignes**, dont deux à écarter à la main :

- `GET /admin/impersonate` — appelé par `listImpersonations`, mais le chemin est
  `` `/admin/impersonate${q}` `` et la normalisation en fait un segment. **Faux positif.**
- `POST /admin/impersonate` — délibérément absent : `api.ts` documente que la console supervise les
  sessions d'impersonation mais n'en ouvre pas, parce qu'ouvrir une session frappe une credential.
  **Ne pas câbler** sans décision de sécurité explicite.

Soit **36 routes à câbler**, dont 2 faites.

Artefact restant à corriger dans le script avant la vérification finale : un `${q}` en fin de
chemin **sans slash** produit un faux positif (c'est ce qui masque `GET /admin/impersonate`).

---

## 4. Déjà corrigé — 2026-08-07

Backend :

- `ProjectOperations.CreateRoleAsync` — la création de rôle était écrite deux fois
  (`ProjectController`, `SystemAdminController`) et les copies divergeaient : la route système
  journalisait `role.created` avec une organisation nulle, donc invisible dans l'audit du locataire
  propriétaire. Unifiée, et le doublon rend maintenant **409 `role_name_exists`** au lieu de 500.
- `DELETE /admin/userlists/{id}` — créé, avec les deux mêmes refus que la route d'organisation
  (`cannot_delete_immovable`, `userlist_is_assigned_to_project`).
- `OrgController.DeleteUserList` — entrée d'audit `userlist.deleted` ajoutée. C'était la seule
  opération de cette surface qui détruisait des comptes en cascade sans rien écrire au journal.

Frontend :

- `ProjectRoles` — erreurs de création et de suppression affichées, avec un libellé par code
  (`role_name_reserved`, `role_name_exists`, `role_name_too_long`, `role_name_invalid_character`).
- `UserListMembersPanel` — l'assignation et le retrait de rôle n'avalent plus leur 400. Le refus
  le plus fréquent (`User is not in this project's assigned UserList`) est traduit en une phrase
  qui dit quoi faire.
- `UserLists` — bouton de suppression par ligne (absent, pas inerte, sur la liste immuable),
  confirmation qui dit que **les comptes de la liste partent avec elle** (la FK cascade), refus
  affichés, erreur de création affichée.

Tests ajoutés : `CreateRole_DuplicateName_Returns409`,
`AdminDeleteUserList_{Movable,AssignedToProject,NonExistent}`, et 3 tests de suppression de liste
côté SPA.

---

## 5. Règles de réalisation — valables pour tous les lots

1. **`api.ts` : une fonction par route.** `src/api.test.ts` contient un test de contrat qui échoue
   si un export n'a pas sa ligne dans `ROUTES`. Il a cassé deux fois pendant ce chantier, comme
   prévu. Déclarer méthode et corps.
2. **Toute erreur d'API est affichée.** Une promesse rejetée sans `catch` laisse l'opérateur devant
   un écran inchangé et un code qui n'existe que dans les devtools — c'est le bug d'origine.
   Reprendre le motif `apiErrorMessage` de `ProjectRoles.tsx`.
3. **Toute action destructive** passe par une `IamDialog` qui nomme ce qui est détruit *et ce qui
   part en cascade*.
4. **Montage** : chaque page dans `App.tsx`, et dans `Sidebar.tsx` si elle est atteignable au clic.
   `scope.test.ts` associe chaque route à sa portée et doit être étendu.
5. **Tests** : une suite par page — chargement, état vide, refus d'API affiché, chaque interaction.
   La fabrique `vi.mock('@/api')` **remplace** le module : tout export importé par la page doit y
   figurer, même inutilisé, sinon le fichier entier ne se lie plus. Deux échecs déjà dus à ça.
6. **Portée** : vérifier le `[RequireManagementLevel]` réel du contrôleur avant de choisir où
   monter la page. Ce qui est réservé au super admin ne s'affiche pas pour les autres, et
   réciproquement.

---

## 6. Les lots

### Lot A — Clients OAuth2 Hydra (4) · SystemAdmin · page neuve ✅

- [x] `GET /admin/hydra/clients`
- [x] `POST /admin/hydra/clients`
- [x] `GET /admin/hydra/clients/{id}`
- [x] `DELETE /admin/hydra/clients/{id}`

Page système « OAuth2 Clients » : tableau (client_id, nom, grant types, redirect_uris), détail,
création, suppression confirmée. Un client supprimé casse l'application qui s'en sert — la
confirmation doit le dire.

`pages/system/OAuth2Clients.tsx`, destination `oauth2-clients` du niveau déploiement, 18 tests.
Ce que le backend dit et que le plan ne disait pas : **la liste est le registre de Hydra, pas une
projection**. Elle contient donc aussi le client frappé pour chaque projet (`client_`) et pour
chaque compte de service (`sa_`) — deux préfixes que `SystemAdminController` réserve à la création
parce qu'ils portent un sens d'autorisation ailleurs (`IntrospectionController.IsServiceAccountCaller`,
la recherche de projet par `HydraClientId`). La page les marque et la confirmation de suppression
le dit : détruire l'un d'eux laisse un projet enregistré sans client, personne ne s'y connecte plus.
Autre point du contrôleur à afficher : demander `client_credentials` fait enregistrer le client en
`private_key_jwt`, toute autre combinaison en client public (`none`).

### Lot B — Rotation de clés (2) · SystemAdmin ✅

- [x] `GET /admin/key-rotation`
- [x] `POST /admin/key-rotation/reencrypt`

`KeyRotationPanel`, monté dans `pages/system/DeploymentSettings.tsx` (pas de page neuve, pas de
route à monter). Clé active et clés encore configurées en chips, décompte par colonne, bouton
désactivé quand il n'y a rien à faire, confirmation qui nomme les quatre colonnes réécrites, le
coût et l'idempotence. 11 tests.

Deux choses que le code disait et pas le plan : le POST **répond l'état d'après le balayage**, donc
aucune relecture n'est nécessaire — et un balayage partiel doit être dit comme tel, sans quoi
l'opérateur retire une clé encore nécessaire. Il n'y a par ailleurs **aucune progression
incrémentale** à afficher : `ReEncryptAsync` est une requête unique et bloquante ; « progression »
se réduit à un bouton en cours et au reste-à-faire renvoyé à la fin.

### Lot C — Réconciliation des grants (2) · SystemAdmin · page neuve ✅

- [x] `GET /admin/grant-reconcile`
- [x] `POST /admin/grant-reconcile/repair`

Outil de diagnostic : montrer le détail des divergences Keto ↔ base, pas seulement un compte.

Page `pages/system/GrantReconcile.tsx`, destination `grant-reconcile` (superOnly). Le tableau donne
chaque divergence entière — namespace, objet, relation, sujet — et **de quel côté elle manque**, avec
ce que chaque classe implique : un tuple sans ligne est un privilège vivant sans provenance, une
ligne sans tuple n'autorise rien mais sert encore les scopes au consentement. 13 tests.

Ce que le backend dit et que le plan ne disait pas : **la réparation refuse dans le corps, pas dans
le statut**. Au-delà de `GrantReconciler.MaxRepairsPerRun` (100) elle rend **200** avec
`repair_refused` renseigné et n'écrit rien. Un appelant qui ne guette qu'un rejet annonce donc une
réparation réussie de zéro grant — la page distingue les deux (bandeau `warn` contre `success`).

### Lot D — Chaîne d'audit (1) · SystemAdmin ✅

- [x] `GET /admin/audit-chain` — bouton « Verify integrity » dans l'en-tête du journal d'audit
      **système** (`level === 'deployment'` seul : la route marche toutes les organisations d'un
      coup). `components/AuditChainCheck.tsx` : tableau une ligne par chaîne, ruptures en tête,
      id de l'entrée fautive, compteurs `verified` / `unverifiable`, refus affiché. 10 tests.

Près du journal d'audit système. `AuditChain.cs:100` est un HMAC-SHA256 dont la clé vit hors base :
une rupture est un signal sérieux, l'UI ne doit pas la présenter comme une coche. Afficher la
première rupture et la hauteur de chaîne.

La réponse porte **trois** états, pas deux — le plan n'en annonçait qu'un. `intact` (aucun lien
rompu) et `fully_verified` (toute ligne survivante recalculée sous une clé que ce déploiement
détient) sont distincts : `unverifiable` compte les lignes antérieures à la chaîne, antérieures à
sa mise sous clé, ou écrites sous une clé retirée. Une chaîne intacte dont toutes les lignes sont
invérifiables ne prouve rien, et l'UI le dit ainsi. Autre écart : il n'existe pas de « hauteur de
chaîne » dans la réponse — sur une chaîne rompue, `verified + unverifiable` est ce qui a été
parcouru **avant** la rupture, ce que la note sous le tableau précise.

### Lot E — SAML (5) · Org + SystemAdmin

- [x] `GET /org/projects/{id}/saml-providers`
- [x] `POST /org/projects/{id}/saml-providers`
- [x] `PATCH /org/projects/{id}/saml-providers/{pid}`
- [x] `DELETE /org/projects/{id}/saml-providers/{pid}`
- [x] `PATCH /admin/projects/{projectId}/saml-providers/{providerId}`

Pas de page neuve : `pages/project/Authentication.tsx` gère déjà SAML via `/admin/...`. Deux
travaux — choisir la portée à l'appel (motif `isSystemCtx || isSuperAdmin ? adminX : orgX` déjà
utilisé dans `ProjectUsers.handleAssignList`), et **ajouter l'édition**, absente des deux portées.

Fait : les quatre appels passent par `samlApi`, choisi une fois selon la portée ; formulaire
d'édition partagé avec celui de création (`SamlFields`), qui envoie aussi `active` — seul le PATCH
l'accepte, la création force `true`. La suppression n'avait aucun `catch` : son refus est affiché.
11 tests.

### Lot F — Scopes OAuth2 d'un projet (4) · Org + SystemAdmin

- [x] `GET /org/projects/{id}/scopes`
- [x] `PUT /org/projects/{id}/scopes`
- [x] `GET /admin/projects/{id}/scopes`
- [x] `PUT /admin/projects/{id}/scopes`

**Les scopes ne sont éditables nulle part.** Section dans `ProjectSettings.tsx`.
`ProjectOperations.BuiltInScopes` (`openid`, `profile`, `offline_access`) sont implicites et ne
doivent pas être présentés comme supprimables. Le nom est validé serveur par une regex bornée —
afficher son refus.

Fait : section « OAuth2 Scopes » dans `ProjectSettings.tsx` — les trois implicites en puces sans
bouton de retrait, les scopes ajoutés retirables un à un, portée choisie à l'appel
(`isSystemCtx || isSuperAdmin`), refus `invalid_scope_names` recopié avec les noms refusés.
Le PUT remplace toute la liste personnalisée : ajout et retrait envoient la liste voulue. 8 tests.

### Lot G — Rôles en portée système (3) · SystemAdmin ✅

- [x] `GET /admin/projects/{id}/roles`
- [x] `POST /admin/projects/{id}/roles`
- [x] `DELETE /admin/projects/{id}/roles/{rid}`

`ProjectRoles.tsx` choisit maintenant sa portée sur `isSystemCtx`, comme `ProjectUsers`
(`adminListRoles` / `adminCreateRole` / `adminDeleteRole` contre `listRoles` / `createRole` /
`deleteRole`). 9 tests : chaque portée séparément, création, suppression, 409 `role_name_exists`
dans les deux, 400 `role_name_reserved`, repli sur `detail`, refus de suppression affiché.

**Divergences restantes entre les deux implémentations backend** — la création est unifiée sur
`ProjectOperations.CreateRoleAsync`, la lecture et la suppression ne le sont pas :

- `SystemAdminController.AdminDeleteRole` (`:846`) appelle `DeleteRelationTupleAsync` avec
  `role.Name` et `userId.ToString()`, là où `ProjectController.DeleteRole` et tout `KetoService`
  écrivent `role:{Name}` et `user:{Id}`. **Le tuple supprimé n'est pas celui qui a été écrit** : la
  ligne `user_project_roles` part, le grant Keto reste. Un rôle supprimé depuis la portée système
  laisse donc ses porteurs autorisés — c'est exactement ce que `GET /admin/grant-reconcile`
  (lot C) va compter.
- `AdminListRoles` (`:813`) ne trie pas (`ProjectController.ListRoles` fait `OrderBy(Rank)`) et ne
  rend pas 404 pour un projet inconnu : il rend `[]`, que la page affiche en « No roles defined
  yet ». La console retrie côté client, donc seul le 404 manquant se voit.
- Aucune des deux ne renvoie `created_at`, que `ProjectRoles` affiche : la colonne « Created » est
  vide dans les deux portées. Préexistant, hors lot.

### Lot H — Portée projet (4) · Project ✅

- [x] `GET /project/audit-log` — page « Audit log » au niveau projet (`pages/project/ProjectAuditLog.tsx`),
      destination ajoutée dans `scope.ts` donc présente dans l'arbre des deux formes d'URL. Le
      tableau d'audit est réutilisé ; son bouton d'export est devenu optionnel parce que la portée
      projet **n'a pas** de route d'export — un bouton qui rendrait 404 est pire que son absence.
- [x] `POST /project/cleanup` — bouton « Cleanup » dans `ProjectUsers`, `IamDialog` qui exige un
      `dry_run` d'abord ; le bouton destructif n'apparaît qu'après l'aperçu et nomme le compte
      (« Remove N role assignments »). Le serveur ne renvoie qu'un compte, pas la liste des
      attributions : c'est ce compte qui est montré.
- [x] `GET /project/users/{id}` — détail d'un membre en portée projet, avec ses rôles.
- [x] `DELETE /project/users/{id}/sessions` — révocation confirmée par une `IamDialog` qui nomme le
      membre et dit ce qui ne change pas (compte, rôles, mot de passe).

Le panneau des membres pour un **project_admin** : résolu sans nouvelle route backend.
`UserListMembersPanel` reste réservé à l'org admin (il lit `/org/userlists/{id}/users`, gardé en
OrgAdmin) ; un project_admin reçoit désormais un panneau de portée projet monté sous `!isOrgAdmin`,
servi par `GET /project/users`, `GET /project/users/{id}`, `POST|DELETE /project/users/{id}/roles`
et `DELETE /project/users/{id}/sessions` — toutes en `RequireManagementLevel(ProjectAdmin)`.
Ce qu'il n'a pas, faute de route en portée projet : créer/supprimer un compte de la liste, éditer un
profil, renvoyer une invitation, déverrouiller, lister les sessions ouvertes (`POST /project/users`
existe — lot K — mais rien n'ouvre l'édition ni le déverrouillage à ce niveau).

19 tests SPA ajoutés (12 sur `ProjectUsers`, 7 sur `ProjectAuditLog`) + 4 lignes de contrat.

### Lot I — Export des utilisateurs (1) · SystemAdmin ✅

- [x] `GET /admin/organizations/{id}/export/users` — bouton « Export users » dans `OrgDetail`,
      blob + ancre (la route exige un jeton porteur qu'une balise `<a>` n'enverrait pas), 429
      `export_rate_limited` affiché en clair. 3 tests.

### Lot J — Rotation du secret d'un webhook (1) · Webhook ✅

- [x] `POST /org/webhooks/{id}/rotate-secret` — entrée de menu, confirmation qui dit que les
      receveurs rejetteront les livraisons, secret montré une seule fois. 4 tests.
      Le message d'échec de création disait déjà « rotate the secret manually » : la route
      existait, aucun bouton ne l'appelait.

### Lot K — Ce que le verbe a révélé (11) — **à répartir ou à traiter en bloc**

- [x] `PATCH /project/roles/{id}` — **modifier un rôle** (description, rang)
- [x] `GET /admin/projects/{id}` — détail projet, portée système
- [x] `PATCH /admin/projects/{id}` — **doublon, non câblé** (voir ci-dessous)
- [x] `PATCH /org/projects/{id}` — **doublon, non câblé** (voir ci-dessous)
- [x] `GET /admin/organizations/{id}/projects` — projets d'une org, portée système
- [x] `POST /org/userlists/{id}/users` — `orgAddUserToList`. `UserListMembersPanel` choisit la
      portée comme il le fait déjà pour la lecture et le retrait ; l'ajout partait sur `/admin`,
      donc 403 pour un org_admin sur sa propre liste, avalé faute de `catch`.
- [x] `PATCH /org/userlists/{id}/users/{uid}` — `orgUpdateListUser`, même panneau. La LECTURE de
      la fiche suivait le même défaut (`adminGetUser` en toute portée) : elle passe par `orgGetUser`.
- [x] `GET /org/webhooks/{id}` — `getWebhook`, entrée « View details » dans `OrgWebhooks` :
      l'URL et la liste d'événements entières (le tableau les tronque) plus les dix dernières
      livraisons.
- [x] `GET /service-accounts/{id}/roles` — `listSaRoles`. `ServiceAccountDetail` rechargeait tout
      le compte après une assignation, repassant PAT et clé en squelette ; refus affichés.
- [x] `PATCH /org/admins/{id}` — `updateOrgListManager`, boîte « Change Grant » dans `OrgAdmins`,
      portée organisation seulement (aucun PATCH système sur `organizations/{id}/admins`).
      **Défaut backend** : la route n'écrit `ScopeId` que lorsqu'elle en reçoit un, donc elle ne
      sait pas l'effacer — passer une délégation projet en `org_admin` laisserait le tuple Keto sur
      `user:…|project:…`. L'UI refuse cette transition et le dit ; à corriger côté serveur.
- [ ] `POST /project/users` — créer un utilisateur depuis la portée projet

Les deux lignes SAML `PATCH` révélées par le même défaut sont rapatriées dans le lot E.

**Première moitié faite.** `updateRole` dans `ProjectRoles.tsx` : bouton par ligne, formulaire
description + rang prérempli sur le rôle, refus affiché dans le formulaire qui reste ouvert. Le nom
n'est pas modifiable — c'est ce que `DeleteRole` écrit dans Keto (`role:{name}`) pour chaque
porteur, un renommage laisserait les tuples derrière. Une seule route pour les deux portées : il
n'existe pas de `PATCH /admin/projects/{id}/roles/{rid}`, et le `?project_id=` de
`ProjectController` est honoré dès `ManagementLevel.OrgAdmin`, super-admin compris.
`adminGetProject` lisait `/org/projects/{id}` faute de GET système ; il existe désormais et la page
`SystemProjectDetail` y passe. `OrgDetail` liste par `adminListOrgProjects` au lieu de la branche
d'échappement super-admin de `/org/projects?org_id=`. 6 tests.

**Les deux `PATCH` projet sont des doublons — non câblés, délibérément.** `PATCH /project/info`,
`PATCH /org/projects/{id}` et `PATCH /admin/projects/{id}` lient le *même* `ProjectUpdateRequest`
et appellent le *même* `ProjectUpdate.ApplyAsync` (`ProjectUpdate.cs` le documente en tête). Ils ne
diffèrent que par le niveau exigé et la façon de trouver le projet — et `/project/info` sert déjà
les trois niveaux, car son `?project_id=` est ouvert dès OrgAdmin. Câbler les deux autres serait la
troisième porte que le lot G refuse.

Une divergence à corriger côté backend, dans l'autre sens que d'habitude : **seul
`ProjectController.UpdateInfo` écrit une entrée d'audit `project.updated`.** `OrgController.
UpdateProject` et `SystemAdminController.AdminUpdateProject` modifient un projet — nom, politique
de mot de passe, `redirect_uris`, allowlist IP — **sans rien journaliser**. La route que la console
utilise est donc la seule des trois qui laisse une trace ; les deux autres sont atteignables par
PAT et par `/api/manage`.

---

## 7. Vérification de fin de chantier — 2026-08-07

- [x] Audit de couverture rejoué (verbe + chemin, artefact `${q}` corrigé) : **38 → 5 non
      couvertes**, dont aucune n'est un manque :
      - `POST /admin/impersonate` — exclusion délibérée (ouvrir une session frappe une credential)
      - `PATCH /admin/projects/{id}`, `PATCH /org/projects/{id}`, `GET /org/projects/{id}` —
        doublons purs de `PATCH|GET /project/info`, même `ProjectUpdateRequest`, même
        `ProjectUpdate.ApplyAsync`, et `/project/info` couvre déjà les trois niveaux. Les câbler
        aurait été la troisième porte que ce chantier dénonce partout ailleurs.
- [x] `npx vitest run` — **1432 tests, 59 fichiers, vert** (1265 avant, +167)
- [x] `npx tsc -b --noEmit` — propre
- [x] `npx eslint src` — **27 erreurs, identique à la ligne de base mesurée à `HEAD`** (mêmes
      règles, mêmes comptes). Aucune régression : les 22 `set-state-in-effect` sont l'idiome
      `useEffect(load)` préexistant.
- [x] `dotnet test` — **1641 tests, vert** (1637 avant)
- [x] SonarQube — **0 violation ouverte, quality gate OK**
- [ ] `docs/API.md` à jour
- [ ] Rebuild + `./deploy/deploy-dev.sh --dev` — le bundle déployé est identique à `dist`, donc
      rien n'est visible avant redéploiement
- [ ] Supprimer ce fichier

### Bugs trouvés en chemin et corrigés

1. **`AdminDeleteRole` supprimait un tuple Keto inexistant** — il passait `role.Name` et l'id nus
   là où `KetoService` écrit `role:{nom}` / `user:{id}`. Keto répond 204 à une suppression sans
   correspondance : la ligne partait, **le grant restait**, un rôle supprimé en portée système
   laissait ses porteurs autorisés. Le test existant ne regardait que la ligne SQL — d'où
   `DeletedTupleUrls` sur le `KetoStub`, et la régression
   `DeleteRole_RevokesTheKetoGrantItsHoldersActuallyHave`.
2. **Deux des trois `PATCH` projet ne journalisaient rien.** `project.updated` est un événement
   webhook souscriptible : un locataire surveillant ses projets n'entendait jamais parler des
   changements passés par `/org/projects/{id}` ou `/admin/projects/{id}` — nom, politique de mot de
   passe, `redirect_uris`, allowlist IP. Les trois passent par `ProjectUpdate.SaveAndAuditAsync`,
   qui garde l'ordre sauvegarde-puis-audit (`RecordAsync` ouvre son propre `DbContext`).
3. **Les scopes d'`OrgController` étaient les seules routes du contrôleur sans échappatoire
   super-admin** — un 404 sur un projet réel, indiscernable d'un projet inexistant.
4. **`PATCH /org/admins/{id}` ne pouvait pas effacer une portée** : promouvoir un `project_admin`
   en `org_admin` réécrivait le tuple sur `user:…|project:…` sous la relation `org_admin`. Seul
   `project_admin` porte une portée — l'invariant est maintenant écrit.
5. `AdminListRoles` rendait `[]` sur projet inconnu (404 en portée projet) et ne triait pas.
6. `S107` sur `CreateRoleAsync` (9 paramètres) — champs groupés dans `NewRole`, comme
   `UserListOperations` le fait avec `UserListDeps`.

### Restes signalés, non corrigés (décision à prendre)

- **L'URL de métadonnées SP affichée par la console est fausse.** `Authentication.tsx` montre
  `${origin}/admin/projects/{id}/saml/metadata` avec « donnez ceci à votre IdP ». Ce chemin
  n'existe dans aucun contrôleur : le seul point est `GET /auth/saml/metadata`, anonyme, construit
  sur `PublicUrl` et non par projet. Un test épingle la chaîne actuelle.
- **Les scopes répondent 200 même quand la mise à jour du client Hydra échoue** (warning
  journalisé). La console affiche un succès alors que Hydra peut être en retard sur la base.
- `PATCH /org/admins/{id}` n'a aucune contrepartie système — ici c'est le super admin qui n'a pas
  de porte.
- Ni `GET /admin/projects/{id}/roles` ni `/project/roles` ne renvoient `created_at`, que la page
  affiche : la colonne « Created » est vide dans les deux portées.
- `POST /project/cleanup` ne renvoie qu'un compte, pas le détail : l'aperçu `dry_run` montre un
  nombre, pas une liste.

## 8. En marge — deux dossiers ouverts, hors de ce chantier

**Bug de redirection après connexion.** Après une authentification réussie l'opérateur reste sur
la page de login ; un second essai passe. Non reproduit. Deux pistes, par ordre de vraisemblance :

1. `values.dev.yaml` déclare `publicUrl: http://iam.localhost` et
   `adminUrl: https://admin.iam.localhost` — même domaine enregistrable, **schémas différents**.
   Le *schemeful same-site* compte http et https comme deux sites, donc le cookie de session Hydra
   en `SameSite=Strict` n'est pas envoyé quand la console démarre l'autorisation. Le commentaire en
   tête de ce fichier décrit le symptôme voisin (« Hydra ne voyait pas de session, ne pouvait pas
   skip, redemandait le mot de passe ») et l'a corrigé par le sous-domaine, sans toucher au schéma.
2. `safeNavigate` refuse le `redirect_to` : il log alors
   `Refusing to navigate to untrusted redirect_to:` en console. **Test à cinq secondes qui tranche.**

**Durcissement de sécurité, arrêté à l'étape 3.** `.security-hardening/01` et `02` sont écrits ;
l'agent de revue d'architecture est mort sur une limite de dépense. Trois High, dont l'écouteur
admin servi sur l'entrypoint Traefik public (`ingress.admin.className` existe et n'est jamais posé
en prod), et une chaîne de prise de contrôle confirmée sous condition que le CNI n'applique pas les
NetworkPolicy.
