# Step 27 — Documentation completion and release 0.2.0

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Base commit:** `75e9576`
**Scope:** `sdk/`, `frontend/`, `docs/`, root files, `deploy/rediensiam/{Chart,values}.yaml`, and
one version property in `src/RediensIAM.csproj`.
**Not committed.**

Every claim written this pass was checked against the code. Where a document and the code
disagreed, the code won; the disagreements are in §4.

---

## 1. What was written

| File | Action | Notes |
|---|---|---|
| `CHANGELOG.md` | **new** | The release deliverable. Grouped by what the reader must *do*, not by commit |
| `sdk/dotnet/RediensIAM.Client/README.md` | **new** | Full option reference |
| `sdk/rust/rediensiam-client/README.md` | **new** | Full option reference |
| `sdk/typescript/rediensiam-web/README.md` | **new** | Full option reference |
| `frontend/admin/README.md` | **rewritten** | was 100% Vite template boilerplate |
| `frontend/login/README.md` | **rewritten** | was the same boilerplate, byte-identical |
| `sdk/README.md` | 3 targeted edits | links to the new per-SDK pages; one stale claim corrected; version bump |
| `README.md` | 1 targeted edit | CHANGELOG added to the documentation table, first row |
| `docs/SECURITY.md` | 2 targeted edits | one claim overtaken by `75e9576` — see §4 |

### `CHANGELOG.md`

Written for the one person who matters here: an integrator deciding whether it is safe to deploy
this on Monday. Structure:

1. **Read this first — the upgrade in order.** Four numbered steps. Step 3 (server) before step 4
   (SDKs), with the reason stated in the same breath: the `ver` check means an upgraded SDK refuses
   *every* answer from an un-upgraded server. This is the single fact most likely to cause an
   outage during the upgrade, so it is above the fold and repeated in break 4.
2. **Breaking — the wire contract.** The four breaks, each with a before/after table, *what fails*,
   and *what to do*, plus a symptom→cause table at the end covering all four at once.
3. **Breaking — behaviour you will meet as an error.** The `503` on
   `POST /account/mfa/phone/setup`, and the MFA-downgrade `409`.
4. **Breaking — storage and deployment.** The `k<id>:` ciphertext envelope (with the rollback
   warning) and the Postgres four-role split (with the "new installs only" warning).
5. **Added / Changed / Security.** `/api/manage`, key rotation, RLS-shipped-off, the audit floor,
   cache-key digests, `apiOrigins`, discovery validation, CSP, trusted proxies.
6. **Not fixed.** Points at the ledger §9/§10 rather than restating it, and at `SECURITY.md` §8.

Two things it deliberately does **not** do:

- It does not claim the four breaks are the whole of the release. It says the ranked list of what
  is still open is the ledger, and names the top items.
- It does not present the Postgres role split as closing T-04. It says the split happens on a
  first-ever start only, that an existing installation keeps running on `iam`, and that T-04
  remains the ledger's highest-ranked open finding for those clusters. See §4 for the wrinkle.

### The three SDK READMEs

Each covers, per the brief: install, the required options and that they fail **at construction**,
HTTPS with loopback as the only exemption, the `ver` contract check and the deploy-order
consequence, the role helpers with a `HasRole` vs `HasProjectRole` table, caching, and — for the
browser SDK — the deliberate absence of introspection.

Facts each one states that were verified in code rather than copied from `sdk/README.md`:

- **.NET.** `Uri.IsLoopback` covers `localhost`, **all of `127.0.0.0/8`** and `::1` — broader than
  the Rust and browser SDKs, which match three literal host strings. `AuthorizeAsync` is **not**
  cached (only `IntrospectAsync` is). `RediensIamDefaults.OrgIdClaim` / `ProjectIdClaim` are the
  literal strings `"org_id"` / `"project_id"`. `AddRediensIam` runs `Validated()` at registration
  *and* the constructor runs it again.
