# RediensIAM — Findings sécurité & déploiement

**Date** : 2026-07-28
**Contexte** : découverts en intégrant RediensIAM comme IdP OIDC du projet **yandee-web**
(le gateway yandee valide les tokens ; le portail superadmin se connecte via RediensIAM).
Déploiement dev local sur k3s. Revue croisée + déploiement réel.

Sévérité : 🔴 haute · 🟠 moyenne · 🟡 basse/config.

---

## A. 🟠 L'autorisation se fait sur les claims du token, pas en live (Keto/DB)

**Fichier** : `src/Filters/RequireManagementLevelAttribute.cs`
**Constat** : `OnActionExecuting` appelle `claims.GetManagementLevel()`
(`src/Middleware/GatewayAuthMiddleware.cs` → `ClaimsExtensions.GetManagementLevel`)
qui lit **`claims.Roles`** (issus du claim `ext.roles` du JWT, remplis à l'émission par
`HydraService.ValidateJwtAsync`). **Aucun appel à Keto ni à la base** dans le chemin
d'autorisation. `KetoService.CheckAsync` existe mais n'est pas invoqué par le filtre.

**Contradiction** : `docs/ARCHITECTURE.md` affirme « Defence in depth… **Every
privileged path checks the database, not just the token** » et « SA active + org
active checked on every introspect ». Le filtre ne le fait pas.

**Impact** : un rôle **révoqué** (retrait de `super_admin`) ou une **org suspendue**
**après** émission du token restent effectifs jusqu'à l'**expiration** du token.
L'introspection Hydra ne détecte que la révocation de token/session, pas le
downgrade de rôle porté par `ext.roles`. Fenêtre de privilège = TTL du token.
S'applique à RediensIAM **et** à tout resource server externe qui consomme
`ext.roles` (ex. le gateway yandee).

**Fix proposé** : soit re-checker Keto/DB en live dans `RequireManagementLevel`
(un `CheckAsync` System/super_admin, déjà mis en cache ≤60 s ailleurs), soit
**révoquer les tokens Hydra** du sujet lors d'un changement de rôle / suspension
d'org (revoke consent + access tokens).

---

## B. 🟠 Pas de surface d'introspection pour resource server externe (RFC 7662)

**Constat** : aucune route ne permet à un service tiers (ex. gateway yandee) de faire
**valider un token en s'authentifiant lui-même** (client_credentials / service
account), à la RFC 7662. Options actuelles, toutes mauvaises pour un RS externe :
- taper **Hydra-admin** `:4445 /admin/oauth2/introspect` directement (ce que fait
  `HydraService.ValidateJwtAsync`) → bypasse RediensIAM + exige d'ouvrir la
  NetworkPolicy verrouillée de Hydra-admin ;
- relayer le token user vers `/account/me` → **oracle** (n'importe quel porteur de
  token peut vérifier sa validité) ;
- `/api/manage/*` (`ManagedApiController`) = opérations de gestion, pas de
  validation de token.

**Impact** : chaque RS externe est forcé de contourner RediensIAM (couplage
Hydra-admin) ou de faire de la validation locale JWKS sans les checks live (hérite
du Finding A). Côté yandee on a choisi la **validation JWKS locale** faute de mieux.

**Fix proposé** : exposer `POST /api/introspect` (ou `/api/authorize`) **gated
service-account / client_credentials**, qui introspecte le token présenté **+**
fait les checks Keto/DB live (corrige aussi A pour les RS externes) et renvoie
`{ active, user_id, roles }`.

---

## C. 🟡 L'ingress ne supporte pas un base-path (`/iam`)

**Fichiers** : `deploy/rediensiam/templates/ingress.yaml` (paths `/`, `/oauth2`,
`/userinfo`, `/.well-known`), `deploy/rediensiam/values.dev.yaml` (`issuer:
http://localhost`).
**Constat** : RediensIAM se sert à la **racine** d'un host. Le servir sous un
sous-chemin (`https://host/iam`, souhaité pour du path-routing multi-app) casse :
l'issuer Hydra et les URLs de discovery/redirection OIDC pointent à la racine.

**Impact** : impossible de cohabiter avec une autre app sur le même host via
sous-chemin ; il faut un **host dédié** (ex. `iam.example.com`). En dev local, ça
force `iam.localhost` (host séparé) plutôt que `localhost/iam`.

**Fix proposé** : supporter un `basePath` optionnel (strip-prefix ingress + issuer
Hydra + `<base href>` des SPA préfixés), ou documenter « host dédié obligatoire ».

