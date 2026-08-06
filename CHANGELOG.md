# Changelog

All notable changes to RediensIAM.

Versions are `MAJOR.MINOR.PATCH`. Before 1.0.0, **a minor bump may break the wire contract** — and
0.2.0 breaks it in four places. The chart version, the chart `appVersion`, the container image tag,
all three SDKs and both SPAs share one number.

---

## [0.6.1] — 2026-08-06

**Un correctif de console, une ancre de confiance rangée, et deux fichiers de verrouillage qui
n'avaient jamais suivi.** Aucun changement de contrat : `ver: 2` est inchangé, aucune migration.

### Corrigé — la page « service accounts » d'un projet listait tout ce que l'appelant pouvait voir

`/service-accounts` scope sur **l'appelant**, pas sur le projet : un super-admin y reçoit tous les
comptes du déploiement, un `org_admin` toute son organisation. `ProjectServiceAccounts.tsx` affichait
cette réponse **entière**, sur une page nommée d'après un seul projet, avec un bouton de suppression
à côté de chaque ligne.

Un `ServiceAccount` ne porte pas de `ProjectId` — il appartient à un projet exactement quand il est
sur la liste d'utilisateurs assignée à ce projet. C'est donc cette liste qui filtre. Sans liste
assignée, la page n'affiche plus rien : c'est la réponse vraie, et se rabattre sur « tout afficher »
est la façon dont ce défaut reviendrait.

**Le contrôleur n'a pas bougé**, et six tests d'intégration le démontrent au lieu de l'affirmer :
`AssignUserListAsync` exige `ul.OrgId == project.OrgId`, donc la liste d'un projet porte toujours
l'`OrgId` de son organisation et le filtre de l'`OrgAdmin` l'attrape déjà. Les tests fixent les trois
niveaux, et fixent surtout que la liste et `CanAccessAsync` restent d'accord — la même question
répondue à deux endroits est la façon dont ce contrôleur peut dériver.

### Corrigé — `App:TrustedProxies` était la dernière lecture de configuration hors d'`AppConfig`

C'est une **ancre de confiance** : limites de débit, allowlists IP de projet et adresse source dans
l'audit en dépendent toutes. Une lecture directe n'a ni défaut, ni borne `Math.Clamp`, ni surcharge
par la ligne `instances`. Elle devient `AppConfig.TrustedProxies`.

Le correctif qui compte est le **test** : un contrôle structurel échoue désormais sur toute lecture
de configuration hors d'`AppConfig.cs`. Sans lui, la quatrième fuite serait arrivée en silence,
comme celle-ci. `InstanceConfiguration.cs` reste exempté — il tourne avant la DI et construit la
configuration qu'`AppConfig` lit ensuite.

### Corrigé — trois reliquats de la suppression de Dragonfly

- `CS1587` dans `src/Health/HealthChecks.cs` : le `<summary>` du `CacheHealthCheck` supprimé était
  resté sans classe en dessous. **Invisible en build incrémental**, ce qui explique qu'il ait
  survécu à la release.
- Deux commentaires décrivaient encore Dragonfly comme le magasin des sessions.

### Corrigé — les fichiers de verrouillage n'avaient jamais suivi la version

`sdk/rust/rediensiam-client/Cargo.lock` et les deux `package-lock.json` des SPA étaient restés à
**0.5.0** alors que la 0.6.0 était publiée. Le CHANGELOG dit que les trois SDK et les deux SPA
partagent un numéro ; trois fichiers ne le savaient pas. Ils sont régénérés par leurs outils, pas
édités à la main.

---

## [0.6.0] — 2026-08-05

**Two breaking changes, and one component removed.** Read both sections before upgrading: the
wire contract moves to `ver: 2`, and the deployment loses Dragonfly — which means a schema
migration and a chart change in the same step.

### Breaking — one value, one name: `aud` is now `project_id`

The tenant a resource server declares it serves travelled the stack under **seven** names:
`project_id` (the API), `client_<project_id>` (the OIDC client), `clientId` (the browser SDK),
`Audience` / `audience` (the backend SDKs), `aud` (the wire), `IAM_AUDIENCE` (the gateway) and
`OIDC_PROJECT_ID` (the infrastructure). It is one value. A deployment needed a test whose entire
job was to check that two of those names agreed — and a test that verifies a tautology is a naming
bug with extra steps.

- **`/api/introspect` and `/api/authorize` take `project_id`, not `aud`.** Missing or blank is
  `400 {"error":"project_id_required","ver":2}`. There is no alias and no grace period.
- **The introspection response no longer echoes the audience.** `project_id` in the answer is the
  *token's* project, which is what it always was; echoing back the value you just sent was
  tautological once the field had the same name. `ver` is what tells a client the server enforced
  the binding, and it is what the SDKs check.
- **`ver` is `2`.** The backend SDKs refuse any answer below it, which is how they detect a server
  that silently ignored the field.
- **SDK options renamed.** `RediensIamOptions.Audience` → `ProjectId`; `Config::audience` →
  `project_id`. Still required, still no default, still throws at construction.
- **`/api/introspect` binds its two form fields as explicit action parameters.** Form binding
  resolves a record through its *constructor*, so `[property: FromForm(Name = "project_id")]`
  lands where the binder never looks — the same silent failure that made `token_type_hint` a field
  nobody could set. Two parameters, two explicit names.
- The audit actions are `api.introspect.project_mismatch` and `api.authorize.project_mismatch`.

**What you must change.** One line per backend service: rename the option, set it to the project
id you already hold. Deploy the callers before the server, or accept a window of 400s.

### Breaking — Dragonfly is gone; its contents are in PostgreSQL

The deployment ran two datastores where one would do, and the second carried far more than a
cache: the DataProtection key ring that signs session cookies, the pending-MFA session, the OAuth2
social `state`, the TOTP anti-replay set, the lockout counters and the webhook queue. Ten of its
thirteen uses were shared state or security controls; three were cache. None of it survives a
replica keeping its own copy.

- **New tables** — `shared_state`, `rate_counters`, `webhook_pending`, `data_protection_keys`.
  Migration `20260805154942_DropDragonflySharedStateToPostgres`. All four are declared
  deployment-global in `files/rls.sql`; they carry no `OrgId` and belong to no tenant.
- **`Cache__ConnectionString`, `Cache__TlsCaFile`, `Cache__InstanceName` are gone.**
- **`Cache__PatTtlMinutes` → `Security__PatCacheTtlMinutes`.** It never was a cache setting: it is
  the ceiling on how long a revoked personal access token keeps working. Liveness — account
  active, organisation not suspended, token unexpired — is still re-checked on *every* hit,
  whatever this says.
- **`rediensiam.secrets.cacheUrl`, `rediensiam.dragonfly.*` and the `dragonfly-password` secret key
  are gone** from the chart, along with the Dragonfly Deployment, its Service, its NetworkPolicy
  and its TLS Certificate.
- The lockout counter is now `INSERT … ON CONFLICT DO UPDATE … RETURNING` — atomic by definition,
  which is the property the Lua script existed to buy, without `SCRIPT LOAD` or EVALSHA.

**Five defects went with the component.** Two multiplexers to the same server; `Cache__InstanceName`
prefixing only the `IDistributedCache` half of the keys, so two deployments sharing a Dragonfly
still shared `otp:`, `pat:`, `rate:` and the webhook queue; a health check that probed only one of
the two connections; `IsConnected` without a `PING`; and a DataProtection prefix written as a
literal rather than read from the setting.