- **Rust.** `RediensIamClient` deliberately has no `Debug` (it would print the service-account
  token). The crate is built with `rustls-tls` and webpki roots only, so **the OS trust store is
  not consulted** and a private-CA deployment will fail to validate — called out in a blockquote at
  the top of the install section, because someone will hit it. `authorize` is not cached. The
  cache is one `RwLock<HashMap>` with an opportunistic sweep.
- **Browser.** Argument order is `hasProjectRole(role, projectId?)` — **role first**, the opposite
  of the two backend SDKs, because the project is optional. This is the kind of thing that costs an
  hour if it is not written down; each of the three READMEs says it. The only thing written to
  browser storage is the PKCE verifier + `state` under `sessionStorage['rediensiam:pkce']`;
  everything else is in-memory fields. `hasProjectRole` with no project available at all returns
  `false` rather than matching loosely.

### The two SPA READMEs

Both replaced entirely. Cover: what the SPA is and where it is served from, the routes, the CSP
arrangement, the MFA flow, how to run and build it, the directory layout, testing, and the npm
advisories.

The CSP section is the one that needed the most care, because the two SPAs differ:

| | Admin | Login |
|---|---|---|
| Meta `connect-src` | `'self' https: http:` — **cannot** pin the issuer | `'self'` — mirrors the header exactly |
| Header `connect-src` | `'self' <issuerOrigin>`, computed at runtime | `'self'` |
| Header `img-src` | `'self' data:` | `'self' data: https:` (tenant logo, social icons) |

Both READMEs state the mechanism plainly: browsers enforce the **intersection**, a request must
satisfy both, the header is the enforcing copy, and the meta tag cannot carry `frame-ancestors`
because browsers ignore that directive in a meta tag.

**"Neither SPA has any test" is stated plainly, twice each** — once in a blockquote under the
title, once in a `## Testing` section that names the unused packages (`vitest`, `jsdom`,
`@testing-library/react`, `@testing-library/jest-dom`, `@testing-library/user-event`) and says
they were added in anticipation and nothing was ever written. The login SPA's section goes further
and names the two files that are pure security logic with no React at all — `isSafeRedirect` and
`sanitizeCss` — because they are the cheapest possible first test and the sanitiser's own header
comment says regex CSS sanitisation is fragile.

---

## 2. What was found stale

### `frontend/admin/README.md` and `frontend/login/README.md`

**Both were the unmodified `npm create vite` React+TS template README, byte-for-byte identical to
each other.** Neither mentioned RediensIAM, the backend, the CSP, MFA, or how to build. They
documented how to enable `tseslint.configs.recommendedTypeChecked` and `eslint-plugin-react-x`,
neither of which is in either `eslint.config.js`.

### `sdk/README.md`

Substantively accurate — it was edited repeatedly during the audit and the `aud` migration section
is good. Three things were wrong or stale:

