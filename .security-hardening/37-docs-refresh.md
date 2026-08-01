# 37 — Bringing the documentation back to the code

**Date:** 2026-08-01 · **Branch:** `security/hardening-2026-07-30` · **Base:** `cce1ee5`
**Scope:** `docs/`, `CHANGELOG.md`, `README.md`. No file under `src/`, `tests/` or `deploy/` was
edited — three other agents own those.
**Not committed.**

Steps 32, 36 and 33 each ended with a section headed "what `docs/SECURITY.md` should now claim", and
none of their authors was allowed to make the edit. This is that edit, plus the drift that
accumulated around it.

---

## 1. Method, and why it is stated first

`.security-hardening/README.md` says a claim in here is worth exactly as much as the command output
quoted beside it, and that five reports were moved out for asserting things the source contradicts.
So every claim below was re-derived from the source at `cce1ee5` before it was written into a
document, and where a claim could **not** be re-derived it is marked as quoted rather than verified.
The distinction is kept explicitly in `SECURITY.md` itself, not only here.

The working tree was clean for `src/`, `tests/` and `deploy/` when this started, and `cce1ee5`
already carries all three passes — so unlike step 32's situation, everything below was verifiable
against committed code rather than against another agent's in-flight tree.

---

## 2. Verified against the code

Read in the source and confirmed before being written down.

| Claim now in the docs | What was read |
|---|---|
| The login path pins its tenant before any user lookup | `TenantScopeInterceptor.PinToOrganisationAsync` (`:120-145`); `CurrentScope` reads the pin ahead of the claims (`:153-165`) |
| The org comes from client metadata, not from caller input | `LoginChallengeProject.ResolveOrgOrNull` (`:35`); `AuthController.PinScopeToChallengeAsync` (`:86-87`) |
| 20 pin/verify sites across the login flow | `grep -n "PinScope\|EnsureScopedToProject" src/Controllers/AuthController.cs` — `:183, 227, 256, 303, 313, 522, 557, 584, 617, 657, 694, 795, 871, 878, 1009, 1104, 1384, 1388, 1454, 1693` |
| The pin refuses to re-scope a request its token already scoped elsewhere | `:128-133` |
| `LegitimatelyUnscopedPaths` has twelve entries and no longer names the whole of `AuthController` | `:71-92`, including the comment at `:73-75` |
| `SamlController` is still unscoped | `:80` — the array entry says so in as many words; no `PinToOrganisationAsync` call exists in `SamlController.cs` |
| `AdminLogin` is structurally unscopable | `files/rls.sql:132-134` — the `users` policy is `EXISTS (… ul."OrgId" = rls_org())`; the `__system__` list has `OrgId IS NULL` |
| An `OrgId IS NULL` audit insert is refused under a pinned scope | `files/rls.sql:127` (`audit_log` → `"OrgId" = rls_org()`) with `:188` applying it as `FOR ALL … WITH CHECK` |
| MFA failure rows now carry a tenant | `AuthController.cs:592-593`, `:633-634`, via `MfaSessionTenant()` at `:117-121` |
| The cross-tenant social match is closed | `AuthController.cs:1540-1542` — `s.User.UserListId == project.AssignedUserListId` |
| The scope counter exists | `IamMetrics.cs:53-56` |
| SAML `Destination` is validated, and where | `SamlService.DestinationMatches` (`:146-150`), called from `SamlController.cs:178` |
| The check runs *before* the pending record is consumed | `SamlController.cs:155` read → `:178` destination → `:185` `GetAndDeletePendingAsync` → `:208` `Unbind` |
| Absent `Destination` is accepted and logged at Warning | `SamlController.cs:176-177` |
| `MigrateOnStartup` is honoured | `AppConfig.cs:28`; `Program.cs:223-243` |
| The four dead config keys are gone | `src/appsettings.json` in full — none of `ProjectTtl`, `JwksTtl`, `FrontendUrl`, `LoginPath` appears anywhere outside the removal record and its regression test |
| `BYPASSRLS` is now unconditional | `templates/postgres.yaml:97-106` |
| V-04 gained a positive control | `verify-deployment.sh:127-138`; the override file is layered at `:47-51` |
| `helm --wait` is gone | `deploy.sh:94-102`, `wait_workloads` |
| The smoke-probe abort is fixed | `deploy.sh:537-542` — the `\|\| true` and the comment naming the failure |
| The self-signed issuer is namespaced and release-named | `cert-manager-issuer.yaml:9-11`; `_helpers.tpl:52-66` |
| PGDATA moved, with a guard | `postgres.yaml:184-201`, `:235-242` |
| `values.prod.yaml` admin issuer is `""`, not `selfsigned` | `values.prod.yaml:30` |
| The `/api/authorize` ownership gap is still open | `IntrospectionController.cs:218-230` — `if (scope is null) return IsKnownNamespace(ns) \|\| await RefuseAsync();`. A **known** namespace still passes with no ownership check. The `System` half is separately closed at `:159` |
| 99 hand-written `RecordAsync` calls | `grep -rn "\.RecordAsync(" src/` excluding `bin`/`obj` — 99 |

