# Testing RediensIAM

What exists, how to run it, and — just as usefully — what has no tests at all.

---

## At a glance

| Suite | Where | Count | Runs today? |
|---|---|---:|---|
| Backend integration tests | `tests/RediensIAM.IntegrationTests/` | **1460** | yes |
| Admin SPA unit tests | `frontend/admin/src/**` | 88 across 6 files | yes, ~3s |
| Login SPA unit tests | `frontend/login/src/**` | 80 across 3 files | yes, ~3s |
| .NET SDK tests | `sdk/dotnet/RediensIAM.Client.Tests/` | 14 | yes |
| Rust SDK tests | `sdk/rust/rediensiam-client/src/lib.rs` (inline) | 15 (13 unit + 2 doctests) | yes |
| TypeScript SDK tests | `sdk/typescript/rediensiam-web/src/index.test.ts` | 14 | yes |
| Deploy-layer static tests | `deploy/tests.sh` | 36 checks | yes, no cluster needed |
| Deployment verification | `deploy/verify-deployment.sh` | 26 checks | yes, against a live cluster |
| Detection-rule self-test | `deploy/monitoring/selftest.sh` | 6 assertions | yes, against a live database |
| Playwright E2E | `tests/e2e/` | 158 across 15 specs | **not since April** — needs setup and a live stack |

---

## Backend integration tests

```bash
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true
```

**1460 tests**, and the number is exact rather than approximate: 1313 `[Fact]` + 37 `[Theory]`
expanding to 147 `[InlineData]` rows, with no `[MemberData]` or `[ClassData]` anywhere.

Roughly three minutes. They are "integration" tests in the real sense — the fixture starts three
containers per run via Testcontainers:

| Container | Purpose |
|---|---|
| `postgres:17` | the real database, real migrations |
| `docker.dragonflydb.io/dragonflydb/dragonfly:latest` | the real cache, not an in-memory fake |
| `mailhog/mailhog:v1.0.1` | outbound mail |

Ory Hydra and Ory Keto are stubbed with **WireMock.Net** (`Infrastructure/HydraStub.cs`,
`Infrastructure/KetoStub.cs` — the latter runs two servers, read API and write API). The app itself
is hosted in-process by `WebApplicationFactory`.

Everything shares one xUnit collection (`[Collection("RediensIAM")]`), so the containers start once
per run rather than once per class.

### The `-p:SonarQubeTargetsImported=true` flag, and why

**Short version: pass it whenever you run `dotnet` from the repository root.**

Long version, because the mechanism is not obvious and there is nothing in the repo that explains
it. There is no `Directory.Build.props` or `Directory.Build.targets` here. The import comes from a
**user-global MSBuild hook** that `dotnet sonarscanner` installs:

```
~/.local/share/Microsoft/MSBuild/Current/Microsoft.Common.targets/ImportBefore/SonarQube.Integration.ImportBefore.targets
```

MSBuild auto-imports everything in an `ImportBefore` directory into every project it evaluates. That
file checks for `$(MSBuildStartupDirectory)/.sonarqube/conf/SonarQubeAnalysisConfig.xml` and, if it
finds one, imports the scanner's analysis targets. A `.sonarqube/` directory left behind by an
interrupted `sonar-scan.sh` (the script clears it at the *start* of a scan; `dotnet sonarscanner end`
is what normally cleans up) is enough to arm it.

Passing `-p:SonarQubeTargetsImported=true` short-circuits the import condition.

Two honest qualifications:

- **It only bites from the repository root.** `MSBuildStartupDirectory` is the cwd, so running from
  inside `tests/` or `sdk/dotnet/…` never arms the hook.