1. **`token_type_hint` is no longer a declared field.** The README said the server "does not read
   it". Stronger than that: `IntrospectionRequest` is `record IntrospectionRequest(string Token,
   string? Aud = null)` — the field was **removed**, with a docstring explaining that declaring a
   field nothing reads is worse than not declaring it. This resolves **M7**, which ledger §10 lists
   as a finding "no step ever owned". Sending it is still harmless (model binding discards it), so
   the SDKs are unaffected. Corrected in place.
2. `rediensiam-client = "0.1"` in the Rust install snippet → `"0.2"`.
3. The backend-SDK table linked to directories; it now links to the new per-SDK READMEs, with a
   sentence saying what each page is for so the two layers do not read as duplicates.

### `docs/SECURITY.md`

See §4 — one claim was overtaken by the tip commit.

---

## 3. Version bump

Everything is on **0.2.0**. Previous state was four different numbers.

| File | Was | Now |
|---|---|---|
| `deploy/rediensiam/Chart.yaml` `version` | `0.1.0` | `0.2.0` |
| `deploy/rediensiam/Chart.yaml` `appVersion` | `"0.1.0"` | `"0.2.0"` |
| `deploy/rediensiam/values.yaml` `rediensiam.image.tag` | `"0.0.1"` | `"0.2.0"` |
| `sdk/dotnet/RediensIAM.Client/RediensIAM.Client.csproj` `<Version>` | `0.1.0` | `0.2.0` |
| `sdk/rust/rediensiam-client/Cargo.toml` `version` | `0.1.0` | `0.2.0` |
| `sdk/rust/rediensiam-client/Cargo.lock` (own entry) | `0.1.0` | `0.2.0` |
| `sdk/typescript/rediensiam-web/package.json` `version` | `0.1.0` | `0.2.0` |
| `frontend/admin/package.json` `version` | `0.0.0` | `0.2.0` |
| `frontend/login/package.json` `version` | `0.0.0` | `0.2.0` |
| `sdk/README.md` Rust install snippet | `"0.1"` | `"0.2"` |
| `src/RediensIAM.csproj` | *no version at all* | `<Version>0.2.0</Version>` |

`grep` for `0.1.0`, `0.0.1` and `0.0.0` across the tree afterwards returns only IP addresses
(`127.0.0.1`, `0.0.0.0`, `10.0.0.1`), dependency versions belonging to other packages, and dated
`.security-hardening/` reports. `deploy.sh` contains no version literal.

### The backend version, and why `AssemblyVersion` is pinned

`src/RediensIAM.csproj` carried no version property. Adding `<Version>0.2.0</Version>` alone would
have moved `AssemblyVersion` from the implicit `1.0.0.0` to `0.2.0.0`, and that is not free:

`KeyRingProtection.cs` returns `new EncryptedXmlInfo(…, typeof(RootKeyXmlDecryptor))`, so ASP.NET
Core DataProtection **persists the assembly-qualified type name into the key ring**:

```
decryptorType="RediensIAM.Config.RootKeyXmlDecryptor, RediensIAM, Version=1.0.0.0, …"
```

Every already-persisted key entry names `Version=1.0.0.0`. Rather than rely on the default
`AssemblyLoadContext` ignoring the version for a non-strong-named assembly, `AssemblyVersion` and
`FileVersion` are pinned to `1.0.0.0` with a comment saying why and saying to bump `<Version>`
instead. The product version moves; the identity the key ring stored does not. Two lines, zero
risk on the key ring.

### Two things about the image tag worth recording

- `values.dev.yaml` and `values.prod.yaml` override nothing here, but **`deploy.sh` does**:
  `--set rediensiam.image.tag=dev` / `=prod`, and then replaces the tag entirely with the digest
  resolved from `docker push`. So `values.yaml`'s tag is a default that a scripted deploy never
  uses. It is still correct to have it agree with everything else, and a manual
  `helm install` does use it.
- `helm template` confirms both environments render `image: rediensiam:0.2.0` when the digest is
  unset.

### The `v0.0.1` tag

The repository's only git tag is `v0.0.1`, pointing at `6a76d4e` (2026-04-25). Nothing was ever
tagged `0.1.0` — that number lived only in `Chart.yaml` and the three SDK manifests. The
CHANGELOG's `[0.1.0]` section says this outright and tells the reader to treat "0.1.0" as "anything
deployed from this repository before 0.2.0", so nobody goes looking for a tag that does not exist.

---

## 4. Where the code contradicted a document

### One claim in `docs/SECURITY.md` was overtaken by the tip commit

`docs/SECURITY.md` was written at `c616928`. The tip, `75e9576` — *"sec: refuse the System
namespace to every /api/authorize caller"* — landed after it and closed half of the finding the
document reports as fully open.

| `SECURITY.md` said (§3, and the §8 table) | Code at `75e9576` |
|---|---|
| "`IsObjectInScopeAsync` … returns `true` when both are absent. A deployment-level service account asking about a token whose `org_id` is also empty gets no object check, and **can reach the `System` namespace**" | `Authorize` refuses `System` to **every** caller before object scoping runs, with an audit row. The both-null path now returns `IsKnownNamespace(ns)`, not `true` — so unknown namespaces are refused there too |

What remains is genuinely narrower: a deployment-level caller presenting a token with no `org_id`
still gets no *ownership* check against `Organisations`, `Projects` or `UserLists`. That is a
disclosure, not a forgery — the subject of an authorisation check is always the presented token's
user.

Both places were corrected: the §3 bullet now describes what the code does and says which commit
closed the `System` half; the §8 table row was re-graded from **Medium** to **Low–Medium** with the
residue described. **No code was changed.**

This is the second time in two passes that a *narrowing* of a finding arrived after the document
that reported it. Worth a habit: the last commit before a documentation pass ships is the one most
likely to have invalidated a line in it.

### `verify-deployment.sh --dev` passes V-20 and V-21, which the ledger's §9 item 1 implies it cannot

Ledger §9 ranks **T-04** — "one Postgres `iam` SUPERUSER shared by app+Hydra+Keto, `local all all
trust`" — as the single **Critical** open finding, marked "Verified live". On the current dev
cluster:

```
PASS  V-20  pg_hba.conf grants no 'trust' (all methods are scram-sha-256)
PASS  V-21  no component connects as superuser 'iam' (users:iam_app iam_hydra iam_keto)
```

Both halves of T-04 are closed **on this cluster**, because it was rebuilt from scratch after the
role split landed and the roles are created by initdb. The ledger entry is not wrong as a general
statement — an *existing* installation gets none of this from a chart upgrade, and `iam` still
exists as the break-glass superuser — but "Verified live" now describes a cluster that no longer
exists.

The CHANGELOG is written to that distinction rather than to either extreme: it presents the split
as a **new-install** behaviour, points existing installations at the manual migration in
`15c-infra-residuals.md`, and says T-04 remains the ledger's top open item for those clusters.
**The ledger was not edited** — it is another step's artefact and the correction belongs in its own
pass.

### Smaller drift confirmed rather than assumed

- **7 high npm advisories per SPA**, re-measured this pass with `npm audit --package-lock-only` in
  both. Matches step 26's correction of the ledger's "9 per SPA (1 low, 8 high)". `react-router`
  and `react-router-dom` are among them in both. Written into both SPA READMEs with the caveat that
  the judgement not to force-upgrade has not been re-tested since the SPAs were rewritten.
- **`deploy-dev.sh` does not exist.** Two of my own drafts referenced it before the file listing
  was checked; corrected to `./deploy/setup.sh --dev` (first install) and `./deploy/deploy.sh
  --dev` (rebuild), which is what `README.md` and `deploy/` actually ship. Recording it because a
  plausible-sounding script name is exactly the kind of thing that survives into a README.
- **`bare helm lint .` fails at HEAD, before any change of mine** — `_helpers.tpl:109` dereferences
  `.Values.rediensiam.publicUrl`, which the base `values.yaml` leaves unset. Confirmed by stashing
  my two chart edits and re-running: identical error. The chart is never deployed without an
  environment file; both environment files lint clean. Not introduced here, not fixed here
  (`deploy/` templates were out of scope), recorded so the next reader does not chase it.

---

## 5. Could not verify

| Thing | Why |
|---|---|
| Anything about production | Never deployed from this branch. Every prod claim in the CHANGELOG is template- and preflight-verified only, and the Dragonfly-TLS-off-in-prod line says so |
| The `k<id>:` envelope end to end through an actual rotation | The format, the sweep and the `CryptographicException` on a dropped key were read in code and are covered by the suite; **no key was rotated on a live cluster this pass**, and the rollback warning in the CHANGELOG is reasoned from `ParseEnvelope` + `Convert.FromBase64String`, not observed |
| The Postgres role migration for an *existing* cluster | The dev cluster is a fresh install. The migration in `15c-infra-residuals.md` was not executed against a pre-split cluster by me |
| The Playwright E2E suite | `tests/e2e/node_modules` still absent. Both SPA READMEs describe it as not run in CI and unverified in the current tree, per `docs/TESTING.md`, rather than as working |
| `npm run dev` against a live backend, for either SPA | The admin SPA has no `server.proxy` and no `VITE_API_BASE_URL`, so it cannot work standalone — stated as a fact about the config, which was read, not as a failed attempt. The login SPA's `VITE_API_BASE_URL` path was read in `src/api.ts` and not exercised |
| Whether an upgraded SDK against a real un-upgraded server produces the `ServerTooOld` message | Proven by the Rust wire tests (`an_answer_without_ver_is_refused`, which serves a real `ver`-less response over a loopback socket) and the .NET tests. Not proven against an actual 0.1.0 deployment, which no longer exists |

---

## 6. Verification output

All of the following was run after every edit in this pass.

### Full .NET suite — `dotnet test RediensIAM.slnx -p:SonarQubeTargetsImported=true`

Run twice: once at `75e9576` before any edit, to establish the baseline, and again after the
version bump touched `src/RediensIAM.csproj`. Identical results both times.

```
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 156 ms
         - RediensIAM.Client.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:  1346, Skipped:     0, Total:  1346, Duration: 3 m 32 s
         - RediensIAM.IntegrationTests.dll (net10.0)