## 3. Quoted from a report, not verified here

Marked as such **in `SECURITY.md` itself**, not only in this file.

1. **The 80-connection before/after measurement** (`32 §4`). It needs a running dev cluster and a
   build of each image; the cluster belongs to other agents this round and `deploy.sh --dev` was
   reported broken during step 32. `SECURITY.md` §2 now prints the table with the sentence *"Those
   numbers are quoted from that report and were not re-measured for this document"*, and names what
   *was* verified — the counter, the pin sites, the two defect fixes.
2. **What ITfoxtec 4.17.0 does and does not validate** (`36 §1`). Established there by decompiling
   the referenced assembly. Not re-derived. `SECURITY.md` §7 says so explicitly, because the whole
   "honest limit" of the `Destination` control rests on the claim that this SP accepts an
   assertion-only-signed response — which is a property of the library, not of our code.
3. **The prod-profile install itself** (`33`). The namespace was created and destroyed before this
   step ran; the evidence is the report's transcript. What *is* verified here is that all six fixes
   are present in `deploy/` at `cce1ee5`, which is the part a reader can act on.

## 4. Where a report and the code still disagree

**One, and it is in `deploy/`, so it is not mine to fix.**

`deploy/rediensiam/values.dev.yaml:38-41` still says the `BYPASSRLS` grant happens *"at initdb when
this flag is set"*. `values.yaml:302-305` and `templates/postgres.yaml:97-106` both say — correctly —
that it is unconditional and that it was conditional until 2026-08-01. The dev values file was not
updated with the others. It is a comment, and it is the *old* claim that step 33 proved false.
**For the `deploy/` owner:** three lines.

Nothing else found. In particular, `SECURITY.md` §8's `/api/authorize` row survived checking: I
expected the fix in `75e9576` to have closed it and it did not — it closed only the `System`
namespace half, and the ownership half at `IntrospectionController.cs:228` is exactly what the row
describes. It stays listed as open.

---

## 5. What was corrected

### `docs/SECURITY.md`

- **§2's central assertion, replaced.** The paragraph beginning *"That is not an oversight; it is
  unavoidable. **The login path resolves a user before a tenant is known.**"* was true when written
  and is now false. Two new sections replace it — "What is scoped, and what still is not" and "What
  still runs unscoped, and why" — with the pin mechanism, the three properties that make it safe
  (server-authored scope, anti-escalation refusal, unchanged failure branch), the two defects it
  surfaced, and a table of what genuinely cannot be scoped.
- The `5 / 15` measurement block was **dropped rather than updated**, with the reason stated: it was
  a mixed minute including SuperAdmin listings and PAT introspection, so it never shared a
  denominator with the login figure people were reading it as.
- **`SamlController` is now a named open item**, in §2's table and in §8. Step 32 left it unpinned
  because the file belonged to another work stream; that is a schedule reason, not a technical one,
  and the document should not let it disappear into a list.
- **§8 gained three rows and lost none:** the SAML ACS not being tenant-scoped, `Destination` being
  bypassable against assertion-only signers, and ACME never having been executed. Two rows were
  rewritten rather than removed — Dragonfly TLS ("the *cutover* is untested" rather than "the
  control is untested", because the control has now been observed) and RLS in prod (which now
  carries the fact that it could never have been enabled on any prod database before this release).
- **§6's "Production has never been deployed from this branch"** became an accurate account of what
  the scratch-namespace install did and did not establish, including the V-04-passing-on-404s defect
  by name, because "an assertion that read green while measuring nothing" is the single most
  reader-relevant thing in step 33.