**Upgrade order.** The migration runs at startup (`Database__MigrateOnStartup`, default true).
Dragonfly holds no durable data, so nothing is migrated *out* of it — but the DataProtection key
ring restarts empty, which invalidates every session exactly once. Plan the rollout accordingly.

---

## [0.5.0] — 2026-08-02

**Breaking:** signing in now creates an SSO session. Deployments that want a password at every
authorization must set `Security__SsoSessionMinutes: 0` deliberately.

### Changed — signing in once means something

- **Hydra was never asked to remember anybody.** `AcceptLoginAsync` sent `remember: false,
  remember_for: 0`, so every authorization request needed the password again: refreshing the
  console asked for it, and so did opening a second application against the same identity provider.
  It read like a cookie problem for a long time and was not. `Security__SsoSessionMinutes` now sets
  the lifetime — eight hours by default, a week at most, and zero restores the old behaviour as a
  decision rather than an accident.

### Fixed — configuration that could not be changed

- **Roughly twenty settings were frozen at first install.** The `instances` row re-applied the
  environment only when `RECONFIGURE_FROM_ENV` was set, and nothing set it — so the lockout policy,
  the Argon2 cost, the audit retention and the rest kept whatever the first boot wrote, while
  `kubectl get deploy` showed the value an operator had just changed. The environment is now
  re-applied on every boot; the flag only decides whether that counts as a deliberate
  reconfiguration worth stamping.
- **The chart could only set 23 of them.** `rediensiam.app.extraEnv` passes arbitrary non-secret
  name/value pairs into the pod, so every variable in the new reference is reachable without
  editing a template.
- `app.adminPath` is gone. It travelled from the chart to an env var to a database column and no
  code ever read it; the console's path is the constant `Roles.ConsoleBasePath`. The column stays,
  documented as unused, because dropping it is a migration for no gain.

### Fixed — the admin ingress was served by the public controller

- All three Ingresses read one `ingress.className`. With two controllers — a public one on a
  forwarded address, an admin one on an address that is never forwarded — the admin Ingress
  inherited the public class and was therefore answered by the public controller. No public DNS
  record points at the admin host, so the topology looked like the protection; it was not, and a
  request carrying the admin `Host` sent to the public address was served. `ingress.admin.className`
  now exists, falling back to the shared one, and the P-04 deny router stays on the public class
  because that is where it has to be.

### Fixed — SDKs

- **Rust: `http://[::1]` was rejected.** The check matched the bare `::1` while the URL parser keeps
  the brackets, so IPv6 loopback — the one form of local development the exception exists for —
  failed with a configuration error.
- **TypeScript: a refused authorization looked like an ordinary page load.** An authorization server
  answers a refusal on the redirect URI with `error` where `code` would be; `handleRedirect()`
  returned false for it, which callers read as "this is not a callback" and answer by starting the
  flow again — walking the user back into the same refusal. It now throws `authorization_denied`.
- The Rust README claimed the crate compiled in webpki roots and warned that the OS trust store was
  not consulted, which would have told an integrator with a private CA that it could not work. It
  uses `rustls-tls-native-roots`, and a test proves it with a real handshake.

### Documentation

- [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) — every environment variable, its default, its
  bounds, whether the chart sets it, and where each value actually comes from.
- [`docs/CONSOLE.md`](docs/CONSOLE.md) — the console: three scopes, what each page does, and the
  order a first run has to happen in.
- `docs/API.md` recounted against the code: 187 routes, four of which were undocumented.
- All three SDK READMEs rewritten from source.

### Fixed — everything a SonarQube analysis found

The gate failed on three conditions: 72 open issues, 72 % new-code coverage against a required 80,
and one unreviewed security hotspot. All of it is addressed at the cause — **no suppression was
added anywhere**, and three existing ones were removed by fixing what they hid.

- **A regex vulnerable to catastrophic backtracking** in the browser SDK (`typescript:S5852`).
  `=+$` stripped base64 padding; it bought nothing, because `btoa` emits `=` only as trailing
  padding, so once `+` and `/` are rewritten every remaining `=` is padding. Linear now.
- **41 form labels associated with nothing** across nine console files (`typescript:S6853`). A
  `<label>` without `htmlFor` is not announced and does not focus its field on click — in an admin
  console. Fixing them exposed **a real defect**: `LogoUpload` derived its element id from the
  caption text, which is not unique, so every social provider's "browse" button opened the file
  picker of whichever one came first in the DOM and uploaded the logo to that one.
- **The project's only bug** (`typescript:S1082`) — a clickable element in `IamMenu` with no
  keyboard listener, and a `role="menu"` container that could not be focused. It is now a real menu
  widget: arrows walk the enabled items, Escape and activation return focus to the trigger. Note
  the trap it hides, now pinned by a test: closing on `keydown` for Space unmounts the item before
  its own handler runs, because a `<button>` fires its click on `keyup`.
- **Eight methods above the cognitive-complexity limit** (`csharpsquid:S3776`), the worst at 32 for
  a limit of 15. Extracted into named methods, behaviour identical — the audit chain's deliberate
  counter asymmetry is preserved and documented rather than tidied into something simpler and
  wrong, and `ProjectController.CreateUser` lost a fourth hand-written copy of the password rules
  in favour of the shared one.
- **`CA1847` — but not the way the rule asked.** `string.Contains(char)` has no EF translation, so
  the mechanical fix compiled and then failed three tests with "Translation of method
  'string.Contains' failed", which would have made the key-rotation sweep throw at runtime. The
  predicate is now `EF.Functions.Like(value, "%:%")`, which is the `LIKE` the method's own comment
  already described.
- **The scan was also lying about coverage.** `sonar.javascript.lcov.reportPaths` pointed at files
  nothing generated, so the dashboard showed both SPAs as untested. They report now — 95 % of lines
  in the login SPA — and `sonar-scan.sh` generates the reports it claims to import.
- The `**/Migrations/**` exclusion is gone: those files carry `<auto-generated/>` and the analyser
  skips them anyway, while excluding them *after* MSBuild had analysed them made the scanner warn
  about a protobuf reference on every run.

One finding is left open deliberately: `csharpsquid:S4502` on the SAML assertion consumer. The
`[IgnoreAntiforgeryToken]` attribute is what raises it and is what SAML requires — an identity
provider posts a signed assertion from another site, and verifying that signature *is* the
integrity control. No code change clears it without breaking SAML, and the server has already
reviewed it as safe.

### Builds

- **Zero warnings**, across the API, the test project and both SPAs. Four Sonar findings and one
  compiler warning fixed rather than suppressed, and three existing suppressions removed by fixing
  what they were hiding — including a `Password=postgres` literal in the design-time factory.

---

## [0.4.0] — 2026-08-02

**Breaking for anyone with a bookmark: the admin console moved from `/admin/` to `/console/`.**

### Fixed — the deployment's own configuration was ignored

- **A row in the database shadowed `App:PublicUrl`, `App:AdminSpaOrigin` and `App:Domain`.** They
  are captured into the `instances` row at first install and served back as a configuration source
  above the environment; `ApplyEnv` only runs again when `RECONFIGURE_FROM_ENV` is set, and nothing
  sets it. So changing a hostname in the chart did nothing on an existing database: the chart said
  one thing, `kubectl get deploy` showed it, and the process used the value from months earlier.
  Moving the console to its own hostname failed exactly that way, and every symptom pointed
  elsewhere — a bare 400 from host filtering, with the correct value in the pod's environment.
  These three now come from the deployment only, for the reason the same file already gives for the
  trust anchors: a process must not learn its own topology from data it can write.

### Fixed — dev

