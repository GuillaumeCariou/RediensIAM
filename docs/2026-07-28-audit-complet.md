# RediensIAM — Audit complet (code, sécurité, multi-tenant, intégration)

**Date** : 2026-07-28
**Périmètre** : lecture intégrale de `src/` (10 306 lignes C#), `frontend/admin`, `frontend/login`,
`deploy/`, `tests/`, `README.md`, `docs/ARCHITECTURE.md`.
**Complète** : [`2026-07-28-findings-securite-deploiement.md`](2026-07-28-findings-securite-deploiement.md)
(findings A→E issus de l'intégration yandee-web ; non répétés ici, toujours ouverts).

Sévérité : 🔴 critique · 🟠 haute · 🟡 moyenne · ⚪ basse.

---

## 0. Comment lire ce document

Chaque finding porte un identifiant (`SEC-nn` / `FUNC-nn`) et — quand il est reproductible —
**un test qui échoue aujourd'hui** dans `tests/RediensIAM.IntegrationTests/Tests/Regression/`.

```bash
dotnet test tests/RediensIAM.IntegrationTests/ --filter "FullyQualifiedName~Tests.Regression"
```

**État après correction : 1093 tests, 1093 verts.** Les 34 tests de régression échouaient à la
rédaction (un par finding ouvert) ; ils passent tous maintenant. Zéro warning de build, zéro
paquet NuGet vulnérable.

Les findings marqués **OUVERT** ci-dessous n'ont pas de test rouge : ils demandent une décision
d'architecture ou un composant qui n'existe pas encore (voir §8).

---

## 1. Synthèse exécutive

RediensIAM a de vraies qualités : Argon2id paramétré, HKDF par usage, AES-GCM pour les secrets
au repos, anti-replay TOTP, rate-limiting Lua atomique, revalidation SSRF à la livraison des
webhooks, `ForwardedHeaders` fail-closed en production, NetworkPolicies, securityContext durci,
1059 tests d'intégration sur containers réels. Ce n'est pas un prototype.

Mais l'objectif annoncé — **« permettre l'utilisation du gateway pour centraliser le contrôle
des identités, de l'authz et de l'authn », plug-and-play** — n'est pas atteint aujourd'hui, pour
trois raisons structurelles :

1. **L'isolation entre tenants n'est pas tenue sur les chemins d'authentification.** Le
   `project_id` qui détermine « à quel tenant appartient cette connexion » est lu depuis la
   requête d'autorisation, donc contrôlé par l'appelant. `POST /auth/login` le recoupe avec les
   métadonnées du client OAuth2 ; **aucun autre point d'entrée ne le fait** (thème, page de login,
   démarrage social, démarrage SAML). C'est SEC-02, et c'est bloquant.
2. **Le token n'est pas vérifié pour l'audience.** N'importe quel access token émis par le Hydra
   du déploiement — y compris pour l'application d'un tenant — est accepté par l'API
   d'administration de l'IAM. C'est SEC-01.
3. **Il n'y a pas de SDK, ni de contrat d'intégration stable.** Un resource server externe (le
   gateway) n'a aucune surface propre pour valider un token : soit il tape le port admin de
   Hydra, soit il fait du JWKS local sans les checks live. C'est le finding B de la note du
   28/07, et c'est ce qui empêche le « plug-and-play ». Voir §5.

Verdict franc : **le produit n'est pas prêt pour du multi-tenant hostile** (tenants qui ne se
font pas confiance). Il est utilisable pour du multi-tenant *coopératif* (filiales d'une même
entreprise) une fois SEC-01/02 corrigés.

---

## 2. Findings sécurité

### SEC-01 🔴 — Confusion d'audience : tout token Hydra ouvre l'API d'administration

**Fichier** : `src/Services/HydraService.cs:289-332`

`ValidateJwtAsync` introspecte le token puis lit `ext.user_id`, `ext.org_id`, `ext.roles`.
Elle n'inspecte **jamais** `client_id`, ni `aud`, ni `token_use`, ni `scope` — alors que la
réponse d'introspection de Hydra (`IntrospectedOAuth2Token`) les expose tous.

Conséquences :

- Un token émis pour l'application d'un tenant (`client_{projectId}`) est accepté tel quel par
  `/admin/*`, `/org/*`, `/project/*`, `/api/manage/*`. Le seul rempart restant est le contenu de
  `ext.roles` — c'est-à-dire une donnée de session, pas une frontière d'audience.
- Hydra rapporte un **refresh token** comme `active: true` quand on introspecte sans
  `token_type_hint`. Un refresh token présenté en `Authorization: Bearer` est donc accepté comme
  un access token.
- Un déploiement qui partage son Hydra avec d'autres applications (cas explicite du gateway
  yandee) transforme chacune de ces applications en vecteur d'accès à l'IAM.

**Tests** : `CrossTenantRegressionTests.AdminApi_TokenIssuedToTenantClient_IsRejected`,
`…ProtectedApi_RefreshTokenPresentedAsBearer_IsRejected`, plus le contre-test
`…AdminApi_TokenIssuedToAdminClient_IsAccepted` qui garantit la non-régression.

**Correctif** : envoyer `token_type_hint=access_token`, refuser si
`token_use != "access_token"`, et exiger que `client_id` (ou `aud`) corresponde à l'audience
attendue de la surface appelée — `client_admin_system` pour `/admin` et `/org`, le client du
projet pour `/account`. Rendre l'audience configurable (`Hydra:ExpectedAudience`).

---

### SEC-02 🔴 — Isolation multi-tenant contournable via `project_id`

**Fichiers** : `src/Controllers/AuthController.cs:1389-1400` (`ExtractProjectId`), `:168-192`
(`GetTheme`), `:144-166` (`BuildLoginPageInfoAsync`), `:1103-1125` (`OAuthStart`) ;
`src/Controllers/SamlController.cs:29-60` (`Start`).

`ExtractProjectId` lit `project_id` dans `oidc_context.extra`, sinon dans la query string brute
de `request_url`. Cette valeur vient de l'URL d'autorisation, donc de l'appelant.

`POST /auth/login` fait bien le recoupement :