- **In the tree's current state it is a noisy warning, not a failure.** `.sonarqube/bin/targets/` is
  empty, so the `Exists(...)` guard fails and the import is skipped; you get
  `Sonar: The analysis targets file not found: …` twice and a successful build. Pass the flag
  anyway: if that directory is ever repopulated by a partially-completed scan, an ordinary
  `dotnet test` starts writing `ProjectInfo.xml` into `.sonarqube/out/` against stale configuration.
  It is cheap insurance and it keeps the output readable.

The permanent fix is `rm -rf .sonarqube` at the repository root.

### Useful invocations

```bash
# One class
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true \
  --filter "FullyQualifiedName~LoginTests"

# The regression suite only
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true \
  --filter "FullyQualifiedName~Tests.Regression"

# Verbose
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true \
  --logger "console;verbosity=detailed"
```

### Layout

| Directory | Tests | Covers |
|---|---:|---|
| `Tests/Auth/` | 248 | login, MFA, registration, password reset, social, consent |
| `Tests/System/` | 182 | the super-admin surface |
| `Tests/Regression/` | 174 | one suite per audit finding — see below |
| `Tests/Org/` | 135 | the org-admin surface |
| `Tests/Project/` | 95 | the project-admin surface |
| `Tests/Services/` | 91 | service-layer units |
| `Tests/Security/` | 78 | headers, tenant scope, gateway middleware |
| `Tests/ServiceAccounts/` | 72 | service accounts, PATs, API keys |
| `Tests/Account/` | 67 | end-user self-service |
| `Tests/Unit/` | 58 | pure units, no fixture |
| `Tests/Webhooks/` | 36 | delivery, signing, SSRF |
| `Tests/ManagedApi/` | 20 | `/api/manage` |
| `Tests/Api/` | 8 | introspection, authorize |
| `Tests/Middleware/` | 4 | |

(Attribute counts, so a `[Theory]` counts once here and several times at run time.)

---

## The regression-test convention

`tests/RediensIAM.IntegrationTests/Tests/Regression/` holds **16 files, 241 executed tests**. The
rule is one suite per audit finding, and **each test must fail against the pre-fix build**. They are
guards, not a to-do list — a regression test that passes on the vulnerable code proves nothing.

| File | Finding(s) |
|---|---|
| `ApiSurfaceTests.cs` | P-06 audience binding, P-05 object scoping, MFA-downgrade guard, `/admin` ↔ `/api/manage` parity |
| `AuthEnhancementRegressionTests.cs` | MFA factor *addition* unguarded, org-suspension session revocation |
| `AuthHardeningRegressionTests.cs` | MFA credential binding, credential stuffing, account-enumeration oracles |
| `BackendHardeningRegressionTests.cs` | R-10, R-17, R-18, R-20, T-N6 |
| `ClaimForgeryRegressionTests.cs` | R-23 + T-N3 — the `ext.roles` claim-forgery chain |
| `CrossTenantRegressionTests.cs` | tenant isolation via attacker-controlled `project_id` |
| `FlowStateRegressionTests.cs` | `SameSite=Strict` session-cookie flow breakage |
| `KeyRotationRegressionTests.cs` | S-10 — HKDF root and Argon2 pepper rotation, chain C-3 recovery |
| `MfaTakeoverRegressionTests.cs` | R-24 + T-N2 — silent TOTP takeover, missing audit |
| `OrphanedGrantRegressionTests.cs` | R-01 — an `org_admin` grant outliving its organisation |
| `PentestFindingsTests.cs` | P-01, P-02, plus held controls |
| `PlatformRegressionTests.cs` | PAT revocation latency, webhook test dispatch, CSV export escaping |
| `RedirectAndSsrfRegressionTests.cs` | redirect allowlist and SSRF denylist bypasses (**pure unit — runs with no containers**) |
| `ResidualFindingsTests.cs` | P-03, P-08, the T-07a–d authentication defaults |
| `StructuralDebtTests.cs` | S-1, S-3, S-8 |
| `TrustAnchorRegressionTests.cs` | R-09, R-14/T-N5, R-22, T-N4 |