- **The dev console moved to `http://admin.iam.localhost/console/`**, a host under the issuer's own
  domain, mirroring production. Hydra's CORS origins follow it — pointed at the old NodePort origin,
  the SDK's discovery request was blocked and the console rendered nothing, the browser's only
  account of it being one console error. The NodePort survives as a troubleshooting door and now
  answers only to a request carrying the right `Host`, which is host filtering working.
- **`deploy.sh`'s smoke tests probe the ingress**, not the Service ClusterIP. A ClusterIP is
  routable from inside the cluster: probing it from the operator's shell worked often enough to look
  deliberate and failed often enough to report `000` against a healthy install. It also proved
  nothing about the path a user takes.

### Fixed — the console could not be reached by URL

- **Thirty of the console's forty-nine pages answered a browser with a bare 401.** The console and
  the management API shared the `/admin` prefix, and the API owns it: `SystemHealthController` is
  mounted on `admin/system`, which is exactly where the console's System scope lives. A bookmark, a
  refresh or a pasted link was judged by the API's bearer gate before a byte of the SPA had loaded,
  so the screen showed a spinner that could never resolve — no JavaScript had arrived to resolve
  it. The console now has its own prefix and `/admin/*` is the API's alone, which removes the
  collision by construction rather than exempting one route from the gate.
- The prefix is `Roles.ConsoleBasePath`, read by the server fallback, the OIDC redirect URI and
  Vite's `base`. `BrowserRouter` takes it from `import.meta.env.BASE_URL`: it was a literal
  `"/admin"`, and it survived the move silently — the router then refused to match any URL at all,
  with one console warning and a blank page as the only symptom.
- `ConsoleRoutingTests` asks for all forty-six console routes the way a browser does, with no
  `Authorization` header, and refuses a 401. Against the old prefix, twenty-seven of them fail.
- `ingress.public.adminOnlyPaths` gains `/console`, so the console stays off the public host exactly
  as it did when it shared a prefix with the API.

### Changed

- `frontend/admin/src/pages/` — the three pages used from more than one scope moved into
  `pages/shared/`, and the one component still named `*Page` lost the suffix. They sat loose beside
  four scope folders with nothing saying why.

### Testing

- The Playwright suite was rebuilt rather than repaired. All fifteen previous spec files mocked the
  admin API through `page.route()`, which made them a slower copy of the vitest suites; ten of them
  authenticated nothing at all, because the fixture replayed a `sessionStorage` token that
  `oidc-client-ts` used to write and that the SDK replacing it never did. The new suite runs against
  a real deployment, needs no configuration — it reads the installer's own bootstrap account — and
  proves those credentials before the first test rather than after a dozen timeouts.

### Documentation

- `app.adminPath` is documented as what it is: plumbed from the chart to an env var to a database
  column, and **read by nothing**. It never controlled the console's prefix.

---

## [0.3.0] — 2026-08-02

**Two breaking changes for operators**, both in the list below: `Security:RequireAdminMfa` is gone,
and the admin ingress issuer moved from `rediensiam.ingress.admin.clusterIssuer` to
`rediensiam.ingress.admin.tls.clusterIssuer`.

### Removed — a setting whose correct value changed by itself

- **`Security:RequireAdminMfa`.** Its own documentation admitted the problem: off is right for the
  first ten minutes of a deployment's life and wrong for the rest of it. It was dangerous in both
  directions — left false it leaves a `super_admin` on a password forever; set true too early it
  locks the operator out of the console they need in order to configure the SMTP or SMS that makes
  a factor deliverable at all. It is replaced by a derived rule that closes itself: **the first
  administrator of a deployment signs in without a factor, every one after that must enrol.** An
  administrator who already has a factor is challenged for it, exactly as before. The standing
  reminder in the console is unchanged and still cannot be dismissed — it is now the only thing
  between exactly one account and a password on its own.

### Fixed — signing out never worked

- **Hydra refused every logout.** No client this application registers declared
  `post_logout_redirect_uris`, and the browser SDK always sends one, so signing out of the console
  ended on Hydra's error page with the session still open. The console's client now registers its
  own base path, tenant projects accept the field at creation, and the console asks for it in both
  project forms.
- **`hydra.urls.logout` pointed at an API.** `/auth/logout` is a controller that answers JSON, so
  the browser landed on a raw `{"logout_challenge":"…"}` body, nothing ever accepted the request,
  and the session outlived the sign-out. The login SPA now serves a `/logout` page that confirms
  the challenge, accepts it, and follows the target through the same open-redirect guard as every
  other redirect it takes.
- **`hydra.urls.error` was never set**, so an OAuth2 failure rendered Hydra's own "configuration
  key urls.error is not set" page. The login SPA has had an error route all along.

### Fixed — the chart could not be published

- **`helm template` failed on the chart's own defaults.** `publicUrl` and `adminUrl` existed only
  in the environment files, so a bare render reached `urlParse` with a nil and died on "wrong type
  for value; expected string; got interface {}" — an error naming neither the key nor the file to
  put it in. Both are now declared and guarded by `required`, so the message names what is missing.
- **The two Ingresses pinned Traefik's entrypoint names**, and the admin one emitted its `tls:`
  block with no guard at all. `ingress.public.entrypoints`, `ingress.admin.entrypoints` and
  `ingress.admin.tls.enabled` now carry those decisions. Defaults render identically, and the P-04
  deny router — the control that keeps `/admin`, `/org`, `/project` and `/service-accounts` off the
  public host — is asserted present in every variation.
- **The default image tag had drifted** to 0.2.3 while the chart said 0.3.0. Only `deploy.sh`
  passing `--set image.digest` hid it; a plain `helm install` pulled an image four releases old.
  The tag now defaults to the chart's own `appVersion`.
- **`deploy.sh` hardcoded the Postgres host** in all three generated DSNs. `PG_HOST` overrides it —
  under CloudNativePG that is `<cluster>-rw.<namespace>.svc` — and defaults to the StatefulSet the
  chart installs, so an existing install is untouched.

### Fixed — tests that could not fail

- The two registration tests asserted only that the call did not throw, which is true of a payload
  missing every field that matters. The Hydra stub can read request bodies now, and they assert on
  what was sent.
- `deploy/tests.sh` compared YAML *values* where it meant to compare key names, so two keys sharing
  a value read as a duplicate and two genuinely duplicated keys did not. Several checks piped into
  `grep -q` under `set -o pipefail`, where grep exiting at the first match gives the writer a
  SIGPIPE and the pipeline reports failure — which is why a different check failed on each run.

---

## [0.2.6] — 2026-08-02

### Fixed — the light theme was only half a theme

- **The sidebar stayed dark in the light theme.** `--iam-sidebar` and its four companions were a
  fixed dark navy in both palettes, so switching to light drew a dark rail down the side of a light
  application. The rail is now a surface of whichever theme is active: one step under the page in
  light, as it already was in dark.
- **Three status colours failed contrast on their own chips.** `--success`, `--warn` and `--info`
  were chosen against white and landed at 3.67:1, 2.91:1 and 3.83:1 once their `--*-soft` tint sat
  behind them — below the 4.5:1 floor for the 11px text chips use. Darkened in the light theme; the
  dark values already cleared it.
- `.iam-nav-icon` carried `opacity: 0.8`, which is a colour nobody chose and put the sidebar icons
  under the 3:1 floor for non-text contrast.
- Two components spelled `className="mono"` where the rule is `.iam-mono`, so the role label under
  the sidebar and the command-palette subtitles silently lost their monospace.

### Testing