```

**1346 integration tests + 11 SDK tests, 0 failed.** Baseline held.

The `AssemblyVersion` pin was confirmed in the built output rather than assumed:
`strings -e l src/bin/Debug/net10.0/RediensIAM.dll` still contains `1.0.0.0`, and the
informational version reads `0.2.0+75e95763403ad1ede41bf6b356f783cfd89d9c78`. Product version
moved, assembly identity did not — which is what keeps the persisted DataProtection key ring
loadable.

Two pre-existing warnings, unchanged by this pass: `SCS0016` (CSRF) on
`IntrospectionController.cs:72` and `S2139` on `Program.cs:231`. Three `Sonar: … analysis targets
file not found` notices, which is the empty `.sonarqube/bin/targets/` behaving as step 26 described
— a warning, not an error.

### Rust SDK — `cargo test`

```
running 12 tests
test tests::cache_key_is_not_the_token ... ok
test tests::cache_key_is_a_sha256_digest ... ok
test tests::an_answer_without_ver_is_refused ... ok
test tests::base_url_must_be_https_except_on_loopback ... ok
test tests::role_and_tenant_helpers ... ok
test tests::an_authorize_answer_without_ver_is_refused ... ok
test tests::tenant_roles_do_not_match_across_projects ... ok
test tests::authorize_sends_the_audience ... ok
test tests::requires_base_url_and_token ... ok
test tests::inactive_has_no_roles ... ok
test tests::introspect_sends_the_audience ... ok
test tests::audience_is_required_at_construction ... ok

