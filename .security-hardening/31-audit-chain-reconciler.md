# 31 — Keying the audit chain, and reconciling the grant dual write

Two residuals `docs/SECURITY.md` names as open: the audit hash chain was **unkeyed**, and the
Keto/Postgres grant dual write had **no reconciler**. Both are closed here. Both are closed with a
named boundary rather than a claim of completeness, and the last section says exactly what the next
revision of `SECURITY.md` should say.

Branch `security/hardening-2026-07-30`. Suite at the end: **1413 passed, 0 failed, 0 skipped**
(baseline 1394, +19), **0 build warnings** across `RediensIAM.slnx`.

---

## 1. The audit chain is now an HMAC

### What was wrong

`AuditChain.Compute` was `SHA-256(prevHash ‖ every column of the row)`. Every input was a value the
attacker already had. So an attacker with write access to `audit_log` — the credential the
application itself holds, and the one a compromised Postgres hands over — could rewrite a row,
recompute its hash, re-chain every row after it, and hand back a table that verified clean. The
chain proved **ordering**, and was documented as proving tamper-evidence.

### The keying

The link is `HMAC-SHA256(K, prevHash ‖ row)` where `K` comes from the same HKDF root the rest of
the deployment already carries, under its own purpose string:

```
AppConfig.AuditChainKey => DeriveRing("rediensiam-audit-chain-v1")
```

Same shape as `KeyRingProtection`'s `rediensiam-dataprotection-v1` (`src/Config/AppConfig.cs:217`).
Purpose separation means a compromise of any other derived subkey does not yield the ability to
forge a chain, and the purpose is versioned because changing it would orphan every existing link.

**What it now resists that it did not:** an attacker who can write to `audit_log` can no longer
produce a table that verifies. The key is in the process environment, not in the database, so the
two capabilities have been separated — database write access alone is no longer sufficient to
rewrite history undetectably. What it still does not resist is unchanged and should stay named:
the application itself holds `K` (an RCE in the app can forge freely), and **deleting rows off the
end of a chain is still invisible** — truncation removes the evidence of itself. The chain detects
edits and mid-chain deletions, not tail truncation and not an emptied table.

### The storage format is the version marker

| Stored `Hash` | Means | Worth |
|---|---|---|
| `k{keyId}:{64 hex}` | HMAC under audit-chain key `keyId` | authentic, if `keyId` is configured |
| `{64 hex}`, no prefix | pre-keying SHA-256 | ordering only — anyone with DB write can reproduce it |
| `""` | pre-chain row | nothing |

Base-16 contains no `:`, so the three are distinguishable by shape and **no existing row had to be
touched**. Note the deliberate difference from `TotpEncryption`'s envelope, where a missing prefix
means key id 1: here a missing prefix means *unkeyed*, and treating it as key 1 would re-admit the
forgery. `src/Data/AuditChain.cs`.

Column widened 64 → 80 chars: `src/Data/Migrations/20260801091553_AuditChainKeyedHash.cs`. That
migration is the whole schema change; it carries no data migration and says why.

### The migration boundary, and why not a re-chain

Three options for the rows already live on the dev cluster:

1. **Re-chain them under the key.** Rejected. It means the application rewriting every row of a
   table whose entire value is that it is append-only, with hashes computed long after the fact.
   The result would *look* authentic while proving nothing about what those rows said when they
   were written — it would launder unverifiable rows into verified ones, which is the exact lie
   this control exists to avoid.
2. **Declare a break point and stop verifying before it.** Rejected as the sole answer: it discards
   the ordering evidence the old rows do carry.
3. **Chosen: keep them, walk them, and refuse to call them valid.** Old rows are verified with the
   old algorithm (so a careless edit to one is still caught) and counted as `Unverifiable`.

`VerifyChainAsync` now returns `AuditChainStatus(FirstBreak, Verified, Unverifiable)` instead of a
bare `long?`. `Intact` means no broken link. `FullyVerified` — no break **and** nothing
unverifiable — is the flag that means what "no break" used to pretend to mean. An operator reading
`intact: true, unverifiable: 4200` gets the truth: the chain walks, and 4200 rows are worth exactly
what they were worth before this change.

**The boundary is positional, and that is load-bearing.** Unkeyed hashes are accepted only in the
leading run, before the first keyed row. Without that rule the boundary *is* the attack: downgrade
the row you want to rewrite to the old format, recompute its SHA-256, and walk through. After the
first keyed row, an unkeyed hash is a break. Same rule for empty hashes — tolerated only at the
front, so a row inserted straight into the table with no hash is a break wherever else it lands.