- `theme.test.ts` converts every palette token from oklch to relative luminance and asserts WCAG AA
  on 30 foreground/background pairs, per theme — text on every surface, chips on their tints, the
  accent on the page and on the rail, the inverted toast. It also asserts the sidebar sits with the
  theme rather than against it, and that no component drops the `iam-` prefix off a class that has
  a rule only with it.

---

## [0.2.5] — 2026-08-02

### Fixed

- **The chart locked the first admin out of the console.** `AppConfig` defaults
  `Security:RequireAdminMfa` to false on purpose — enrolling a factor needs SMTP or SMS,
  configuring either needs the console, and the console carries a standing reminder
  (`MfaReminder`) for an admin with no factor. `values.yaml` set it to `true` anyway, which
  overrode that default on every install: the first sign-in answered `requires_mfa_setup` and sent
  the operator to an enrolment page for a delivery channel that could not exist yet. The chart
  default now matches the code; `values.prod.yaml` turns it on explicitly, which is where that
  decision belongs. `deploy/tests.sh` holds both halves.

---

## [0.2.4] — 2026-08-02

No wire-contract change. **Contains a fix for a full second-factor bypass — upgrade before anything
else in this series.**

0.2.3 was built and deployed to dev but never tagged; everything it carried ships here.

### Fixed — the console showed things that were not true

- **The login-activity chart drew a sine wave.** `logins_by_hour` was declared in the console's
  `Metrics` interface and never sent, so `ActivityChart` generated its own bars from `Math.sin()`
  and rendered them, identical on every deployment and every reload, beside real counts.
  `GET /admin/metrics` now returns 24 hour-aligned buckets counted from the audit log, and the
  component renders "No sign-ins recorded in this window" when there are none.
- **A failed load looked like a real configuration.** `Authentication` and `ProjectSettings`
  swallowed the initial GET's error and rendered their `useState` defaults; Save then wrote every
  field back, so one transient 500 plus one Save replaced a tenant's theme, providers, verification
  flags, allowed domains, IP allowlist and scopes with defaults — and reported success. Both now
  refuse to render the form after a failed load.
- **An emptied "inactive for … days" field meant every user.** Clearing a `type="number"` input
  yields `''`, and `Number('')` is `0`, so the cleanup dialog offered to act on the whole list.

### Fixed — accessibility

- **Nine table rows were reachable by mouse only.** Organisations, projects, user lists, users,
  service accounts and user-list members navigate on a whole-row `onClick` with no tabindex and no
  key handler; a keyboard user tabbed past every one. A shared `rowActivation` helper gives them
  focus and Enter/Space.
- **Fifteen toggles announced themselves as unlabelled buttons.** The Authentication page built
  each switch from a bare `<button>` with no role, no state and no accessible name — "Require MFA"
  was indistinguishable from "Reject breached passwords". Now a labelled checkbox.
- **The login preview iframe could not run scripts**, so the preview it existed to show never
  rendered.

### Fixed — stale effects

- `ProjectUsers` read `isOrgAdmin`/`orgId` from context but depended only on `projectId`, so an org
  admin opening the page directly got an empty user-list dropdown until a manual refresh.
  `OrgAuditLog` had the same shape against the scope switcher.

### Deployment

- **The production PodDisruptionBudget protected nothing.** `maxUnavailable: 1` against
  `replicaCount: 1` lets a node drain take the last pod. Prod now runs two replicas.
- **`preflight.sh` validated the committed defaults, not the deploy.** Three checks — ingress
  class, trusted proxies, default-deny scope — grepped `values.yaml` directly and ignored
  `values.<env>.override.yaml`. They now read the rendered chart.
- `deploy/tests.sh`: 36 static checks over the deploy layer, which had no test harness at all.

### Documentation

- `docs/TESTING.md` claimed 1345 backend tests and said twice that neither SPA has any, while 169
  frontend tests run in seconds. `docs/API.md` undercounted the routes it lists (184 → 187).
  `frontend/admin/README.md` described Radix, shadcn, `oidc-client-ts` and `recharts`, none of
  which the SPA still uses. The root README had RLS and cache TLS as off in both environments; both
  are on in dev and prod.

### Security

- **An attacker holding only a password could take over an MFA-protected account.**
  `POST /auth/mfa/setup/totp/start` and `/confirm` authenticated off the `mfa_pending_user` session
  key, which is also set when the server has just *challenged* for a factor. The
  `mfa_setup_required` flag meant to separate enrolment from challenge was written twice and read
  nowhere. So after answering the password step an attacker could ask for a fresh TOTP secret,
  confirm it with a code from their own authenticator, and complete the login — replacing the
  victim's factor and destroying their backup codes. It applied to tenant accounts and to the
  console's `super_admin` alike. Both endpoints now require an enrolment session *and* an account
  with no existing factor, and the flag is cleared when the login completes. Re-enrolling over an
  existing factor remains available at `/account/mfa/totp/*`, which demands re-authentication.
- **`/metrics` was reachable on the public port.** It was bound with `RequireHost("*:5001")`, which
  matches the `Host` header rather than the port the connection arrived on — and host filtering
  strips the port before comparing. Anyone could scrape login outcomes, per-tenant DB-scope counters
  and route volumes by sending the admin port in a header. Now bound to `Connection.LocalPort`.
- **The `/preview` framing exemption reached the login page.** It matched the whole path subtree,
  and the SPA fallback answers `/preview/<anything>` with `index.html`, whose catch-all route renders
  the real login form — served with `X-Frame-Options` omitted. Narrowed to that exact path,
  case-sensitively, with `frame-ancestors 'self'` and no second origin.

### Fixed — data loss

- `GET /project/info` returned none of the nine fields its own `PATCH` accepts. The console's
  Authentication screen reads them, edits one and writes them all back, so opening that page and
  pressing Save replaced the project's login theme, identity providers, self-registration setting,
  verification flags, allowed e-mail domains, IP allowlist and OAuth2 scopes with the page's
  hardcoded defaults.
- `GET /project/users` answered 404 for a project with no user list assigned — the same shape as the
  two stats handlers, and the third instance of it. The console fetches users and roles together, so
  the members panel of every freshly created project rendered empty.

### Fixed — admin console

- Eleven dialogs rendered their submit button outside the form it submits, so the primary action did
  nothing at all: editing any user, renaming an organisation or project, creating projects and user
  lists, generating a PAT, and assigning roles. Ten more dialogs had a Cancel button that was a
  submit with no handler — inert, beside a live Delete.
- Seven `<select>` controls could hold a value absent from their own options, so the browser painted
  the first entry as chosen while state stayed empty. Two of them granted a role org-wide while
  displaying a project as the scope.
- Dark theme: `color-scheme` was never declared, so every widget the browser draws itself — select
  popups, date pickers, checkboxes, autofill, scrollbars — stayed in light chrome. The destructive
  button ignored the foreground token added for it, the sidebar popover and footer used page tokens
  on a rail that is dark in both themes, the avatar hardcoded a light-only colour pair, and twelve
  opacity-modified utilities compiled to nothing because Tailwind cannot apply a modifier to a bare
  `var()`. Nav labels, table headers and placeholders were also below AA contrast.
- The sidebar showed a hardcoded `v0.1`; it now reports the running server's version, which
  `/admin/config` returns.
- The theme selector moved from the top bar to the sidebar, beside the product name and version.

### Fixed — deploy

- **Every deploy uninstalled the release it was upgrading.** `helm_deploy` ran
  `helm rollback <rel> 0 || helm uninstall --no-hooks` first; `rollback 0` errors on a
  single-revision release, so the uninstall fired — and since uninstall-then-install returns the
  release to revision 1, it fired on every subsequent deploy. Each one deleted the backup PVC and
  every dump retained in it. It now upgrades in place, and only rolls back a release genuinely stuck
  in a `pending-*` state.
