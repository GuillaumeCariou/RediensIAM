# Step 26 — Documentation

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30`
**Scope:** `docs/` and the root `README.md` only. `src/`, `tests/`, `sdk/`, `frontend/` and
`deploy/` were **read** extensively and **not written** — two other agents owned them concurrently.
**Not committed.**

Every claim in the documents was checked against the code. Where an audit report and the code
disagreed, the code won and the disagreement is recorded in §3 below — there are **eight** of them,
plus one previously unreported fail-open path found while writing.

---

## 1. What was written

| File | Action | Size |
|---|---|---|
| `docs/ARCHITECTURE.md` | **rewritten** | 250 → 605 lines |
| `docs/API.md` | **new** — complete route reference, 184 routes | 524 lines |
| `docs/SECURITY.md` | **new** — security posture | 460 lines |
| `docs/TESTING.md` | **new** | 372 lines |
| `README.md` | **rewritten** | 301 → 227 lines |
| `docs/INTEGRATION.md` | targeted corrections (10 edits) | — |
| `docs/DEPLOYMENT.md` | targeted corrections (3 edits) | — |

`docs/2026-07-28-audit-complet.md` and `docs/2026-07-28-findings-securite-deploiement.md` were left
alone: they are dated records of a point in time, still referenced by `INTEGRATION.md` and the
ledger, and rewriting them would destroy the audit trail.

### `docs/ARCHITECTURE.md`

Now describes: the claim/grant split and the `GrantedLevel` type (S-1); the three gates in
`GatewayAuthMiddleware` — token resolution, audience gate, default deny; Keto as the single
authority (S-8) with the dual-write caveat; audit on `SaveChanges` with the per-org hash chain
(S-3); the tenant-scope interceptor and where RLS actually stands (S-5); key rotation and the
`k<id>:` envelope (S-10); the four Postgres roles; cache TLS pinning; the encrypted DataProtection
key ring; and a new §"Reachability" explaining the `/admin` ↔ `/api/manage` pairing once instead of
duplicating it.

### `docs/API.md`

184 controller actions across 11 controllers, extracted mechanically from the attribute blocks in
`src/Controllers/*.cs` and re-verified after the concurrent controller refactor landed (count and
per-controller distribution unchanged). Grouped by prefix, with required authority and public-host
reachability per group. Cites method and type names, never `file:line`, for controller code.

The load-bearing distinction the file makes: **reachability is an ingress property, not an
application one.** The port split is not a trust boundary — `MapControllers()` maps everything on
both listeners and only Swagger and `/metrics` are port-gated. `adminOnlyPaths` is
`[/admin, /org, /project, /service-accounts]`; `/api` is deliberately absent, which is why
`/api/manage/*` is public and `/admin/*` is not, on the same controller and the same filter.

### `docs/SECURITY.md`

Written to be read by someone deciding whether to deploy this. Nine sections; the ones that carry
the weight are §2 (tenant isolation and its honest limit), §3 (the token contract and the four wire
breaks), §5 (the audit trail and its caveats) and §8 (what is deliberately open).

Three claims it deliberately refuses to make:

- **RLS would not make the login path tenant-safe.** The interceptor's fallback is the string
  `'system'`, not deny, and `LegitimatelyUnscopedPaths` lists nine paths that run unscoped by
  necessity — the whole of `AuthController` among them, because login resolves a user before a
  tenant is known. RLS is real defence in depth on the authenticated tenant paths and nothing at all
  on the highest-traffic unauthenticated surface.
- **The audit hash chain is unkeyed SHA-256, not an HMAC**, and `VerifyChainAsync` has no production
  caller. Tamper-evidence against a careless adversary, not a capable one, and nothing runs the
  verifier.
- **"Keto is the only store consulted" is an overstatement.** The dual write to `org_roles` has no
  reconciler, and `AuthController`'s consent path still reads `org_roles` to resolve scopes.

---

## 2. What was stale, and corrected

### `docs/ARCHITECTURE.md`

| Claim | Reality |
|---|---|
| SAML: "`idp_id` is NOT checked against the challenge's project — SEC-02" | **Wrong for months.** `SamlController.Start` binds the IdP to the challenge's project and 404s otherwise. R-11, closed |
| "Backend → Postgres: shared user `iam` with full DB" | Four least-privilege roles (`iam_app`, `iam_hydra`, `iam_keto`, `iam_backup`), `scram-sha-256`, `iam` in no DSN |
| "Operator → admin: SA active + org active checked on **cache miss only** (5 min TTL)" | Contradicted the same document's own PAT-cache paragraph. Re-checked on **every** hit |
| Reconfigure recipe using `yq -i '.rediensiam.env.RECONFIGURE_FROM_ENV = "true"'` | **That key does not exist.** See §4 |
| `Security__TotpSecretEncryptionKey` in the secrets list | The key ring is `Security:EncryptionKeys`; the chart value is `secrets.encryptionKey` |
| "1198 tests" | 1345 |
| No mention of GrantedLevel, default-deny, the audit chain, the tenant interceptor, key rotation, the four PG roles, cache TLS, or DataProtection encryption | All added |

### `README.md`

The install section (rewritten in step 22) still matches `deploy/setup.sh` — `--dev`, `--prod`,
`--plan`, `--upgrade`, `NAMESPACE=`, the generated-credentials rule and the stage scripts all check
out. It was kept and trimmed.

Everything below it was wrong:

- **The entire `values.yaml` reference table was keyed on a `rediensiam.env.*` map that the chart
  does not have.** `grep` for `env:` under `rediensiam:` in `values.yaml` returns nothing, and
  `templates/deployment.yaml` has no passthrough range — it sets a fixed list of 26 variables.
  Roughly 30 documented keys were unsettable.
- `secrets.totpEncryptionKey` → `secrets.encryptionKey`. `postgres.password` →
  `postgres.local.password` (and it is now the break-glass superuser, not a runtime credential).
  `secrets.argon2Pepper` → `security.argon2Pepper`.
- Missing entirely: the four `postgres.local.roles.*Password` keys, both TLS flag pairs,
  `postgres.rls.enabled`, `image.digest`, `ingress.public.adminOnlyPaths`,
  `networkPolicy.defaultDenyScope`, `secrets.encryptionKeys`, `security.argon2Peppers`,
  `postgres.external.podSelector`, `hydra.hydra.config.ttl.*`, `hydra.maester.enabled`.
- Tests: "1093 tests" → 1345; "`Tests/Regression/` … All 34 are green" → 16 files, 241 executed
  tests; the whole directory was attributed to the 2026-07-28 audit, but 11 of the 16 files come
  from later steps. None of the four `dotnet test` invocations carried
  `-p:SonarQubeTargetsImported=true`.
- E2E: "50 tests across 13 files" → 158 across 15.

The replacement table no longer claims to be exhaustive, and says so. `values.yaml` is heavily
commented and is the source of truth; a second copy in the README drifts by construction, which is
exactly what happened.

### `docs/INTEGRATION.md`

Substantively current — it was updated during steps 15a, 19 and 20 and its `aud` migration section
is good. Two classes of rot:

1. **All 20 `Controller.cs:NNN` citations were already wrong.** Spot-checked:
   `CreateProjectRequest` `:934` → `:982`; `AdminCreateProjectRequest` `:1124` → `:1258`;
   `CreateHydraClientRequest` `:1130` → `:1266`; the `SystemAdminController` class attribute `:16` →
   `:29`; `OrgController` `:19` → `:20`. All replaced with type and method names. Two record names
   in the endpoint table were also invented rather than checked — `CreateServiceAccountRequest` and
   `GeneratePatRequest` do not exist; the real ones are `CreateSaRequest` and `GenerateSaPatRequest`.
2. **Two of the three "deployment notes that bite integrators" were fixed long ago** — the
   `App__TrustedProxies` crash-loop (R-25, `values.yaml` now ships the k3s CIDRs) and the admin-console
   CSP (R-26, `Program.cs:466-470` names the issuer origin). The table now records status rather than
   implying all three are live, and gained a row for the `/admin` 403 an integrator will actually
   hit on the public host.

### `docs/DEPLOYMENT.md`

Written this session (step 22) and accurate except for two known-gaps rows that steps 21 and 23
overtook:

- "Dragonfly TLS off — one chart change remains (mount the CA into the app pod)". The mount exists
  (`templates/deployment.yaml:180-195`), the application side is done and pinned, and dev runs with
  it on. The gap is **prod only**, because `values.prod.yaml` sets no `dragonfly` block.
- "RLS off — the application side (A-1) is done and undeployed". A-1 is in the build.

---

## 3. Where the code contradicted an audit report

Eight, plus one new finding. Listed because the brief asked for them and because a report that
overclaims once will be quoted out of context later.

| # | Report | Claim | Code |
|---|---|---|---|
| 1 | `17-structural-debt.md` §1 | S-1 **Partial** — "the compile break is one line away and needs three controller edits this pass may not make" | **Landed.** All three controllers converted; `GetManagementLevel` was made private and has since been **deleted outright**. The compile break is done |
| 2 | `23-cache-hardening.md:15` | Dragonfly TLS "**Done and live**" | **Dev only.** `values.prod.yaml` sets no `dragonfly` block and inherits `enabled: false`. The report's own residuals table (`:483`) says so; the summary row does not carry the qualifier |
| 3 | `21-rls-app-support.md:16,257` | A-3 blocked on "one chart change (a volume mount) outside this scope" | The mount is at `templates/deployment.yaml:180-181, 190-195`, projecting `ca.crt` only. Step 23 landed it |
| 4 | `19-api-surface.md` §7.1 | `ver: 1` on "**every** answer" | The 200s and the `audience_required` 400 only. Not on `403 service_account_required`, not on ASP.NET Core's `ValidationProblemDetails` for a missing required field (no `InvalidModelStateResponseFactory` override exists in `src/`), not on the middleware 401. Harmless for an SDK enforcing `ver >= 1` — all are non-200s — but not what "every" means |
| 5 | `19-api-surface.md` §7.3 | "Unknown Keto namespaces are refused" | **Conditional.** `IsObjectInScopeAsync` computes `scope = CallerOrgScope ?? subject.OrgId` and `return true` when both are null. A deployment-level service account asking about a token with no `org_id` gets no object check and can reach the `System` namespace. **Not named by any report** — see below |
| 6 | `20-sdk-audience.md` §1 | audience binds when `aud` equals `project_id`, equals `org_id`, **or appears in the token's OAuth2 `aud`** | The third path is unreachable for tokens this deployment mints. `AcceptConsentAsync` sends `grant_scope`, `session`, `remember` — no `grant_access_token_audience` anywhere in `src/`. Binding works via `project_id`/`org_id` only unless the RP requested an audience at `/oauth2/auth` |
| 7 | `15a-backend-residuals.md` §8 | "Three contracts have already broken in this release" | …above a seven-row table. The sentence means the three from report 19; as written it reads as a count of the rows below it |
| 8 | `19` §7 and `15a` §8 together | the release's wire-contract breaks | Neither lists the **`ext.roles` project-qualification** break, which is the loudest one and the fourth. It landed in step 4 (`04-critical-fixes.md:307`) and fell between the two later summaries. `SECURITY.md` §3 lists all four together |

Smaller factual drift, corrected silently in the docs:

- Ledger and report 17 say "~98 hand-written `RecordAsync` call sites". The current count is **99**.
- `src/Data/TenantScopeInterceptor.cs:54` names `RlsScopeAlignmentTests` as pinning the empty
  query-filter set. **No such class exists**; the tests are `TenantScopeInterceptorTests` in
  `tests/…/Tests/Security/`.
- `src/Middleware/GatewayAuthMiddleware.cs:58-62` still refers the reader to
  `ClaimsExtensions.GetManagementLevel`, which no longer exists.
- The task brief states a stale `.sonarqube/` "breaks dotnet from the repo root". In the current
  tree it produces a **warning, not an error**: `.sonarqube/bin/targets/` is empty, the
  `Exists(...)` guard fails and the import is skipped. `dotnet build` succeeds with two `Sonar:`
  diagnostics. The flag is still correct to pass — a partially-completed `sonar-scan.sh` repopulates
  that directory — and `TESTING.md` documents the real mechanism (a user-global MSBuild
  `ImportBefore` hook armed by `$(MSBuildStartupDirectory)/.sonarqube/conf/`), which is written down
  nowhere else in the repo.
- Ledger §9 item 4 records 9 npm advisories per SPA (1 low, 8 high). Current: **7 high per SPA** in
  both. Improved by step 15b, not closed; the remainder need `npm audit fix --force`.
- T-07c (`Project.RequireMfa` defaults false) is recorded in the ledger as an open oversight. It was
  **resolved as a deliberate product decision**, documented in the entity itself
  (`src/Data/Entities/Project.cs:17-20`). `SECURITY.md` lists it under "deliberate product
  decisions, not defects" rather than as either open or fixed.

### One new finding

**`/api/authorize` object scoping fails open when both scopes are absent.**
`IntrospectionController.IsObjectInScopeAsync` returns `true` unconditionally when
`CallerOrgScope` is null *and* the subject token has no parseable `org_id`. The method's own doc
comment describes the deployment-level fallback but not the both-null case. The `System`-namespace
block is gated on the same condition, so a deployment-scoped caller presenting a token with no
`org_id` can ask `System:rediensiam#super_admin`.

Reachability is narrow — it needs a deployment-scoped service-account credential *and* a token with
no organisation — and the subject of an authorisation check is always the presented token's user, so
this is a read primitive, not a forgery. It is recorded in `SECURITY.md` §8 and belongs in the
ledger. **No code was changed; `src/` is another agent's scope.**

---

## 4. Could not verify

| Thing | Why |
|---|---|
| Anything about production | Never deployed from this branch. Every prod path is template- and preflight-verified only. Documented as such in three places |
| Live cluster state | I ran no `kubectl`. Every infrastructure claim is read off the chart, the values files and the scripts. `verify-deployment.sh` is what closes that gap and the docs say so |
| Backup restore | Proven byte-identical once by hand (`15c §T-03`). Not automated, not repeatable by me |
| Whether detection D-01 was ever run against live data | Ledger §11 item 2 says there is no evidence. I found none either. P-01's write path is closed; rows written before the fix are not cleaned |
| The Playwright E2E suite | `tests/e2e/node_modules` does not exist; it needs `npm install`, a browser download, a live dev stack and matching credentials. `test-results/` is four months old. Documented as unverified rather than as working |
| T-26 beyond `15a §7` | That assessment found one real defect and cleared the two things T-26 was feared for. It is not an exhaustive XXE and signature-wrapping review and `SECURITY.md` says so |
| `RECONFIGURE_FROM_ENV` end to end | The app reads it (`InstanceConfiguration.cs:186-187`) but the chart cannot set it — see below |

### A gap found while documenting

**The chart has no generic environment passthrough.** `templates/deployment.yaml` sets a fixed list
of 26 variables and `values.yaml` has no `rediensiam.env` map. `INSTANCE_ID` and
`RECONFIGURE_FROM_ENV` are read by the application but **cannot be set through the chart as it
ships**, so the documented reconfigure procedure is not executable — it requires editing
`templates/deployment.yaml` or writing the `instances` row directly.

This is not a security defect, but it means the Zitadel-style configuration model has no supported
operator interface for its own second half. Flagged in `ARCHITECTURE.md` and `README.md`; not fixed,
because `deploy/` is another agent's scope. Cost to close: one `range` block in `deployment.yaml`
and one map in `values.yaml`, well under an hour.

---

## 5. Root scratch files — recommendation: **remove all three**

`findings.md` (129 lines), `progress.md` (59), `task_plan.md` (133).

They are not audit artefacts. They are `planning-with-files` scratch from an **April 2026 UI
overhaul**:

- `task_plan.md:1` — "Task Plan: RediensIAM Full UI Overhaul", `## Current Phase — Phase 10 —
  COMPLETE`, ten phases all `[x]`.
- `progress.md:3` — "Session: 2026-04-25", logging a fetch of a Claude Design bundle and files read
  from `/tmp/design_extracted/`.
- `findings.md:1` — "Findings & Decisions — RediensIAM UI Overhaul", describing "17 theme presets"
  and a density/radius tweaks panel.

`git log` puts their last touch at `8f7e6cd` (April), and their content describes work that has
since been superseded — step 6 rewrote both SPAs for the CSP and font findings, and step 15b bumped
their dependencies. They reference `/tmp` paths that no longer exist and a "Phase 10 complete" state
with no relationship to the current branch.

They are actively harmful in one specific way: three files named `findings.md`, `progress.md` and
`task_plan.md` sitting at the root of a repository whose `.security-hardening/` directory contains a
finding ledger read as the security audit's own tracking files. They are not.

**Recommendation:** `git rm findings.md progress.md task_plan.md`, and add the three names to
`.gitignore` so the next `planning-with-files` run does not re-commit them. I did not delete them —
the brief said to propose, not to act.

---

## 6. Rules observed

- **Nothing was committed.**
- **No scripted or regex bulk edits to documentation.** Every documentation change is an individual
  `Write` or `Edit`. Two `sed` invocations were used, both on link anchors only
  (`SECURITY.md#the-audit-trail` → `#5-the-audit-trail`), after a link check; no prose was
  machine-edited.
- A Python script was used to **extract** the 184 routes from `src/Controllers/` for `API.md`. It
  reads; it writes nothing into the repository. It lives in the session scratchpad.
- **Nothing outside `docs/` and `README.md` was modified.** `git status` shows the two other agents'
  changes in `src/`, `sdk/` and `frontend/` untouched by this pass.
- No finding is described as closed where the ledger says otherwise **except** where I verified the
  closure in code myself and said which report closed it. §3 lists every place I went against a
  report.