### The `PENTEST_` prefixes

One file uses a naming scheme, `PentestFindingsTests.cs`, and its class `<summary>` (`:5-14`) is the
only place in the repository where the convention is written down:

- **`PENTEST_FAILING_*`** — an attack that **succeeded when the test was written**. The assertion
  states the behaviour the system *should* have, so the test fails until the hole is closed. Do not
  "fix" one by relaxing its assertion; it is the target for the fix.
- **`PENTEST_HELD_*`** — an attack that was tried and refused. Kept so the control cannot silently
  regress.

There are 4 `PENTEST_FAILING_` and 9 `PENTEST_HELD_`, all in that one file. **All four
`PENTEST_FAILING_` tests now pass unmodified** — the prefix records what *was* failing when the test
was written and nothing renames them afterwards, which is a readability trap worth knowing about.

The other 15 files use ordinary descriptive xUnit names and carry their finding IDs in a class-level
`<summary>`. That comment block is the only in-repo mapping from test to finding; there is no README
under `tests/`.

---

## SDK tests

```bash
# .NET — registered in RediensIAM.slnx under /sdk/
cd sdk/dotnet/RediensIAM.Client.Tests && dotnet test

# Rust — inline #[cfg(test)] mod tests, 8 #[test] + 4 #[tokio::test]
cd sdk/rust/rediensiam-client && cargo test

# TypeScript — Node's built-in test runner, zero test dependencies
cd sdk/typescript/rediensiam-web && npm test
cd sdk/typescript/rediensiam-web && npm run typecheck
```

The .NET SDK suite is a single file, `AudienceBindingTests.cs`: 6 facts plus a 5-case theory, all
covering the `aud`/`ver` contract added in this release. The Rust tests include wire-level
assertions — a one-shot loopback listener reads the bytes the client actually writes, which is why
`tokio` is a dev-dependency. The TypeScript suite runs under `node --test` with type stripping,
preserving the SDK's zero-dependency claim.

---

## Deployment verification

```bash
./deploy/verify-deployment.sh --dev
./deploy/verify-deployment.sh --prod
./deploy/verify-deployment.sh --help
```

Environment: `NS` (default `default`), `RELEASE` (default `rediensiam`).

This asks the **cluster**, not the repository. It exists because step 12 of the audit found nine
controls that steps 6, 9, 10 and 11b had claimed and that were true of files on disk and false of
the running system — a gap neither `helm template` nor a test suite can catch.

**26 checks, V-01…V-26**, covering: registry loopback binding and digest pinning (V-01, V-07, V-08,
V-19), no Secrets-granting ClusterRole (V-02, V-03), the public-host management deny (V-04), ingress
TLS (V-05), OIDC discovery (V-06), pod hardening (V-09…V-14), NetworkPolicies (V-15), admin service
type (V-16), the live CSP header (V-17, V-18), Postgres privilege separation (V-20, V-21), the
backup (V-22), Postgres TLS (V-23), the cache password and TLS (V-24, V-26), and the RLS job (V-25).

**V-04 carries a positive control, and the reason is worth reading.** The script did not layer
`values.<env>.override.yaml`, the file `setup.sh --prod` writes with the operator's answers, so on
`--prod` it probed the committed default hostname instead of the real one. Traefik answers 404 to
every path on a Host it has no router for, and V-04 counted 404 as a refusal — so the P-04
management-API assertion **passed while measuring nothing at all**, on a host that did not exist.
That is the same class of defect this script exists to catch, inside the script. It now reads the
override file, and V-04 first requires `/login` on the public host to answer 2xx/3xx; if it does
not, the four deny probes are reported as inconclusive rather than as passes. On a correctly
measured run the refusals are 403 from the `ipAllowList` middleware, not 404 from Traefik shrugging.
Found in `SECURITY-AUDIT-LOG.md` step 33 §2.5.

### Pass, fail, skip