- **No backup has ever worked.** `GRANT pg_read_all_data TO iam_backup` had been removed from the
  Postgres init while every comment and document citing it stayed in place, so `pg_dumpall` hit
  permission denied on the first table. The 13 detection rules authenticate as the same role and had
  been erroring for just as long.
- **The image could not build the console.** The admin build stage's context excluded the SDK tree
  its Vite alias resolves to. Because `deploy.sh` builds both SPAs on the host first, a failed image
  build still had a `dist` to ship: the deploy reported success and pushed a stale image. Every
  deploy since the console moved onto `rediensiam-web` shipped pre-cleanup code.
- `monitoring/selftest.sh` never got the password the T-04 role split made mandatory, so its six
  assertions failed as value mismatches rather than as an authentication error.
- `reset-dev.sh` treated an issuer it could not read as confirmation that the release was a dev one.
- Destructive commands in `deploy.sh` named the release literally instead of using `${RELEASE}`.

### Changed

- `Security:RequireAdminMfa` now defaults to **false**, and the console shows a standing reminder
  instead. Gating the bootstrap admin's first login on enrolment locks the operator out of the
  console they need in order to configure the SMTP or SMS provider that makes a factor deliverable.
  `values.prod.yaml` sets it back to `true`; turn it on in your own values once configured.

### Added

- `deploy/tests.sh` — static tests for the deploy layer, which had no harness. Four of the faults
  above are pinned by it.
- Contract tests in the admin console for markup that type-checking cannot see: dialog submit
  buttons with no form owner, buttons that do nothing, selects that can display a value they do not
  hold, light/dark palette parity, `iam-*` class names with no rule, and that the image copies
  everything the console's aliases import.

---

## [0.2.2] — 2026-08-01

No wire-contract change. **But one behaviour change for existing SAML integrations, and an upgrade
of an existing deployment now needs manual steps for the first time in this series — read "Before
you upgrade" below.**

### Fixed

- **One tenant's user could be signed in for another tenant's social login.**
  `FindOrCreateSocialUserAsync` matched a provider subject on `Provider` and `ProviderUserId` alone,
  with no tenant constraint. A single Google account invited by two customers resolves to the same
  `ProviderUserId`, so the *first* tenant's user was returned for the *second* tenant's login and
  the session was minted against it. The lookup is now constrained to
  `project.AssignedUserListId`. This is a real cross-tenant authentication path and it was open.
- **MFA failure audit rows were written with no tenant.** `VerifySmsOtp` and `VerifyTotp` recorded
  `user.mfa.*.failed` with `OrgId = null`, so the affected tenant could not see its own users' MFA
  failures in its audit view. They now record against the pending MFA session's org and project.
- **`Database:MigrateOnStartup` was decorative.** The key shipped in `appsettings.json` and nothing
  read it: `Program.cs` migrated unconditionally, so an operator who set it to `false` still got
  migrations applied, with no signal that the instruction had been ignored. It is honoured now.
  Default is still `true`, so nothing changes until someone sets it. When false, the app starts and
  logs at Warning how many migrations it is behind — refusing to boot would make the switch useless,
  and staying quiet would surface an un-migrated schema as unexplained 500s a long way from the
  cause.
- **`verify-deployment.sh --prod` measured the wrong host, and V-04 passed because of it.** The
  script did not layer the operator answers from `values.prod.override.yaml`, so it probed the
  committed default hostname. Traefik answers 404 on a Host it has no router for, V-04 counted 404
  as a refusal, and the P-04 management-API assertion **read green while measuring nothing**. The
  override file is now layered, and V-04 requires `/login` on the public host to answer first or the
  deny probes are reported inconclusive rather than passing.
- **Row-level security could never have been enabled on any production database.** `init.sh` granted
  `iam_backup` its required `BYPASSRLS` only when `postgres.rls.enabled` was already true at initdb
  — and `setup.sh --prod` forces RLS off on a first install without asking. The grant is
  unconditional now. **Databases created before this release still need
  `ALTER ROLE iam_backup BYPASSRLS` applied by hand**; `files/rls.sql` fails the deploy rather than
  the backup if it is missing.
- **A first-ever production install could not complete**, in four further ways, all in `deploy/`:
  a fixed-name cluster-scoped `ClusterIssuer/selfsigned` that made `helm install` fail on any
  cluster already holding one and let `helm uninstall` of either release delete the other's issuer;
  PostgreSQL crash-looping on a fresh volume because a freshly provisioned root is owned by uid 0
  and `initdb` cannot chmod a directory it does not own; `helm --wait` deadlocking on a backup PVC
  that a `WaitForFirstConsumer` StorageClass will not bind until the nightly CronJob fires; and a
  smoke probe whose failed `curl` killed the whole script under `set -e`, telling the operator a
  healthy install might be half-changed.

### Added

- **The tenant login path now runs under its own organisation's RLS scope.** A Hydra login challenge
  names an OAuth2 client, and RediensIAM writes `org_id` into that client's metadata at project
  creation — so the tenant is knowable before any user lookup, usually with **no database read at
  all**. `TenantScopeInterceptor.PinToOrganisationAsync` publishes it as `rediensiam.org_id` before
  the request reads anything. Password login, registration and its verification, consent, every MFA
  step, the social flows and the password-reset request are all pinned. Measured on the dev cluster
  over ten complete authorization-code logins: 80 connection checkouts, **0 scoped before, 80 scoped
  after**, with no extra round trips. The scope is never derived from caller input, and the pin
  refuses to move a request its own token already scoped to a different organisation.
- **SAML responses are validated against this deployment's ACS URL.** A response whose `Destination`
  names a different endpoint is refused. See the behaviour change below, and
  [`docs/SECURITY.md`](docs/SECURITY.md) §7 for the limit — this is solid against IdPs that sign the
  `<Response>` element and bypassable against IdPs that sign only the assertion, which this SP
  accepts.
- `iam_db_connection_scope_total{scope}` — a Prometheus counter incremented at the point the
  database scope is decided, so the scoped/unscoped ratio is measurable instead of asserted.

### Changed

- The chart's self-signed issuer is a namespaced `Issuer/<release>-selfsigned` rather than a
  cluster-scoped `ClusterIssuer/selfsigned`. `ingress.admin.clusterIssuer` now defaults to `""`,
  meaning "the chart's own Issuer"; a non-empty value still names a real ClusterIssuer you run.
- `PGDATA` moved to `/var/lib/postgresql/data/pgdata`.
- `deploy.sh` no longer passes `helm --wait`; it runs `kubectl rollout status` on the workloads that
  actually render. The RLS post-upgrade Job now polls for `__EFMigrationsHistory` and fails loudly
  rather than assuming Helm ordered it after the app.
- Four dead configuration keys removed from `appsettings.json`: `Cache:ProjectTtlMinutes`,
  `Cache:JwksTtlMinutes`, `App:FrontendUrl`, `App:LoginPath`. None had a reader anywhere in the
  repository. No chart or environment-variable spelling referenced them either.

### Before you upgrade

1. **SAML operators, before anything else.** For each configured IdP, confirm that
   `{App:PublicUrl}/auth/saml/acs` is the ACS URL registered at that IdP. `GET /auth/saml/metadata`
   prints the exact value the app will compare against, in `AssertionConsumerService/@Location`.
   Host case, scheme case, an explicitly written `:443`/`:80` and a trailing slash are tolerated; a
   different host, a different path, a scheme downgrade or a genuinely different port are not. The
   usual way this bites is an `App:PublicUrl` pointing at a cluster-internal address while the IdP
   holds the public ingress hostname. Miss it and every SAML login fails with
   `400 saml_response_invalid`. IdPs that send no `Destination` are unaffected — absence is accepted
   and logged at Warning.