```csharp
var registeredProjectId = req.Client?.Metadata?.GetValueOrDefault(CtxProjectId)?.ToString();
if (registeredProjectId != null && registeredProjectId != projectId) { /* reject */ }
```

Les quatre autres entrées ne le font pas. Chaîne d'attaque, du moins grave au plus grave :

1. `GET /auth/login/theme` et `GET /auth/login` → **divulgation** de la configuration de login
   d'un autre tenant (nom du projet, politique de mot de passe, providers configurés, leurs
   `client_id`).
2. `GET /auth/oauth2/start?login_challenge=<le mien>&provider_id=google` avec le `project_id` de
   la victime → le backend construit l'URL d'autorisation avec **les credentials IdP de la
   victime**, stocke *mon* `login_challenge` dans l'état OAuth, et au callback appelle
   `AcceptLoginAsync(monChallenge, subject = "{orgVictime}:{userVictime}")`. **Mon client OAuth2
   reçoit un code d'autorisation pour un utilisateur d'un autre tenant.**
3. `GET /auth/saml/start?idp_id=<IdP de la victime>` → même résultat via SSO entreprise.
   `idp_id` n'est jamais recoupé avec le projet du challenge.

C'est une évasion complète de tenant, exploitable par tout titulaire d'un client OAuth2 sur le
déploiement — c'est-à-dire tout tenant.

Note aggravante : même sur `/auth/login`, la garde est conditionnée à
`registeredProjectId != null`. Un client sans métadonnée `project_id` (créé à la main via
`POST /admin/hydra/clients`, qui n'en pose aucune) désactive silencieusement le contrôle.

**Tests** : `CrossTenantRegressionTests.LoginTheme_ProjectIdNotOwnedByClient_IsRejected`,
`…LoginPageInfo_…`, `…OAuthStart_…`, `…SamlStart_IdpFromForeignProject_IsRejected`.

**Correctif** : une seule fonction de résolution, utilisée partout, qui dérive le projet de
`client.metadata.project_id` **en priorité** et n'accepte le `project_id` de la requête que s'il
est identique. Refuser (au lieu de faire confiance) quand la métadonnée est absente. Pour SAML,
exiger `idp.ProjectId == projetDuChallenge`.

---

### SEC-03 🟠 — Open redirect par backslash

**Fichier** : `src/Services/RedirectValidator.cs:35-40`

```csharp
if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
```

`/\evil.com` passe le test (commence par `/`, pas par `//`) et est renvoyé tel quel dans
`Location`. Les navigateurs normalisent `/\` en `//` : la redirection part vers `evil.com`.
Le SPA de login rejette déjà les backslashes (`frontend/login/src/safeNavigate.ts:11`) — c'est
le serveur qui ne le fait pas, alors que c'est lui qui écrit l'en-tête.

**Test** : `RedirectAndSsrfRegressionTests.TryReconstruct_BackslashRelativePath_IsRejected` (4 cas).

**Correctif** : rejeter tout `url` contenant `\` avant le court-circuit relatif.

---

### SEC-04 🟠 — Trois contournements du filtre SSRF des webhooks

**Fichier** : `src/Controllers/WebhookController.cs:282-296` (`IsPrivateIp`)

```csharp
if (ip.AddressFamily == AddressFamily.InterNetworkV6)
{
    if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
    return ip.Equals(IPAddress.IPv6Loopback);   // ← sortie anticipée
}
```

1. **IPv4 mappé en IPv6** : `::ffff:10.0.0.1`, `::ffff:169.254.169.254` sortent par cette branche
   sans jamais atteindre les tests IPv4. Même hôte atteint, filtre contourné.
2. **fc00::/7 (unique-local)** : `IsIPv6SiteLocal` ne couvre que `fec0::/10`, déprécié.
   `fd00::1` passe.
3. **100.64.0.0/10 (CGNAT)** : non couvert — **c'est la plage Tailscale**, et ce déploiement
   expose son ingress admin sur `100.64.0.3` (`deploy/`). Un webhook vers `https://x/` résolvant
   en `100.64.0.3` atteint la console d'administration.

Quatrième problème, hors `IsPrivateIp` : le `HttpClient` nommé `"webhook"` suit les redirections
par défaut. Une URL publique qui répond `302 → http://169.254.169.254/…` contourne la validation,
qui n'est faite que sur l'URL initiale.

**Tests** : `RedirectAndSsrfRegressionTests.IsPrivateIp_Ipv4MappedIpv6_IsBlocked`,
`…UniqueLocalIpv6…`, `…CgnatRange…`, `…AddressFamilyCoverage_IsExplicit`, plus
`IsPrivateIp_PublicAddresses_StillAllowed` en garde-fou.

**Correctif** : normaliser via `MapToIPv4()` avant test ; ajouter `fc00::/7`, `100.64.0.0/10`,
`192.0.0.0/24`, `198.18.0.0/15`, `::/128`, `2001:db8::/32` ; configurer
`AllowAutoRedirect = false` sur le client webhook.

---

### SEC-05 🟠 — Second facteur WebAuthn non lié à l'utilisateur en attente

**Fichier** : `src/Controllers/AuthController.cs:1445`

```csharp
var cred = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == response.RawId);
```

La recherche est **globale**. La propriété d'appartenance n'est déléguée qu'à
`IsUserHandleOwnerOfCredentialIdCallback`, que Fido2NetLib n'invoque que si la réponse porte un
`userHandle` — ce que ne font pas les credentials non-découvrables (clé de sécurité classique en
second facteur). Un attaquant qui connaît le mot de passe de la victime et possède **n'importe
quel authenticator enregistré sur l'instance** valide le second facteur de la victime.

Aggravant : `WebAuthnOptions` demande `UserVerification = Preferred` et non `Required` —
possession sans vérification suffit. `ARCHITECTURE.md` affirmait l'inverse (corrigé).

**Test** : `AuthHardeningRegressionTests.WebAuthnVerify_CredentialOwnedByAnotherUser_IsRejected`
(la victime atteint l'étape MFA, l'attaquant soumet son propre credential → doit renvoyer 401).

**Correctif** : `c.CredentialId == response.RawId && c.UserId == uid`, et passer
`UserVerification` à `Required`.

---

### SEC-06 🟠 — Oracle d'énumération de comptes sur le reset de mot de passe

**Fichier** : `src/Controllers/AuthController.cs:847-883`

Le code prend soin d'égaliser le temps de calcul (écritures Redis factices dans la branche
« utilisateur inconnu »)… puis trahit le résultat dans le corps de la réponse :