| Result | Meaning |
|---|---|
| `pass` | the assertion was evaluated and held |
| `fail` | it was evaluated and did not hold — **or it could not be evaluated at all** |
| `--` (skip) | the precondition genuinely does not exist here, so there is nothing to assert |

Exit `0` all passed · `1` at least one failure · `2` could not run. Skips do not affect the exit
code.

The "could not evaluate is a failure" rule is deliberate and is the discipline that makes the script
worth running. A check that silently reports all-clear because it never ran is worse than no check.
Failures are re-printed at the end under *"Controls claimed by the repository that are NOT live"*.

### Known behaviour under CloudNativePG

V-20, V-21 and V-23 all key off `kubectl get pod rediensiam-postgres-0` and **skip** when the
built-in StatefulSet is not deployed.

**V-22 does not skip — it fails.** The backup CronJob template is gated on
`postgres.local.enabled`, and `setup.sh` explicitly makes the operator acknowledge that the backup
is theirs to provide under CNPG. A correctly configured CNPG deployment with WAL archiving still
trips `fail V-22 "no rediensiam-backup CronJob"` and exits 1. That is a false positive; making these
checks CNPG-aware is about an hour and has not been done.

---

## Detection rules

`deploy/monitoring/` holds two scripts and no framework, by design: single-node k3s, one operator,
no SIEM.

```bash
./deploy/monitoring/audit-detections.sh                      # last 24h, print only
./deploy/monitoring/audit-detections.sh --window 7days        # weekly review
ALERT_URL=https://ntfy.sh/<topic> ./deploy/monitoring/audit-detections.sh
```

**13 rules, D-01…D-13** — nine `page`-severity, four `review`. They query the `audit_log` table
through `kubectl exec` as the **read-only `iam_backup` role**, because detection must never be able
to write. The password is pulled from the release Secret at run time and never held in a file.