test result: ok. 12 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s

   Doc-tests rediensiam_client
running 1 test
test src/lib.rs - (line 7) - compile ... ok
test result: ok. 1 passed; 0 failed
```

The doc-test compiling matters here: the crate-level example in `lib.rs` is the same shape as the
one in the new README, so a required option added later cannot silently rot the documented snippet.

### Browser SDK — `npm test` (`node --test src/*.test.ts`)

```
✔ config is validated up front
✔ issuer must be https, except on loopback
✔ apiOrigins are held to the same scheme rule
✔ the bearer only goes to the app origin or a declared api origin
✔ discovery endpoints must sit on the issuer origin
✔ a fresh client is not authenticated
✔ base64url output has no padding or non-url-safe characters
✔ PKCE challenge matches the RFC 7636 test vector
✔ generated verifiers are unique and long enough
✔ claims are read from the ext object Hydra produces
✔ tenant roles only match when qualified by their own project
✔ malformed tokens decode to null rather than throwing
ℹ tests 12   ℹ pass 12   ℹ fail 0
```

### .NET SDK build

```
dotnet build sdk/dotnet/RediensIAM.Client/RediensIAM.Client.csproj -p:SonarQubeTargetsImported=true
  RediensIAM.Client -> .../sdk/dotnet/RediensIAM.Client/bin/Debug/net10.0/RediensIAM.Client.dll
Build succeeded.
    4 Warning(s)
    0 Error(s)
```

The four warnings are two `NU1510` "will not be pruned" notices reported twice. Pre-existing.

### SPA builds

```
frontend/login  $ npm run build
  dist/assets/index-CEXU3-Gb.css     13.90 kB │ gzip:   3.35 kB
  dist/assets/index-BMLEKWbZ.js     291.33 kB │ gzip:  87.82 kB
  ✓ built in 495ms

frontend/admin  $ npm run build
  dist/assets/index-BjANZH3p.css     60.36 kB │ gzip:  12.83 kB
  dist/assets/index-D3OU0zsq.js     749.36 kB │ gzip: 200.11 kB
  ✓ built in 896ms
  (!) Some chunks are larger than 500 kB after minification.
```

Both include `tsc -b`, so this is a typecheck as well as a bundle. The admin chunk-size warning is
pre-existing and informational.

### `helm lint` — both value combinations

```
$ helm lint . -f values.dev.yaml
==> Linting .
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed

$ helm lint . -f values.prod.yaml
==> Linting .
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed
```

`helm lint .` with **no** values file fails at `_helpers.tpl:109` on an unset
`rediensiam.publicUrl`. Verified pre-existing by stashing this pass's two chart edits and
re-running — byte-identical error. See §4.

### `helm template` — both value combinations

```
$ helm template rediensiam . -f values.dev.yaml    → 1884 lines, exit 0
$ helm template rediensiam . -f values.prod.yaml   → 1886 lines, exit 0

$ grep 'image: rediensiam' <dev render>   → image: rediensiam:0.2.0
$ grep 'image: rediensiam' <prod render>  → image: rediensiam:0.2.0
```

### `./deploy/verify-deployment.sh --dev`

```
PASS  V-01      registry bound to 127.0.0.1 (loopback only)
PASS  V-02      no rediensiam ClusterRole grants access to Secrets
PASS  V-03      hydra-maester is not deployed
PASS  V-04/admin/ public host refuses /admin/ (403)
PASS  V-04/org  public host refuses /org (403)
PASS  V-04/project public host refuses /project (403)
PASS  V-04/service-accounts public host refuses /service-accounts (403)
--    V-05      dev is deliberately cleartext (iam.localhost cannot be certified)
PASS  V-06      Hydra :4444 discovery answers 200
PASS  V-07      image pinned by digest (sha256:a84fd895c408413250bd098c276163963de3895d737783d3fdbf4bcbf2c10e5c)
PASS  V-08      imagePullPolicy=IfNotPresent
PASS  V-09      pod seccompProfile=RuntimeDefault
PASS  V-10      container runAsNonRoot=true
PASS  V-11      container allowPrivilegeEscalation=false
PASS  V-12      container readOnlyRootFilesystem=true
PASS  V-13      container drops ALL capabilities
PASS  V-14      automountServiceAccountToken=false
PASS  V-15      rediensiam-default-deny-ingress exists
PASS  V-15/hydra rediensiam-hydra-lockdown exists
PASS  V-15/keto rediensiam-keto-lockdown exists
PASS  V-15/postgres rediensiam-postgres-lockdown exists
PASS  V-15/dragonfly rediensiam-dragonfly-lockdown exists
PASS  V-16      admin service is NodePort (dev, expected)
PASS  V-17      CSP carries script-src, base-uri, form-action, frame-ancestors, object-src
PASS  V-18      CSP names no external font host
--    V-19      values.yaml pins no image digest — cannot check for drift
PASS  V-20      pg_hba.conf grants no 'trust' (all methods are scram-sha-256)
PASS  V-21      no component connects as superuser 'iam' (users:iam_app iam_hydra iam_keto)
PASS  V-22      backup CronJob last succeeded 2026-07-31T13:27:47Z
PASS  V-23/server Postgres runs with ssl=on
PASS  V-23/hba  pg_hba.conf admits TLS only (hostssl; local socket unaffected)
PASS  V-23/dsn  app, hydra and keto DSNs all request TLS
PASS  V-24      cache requires a password (48 chars)
PASS  V-26/server Dragonfly runs with --tls (cleartext is refused, not merely unused)
PASS  V-26/dsn  app cache DSN requests TLS (ssl=true)
PASS  V-26/pin  app pinned the cache certificate — server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
--    V-25      postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)
───────────────────────────────────────────────────────────────
 34 passed · 0 failed · 3 skipped
 All asserted controls are live.
```

**0 failures.** The three skips are the documented ones: dev is deliberately cleartext, no digest
is pinned in `values.yaml` (`deploy.sh` supplies it at deploy time), and RLS is off everywhere.

### Working tree after the pass

```
 M README.md
 M RediensIAM.slnx                                     (pre-existing, from a prior pass)
 M deploy/rediensiam/Chart.yaml
 M deploy/rediensiam/values.yaml
 M docs/SECURITY.md
 M frontend/admin/README.md
 M frontend/admin/package.json
 M frontend/login/README.md
 M frontend/login/package.json
 M sdk/README.md
 M sdk/dotnet/RediensIAM.Client/RediensIAM.Client.csproj
 M sdk/rust/rediensiam-client/Cargo.lock
 M sdk/rust/rediensiam-client/Cargo.toml
 M sdk/typescript/rediensiam-web/package.json
 M src/RediensIAM.csproj
?? CHANGELOG.md
?? sdk/dotnet/RediensIAM.Client/README.md
?? sdk/rust/rediensiam-client/README.md
?? sdk/typescript/rediensiam-web/README.md
```

No `dist/`, no `target/`, no `bin/`/`obj/` leaked into the tree despite four builds. One file in
`src/` touched, for the version property only.

---

## 7. Rules observed

- **Nothing was committed.**
- **No scripted or regex bulk edits.** Every change is an individual `Write` or `Edit`. Not one
  `sed`, `perl -i` or `find -exec` ran against a tracked file this pass — the only `sed` calls were
  read-only, printing line ranges to the terminal.
- **Every claim was checked against code**, including the ones inherited from `sdk/README.md`,
  which the brief warned had been edited repeatedly. Two of them were wrong and are corrected in
  §2. Where something could not be verified it is in §5 and is described as unverified in the
  document that mentions it, not omitted.
- **No open finding is described as closed.** The two places where I went against a written record
  — the `System` namespace and T-04 on the dev cluster — are in §4 with the commit or the check
  that justifies them, and in both cases the remaining half is stated.
- **`src/` was touched once**, for `<Version>` and the `AssemblyVersion` pin that protects the
  DataProtection key ring. No behaviour changed; the full suite is green.

---

## 8. Left for a later pass

Not done, deliberately, because each belongs to a scope this pass did not own:

1. **Ledger §9 item 1 and item 12 are stale.** T-04's "Verified live" describes a cluster that has
   been rebuilt, and P-06/S-2 is listed as "no audience binding, `aud` not mandatory, no `ver`",
   all three of which are now in the code and shipped in this release. §9 item 5 (R-30, "no SDK
   requires HTTPS") is in the same state. The ledger is another step's artefact; correcting it is a
   pass of its own, and doing it piecemeal from here is how a ledger stops being trustworthy.
2. **Tag `v0.2.0`.** The version numbers agree; nothing is tagged. That is a release action, not a
   documentation one, and it must follow the commit this pass did not make.
3. **The three root scratch files** — `findings.md`, `progress.md`, `task_plan.md` — are still
   there. Step 26 §5 recommended removing them and adding the names to `.gitignore`; that
   recommendation stands and was not acted on here either.
4. **A first test in either SPA.** Both READMEs now name the target and say the tooling is already
   installed, which is the most a documentation pass can do about it.