### Rotation

Each row names the key id its MAC was written under, so rotation behaves the way ciphertext
rotation already does: **every configured root can verify, only the active one writes**. A rotated
root re-keys new rows and leaves every historic row verifiable — a chain spanning a rotation
verifies end to end.

Retiring a root is the one real cost, and it is asymmetric with ciphertexts. There is no
re-encryption sweep for the chain and there cannot be one (that is option 1 above, wearing a
different hat). So rows under a dropped root become **unverifiable, not broken** — the verifier
reports them rather than crying tampering, because "we threw away the key" and "someone attacked
the log" must not look the same. `KeyRotationStatus.TotalPending == 0` is still the signal that a
retired key can be dropped *for ciphertexts*; its doc comment now says explicitly that it says
nothing about the chain, and that dropping a root permanently blinds the audit rows written under
it (`src/Services/KeyRotationService.cs:14`).

### Who now runs the verifier

It had no production caller, which makes it a function that would have noticed, not a control.
Now:

- **`IntegrityMonitorService`** (`src/Services/IntegrityMonitorService.cs`), a hosted service, runs
  one pass at startup and every 24 h: verifies every organisation's chain plus the deployment-wide
  one, publishes `iam_audit_chain_broken_orgs` and `iam_audit_chain_unverifiable_rows`, and logs an
  error naming the organisation and the breaking row id.
- **`GET /admin/audit-chain`** (also `/api/manage/audit-chain`, SuperAdmin, live Keto re-check like
  every other route on that controller) for on demand.

Daily rather than hourly because a chain pass reads every audit row of every organisation; that is
a deliberate cost ceiling, noted in the class.

### Where the key had to reach

`RediensIamDbContext` writes the chain link, and a `DbContext` is built from options alone. It now
takes an optional `AppConfig` (`src/Data/RediensIamDbContext.cs:15`), resolved from DI for every
context the application builds. **A context without one throws rather than writing an unkeyed
row** — a silent fallback to a bare digest would produce rows that verify against nothing and look
exactly like the pre-migration ones. Two callers construct contexts by hand and both were fixed:
`InstanceConfigurationProvider` (which writes the `instances` row before DI exists, and therefore
now builds an `AppConfig` from the configuration snapshot it was handed) and the key-rotation sweep
tests. The design-time migration factory keeps the no-config constructor; it never saves.

---

## 2. The grant reconciler

`src/Services/GrantReconciler.cs`.

Every grant is a Keto tuple *then* a database row, with a compensating tuple delete in the `catch`.
Best effort, not a transaction. A killed process between the two writes, or a compensating delete
that itself fails, leaves the stores disagreeing — and nothing had ever looked.

It walks both stores, projects each database row into **the tuple the write path would have written
for it**, and takes the symmetric difference. Comparing the projected tuple rather than some
abstract "grant" means the comparison is against what the code actually writes, including the
`user:{id}|project:{scope}` scoped-subject form.

### The divergence classes, the direction chosen, and why

**Class A — tuple in Keto, no backing row.** Keto is the authority, so this grant is **live right
now**, and nothing records who granted it, to whom, or when. It is what a process killed between
the two writes leaves behind — and equally what someone writing straight to the tuple store leaves
behind.

> **Direction: delete the tuple.** The row is written second, so a tuple without one is by
> definition a grant that never completed. If it was not a dropped write it is worse than one.
> Revoking is the recoverable direction: an admin can re-grant, whereas privilege left standing
> cannot be un-exercised.

**Class B — row in Postgres, no tuple.** Dead as authorisation — `LiveAuthorizationService` asks
Keto and Keto says no — but not inert: `AuthController`'s consent path still reads `db.OrgRoles` to
resolve scopes into a minted token, so a row nobody can act on can still put scopes in one. It
arises from a failed *removal* (`RemoveManagementRoleAsync` deletes the tuple first, the row
second) and from `OrgController.UpdateOrgRole`, which deletes the old tuple, saves the row, then
writes the new tuple — a crash in that window leaves a row whose tuple is gone.

> **Direction: delete the row. Never create the tuple.** This is the asymmetry that matters.
> Creating a tuple from a row would make `org_roles` a source of authority again — the exact
> coupling S-8 removed — and would hand anyone with database write access an escalation path:
> insert a row, wait for the reconciler to promote it into a real grant. **Authority only ever
> converges downward.** Deleting the row converges the bookkeeping onto what Keto already says,
> which costs the user nothing they still had.