```csharp
if (user != null) return Ok(new { session_id = sessionId });
// …
return Ok(new { });          // ← pas de session_id
```

Adresse connue ⇒ `{"session_id":"…"}`. Adresse inconnue ⇒ `{}`. Énumération triviale de la base
utilisateurs d'un tenant, à travers un endpoint public non authentifié.

**Test** : `AuthHardeningRegressionTests.PasswordResetRequest_ExistingAndUnknownEmail_ResponsesAreIndistinguishable`

**Correctif** : renvoyer un `session_id` dans les deux cas (la branche « void » en génère déjà un,
il suffit de le renvoyer) ; l'étape de vérification échouera naturellement.

---

### SEC-07 🟠 — Le compteur de rate-limit par IP est effaçable à volonté

**Fichiers** : `src/Services/LoginRateLimiter.cs:50-54`, appelé depuis
`AuthController.CompleteLoginAsync` et `AdminLogin`

```csharp
public async Task ResetAsync(string ipAddress, Guid userId, string keyPrefix = "login")
{
    await _db.KeyDeleteAsync($"rate:{keyPrefix}:{ipAddress}");      // ← compteur partagé
    await _db.KeyDeleteAsync($"rate:{keyPrefix}:user:{userId}");
}
```

Toute connexion réussie efface le compteur **par IP**, qui est partagé entre toutes les cibles.
Un attaquant disposant d'un compte légitime sur n'importe quel tenant alterne « 4 essais sur la
victime / 1 connexion sur mon compte » et brute-force indéfiniment depuis la même adresse.
Le budget par IP ne borne plus rien.

**Test** : `AuthHardeningRegressionTests.RateLimiter_SuccessfulLogin_DoesNotClearSharedIpCounter`

**Correctif** : ne réinitialiser que le compteur par utilisateur. Le compteur par IP doit expirer
uniquement par TTL.

---

### SEC-08 🟠 — Révocation des PAT sans effet pendant 5 minutes

**Fichier** : `src/Services/PatService.cs:50-118`

L'introspection d'un PAT est mise en cache sous `pat:{sha256}` pour `Cache:PatTtlMinutes`
(5 min par défaut). Seul `RevokePat` évince la clé. Donc :

- désactiver un service account (`Active = false`) → le token reste valide 5 minutes ;
- suspendre une organisation → idem, tous ses PAT restent valides ;
- retirer un rôle au service account → l'ancien rôle reste porté par le cache.

C'est un incident de réponse à incident : « on coupe l'accès » ne coupe rien pendant 5 minutes.
`ARCHITECTURE.md` affirmait « SA active + org active checked on every introspect » — faux, c'est
vérifié seulement au cache miss (corrigé dans le doc).

**Tests** : `PlatformRegressionTests.PatIntrospection_AfterServiceAccountDeactivated_IsRejectedImmediately`,
`…AfterOrganisationSuspended_…`

**Correctif** : indexer les clés de cache par service account (`pat:sa:{saId}` → set de hashes)
et purger sur `Active=false`, suspension d'org, et changement de rôle ; ou baisser le TTL à
~30 s et documenter la fenêtre.

---

### SEC-09 🟡 — Le changement de mot de passe ignore la politique du tenant

**Fichier** : `src/Controllers/AccountController.cs:82-83`

```csharp
if (body.NewPassword.Length < 8)
    return BadRequest(new { error = "password_too_short", min_length = 8 });
```

Minimum codé en dur, pas de vérification majuscule/chiffre/spécial, pas de contrôle HIBP — alors
que `Project.MinPasswordLength`, `PasswordRequire*` et `CheckBreachedPasswords` sont appliqués à
l'inscription (`AuthController.ValidatePasswordPolicyAsync`) et à la création par un admin
(`ProjectController.CreateUser`). Un utilisateur contourne la politique de son tenant en changeant
son mot de passe après coup. Même trou dans `UserHelpers.ApplyUpdate` (reset admin).

**Tests** : `AuthHardeningRegressionTests.ChangePassword_BelowProjectMinimumLength_IsRejected`
+ `…ChangePassword_MeetingProjectPolicy_IsAccepted`

**Correctif** : extraire `ValidatePasswordPolicyAsync` dans un service partagé et l'appeler depuis
les quatre chemins d'écriture de mot de passe.

---

### SEC-10 🟡 — SSRF authentifiée via la découverte OIDC

**Fichier** : `src/Services/SocialLoginService.cs:373-396`

`GetDiscoveryAsync` exige HTTPS hors localhost, mais **n'applique pas** le filtre
`WebhookUrlValidator` que le reste du code utilise. Un org-admin qui configure un provider OIDC
avec `issuer_url = https://<hôte-interne>` fait émettre au serveur une requête vers le réseau
interne. Le document récupéré est ensuite mis en cache **sans expiration** et sans validation de
l'`issuer`.

**Correctif** : réutiliser `IWebhookSsrfValidator`, désactiver le suivi de redirection, vérifier
que `issuer` du document correspond à l'URL demandée, et poser un TTL.

---

### SEC-11 🟡 — L'autorisation ne consulte jamais Keto ni la base

Déjà documenté comme **finding A** dans la note du 28/07. Rappelé ici car il conditionne SEC-08
et SEC-01 : `RequireManagementLevelAttribute` autorise sur `ext.roles` uniquement. Une révocation
de rôle ou une suspension d'org n'a d'effet qu'à l'expiration du token.

---

### SEC-12 🟡 — Certificat SAML optionnel

**Fichier** : `src/Services/SamlService.cs:76-85`

En mode « SsoUrl explicite », si `CertificatePem` est vide, `SignatureValidationCertificates`
reste vide. La branche métadonnées, elle, refuse explicitement ce cas
(« metadata contains no valid signing certificates »). L'asymétrie est dangereuse : la validation
de signature est la **seule** défense d'intégrité d'une assertion SAML.
`OrgController.CreateSamlProvider` n'exige pas `CertificatePem` quand `SsoUrl` est fourni.