---

## D. 🟠 Le déploiement **dev** crashe (App__TrustedProxies)

**Fichiers** : `src/Program.cs` (`ConfigureForwardedHeaders`, ~l.380-395),
`deploy/rediensiam/values.dev.yaml`, `deploy/rediensiam/values.yaml` (l.22-26).
**Constat** : l'image tourne en `ASPNETCORE_ENVIRONMENT=Production`. `Program.cs`
**jette et refuse de démarrer** si `App__TrustedProxies` est vide en Production
(« must be set explicitly in Production »). Or `values.dev.yaml` ne fixe pas
`rediensiam.app.trustedProxies` (défaut `""`), et le **commentaire de `values.yaml`
est faux** : « Empty falls back to RFC1918 + loopback » alors que l'app rejette
justement le vide en Production.

**Reproduction** : `./deploy/deploy.sh --dev` → pod `rediensiam` en
**CrashLoopBackOff** (exit 139), helm `context deadline exceeded`.

**Contournement appliqué** (dev) :
`--set rediensiam.app.trustedProxies="10.42.0.0/16,10.43.0.0/16"` (CIDR pods k3s).

**Fix proposé** : mettre `app.trustedProxies` (CIDR du cluster) dans
`values.dev.yaml`, **ou** faire tourner l'image en `ASPNETCORE_ENVIRONMENT=Development`
en dev, **et** corriger le commentaire trompeur de `values.yaml`.

---

## E. 🟠 La console admin ne peut pas faire son login OIDC (CSP trop stricte)

**Fichiers** : `frontend/admin/index.html:8` (meta CSP), `frontend/login/index.html:8`
(meta CSP, pour comparaison), `src/Program.cs` (`AddSecurityHeaders`, header CSP admin),
`frontend/admin/index.html:12` (stylesheet Google Fonts).

**Constat 1 — connect-src** : la meta CSP de l'**admin** a `connect-src 'self'`,
alors que le **login** a `connect-src 'self' http: https:`. La console admin
(oidc-client-ts) fait un **fetch** de la discovery
(`{issuer}/.well-known/openid-configuration`, `getMetadata` → `fetchWithTimeout`)
**avant** le redirect. Comme la console admin est servie sur le **NodePort `:30501`**
(origine ≠ celle de l'issuer public), le fetch est **cross-origin** → **bloqué** par
`connect-src 'self'`. → **login console admin impossible.** Structurel : le NodePort
a toujours une origine différente de l'issuer.

**Constat 2 — fonts** : le **header serveur** CSP admin (`AddSecurityHeaders` :
`script-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none';`)
impose `style-src 'self'` — plus strict que la meta (qui autorise
`https://fonts.googleapis.com`). L'intersection header∩meta = `'self'` → la
**stylesheet Google Fonts** (`frontend/admin/index.html:12`) est **bloquée**.

**Constat 3 — frame-ancestors en meta** : `frame-ancestors` livré via `<meta>` est
**ignoré** par les navigateurs (doit être un header). Le header le pose bien
(`frame-ancestors 'none'`), donc l'embarquement iframe est refusé (attendu) — mais
la directive en meta est du bruit à retirer.

**Impact** : console admin inutilisable via NodePort (login OIDC bloqué) + fonts
manquantes.

**Fix proposé** :
1. Meta admin (`frontend/admin/index.html`) : `connect-src` doit inclure l'origine
   de l'issuer (ou `http: https:` comme le login).
2. Header serveur admin (`Program.cs`) : autoriser
   `style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; connect-src 'self' <issuer>`
   — ou **self-host** les fonts Geist (supprime la dépendance externe + la CSP).
3. Retirer `frame-ancestors` des `<meta>` (le garder en header uniquement).

---

## Priorisation suggérée

| # | Sévérité | Effort | Bloque quoi |
|---|---|---|---|
| D | 🟠 | 1 ligne values | Déploiement dev (crash) |
| E | 🟠 | 2-3 lignes + rebuild | Login console admin |
| A | 🟠 | check Keto live | Révocation de rôle réelle |
| B | 🟠 | nouvelle route | RS externes (intégration propre) |
| C | 🟡 | ingress + config | Cohabitation sous sous-chemin |

*Rédigé par l'assistant lors de l'intégration yandee-web ↔ RediensIAM. Voir aussi
`yandee/yandee_web/docs/2026-07-27-iam-integration-security.md` (côté yandee).*