**Class C — tuples with no backing table, by design.** Not divergence and must never be treated as
such: the bootstrap super admin (`System:rediensiam#super_admin`, written by `Program.cs` with no
row at all), user-list membership, and the structural `org` relations on `Organisations`/`Projects`.
A reconciler that compared those would report the deployment's only super admin as an orphan and
then revoke it. Excluded by construction — only management relations on `Organisations` and
`role:*` on `Projects` are listed, and there is a test that says so.

### What keeps repair safe

- **Read Keto first, the database second.** A grant in flight has written its tuple and not yet its
  row, so reading the database afterwards gives it the best chance of being seen complete. Narrows
  the window; does not close it, which is why:
- **Every item is re-checked against the other store immediately before it is acted on.** An orphan
  tuple is re-checked against a fresh database read; an orphan row is re-checked with an
  authoritative Keto `check`. A grant that completed between scan and repair keeps both halves.
- **Repair is operator-triggered, never automatic.** The daily pass reports; `POST
  /admin/grant-reconcile/repair` acts. Revoking a grant unattended on the strength of one
  background read is a privilege change nobody asked for.
- **A bound: `MaxRepairsPerRun = 100`, above which repair refuses entirely and says why.**
  Divergence at that scale is not dropped writes — it is a Keto restored from an old backup, or a
  half-migrated database. In that state both repairs are destructive: deleting rows discards the
  provenance of grants that ought to be re-created, deleting tuples revokes an organisation's whole
  admin set at once.
- **Listing failures are loud.** `KetoService.ListRelationTuplesAsync` pages properly and
  `EnsureSuccessStatusCode`s: a partial list read as the state of the store turns every unread
  grant into "missing from Keto", and the repair for that class deletes rows. Every other Keto read
  fails soft because a failed check is a denial; this one has to fail loud.

### Where it runs and how the findings surface

Same `IntegrityMonitorService` daily pass. `iam_grant_divergence{class="orphan_tuple"|"orphan_row"}`,
an error log for orphan tuples (live privilege) and a warning for orphan rows, plus
`GET /admin/grant-reconcile` on demand. RLS: the monitor runs unscoped and is listed in
`TenantScopeInterceptor.LegitimatelyUnscopedPaths` with the reason.

---

## 3. Tests

`tests/RediensIAM.IntegrationTests/Tests/Regression/AuditChainKeyingTests.cs` (9):

| Test | Property |
|---|---|
| `ARowRewrittenAndTheChainRecomputedByHand_StillFailsVerification` | the finding: the attacker rewrites a row and re-chains the tail with the unkeyed algorithm he can run himself. Verified clean before; breaks at the rewritten row now |
| `AChainForgedUnderTheWrongKey_FailsVerification` | right shape, wrong key — the format is public, the key is not |
| `RowsWrittenByTheApplication_AreFullyVerified` | the positive case, and every row carries a `k1:` envelope |
| `RowsFromBeforeKeying_WalkButAreReportedUnverifiable` | the boundary: 2 legacy rows + 1 keyed row → intact, `Unverifiable = 2`, `Verified = 1`, `FullyVerified = false` |
| `AKeyedRowDowngradedToTheUnkeyedFormat_IsABreak` | the boundary is not an escape hatch |
| `ARowInsertedWithNoHashAfterTheChainStarts_IsABreak` | a row planted straight into the table |
| `TheChainVerifier_IsRunByAHostedServiceAndPublishesWhatItFinds` | the verifier has a production caller, and a pass leaves a number an alert can fire on |
| `RotatingTheRoot_LeavesEveryHistoricRowVerifiable` | rotation: old rows verify under the rotated ring, new rows go under key 2, the spanning chain verifies end to end |
| `RetiringARoot_MakesItsRowsUnverifiableRatherThanBroken` | a key nobody kept is not evidence of an attack |

The legacy hash is deliberately **reimplemented in the test** rather than borrowed from the
application: anyone who can write to `audit_log` can write that function — that is the finding — so
the test computes it the way an attacker would.