**Correctif** : rendre `CertificatePem` obligatoire dès lors que `MetadataUrl` est absent, et
échouer explicitement si la liste de certificats est vide.

---

### SEC-13 ⚪ — Divers, à corriger en lot

| # | Fichier | Constat |
|---|---|---|
| a | `Program.cs:371-373` | CSP admin sans `default-src` : `connect-src`, `img-src`, `font-src` retombent sur `*`. Ni `base-uri` ni `form-action`. |
| b | `Program.cs:173-196` | `EnsureDbSchemaAsync` avale l'exception finale : le pod démarre avec un schéma cassé au lieu de fail-fast. |
| c | `Program.cs:266-282` | Bootstrap : le tuple Keto `super_admin` est écrit **avant** `SaveChangesAsync`. Si l'écriture DB échoue, il reste un tuple super-admin orphelin. |
| d | `SystemAdminController.cs:405-421` | `AssignOrgAdmin` accepte `body.Role` arbitraire (aucune validation contre l'ensemble connu) et l'écrit comme relation Keto. |
| e | `OrgController.cs:611-641` | `UpdateOrgListManager` court-circuite `KetoService.AssignManagementRoleAsync` : aucun contrôle de niveau, seul `super_admin` est bloqué. |
| f | `WebhookService.cs:292-297` | `ComputeSignature` est appelée **hors** du `try` et fait `Convert.FromBase64String` : un secret non-base64 fait disparaître le job silencieusement (`Task.Run` non observé). |
| g | `WebhookService.cs` | La signature ne couvre ni horodatage ni identifiant de livraison → **rejeu** possible chez le consommateur. |
| h | `Models/TokenClaims.cs:16` | `UserId.Split(':')[1]` : un sujet `a:b:c` donne `b` silencieusement. |
| i | `ProjectController.cs:41` | `Guid.Parse(Claims.ProjectId)` lève `FormatException` → **500** au lieu d'un 403 quand un super-admin appelle `/project/*` sans `?project_id=`. |
| j | `ServiceAccountController.cs:177-193` | Ni `GeneratePat`, ni `RevokePat`, ni `AddApiKey` n'écrivent d'entrée d'audit. `ExpiresAt` est facultatif → PAT éternels par défaut. |
| k | `SystemAdminController` | Pas d'audit sur `UpdateOrg`, `AssignOrgAdmin`, `RemoveOrgAdmin`, CRUD clients Hydra, CRUD rôles, CRUD user-lists. Trous dans la piste d'audit d'un IAM. |
| l | `AuditLogService.cs:27` | `RecordAsync` appelle `SaveChangesAsync()` sur le `DbContext` de la requête : écrire un audit **valide toutes les modifications en attente**, y compris partielles. |

---

## 3. Findings fonctionnels

### FUNC-01 🟠 — Le bouton « tester le webhook » ne teste rien

`OrgWebhookController.TestWebhook` émet l'événement `webhook.test`. Or
`WebhookService.DispatchAsync` ne retient que les webhooks dont `Events` contient le nom émis,
et `webhook.test` **n'est pas** dans `WebhookEvents.All` — donc impossible à souscrire
(`CreateWebhook` rejette les événements hors liste). L'API répond `200 {"message":"test_dispatched"}`
et rien n'est envoyé.

**Test** : `PlatformRegressionTests.WebhookTest_EnqueuesADelivery`
**Correctif** : traiter `webhook.test` comme un envoi direct au webhook ciblé, sans filtrage.

---

### FUNC-02 🟠 — Injection de formule CSV dans tous les exports

`OrgController.CsvEscape` et `SystemAdminController.AdminCsvEscape` ne protègent que virgule,
guillemet et saut de ligne. Un `DisplayName` valant `=HYPERLINK("http://evil","clic")` — que
**tout utilisateur final peut se donner** via `PATCH /account/me` — devient une formule active à
l'ouverture de l'export dans Excel / LibreOffice / Sheets, sur le poste d'un administrateur.

**Test** : `PlatformRegressionTests.UserExport_FormulaPayloadInDisplayName_IsNeutralised` (5 charges)
**Correctif** : préfixer d'une apostrophe (ou d'une tabulation) toute cellule commençant par
`=`, `+`, `-`, `@`, TAB ou CR.

---

### FUNC-03 🔴 — Un `project_admin` ne peut pas utiliser la console d'administration

**Fichier** : `frontend/admin/src/context/AuthContext.tsx:101`

```ts
isProjectManager: roles.some(r => r === 'project_manager' || r.startsWith('project_manager:')) || …
```