- **§7 gained the `Destination` control and its two-paragraph qualifier**, including the operator
  check to run before upgrading.
- **Dangling references to `14-finding-ledger.md` removed** (§ header and §9). That file was moved
  out of the repository in `568d0c2` and three documents plus the CHANGELOG were still pointing at
  it. §9 now points at `.security-hardening/README.md` and `11-pentest-results.md`.
- **Drifted line references fixed** where they had come to point at unrelated code:
  `AppConfig.cs:25,47,48,57,64,118-119` → `:73,95,96,103-105,122,281` (the clamps had moved out from
  under those numbers entirely); the retention floor `:113-119` → `:275-281`; HKDF `:255-260` →
  `:263-266`; `Program.cs:448-476` → `:492-502`; `:85,109,111` → `:92,117,119`; `:28-42` →
  `:33-34,437-446`; `SamlService.cs:29` → `:39`; `InstanceConfiguration.cs:84,114-125` → `:92,122`;
  `values.prod.yaml:29` → `:30`; `values.yaml:333` → `:339-340`; `values.yaml:308` → `:314-315`.
  Where the list was long and likely to drift again, a symbol name replaced the numbers.

### `README.md`

*"Production has never been deployed from this branch"* was **nearly** false and the distinction
matters, so it is now stated as what it is: the prod *profile* installed once into a scratch
namespace and destroyed, six defects found and fixed, and an explicit list of what that does not
establish — ACME never executed, no publicly trusted certificate ever issued, no restore, no upgrade
across a migration, nothing up longer than an hour. It closes on "a scratch namespace is not
production."

### `docs/DEPLOYMENT.md`

- The residuals table: the Dragonfly row narrowed to the cutover, a new ACME row, and the RLS row
  gained the `BYPASSRLS` history.
- The RLS runbook gained a block explaining that step 2 is **not optional** on a database created
  before this release, and why no `setup.sh --prod` install ever had the grant.
- The closing "never deployed" paragraph gained the three things that now block an upgrade of an
  existing installation, including dev: the PGDATA migration and its guard, the issuer rename and
  the session invalidation it costs, and the removal of `helm --wait`.

### `docs/ARCHITECTURE.md`, `docs/DIAGRAMS.md`, `docs/API.md`, `docs/TESTING.md`

The same corrections where they were duplicated: the unscoped-paths list (nine → twelve, and no
longer "the whole of `AuthController`"), the `⚠ RLS on does not make the login path tenant-safe`
callout in DIAGRAMS §6, the encryption tables' "dev only" column, the RLS flowchart's decision node
(it now reads the pin ahead of the claims, which is what the code does), the ACS route in `API.md`,
and the last `14-finding-ledger.md` pointer. `TESTING.md` gained the V-04 story in full, because a
document about verification should carry the case where the verifier lied.

### `CHANGELOG.md`

New **`[0.2.2] — unreleased`**; `[0.2.1]` untouched except for one annotation. Its "Known limits"
paragraph asserts that RLS does not make the login path tenant-safe, which was true of 0.2.1 — so it
is left as the record of that release with a one-line *"Superseded in 0.2.2"* note rather than
rewritten. The dangling ledger link in `[0.2.0]`'s "Not fixed" section was repointed at
`docs/SECURITY.md` §8, which is now the only current statement of what is open.

The two cross-tenant defects lead the section, above the feature work, because a reader scanning for
"does this affect me" needs the social-login one first.

---

## 6. Reports that should be retired

Four. Each was checked against the source before being named; each quote is at the line given.