`tests/RediensIAM.IntegrationTests/Tests/Regression/KetoReconcilerTests.cs` (9): each divergence
class detected (`ATupleWithNoBackingRow…`, `ARowWithNoTuple…`, `AProjectRoleRowWithNoTuple…`),
agreement is not divergence, by-design tuples are never orphans, and repair does what is claimed —
`RepairingAnOrphanTuple_DeletesItFromKeto` asserts the DELETE actually reached Keto (via the write
stub's request log, not the return value), `AnOrphanRowWhoseTupleTurnsUpOnTheRecheck_IsLeftAlone`
and `RepairingAnOrphanRowKetoConfirmsIsGone_DeletesTheRow` are the two halves of the re-check,
`RepairingAnOrphanRow_NeverCreatesTheTuple` asserts the direction claim as an absence of any PATCH,
and `DivergenceAboveTheRepairBound_RefusesToRepairAtAll` proves the bound refuses without deleting
anything. It runs against **its own database** (the `KeyRotationSweepTests` pattern): a reconciler
that compares every grant in the deployment and then deletes things must not be pointed at the
collection's shared database.

Also touched, minimally, because the change reached them: three assertions in `StructuralDebtTests`
(return type), one context construction in `KeyRotationRegressionTests`, one env key in
`TrustAnchorRegressionTests`, and `KetoStub` gained tuple-listing and write-request-log support.

### Suite

```
Passed!  - Failed: 0, Passed: 1413, Skipped: 0, Total: 1413, Duration: 3 m 47 s
dotnet build RediensIAM.slnx -p:SonarQubeTargetsImported=true   →  0 Warning(s), 0 Error(s)
```

---

## 4. What `docs/SECURITY.md` should say next revision

Not edited here, as instructed. The three claims that are now wrong:

1. **§ one-paragraph summary** — "is hash-chained — with an **unkeyed** hash, and with no scheduled
   verifier". Both halves are stale. It should read: hash-chained under an HMAC keyed from the
   deployment root, verified daily by `IntegrityMonitorService` and on demand at
   `GET /admin/audit-chain`; rows written before keying are reported as unverifiable rather than as
   valid, and tail truncation remains undetectable.
2. **§1 "The dual write is real and unreconciled"** — "there is no reconciler and no outbox". Half
   of that is now false. It should read: there is a reconciler (`GrantReconciler`, daily detection
   + operator-triggered repair) which revokes tuples with no backing row and deletes rows with no
   tuple, never the reverse; **there is still no outbox**, so divergence is detected after the fact
   rather than prevented, with a window of up to 24 h before it is reported.
3. **§1 "Keto is the only authority"** — the sentence calling that an overstatement should stay,
   but for the remaining reason only: `AuthController`'s consent path still reads `db.OrgRoles` for
   scopes. That is now the sole reason, and orphan rows feeding it is exactly why class B is
   repaired rather than tolerated.

The "four things are known-open" list at the bottom needs re-counting against the residuals below.

---

## 5. Left open, with its cost

- **No outbox.** The dual write is still two writes. This detects divergence up to 24 h later
  instead of preventing it. An outbox (write the intent transactionally with the row, drain it to
  Keto) is the real fix and is a larger change than this one; the reconciler is what makes its
  absence survivable rather than invisible.
- **Tail truncation of a chain is undetectable**, as is an emptied table. Fixing it needs an
  external anchor — periodically publishing the head hash somewhere the database credential cannot
  reach (a log sink, an object store with retention lock). Cheap to add, and the natural next step;
  the head hash is already sitting there.
- **Retiring a root blinds the chain** for rows written under it. Operational rule, not a bug:
  keep retired roots in `Security:EncryptionKeys` until retention has aged those rows out.
- **User-list membership tuples (`UserLists#member`) are not reconciled.** They are membership, not
  a management grant, and their divergence has a different blast radius; the reconciler's scope is
  named in its doc comment.
- **The daily interval is a compromise.** The grant scan is cheap and would happily run hourly; the
  chain pass reads every audit row. Splitting the loop is the upgrade path and is named in the
  class.
- **The advisory-lock write path is unchanged** — audit writes for one organisation still
  serialise, as before.

### Follow-ups for files owned by other agents

- **`deploy/monitoring/audit-detections.sh` (monitoring agent)** — three new signals to alert on:
  - `iam_audit_chain_broken_orgs > 0` — **page**. A rewritten or removed audit row.
  - `increase(iam_audit_chain_unverifiable_rows[1d]) > 0` — **page**. That number is historic and
    should only ever fall as retention purges it; a rise means rows are being written outside the
    application, or keyed rows are being downgraded.
  - `iam_grant_divergence{class="orphan_tuple"} > 0` for 1 h — **page** (live privilege with no
    provenance). `class="orphan_row"` — ticket, not a page.
- **Helm chart (deploy agent)** — no new configuration. The chain key derives from the existing
  `encryptionKeys` ring; nothing to add to `values.yaml`. The one operational note worth putting in
  the chart's upgrade docs is the retired-root rule above.