Le backend n'émet jamais `project_manager`. `Roles.ProjectAdmin` vaut **`project_admin`**
(`src/Config/Roles.cs:11`), et c'est ce que `AuthController.GetConsent` place dans le token.
Un utilisateur dont c'est le seul rôle tombe donc dans `roles.length === 0 || (!isSuperAdmin &&
!isOrgAdmin && !isProjectManager)` → écran **« No access »**. Le niveau `ProjectAdmin` existe
côté API, il est simplement inaccessible depuis l'UI.

**Correctif** : `r === 'project_admin' || r.startsWith('project_admin:')`. À dériver d'une
constante partagée pour éviter la récidive.

---

### FUNC-04 🟠 — Routes cassées et champs ignorés dans `frontend/admin/src/api.ts`

| Ligne | Appel du SPA | Réalité backend |
|---|---|---|
| 333 | `GET /org/export/audit-log` | La route est `/org/audit-log/export` → **404** |
| 337 | `GET /admin/export/audit-log` | N'existe pas ; c'est `/admin/organizations/{id}/export/audit-log` → **404** |
| 125 | `PATCH /project/info` avec `ip_allowlist`, `check_breached_passwords`, `email_from_name`, `allowed_scopes` | Absents de `UpdateProjectInfoRequest` → **ignorés en silence, 200 renvoyé** |
| 132 | `POST …/saml-providers` avec `name_attribute_name` | Le backend attend `display_name_attribute_name` → ignoré |
| 67 | `POST /org/userlists` avec `org_id` | `CreateUserListRequest` n'a que `Name` ; la liste atterrit dans l'org de l'appelant |
| 7 | `POST /admin/organizations` avec `metadata` | Ignoré |

Le cas le plus grave est le troisième : **l'opérateur active une allowlist d'IP, l'API répond
200, et rien n'est appliqué.** Un contrôle de sécurité qu'on croit actif et qui ne l'est pas est
pire que son absence.

**Tests** : `PlatformRegressionTests.ProjectInfoPatch_IpAllowlistAndBreachCheck_ArePersisted`,
`…ProjectInfoPatch_InvalidCidrInIpAllowlist_IsRejected`,
`…AuditLogExport_RoutesUsedByAdminSpa_Exist` (celui-ci épingle les routes correctes).

**Correctif** : ajouter les champs manquants à `UpdateProjectInfoRequest` + valider chaque CIDR ;
corriger les chemins dans `api.ts` ; et surtout **générer le client TypeScript depuis
l'OpenAPI** (Swashbuckle est déjà en place) pour que ce type de dérive devienne impossible.

---

### FUNC-05 🟠 — Le parcours « MFA obligatoire à la première connexion » est cassé

`AuthController.InitiateMfaAsync` renvoie `{ requires_mfa_setup: true }` quand le projet exige
la MFA et que l'utilisateur n'a aucun facteur. Le SPA de login appelle alors
`/account/mfa/totp/setup` (`frontend/login/src/api.ts:137-148`). Or `/account/*` est derrière
`GatewayAuthMiddleware`, qui exige un Bearer token — que l'utilisateur n'a pas encore, puisqu'il
est justement au milieu du login. Résultat : **401, impasse**.

**Correctif** : exposer des endpoints d'enrôlement adossés à la session MFA en attente
(`/auth/mfa/setup/*`), pas à `/account/*`.

---

### FUNC-06 🟠 — SAML cassé par `SameSite=Strict`

`SamlController.Start` stocke l'ID de la requête dans la session ASP.NET
(`saml_req:{idpId}`), et `AssertionConsumerService` le relit pour valider `InResponseTo`. Mais
l'ACS est un **POST cross-site émis par l'IdP**, et le cookie de session est configuré
`SameSite = Strict` (`Program.cs:53`). Le navigateur ne l'envoie pas → `expectedReqId` est null →
`saml_no_pending_request`. **Aucune connexion SAML ne peut aboutir dans un vrai navigateur.**

Non détecté par les tests parce que `TestFixture` force `SameSite = Unspecified` (ligne 172).

**Correctif** : porter l'ID de requête dans le `RelayState` (déjà signé/opaque) ou dédier un
cookie `SameSite=None; Secure` à ce flux.

---

### FUNC-07 🟠 — L'enrôlement MFA casse dès que le SPA admin est sur une autre origine

Même cause : `/account/mfa/totp/setup` met le secret TOTP dans la session cookie et
`/confirm` le relit. Avec `SameSite=Strict` et CORS `AllowCredentials`, le cookie n'est pas
envoyé si `App__AdminSpaOrigin` ≠ `App__PublicUrl` — ce qui est le déploiement documenté
(NodePort, Tailscale, ingress privé). Idem pour l'enregistrement WebAuthn et
`phone_setup_number`.

**Correctif** : porter cet état dans Redis, clé dérivée de l'identité du porteur du token, pas
dans un cookie.

---

### FUNC-08 🟡 — La MFA par SMS n'envoie aucun SMS

`Program.cs:104` enregistre `ISmsService` → `StubSmsService`, qui se contente de logguer. Aucune
autre implémentation n'existe. Un projet qui active `SmsVerificationEnabled` ou dont les
utilisateurs ont un téléphone vérifié verra `InitiateMfaAsync` renvoyer `requires_mfa` /
`mfa_type: "sms"` — et **le code n'arrivera jamais** : utilisateurs verrouillés dehors.

**Correctif** : refuser d'activer la vérification SMS quand aucun provider réel n'est configuré,
et fournir une implémentation (Twilio / OVH / Vonage) derrière l'interface.

---

### FUNC-09 🟡 — Le lien d'invitation pointe vers une route d'API

`OrgController:427`, `SystemAdminController:353`, `ManagedApiController:205` construisent
`{PublicUrl}/auth/invite/complete?token=…`. Or `/auth/invite/complete` est un **POST** de l'API.
Cliquer sur le lien depuis un mail émet un GET → 404/405. La page qui traite les invitations est
`frontend/login/src/pages/SetPassword.tsx`.

**Correctif** : pointer vers la route du SPA (`{PublicUrl}/invite?token=…`) et centraliser la
construction du lien.

---

### FUNC-10 ⚪ — Incohérences mineures

- `NotificationService.cs:116` : le mail annonce « This code expires in 10 minutes » alors que
  `Security:OtpTtlSeconds` vaut 300 (5 min).
- `NotificationService.SmtpSendAsync` : `SecureSocketOptions.None` quand `StartTls=false` →
  identifiants SMTP en clair ; pas d'option TLS implicite (port 465).
- `BreachCheckService` : fail-open documenté, mais aucun log d'audit sur l'échec.
- `WebhookDispatcherService.ExecuteAsync` : `RecoverAllAsync()` au démarrage sans verrou → avec
  plusieurs replicas, **livraisons dupliquées**.
- `ProjectController.ProjectId` : un org-admin peut viser n'importe quel `?project_id=` ; le
  contrôle d'org est fait ensuite dans `GetProjectAsync`, mais `ListRoles`, `GetStats` et
  `GetAuditLog` réutilisent `ProjectId` dans des sous-requêtes — correct aujourd'hui, fragile.

---

## 4. Gold standard IAM multi-tenant

Ce que font Ory Network, Auth0, Okta, Zitadel, WorkOS — et où RediensIAM se situe.

### 4.1 Identité du tenant

| Pratique | RediensIAM |
|---|---|
| Le tenant est déduit d'une source **serveur** : hostname, client OAuth2 enregistré, ou organisation portée par la session — jamais d'un paramètre de requête | ❌ SEC-02 : `project_id` vient de la query string |
| Un client OAuth2 appartient à exactement un tenant et le porte dans ses métadonnées | ⚠️ posé à la création, mais non exigé ni vérifié partout |
| `subject` porte le tenant (`{orgId}:{userId}`) | ✅ fait |
| `subject_type = pairwise` pour qu'un même utilisateur ait un `sub` différent par client | ❌ `public` (`HydraService.EnsureAdminSpaClientAsync`) |
| Unicité de l'email **scopée au tenant**, jamais globale | ✅ `UserListId + Email` |

### 4.2 Tokens

| Pratique | RediensIAM |
|---|---|
| Valider `iss`, `aud`, `exp`, `nbf`, `token_use` **à chaque requête** | ❌ SEC-01 : rien de tout ça |
| `token_type_hint=access_token` à l'introspection | ❌ un refresh token est accepté |
| Access token court (5-15 min), refresh long avec **rotation + détection de réutilisation** | ⚠️ TTL délégué à Hydra, rotation non configurée |
| Sender-constrained tokens (DPoP, RFC 9449, ou mTLS RFC 8705) | ❌ bearer pur |
| PKCE S256 obligatoire sur tous les clients publics | ✅ SPA admin + login social |
| Cache d'introspection borné par `exp` et invalidé à la révocation | ⚠️ borné (60 s Hydra) mais PAT non invalidé (SEC-08) |
| Pas de token en `localStorage` | ✅ `InMemoryWebStorage` — bon point, rare |
| Introspection RFC 7662 exposée aux resource servers, authentifiée en client_credentials | ❌ finding B du 28/07 — bloquant pour le gateway |

### 4.3 Autorisation

| Pratique | RediensIAM |
|---|---|
| Modèle Zanzibar (relations) plutôt que rôles dans le token | ⚠️ Keto présent mais **hors du chemin d'autorisation** (SEC-11) |
| Décision d'autorisation **live**, jamais figée dans un token | ❌ `ext.roles` uniquement |
| Révocation immédiate : changement de rôle ⇒ révocation des sessions du sujet | ❌ seulement au changement de mot de passe |
| Contrôle d'appartenance avant toute attribution de rôle | ❌ `AssignManagementRoleAsync` n'exige pas que la cible appartienne à l'org |
| Séparation des privilèges : un admin ne peut pas s'auto-élever | ⚠️ partiel (`cannot_modify_own_role`), contourné par SEC-13.e |

### 4.4 Authentification

| Pratique | RediensIAM |
|---|---|
| Argon2id, paramètres ≥ OWASP (m=64 Mo, t=3, p≥1) | ✅ 64 Mo / t=3 / p=4 |
| Pepper côté serveur | ✅ optionnel, HMAC avant Argon2 |
| Anti-énumération sur **tous** les endpoints | ❌ SEC-06 |
| Rate-limit par IP **et** par compte, sans effacement croisé | ❌ SEC-07 |
| MFA obligatoire pour les rôles d'administration | ❌ `AdminLogin` accepte un super-admin sans MFA |
| WebAuthn : credential lié à l'utilisateur, `userVerification=required`, compteur anti-clonage | ❌ SEC-05 ; compteur stocké mais régression non vérifiée |
| Rotation de session à l'élévation de privilège | ✅ après MFA |
| Détection de nouvel appareil | ✅ HMAC(UA + /24) |

### 4.5 Exploitation

| Pratique | RediensIAM |
|---|---|
| Audit exhaustif, immuable, horodaté, exportable | ⚠️ exhaustivité incomplète (SEC-13.k), pas d'append-only |
| Rétention configurable par tenant | ✅ |
| Webhooks signés HMAC **avec horodatage** + anti-rejeu | ⚠️ signature sans timestamp (SEC-13.g) |
| Rotation des clés de chiffrement documentée | ❌ HKDF par usage en place, mais aucune procédure de rotation |
| Quotas par tenant (users, projets, requêtes) | ❌ absent |
| Résidence des données / chiffrement par tenant | ❌ absent |

---

## 5. SDK d'intégration — état des lieux et recommandation

**Question posée : existe-t-il un SDK Rust ou C# pour simplifier l'intégration ?**

Réponse en deux temps.

**Pour Ory (les briques sous-jacentes), oui — officiels et générés depuis l'OpenAPI :**

| Langage | Package | Portée |
|---|---|---|
| Rust | `ory-client` (`clients/client/rust`) | Ory Network complet (Hydra, Keto, Kratos) |
| Rust | `ory-keto-client` (`clients/keto/rust`) | Keto seul |
| C# / .NET | `Ory.Client` (`clients/client/dotnet`) | Ory Network complet, DI-friendly (`ConfigureApi`, `BearerToken`, policies Polly) |
| Go / Java / JS / PHP / Python / Ruby / Dart / Elixir | idem | — |

**Pour RediensIAM lui-même, non — et c'est le vrai blocage du « plug-and-play ».** Aucun SDK,
aucun package publié, aucun client généré. Un intégrateur doit :

1. lire `README.md` pour deviner les routes ;
2. faire du JWKS local **sans** les vérifications live (donc hériter de SEC-11) ; ou
3. taper directement le port admin de Hydra `:4445` — ce qui exige d'ouvrir la NetworkPolicy la
   plus verrouillée du déploiement et court-circuite complètement RediensIAM.

C'est exactement ce qui s'est passé côté yandee-web (finding B du 28/07).

### Recommandation, dans l'ordre

1. **D'abord le contrat, ensuite le SDK.** Publier `POST /api/introspect` (RFC 7662), gardé par
   service account / client_credentials, qui introspecte le token **et** fait les vérifications
   live Keto/DB, et renvoie `{ active, sub, org_id, project_id, roles, exp }`. Sans cette route,
   tout SDK ne ferait qu'emballer un contournement.
2. **Générer les clients depuis l'OpenAPI.** Swashbuckle est déjà configuré et sert
   `/swagger/v1/swagger.json` sur le port admin. `openapi-generator` produit alors un client C#
   (`csharp`, la génération qu'utilise Ory) et Rust (`rust`, `reqwest`) sans code à maintenir à
   la main. Bénéfice immédiat : le SPA admin peut consommer le même contrat généré, et FUNC-04
   (routes fausses, champs ignorés) devient structurellement impossible.
3. **Ajouter une fine couche idiomatique par-dessus le client généré**, c'est là que se joue le
   « plug-and-play » :
   - **C#** : `services.AddRediensIam(o => …)` + un `AuthenticationHandler` qui pose les claims,
     avec cache d'introspection borné par `exp`.
   - **Rust** : un middleware `tower::Layer` pour axum / actix, avec le même cache.
4. **Livrer un mode « gateway »** : middleware prêt à l'emploi qui valide le token, vérifie
   l'audience et interroge `/api/authorize` pour la décision fine — c'est la brique qui manque
   pour tenir la promesse « centraliser identité, authz et authn au niveau du gateway ».

Question ouverte pour toi : viser un SDK **maison** (contrôle total, charge de maintenance) ou
assumer que RediensIAM est « du Ory pré-configuré » et publier une **couche mince** au-dessus
des SDK Ory officiels ? La seconde option est nettement moins coûteuse et me semble la bonne
tant que le périmètre fonctionnel reste celui d'aujourd'hui.

---

## 6. Surface d'attaque — ce qu'un attaquant tente réellement

Regroupé par objectif, avec l'état de RediensIAM en regard.

### Prendre le contrôle de l'IAM

| Attaque | État |
|---|---|
| Confusion d'audience (token d'une app → API d'admin) | ❌ **ouvert** — SEC-01 |
| Rejeu d'un refresh token comme access token | ❌ **ouvert** — SEC-01 |
| Élévation par manipulation de rôle | ⚠️ SEC-13.d/e |
| Bypass du second facteur | ❌ **ouvert** — SEC-05 |
| Credential stuffing / brute force | ⚠️ SEC-07 |
| Énumération de comptes | ❌ **ouvert** — SEC-06 |
| Vol de token par XSS | ✅ CSP + token en mémoire seule |
| CSRF sur les mutations admin | ✅ Bearer obligatoire sur les verbes mutants |
| Fixation de session | ✅ rotation après MFA |
| Rejeu du code d'autorisation | ✅ PKCE S256 |
| Redirection ouverte pour capter un code | ❌ **ouvert** — SEC-03 |
| Injection SQL | ✅ EF Core paramétré partout |
| Désérialisation non sûre | ✅ `System.Text.Json`, pas de polymorphisme |
| Path traversal sur le fallback SPA | ✅ fichier fixe |

### Franchir la frontière entre tenants

| Attaque | État |
|---|---|
| `project_id` forgé → lecture de la config d'un autre tenant | ❌ **ouvert** — SEC-02 |
| `project_id` forgé → code d'autorisation d'un autre tenant | ❌ **ouvert** — SEC-02 |
| `idp_id` d'un autre tenant sur `/auth/saml/start` | ❌ **ouvert** — SEC-02 |
| IDOR sur `/org/*`, `/project/*` | ✅ filtrage systématique sur `OrgId` |
| Attribution d'un rôle à un utilisateur hors org | ⚠️ non vérifié |
| Webhook global (`OrgId = null`) captant tous les tenants | ⚠️ super-admin seulement, non documenté |

### Atteindre l'infrastructure depuis l'IAM

| Attaque | État |
|---|---|
| SSRF webhook → metadata cloud / réseau interne | ❌ **ouvert** — SEC-04 (IPv4 mappé, ULA, CGNAT, redirections) |
| SSRF via `issuer_url` OIDC | ❌ **ouvert** — SEC-10 |
| SSRF via `MetadataUrl` SAML | ✅ filtré |
| Spoofing de `X-Forwarded-For` | ✅ fail-closed en production |
| Injection de formule dans un export | ❌ **ouvert** — FUNC-02 |
| XXE sur le parsing SAML | ⚠️ dépend d'ITfoxtec — à vérifier explicitement |
| Exfiltration par CSS de thème tenant | ⚠️ sanitiseur regex côté client uniquement, le fichier lui-même le reconnaît |

### Traverser le gateway

C'est le point le plus important pour l'objectif du produit, et c'est celui qui est le moins
outillé. Un gateway qui valide localement des JWT :

- ne voit **aucune** révocation de rôle (SEC-11) ;
- accepte des tokens d'audiences étrangères s'il ne vérifie pas `aud` lui-même (SEC-01, hérité) ;
- n'a **aucun** moyen propre de demander « ce sujet a-t-il le droit de faire X ? » — il n'y a pas
  d'endpoint d'autorisation.

Tant que `/api/introspect` et `/api/authorize` n'existent pas, chaque intégration de gateway
réimplémente sa propre politique et diverge. C'est l'inverse de « centraliser le contrôle ».

---

## 7. Checklist « bonnes pratiques IAM multi-tenant »

À utiliser comme grille de revue. `[x]` = déjà tenu par RediensIAM.

**Isolation**
- [ ] Le tenant est résolu depuis une source serveur, jamais depuis un paramètre de requête
- [ ] Chaque client OAuth2 est lié à un tenant et cette liaison est vérifiée à chaque étape du flux
- [x] Le `subject` porte le tenant
- [ ] `subject_type = pairwise`
- [x] Unicité de l'email scopée au tenant
- [ ] Toute requête de données porte un prédicat de tenant vérifié par un test dédié
- [ ] Quotas et limites par tenant

**Tokens**
- [ ] `iss` / `aud` / `exp` / `token_use` validés à chaque requête
- [ ] `token_type_hint` à l'introspection
- [ ] Access tokens ≤ 15 min
- [ ] Rotation des refresh tokens + détection de réutilisation
- [ ] Sender-constrained (DPoP ou mTLS)
- [x] PKCE S256 sur les clients publics
- [x] Aucun token en `localStorage` / `sessionStorage`
- [ ] Introspection RFC 7662 exposée aux resource servers
- [x] Cache d'introspection borné par `exp`
- [ ] Invalidation du cache à la révocation d'identité

**Authentification**
- [x] Argon2id ≥ paramètres OWASP
- [x] Pepper serveur optionnel
- [x] Vérification HIBP à l'inscription
- [ ] Politique de mot de passe appliquée sur **tous** les chemins d'écriture
- [ ] Réponses indiscernables sur tous les endpoints publics
- [ ] Rate-limit par IP et par compte, sans effacement croisé
- [ ] MFA obligatoire pour les rôles d'administration
- [ ] WebAuthn lié à l'utilisateur, `userVerification=required`
- [ ] Vérification de la régression du compteur de signature
- [x] Rotation de session à l'élévation de privilège
- [x] Alerte nouvel appareil
- [x] Liaison de compte social uniquement sur email vérifié des **deux** côtés
- [ ] État OAuth lié au navigateur (cookie), pas seulement à Redis

**Autorisation**
- [ ] Décision live (Keto/DB), jamais uniquement le token
- [ ] Révocation des sessions au changement de rôle
- [ ] Appartenance vérifiée avant toute attribution
- [x] Hiérarchie de niveaux (`ManagementLevel`)
- [x] Rangs de rôles projet (`Role.Rank`)
- [ ] Un seul chemin d'attribution de rôle (pas de contournement par PATCH)

**Exploitation**
- [x] Rétention d'audit par tenant
- [ ] Audit exhaustif sur toutes les opérations privilégiées
- [ ] Audit append-only
- [x] Secrets chiffrés au repos (AES-GCM + HKDF par usage)
- [ ] Procédure de rotation des clés documentée
- [x] Webhooks signés HMAC
- [ ] Signature avec horodatage + anti-rejeu
- [x] Revalidation SSRF à la livraison
- [ ] Denylist SSRF complète (IPv4 mappé, ULA, CGNAT, redirections)
- [x] NetworkPolicies, non-root, capabilities droppées, FS racine en lecture seule
- [x] Fail-closed sur les proxies de confiance en production
- [ ] Neutralisation des formules dans les exports CSV

**Intégration**
- [ ] OpenAPI publiée et versionnée
- [ ] Clients générés (C#, Rust, TS)
- [ ] Middleware gateway prêt à l'emploi
- [ ] Endpoint d'autorisation pour les resource servers
- [ ] Guide de démarrage « 10 minutes »
- [x] Configuration DB-backed (reconfiguration atomique de la flotte)

---

## 8. Ordre de traitement proposé

| Rang | Findings | Pourquoi d'abord |
|---|---|---|
| 1 | SEC-02, SEC-01 | Sans eux, il n'y a pas de multi-tenant. Tout le reste est secondaire. |
| 2 | SEC-05, SEC-06, SEC-07 | Chemin d'authentification, exploitables sans privilège. |
| 3 | FUNC-03, FUNC-04 | La console est inutilisable pour un rôle entier et ment sur l'état de la sécurité. |
| 4 | SEC-03, SEC-04, FUNC-02 | Corrections courtes, périmètre bien délimité. |
| 5 | FUNC-05, FUNC-06, FUNC-07 | Parcours utilisateur cassés en conditions réelles (invisibles en test). |
| 6 | SEC-08, SEC-11 + finding A | Refonte de la révocation — demande une décision d'architecture. |
| 7 | Finding B + §5 | Endpoint d'introspection puis SDK générés : c'est la marche à franchir pour le « plug-and-play ». |
| 8 | SEC-09, SEC-10, SEC-12, SEC-13, FUNC-08→10 | Lot de durcissement. |

---

## 9. SonarQube — projet unifié

`sonar-scan.sh` ne publie plus qu'**un** projet, `RediensIAM`, couvrant le backend et les deux
SPA en une seule analyse. Les `sonar-project.properties` de `frontend/admin` et `frontend/login`
ont été supprimés, ainsi que les deux passes `sonar-scanner-cli` sous Docker et les tokens
`SONAR_TOKEN_ADMIN` / `SONAR_TOKEN_LOGIN` (`SONAR_TOKEN` unique, avec repli sur l'ancien
`SONAR_TOKEN_API`).

Résultat du premier scan unifié :

| Mesure | Valeur |
|---|---|
| Lignes de code | 23 316 — `cs=8623`, `ts=12423`, `css=1500`, `yaml=599`, `js=110` |
| Fichiers | 187 |
| Couverture | 72,3 % (backend seul ; les SPA n'ont aucun test unitaire) |
| Duplication | 3,0 % |
| Issues | 32 — **toutes côté frontend** |
| Hotspots | 4 (66 % revus) |
| Quality gate | **ERROR** — `new_coverage` 64,6 < 80, `new_violations` 32 > 0, hotspots < 100 % |

Deux enseignements :

- Le C# ne remonte **aucune** issue Sonar. Les 32 restantes sont des `S3358` (ternaires
  imbriqués), `S6479` (index de tableau en `key` React), `S6847` (handlers sur éléments non
  interactifs) et deux `S5725` (absence de SRI sur la feuille de style Google Fonts, dans les
  deux `index.html`).
- **Aucun des findings de ce rapport n'est visible par Sonar.** Ni SonarAnalyzer ni
  SecurityCodeScan ne modélisent l'autorisation inter-tenants, la validation d'audience ou la
  liaison d'un credential à un utilisateur. Un quality gate vert ne dira jamais que l'isolation
  entre tenants tient — seuls les tests de `Tests/Regression/` le diront.

Reste à faire côté serveur : supprimer les projets `Admin-SPA` et `Login-SPA`, désormais vides.

---

## 10. Sur la testabilité

Deux angles morts méritent d'être corrigés avant les findings eux-mêmes, sinon les corrections
ne seront pas protégées :

1. **`TestFixture` neutralise `SameSite`** (ligne 172, `SameSiteMode.Unspecified`). C'est ce qui
   masque FUNC-06 et FUNC-07 : les deux flux passent en test et échouent en production. Il faut
   au moins un fixture qui conserve la configuration réelle.
2. **Les SPA n'ont aucun test unitaire** — pas de Vitest, pas de `test:coverage`. FUNC-03 est un
   bug d'une seule chaîne de caractères, qu'un test de `parseToken` aurait attrapé
   immédiatement. C'est aussi ce qui laisse la couverture frontend vide dans SonarQube.

---

*Audit produit par lecture intégrale du dépôt. Les 34 tests de `Tests/Regression/` sont le
livrable exécutable : ils échouent aujourd'hui, ils doivent tous passer après correction, et la
suite complète (1093 tests) doit rester verte.*