| File | The claim, quoted | What contradicts it |
|---|---|---|
| **`34-dead-code.md`** §5.2 | `:303` lists `Database:MigrateOnStartup` in a "read by **nothing**" table, and `:312-314` says *"Setting `Database__MigrateOnStartup=false` … **does nothing**. Migrations always run."* | `AppConfig.cs:28` and `Program.cs:223-243`. The other four rows of that table name keys that are no longer in `appsettings.json` at all. The section ends by assigning an owner for work that is done — it hands over an open item that closed |
| **`26-documentation.md`** | `:61-63` *"`LegitimatelyUnscopedPaths` lists nine paths … the whole of `AuthController` among them, because login resolves a user before a tenant is known"* — and separately `:196` *"**No code was changed; `src/` is another agent's scope.**"* on the `/api/authorize` finding | `TenantScopeInterceptor.cs:71-92` (twelve entries, login excluded by name). And `IntrospectionController.cs:159` closed the `System` half in `75e9576`, fifteen minutes after that report was written. Note the ownership half at `:228` is still open, so `:186` is half right — it is `:196` that is now false |
| **`21-rls-app-support.md`** | `:385` *"not fixable at this layer. Would need a tenant-bearing pre-auth route (host- or path-derived org) — a product change, days not hours, and out of proportion to the finding"* | It was fixed at exactly that layer, in one pass, with no product change and no new route: `TenantScopeInterceptor.cs:120`. This is the dangerous shape — a forward-looking cost judgment that would stop someone doing work already done |
| **`29-rls-prod-tls.md`** | `:654`, the same row in the same words | Same |

**Recommendation: move all four to `~/Desktop/rediensiam-audit-perime/`** and add them to the table
in `.security-hardening/README.md`. Not moved here — that directory is the audit trail's own
housekeeping and the decision should be explicit, not a side effect of a documentation pass.

A qualification worth making, because retiring a report is not free. `21` and `29` are *descriptions
of what was true when they were written* everywhere except that one row, and the README's whole
premise is that such descriptions are legitimate. What makes those two rows different is that they
are not descriptions: they are **estimates of what a future fix would cost**, and both are wrong by
an order of magnitude in the direction that discourages the work. `34` and `26` are worse — they
each assert a *current* status and hand an owner an open item that is closed.

Two more that were checked and are **not** retirable, recorded so nobody re-litigates them:

- **`22-setup-scripts.md`** clears itself at `:307` — *"`setup.sh --prod` has never been run against
  a real production cluster … none of it has issued a certificate or served a request."* It claims
  no verified prod install, so step 33 finding four broken things does not contradict it.
- **`09-infra-security.md`** does not bless the `selfsigned` issuer; it calls it a known defect and
  gives two exits, both still valid. It says "ClusterIssuer" where the chart now renders a namespaced
  `Issuer` — naming drift, not a false claim.

One loose end, not a retirement: **`15c-infra-residuals.md:214`** recommends
`helm upgrade … --wait --timeout 8m`. That is precisely the deadlock step 33 §2.3 removed from
`deploy.sh` — the `<release>-backup` PVC does not bind until the nightly CronJob fires on a
`WaitForFirstConsumer` StorageClass. Anyone following that T-04 sequence by hand will hit it. Worth
a footnote in that file rather than a move.

---

## 7. What this pass could not settle

**The audit-chain and dual-write rows in §8 are about to change, and they were left alone.**

While this ran, another agent's uncommitted work appeared in the tree converting `AuditChain` to a
keyed HMAC (`AuditChain.Compute(KeyRing, …)`, `k{keyId}:` envelope, `AppConfig.AuditChainKey`),
adding an `IntegrityMonitorService` and `VerifyAllChainsAsync`, and adding an
`IamMetrics.GrantDivergence` gauge. Those touch three §8 rows directly:

- *"The audit chain hash is unkeyed; no scheduled verifier; no DB-level append-only"*
- *"No reconciler for the Keto/`org_roles` dual write"*
- §5's three caveats

**None of it is at `cce1ee5`, and none of it is finished.** Documenting it would be exactly the
failure this directory's README names — writing down what an author intended and believed. So §5 and
those §8 rows still describe HEAD, unchanged, and remain accurate as of `cce1ee5`. **They are the
first thing the next documentation pass should re-read**, and they will need it.

Also outstanding, and outside this scope:

- `values.dev.yaml:38-41`, §4 above — a stale comment in `deploy/`.
- The version bump. `Chart.yaml`, both SPAs' `package.json` and the three SDKs are all still
  `0.2.1`; the CHANGELOG header says they share one number. `[0.2.2]` is marked unreleased, so this
  is consistent today, but the bump has to happen across `deploy/`, `frontend/` and `sdk/` at
  release time and none of those is this agent's.
- `CHANGELOG.md` labels `[0.2.0]` *"— unreleased"* while `[0.2.1]` above it carries no marker. Given
  `f42e7ac` is `release: 0.2.1`, that label looks wrong — but the brief named only `[0.2.1]` as
  released, so this was left rather than guessed at. One word, once somebody confirms it.