| ID | Sev | Detects |
|---|---|---|
| D-01 | page | service-account role rows scoped to a foreign org (P-01's residual data) |
| D-02 | page | tenant role names that read as platform authority |
| D-03 | page | cross-tenant introspection / authorize refusals |
| D-04 | page | MFA factor-mutation burst, or mutation from an unseen address |
| D-05 | page | authentication-failure burst, per actor and per IP |
| D-06 | page | activity inside a suspended org after the suspension |
| D-07 | page | per-org audit retention set below the global floor |
| D-08 | review | trust-anchor writes |
| D-09 | page | service-account action from a never-before-seen source IP |
| D-10 | review | password change by a management-role holder |
| D-11 | page | audit log empty, or silent for more than 48 h |
| D-12 | review | every MFA factor mutation in the window, for the weekly eyeball |
| D-13 | review | nested `providers[].logo_url` pointing off-instance |

Exit `0` no page-severity hit · `1` a page-severity hit · `2` could not run **or alert delivery
failed** — a silent alerting channel is worse than none. The summary distinguishes "no hits" from
"rules failed to run", after an incident where removing `trust` from `pg_hba.conf` broke every query
and the script still printed an all-clear.

### The self-test

```bash
./deploy/monitoring/selftest.sh
```

The live `audit_log` is usually empty, so "0 rows from every rule" proves only that the SQL parses.
This proves the predicates actually **fire**. It does not inject rows: each predicate is re-run
against synthetic rows supplied as CTEs, every statement is a `SELECT` inside
`BEGIN READ ONLY … ROLLBACK` with `ON_ERROR_STOP=1`, so it is safe to run against production.

**6 assertions**, each with an expected hit count so a predicate matching *everything* fails as
loudly as one matching nothing: D-01, D-02, D-04, D-06, D-09, D-13. The threshold and trivial rules
(D-03, D-05, D-07, D-08, D-10, D-11, D-12) are not self-tested.

The script carries an honest note about its own shortcut: the predicates are restated rather than
shared with the rule script, so a rule edited without editing its test passes a stale check. The
upgrade path — one `.sql` file per rule, read by both — is written down next to the note.

---

## End-to-end tests

`tests/e2e/` — Playwright, **158 tests across 15 spec files** covering both SPAs plus the account
and org surfaces.

```bash
cd tests/e2e
npm install && npx playwright install chromium     # first time
cp .env.example .env                                # then edit
npm test
```

`.env` must match the running deployment:

```env
TEST_BASE_URL=http://localhost          # public ingress
TEST_ADMIN_URL=http://localhost:30501   # NodePort, smoke tests only
TEST_SUPER_ADMIN_EMAIL=…                # must match the generated bootstrap credentials
TEST_SUPER_ADMIN_PASSWORD=…
```

`App__AdminSpaOrigin` and the Hydra `redirect_uri` registered at deploy time must both equal
`TEST_BASE_URL`, or the OIDC callback yields an empty session.

**These have not been run since April.** `tests/e2e/node_modules` does not exist, and
`test-results/` holds four-month-old artefacts. Both SPAs have changed since (the step-6 CSP work,
self-hosted fonts, the login preview repair). Treat the suite as unverified until someone runs it.

The admin project cannot use `storageState`: `frontend/admin/src/auth.ts` keeps the access token in
an `InMemoryWebStorage`, so it lives only in the JS heap and a reload always re-runs
`signinRedirect`. Every admin context therefore completes a real OIDC round-trip and mocks its API
calls with `page.route()`. A stale `.auth/admin-session.json` from an old run is inert — delete it.

---

## What has no tests

### What the SPA suites still do not reach

Both SPAs now have suites (88 and 80 tests, `npm test` in either directory, vitest + jsdom +
Testing Library, config in the `test` block of `vite.config.ts`). What they do not cover:

- **anything needing layout or a real top layer** — jsdom has neither, so native `<dialog>` Tab
  containment and everything visual is asserted only structurally;
- **the router and the page shells** — the suites cover the pieces with a contract worth pinning
  (re-auth, the command palette, the SMTP error codes, the auth 401 split) plus two static
  passes over the source (`contracts.test.ts`, `theme.test.ts`). Ordinary CRUD pages are not
  rendered;
- **the real backend** — every call is mocked. Only the Playwright suite crosses that line, and
  it has not run since April.

### Other gaps carried from the audit

| What | Why it matters |
|---|---|
| **P-04** — the public-host management deny | a chart-level control; its only proof is a live `curl` and `verify-deployment.sh` V-04. A `helm template` assertion would cost about ten lines |
| **`deploy/*.sh`** | no `bats`, no `shellcheck` gate. `verify-deployment.sh` covers the cluster, not the scripts that build it |
| **Backup restore** | proven byte-identical once, by hand (`SECURITY-AUDIT-LOG.md` step 15c §T-03). Not automated, not scheduled |
| **`AuditLogService.VerifyChainAsync`** | tested in `StructuralDebtTests`; **no production caller**. The chain is verified in CI and by nothing in the running system |

---

## Static analysis

The backend references `SecurityCodeScan.VS2019` and `SonarAnalyzer.CSharp` directly
(`src/RediensIAM.csproj:45,49`), so most C# issues surface at build time.

```bash
bash sonar-scan.sh
```

publishes a **single** SonarQube project, `RediensIAM`, covering the C# backend and both SPAs in one
analysis — the MSBuild scanner indexes non-MSBuild files under `sonar.projectBaseDir`. The former
`Admin-SPA` / `Login-SPA` projects and their `sonar-project.properties` files are gone; delete those
projects server-side if your instance still lists them.

The script prompts for a token on first run and writes `.sonar.env` under `umask 077`; it does not
embed one.

**Neither analyser models cross-tenant authorisation.** A clean quality gate is not evidence of
tenant isolation: every cross-tenant finding in the July 2026 audit was invisible to both.