2. **The PGDATA move is a migration.** An installation created before this release keeps its data
   directory at the volume mount root. A `pgdata-location-guard` init container **will stop the next
   deploy on purpose** rather than let Postgres `initdb` an empty cluster beside the real data and
   report success. It prints the commands; it is one `mv` with the StatefulSet scaled to zero. Doing
   nothing is safe — a running pod is unaffected.
3. **The issuer rename re-issues the Postgres and Dragonfly certificates**, which restarts
   Dragonfly, which empties the DataProtection key ring. Every session is invalidated once.
4. **If you intend to enable RLS on an existing database**, apply
   `ALTER ROLE iam_backup BYPASSRLS` as superuser first. See above.

### Known limits

- The production profile has now been installed once — into a scratch namespace on the single-node
  dev cluster, then destroyed. That establishes that the chart, the two scripts and the values files
  agree with each other. **It does not establish that production works.** ACME has never been
  executed and no publicly trusted certificate has ever been issued; no backup has been restored; no
  upgrade has been run across a schema migration on a populated database; the Postgres `requireSsl`
  and Dragonfly TLS *cutovers* against pre-existing state remain reasoned about, not observed;
  nothing has been up longer than an hour. A scratch namespace is not production.
- Scoping the login path does not make tenant isolation structural. The admin console cannot be
  scoped (its users live in a list with `OrgId IS NULL`, invisible under every tenant scope by
  construction), the token-keyed endpoints identify their subject by a random token, and
  **`SamlController` can be pinned and has not been.** See
  [`docs/SECURITY.md`](docs/SECURITY.md#what-is-scoped-and-what-still-is-not).
- RLS remains off by default and in `values.prod.yaml`.

---

## [0.2.1]

No wire-contract change. Upgrading from 0.2.0 needs nothing beyond a redeploy.

### Fixed

- **Admin console: a failed MFA mutation was reported to nobody.** `ReauthDialog`'s `guard()`
  resolved when the re-authentication prompt opened rather than when the mutation finished, so a
  mutation that failed *after* a correct proof — a 500, a 409, a dropped connection — threw into a
  caller whose `catch` had already gone out of scope, and the rethrow became an unhandled promise
  rejection. The user typed the right password, the prompt closed, and no error appeared:
  indistinguishable from success. On regenerating backup codes or deleting a passkey, believing a
  change happened when it did not is how an account gets locked out. Unreachable on a *bad* proof,
  which is why reading the code never caught it.
- Login form: the primary email-or-username field had no accessible name.

### Added

- **First tests for both SPAs** — 150 across seven files. `vitest` and `@testing-library` had been
  installed in both and left entirely unused, so every authentication change in 0.2.0 was defended
  by a typecheck and by someone having read it. The bug above is what they found.
- **Row-level security is enabled**, 19 tables with `ENABLE` + `FORCE`. Without `FORCE` the table
  owner is exempt and the policies are decorative. `ALTER ROLE iam_backup BYPASSRLS` is now granted
  at initdb and enforced by a precondition that aborts the deploy — without it `pg_dumpall` fails
  and the nightly backup stops silently, which is worse than the finding it closes.
- `k3s --secrets-encryption` documented in the README, including the `reencrypt` step: enabling the
  flag protects only what is written afterwards.

### Changed

- `values.prod.yaml` now enables Dragonfly TLS. **Verified by rendering only** — there is no
  production cluster, so this is not proven. A cache that survives the upgrade holding an
  unprotected DataProtection key ring needs `DEL rediensiam:dataprotection:keys` first; see
  `docs/DEPLOYMENT.md`.

### Known limits

Enabling RLS does **not** make the login path tenant-safe: users are resolved by e-mail before a
tenant is known, so that path runs unscoped by necessity. Measured over one minute of
tenant-exercising traffic: 5 org-scoped connections, 15 as `'system'`. See `docs/SECURITY.md`.

*(This was true of 0.2.1 and is left as the record of it. **Superseded in 0.2.2**, which pins the
tenant from the login challenge before any user lookup.)*

---

## [0.2.0] — 2026-07-31

The security-hardening release. It is a **breaking** release for anyone who integrates against
`/api/introspect`, `/api/authorize` or the `ext.roles` claim, and it changes how the deployment
talks to its own database.

### Read this first — the upgrade in order

Four wire-contract changes ship together, and one of them makes **deploy order load-bearing**.

1. **Audit your consumers of `ext.roles`.** Any code doing `roles.contains("admin")` on a *tenant*
   role stops matching. It fails closed, so nothing is silently granted — but people lose access.
   Fix these before you deploy anything. See [break 1](#1-extroles-is-project-qualified).
2. **Decide which service account each resource server uses.** An org-scoped service account now
   gets `active: false` for other organisations' tokens. A multi-tenant gateway needs a
   deployment-level (`__system__`) service account. See [break 2](#2-introspection-and-authorisation-are-tenant-scoped).
3. **Deploy the server.** `aud` becomes mandatory the moment it starts.
   See [break 3](#3-aud-is-required-and-every-answer-carries-ver-1).
4. **Then upgrade the SDKs**, setting the new required audience option in each service.
   See [break 4](#4-the-sdks-refuse-a-cleartext-url-a-missing-audience-and-a-server-without-ver).

Steps 3 and 4 are in that order for a reason. An **upgraded SDK refuses every answer from an
un-upgraded server** — it is the anti-downgrade check doing its job, not a bug. The reverse
direction is harmless: an old SDK against a new server gets a clear `400 audience_required`, and an
old server silently ignores the `aud` a new SDK sends, which is exactly the case the check exists
to catch.

If you must upgrade the SDK first, expect that service to be down until the server follows.

---

## Breaking — the wire contract

### 1. `ext.roles` is project-qualified

Tenant role names are chosen by tenant admins and were emitted bare. Two tenants both naming a role
`admin` were byte-identical in every consumer's token, and nothing stopped a tenant from naming one
`super_admin`.

| | 0.1.0 | 0.2.0 |
|---|---|---|
| Tenant role in `ext.roles` | `"admin"` | `"{project_id}/admin"` |
| Management role in `ext.roles` | `"org_admin"` | `"org_admin"` — unchanged, still bare |
| Tenant role named `super_admin` | accepted | rejected at creation, `{"error":"role_name_reserved"}` |

The three management names (`super_admin`, `org_admin`, `project_admin`) are reserved
case-insensitively, and `/` is now rejected in a tenant role name so the qualified form is
unambiguous.

**What fails:** `roles.contains("admin")` matches nothing. In the .NET SDK the qualified string is
what lands in `ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` stops matching — it used to
match *every* tenant at once, which is the defect. The direction of failure is closed, never open.

**What to do:**

| Language | Was | Now |
|---|---|---|
| C# | `info.HasRole("admin")` | `info.HasProjectRole(projectId, "admin")` |
| Rust | `info.has_role("admin")` | `info.has_project_role(&project_id, "admin")` |
| Browser | `iam.hasRole('admin')` | `iam.hasProjectRole('admin')` — project defaults to the token's |
| Raw | `roles.includes("admin")` | `roles.includes(projectId + "/admin")` |

`HasRole` / `has_role` / `hasRole` still exist and now match **management roles only**. Note the
argument order differs: project first in the backend SDKs, role first in the browser one (the
project is optional there).

**Where the tenant role names themselves are unchanged:** this is a claim-encoding change, not a
data migration. Nothing in the database moved.

### 2. Introspection and authorisation are tenant-scoped

`POST /api/introspect` and `POST /api/authorize` now answer only about the caller's own tenant.

- A service account attached to an **organisation** may introspect that organisation's tokens
  only. Another organisation's token answers `{"active": false, "ver": 1}` — deliberately
  indistinguishable from expired or revoked, because confirming that someone else's token exists
  is the disclosure being closed.
- A **deployment-level** service account — one attached to the `__system__` user list, holding no
  org-scoped role — stays unscoped, because that is what a multi-tenant gateway must hold.
- On `/api/authorize`, the `object` is scoped too: asking about another tenant's object answers
  `{"allowed": false}`, in the same shape as a genuine deny so the endpoint cannot be used to
  enumerate. Every refusal writes an `api.authorize.object_out_of_scope` audit row.
- The `System` Keto namespace is refused to **every** caller. `System:rediensiam#super_admin`
  enumerates the deployment's administrators and never authorises the caller's own request. A
  resource server that needs to know whether a subject is a super admin reads the `roles` field of
  `/api/introspect`, which re-verifies against Keto before answering.

**What fails:** a gateway holding one tenant's service account, used to validate several tenants'
tokens, starts denying everyone outside its own organisation.

**What to do:** give a multi-tenant gateway a `__system__` service account. Give a single-tenant
resource server that tenant's own account — it is strictly better scoped. See
[`docs/INTEGRATION.md`](docs/INTEGRATION.md).

### 3. `aud` is required, and every answer carries `ver: 1`

Both endpoints now require the caller to declare which tenant it serves.

```
POST /api/introspect   (form)  token=…&token_type_hint=access_token&aud=<project-or-org-id>
POST /api/authorize    (json)  {"token":…,"namespace":…,"object":…,"relation":…,"aud":"<project-or-org-id>"}
```

Omit it and the answer is `400 {"error":"audience_required","ver":1}`. **No grace period, no
opt-out.** A token that does not belong to the declared audience reads `{"active": false}` /
`{"allowed": false}` — again the same shape as a dead token.

`aud` may be the project id the service fronts, or the organisation id if it fronts a whole
organisation. Matching against `project_id`/`org_id` is case-insensitive; matching against the
token's own OAuth2 audience list is case-sensitive.

> RediensIAM does not itself set an OAuth2 `aud` on the tokens it mints — `AcceptConsentAsync`
> sends no `grant_access_token_audience`. Binding therefore works through `project_id` / `org_id`
> unless the relying party requested an audience at `/oauth2/auth`.

`ver: 1` is stamped on every 200 answer — including `{"active": false}` — and on the
`audience_required` 400. It is **not** on `403 service_account_required`, on ASP.NET Core's
`ValidationProblemDetails` for a missing required field, or on the middleware 401. An SDK enforcing
"`ver >= 1` or fail closed" is unaffected: all of those are non-200s.

`ver` is a **response field, not a token claim.** It describes the server's capability, not the
token.

**What to do:** send `aud`. If you use the SDKs, that is the new required option in break 4.

### 4. The SDKs refuse a cleartext URL, a missing audience, and a server without `ver`

All three checks fire **at construction**, not on the first request: a deployment mistake should
stop the process at startup with a message naming the fix, not turn into failures once traffic
arrives.

| | C# (`RediensIAM.Client`) | Rust (`rediensiam-client`) | Browser (`rediensiam-web`) |
|---|---|---|---|
| Audience option | `RediensIamOptions.Audience` — **required** | `Config::audience` — **required** | n/a |
| Missing audience | `ArgumentException` | `Error::Config` | n/a |
| Cleartext base URL | `ArgumentException` | `Error::Config` | `RediensIamError('config_invalid')` on `issuer` and each `apiOrigins` entry |
| Answer without `ver` | `InvalidOperationException` | `Error::ServerTooOld { found }` | n/a — never calls those endpoints |

**HTTPS is required.** `http://` is accepted **only** on a loopback host — `localhost`,
`127.0.0.1` (all of `127.0.0.0/8` in the .NET SDK, via `Uri.IsLoopback`), `::1`/`[::1]`. There is
deliberately no flag to disable the check, because a flag gets set in production too. The
service-account credential and every token being introspected ride on that URL.

**There is no default audience and there will not be one.** A default is a guess about which
tenant a service belongs to, and a wrong guess reproduces the finding this closes.

**The `ver` check is why deploy order matters.** A server older than contract version 1 does not
*reject* the `aud` you send — it silently discards the unknown field and answers exactly as it
always did. An SDK that only *sent* `aud` could not tell an enforcing server from an ignoring one
and would report success while bound to nothing. Requiring `ver` turns that silent failure into a
loud one:

```
RediensIAM answered with ver=0, expected at least 1: this server predates mandatory
audience binding and silently ignored the aud this client sent. Upgrade RediensIAM
before trusting its answers.
```

**The browser SDK needs no migration.** `rediensiam-web` never called these endpoints, because
introspection needs a service-account credential and a credential shipped to a browser belongs to
anyone who opens devtools. It gained no audience option and never will.

#### Symptom table

| Symptom | Cause |
|---|---|
| Throws at startup naming `Audience` / `audience` | SDK upgraded, option not set for that service |
| Throws at startup naming `https` | `BaseUrl`/`base_url`/`issuer` is `http://` to a non-loopback host |
| `400 audience_required` | An un-upgraded SDK, or a raw-HTTP caller, against an upgraded server |
| `ver=0, expected at least 1` / `ServerTooOld` | An upgraded SDK against an un-upgraded server — deploy order |
| `{"active": false}` on a token you know is good | The `aud` you configured names a different tenant than the token belongs to, **or** your service account belongs to a different organisation than the token |
| A role check that used to pass now fails | Break 1 — the role is qualified now |

---

## Breaking — behaviour you will meet as an error

### `POST /account/mfa/phone/setup` answers `503`

Where the deployment has no SMS provider wired up — the provider is a stub — phone enrolment now
returns `503 {"error":"sms_provider_not_configured"}` instead of appearing to succeed.

**What to do:** handle the 503 as "this deployment cannot do SMS", not as a transient failure. If
you present SMS as an MFA option in your own UI, gate it on this.

### Removing a project's `require_mfa` needs an explicit confirmation

Turning `require_mfa` **off** on a project is refused the first time:

```
409 {"error": "mfa_downgrade_requires_confirmation",
     "enrolled_user_count": 47,
     "consequence": "…a stolen password alone becomes sufficient to sign in…",
     "confirm_with": "confirm_mfa_downgrade"}
```

Retry the same request with `"confirm_mfa_downgrade": true` in the body and it proceeds, writing a
`project.mfa_requirement_removed` audit row. The confirmation travels in the body of the request it
authorises, so it cannot be replayed onto a different one.

Only the true → false direction is guarded, and only when the project's assigned user list contains
at least one user holding a factor (TOTP, verified phone, or WebAuthn credential); with zero
enrolled users there is nothing to warn about and the request proceeds unchanged. Enabling
`require_mfa` is untouched.

This applies on all three prefixes that reach the setting: `/admin` + `/api/manage`, `/org`,
`/project`.

**What to do:** if you script project settings, add the field to any request that clears
`require_mfa`.

---

## Breaking — storage and deployment

### Encrypted values gained a key-id envelope

Ciphertexts written by `TotpEncryption` — TOTP secrets, webhook secrets, SMTP passwords, social
provider client secrets in a project's login theme — may now carry a `k<id>:` prefix naming the
key they were encrypted under.

**This is inert until you rotate.** Key id 1 is written with an **empty** prefix, so a deployment
that has never rotated keeps writing the exact byte format it wrote before, and a value with no
prefix is read as key 1. Upgrading to 0.2.0 alone changes nothing on disk.

Once you add a second key to `Security:EncryptionKeys` and make it active, new writes are prefixed
`k2:`, and `POST /admin/key-rotation/reencrypt` sweeps the cold rows (`GET /admin/key-rotation`
reports what is pending — **`TotalPending == 0` is the only signal that a retired key may be
dropped from the ring**). Dropping a key that still has data under it is unrecoverable; the code
throws a `CryptographicException` naming the missing key id rather than failing silently.

> **Rolling back after a rotation is not clean.** A 0.1.0 binary has no envelope parser: it feeds
> `k2:…` straight to `Convert.FromBase64String` and throws. Any row rewritten under key 2 is
> unreadable to the older release. Before rotating, be sure you are done rolling back — or restore
> from a backup taken before the sweep.

### PostgreSQL is split into four least-privilege roles

The shared `iam` SUPERUSER is no longer a runtime credential. A fresh install creates
`iam_app`, `iam_hydra`, `iam_keto` and `iam_backup` — all `NOSUPERUSER NOCREATEDB NOCREATEROLE
NOBYPASSRLS`, each owning only its own database, with `iam_backup` holding `pg_read_all_data` and
nothing else. `iam` remains as initdb's owner and the break-glass account, and appears in no DSN.
`pg_hba.conf` is `scram-sha-256`.

Four new chart values carry the passwords; `deploy.sh` generates all four:

```
postgres.local.roles.appPassword
postgres.local.roles.hydraPassword
postgres.local.roles.ketoPassword
postgres.local.roles.backupPassword
```

> **These roles are created only on a first-ever start**, because they are created by initdb. An
> **existing** installation is not migrated by upgrading the chart — it keeps running on `iam` and
> gets none of the benefit. The manual migration is in
> [`SECURITY-AUDIT-LOG.md`](SECURITY-AUDIT-LOG.md) step 15c.

The `iam` SUPERUSER with `local all all trust` in `pg_hba.conf` on an existing cluster remains the
highest-ranked open finding in the ledger (T-04). Splitting the roles on a *new* install is the
first half of closing it.

---

## Added

- **`/api/manage/*` — a machine-to-machine parity surface.** `SystemAdminController` is now routed
  under both `/admin` (what the admin SPA calls) and `/api/manage` (reachable with a SuperAdmin PAT
  or a `client_credentials` token). The same actions on the same class, not a second
  implementation — a re-implementation is exactly where an authorisation check goes missing, and
  the `ManagedApiController` that used to re-implement seven of them is gone. Every route passes
  through one `RequireManagementLevel` attribute, i.e. one live Keto re-check, whichever prefix was
  used. `WebhookController` and `SystemHealthController` expose `/api/manage/webhooks` and
  `/api/manage/system` on the same basis.

  **Reachability is an ingress property, not an application one.** `adminOnlyPaths` is
  `[/admin, /org, /project, /service-accounts]`; `/api` is deliberately absent, which is why
  `/api/manage/*` answers on the public host and `/admin/*` returns 403 there. Both listeners map
  every controller — the port split is not a trust boundary.
- **`ver` on the introspection and authorisation answers** (see break 3).
- **Key rotation.** `GET /admin/key-rotation` and `POST /admin/key-rotation/reencrypt`, plus the
  `Security:EncryptionKeys` ring and the `security.argon2Peppers` pepper ring. Operator-triggered
  rather than a background job, so it runs when every replica already has both keys and cannot race
  N replicas rewriting the same rows.
- **Row-level security**, complete and **shipped off** (`postgres.rls.enabled: false` everywhere).
  Policies, SQL and the migration Job are in the chart; the application side (the tenant-scope
  interceptor) is in the build. Turning it on before verifying the application half on a live
  connection is a total outage — see [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).
- **Documentation.** [`docs/API.md`](docs/API.md) (184 routes),
  [`docs/SECURITY.md`](docs/SECURITY.md), [`docs/TESTING.md`](docs/TESTING.md), a rewritten
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), a README per SDK and per SPA, and this file.

## Changed

- **Audience matching, role resolution and scope checks are re-verified live.** Management roles
  reported by `/api/introspect` are re-checked against Keto before being returned, so a role revoked
  after the token was minted does not survive in the answer.
- **Cache keys in the backend SDKs are SHA-256 digests**, replacing a 64-bit non-cryptographic hash
  in the Rust client. The cache returns a full `TokenInfo` — roles included — before any server
  call, so a collidable key was an authentication bypass.
- **The browser SDK's `fetch()` refuses off-origin targets.** The bearer is attached only to the
  app's own origin or to an origin listed in the new `apiOrigins` option; anything else throws
  `untrusted_target`.
- **The browser SDK validates the discovery document.** Every endpoint it uses must sit on the
  issuer's own origin, otherwise whoever answers for the issuer chooses where the PKCE verifier and
  the refresh token are sent.
- **Redis/Dragonfly traffic is TLS-pinned** and the DataProtection key ring is encrypted at rest
  under the root key. On in dev; `values.prod.yaml` sets no `dragonfly` block, so **prod inherits
  `enabled: false`**.
- **`App__TrustedProxies` must be set explicitly in production.** Silently trusting RFC1918 lets any
  pod in a multi-tenant cluster spoof `X-Forwarded-For` and bypass per-IP controls. The chart ships
  the k3s CIDRs for dev.
- **CSP is set by the server**, with a distinct policy for `/admin` (which pins `connect-src` to the
  exact issuer origin) and for the login pages. Each SPA's `index.html` carries a meta policy too;
  browsers enforce the intersection, and a request must satisfy both.

## Security

- **The audit log gained a floor and an append-only guard.** Security-relevant mutations are
  recorded on `SaveChanges` itself, so an endpoint written next year cannot ship unaudited by
  omission. These automatic rows are named `entity.*`, so a query can tell them from the
  hand-written ones that carry intent (`user.password.reset`) — **if you parse the audit log, expect
  new `entity.*` action names.** Updating or deleting an audit row throws.
  Rows are chained per organisation, but the chain is **unkeyed SHA-256, not an HMAC**, and its
  verifier has no production caller — tamper-evidence against a careless adversary, not a capable
  one. Database-side append-only enforcement is not in place. Stated plainly in
  [`docs/SECURITY.md`](docs/SECURITY.md) §5.
- SAML `idp_id` is bound to the challenge's project (`SamlController.Start` 404s otherwise).
- The container registry is bound to loopback and images are deployed **by digest**, so a pod
  restart replays the exact bytes that were reviewed.

## Not fixed

This release does not close everything. The ranked list of what is still open — including the
`iam` SUPERUSER on existing clusters, the absence of a rotation story for the HKDF root and the
Argon2 pepper, an untested backup restore, and 7 high npm advisories in each SPA — was
`SECURITY-AUDIT-LOG.md` step 14 §9 and §10. That ledger has since been moved out of the
repository for going stale; **[`docs/SECURITY.md`](docs/SECURITY.md) §8 is now the only current
statement of what is open and why.**

---

## [0.1.0]

The pre-hardening baseline. There is no changelog entry for it: the repository's only tag before
this release is `v0.0.1` (April 2026), the chart said `0.1.0`, the image tag said `0.0.1` and both
SPAs said `0.0.0`. 0.2.0 is the first release where all of those agree, and the first with a
changelog.

Treat "0.1.0" as "anything deployed from this repository before 0.2.0". Every break above applies.
