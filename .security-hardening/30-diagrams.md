# 30 — Architecture diagrams

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Base commit:** `d4cfb31` ·
**Version:** 0.2.1

Deliverable: [`docs/DIAGRAMS.md`](../docs/DIAGRAMS.md) — 27 Mermaid diagrams across 8 sections,
linked from `docs/ARCHITECTURE.md` (intro, and the *Where to go next* table) and from the README's
documentation table.

Same caveat as every other file in this directory: **this is a point-in-time record of what was
done, not a description of the system.** The diagrams themselves were drawn from the source and the
chart, never from the reports in here.

---

## 1. What was drawn

Split by concern rather than into one large graph. Anything that was clearer as a table stayed a
table.

| § | Diagrams | Drawn from |
|---|---|---|
| 1 Deployment topology | 1.1 what-talks-to-what with per-edge TLS; 1.2 ingress routing and the unconditional-deny middleware; 1.3 a dev/prod/chart-default state table plus the Dragonfly TLS render-guard; 1.4 what the five NetworkPolicies permit | `templates/{deployment,service,ingress,admin-ingress,network-policies,dragonfly,postgres}.yaml`, `values{,.dev,.prod}.yaml`, `Program.cs`, `CacheTls.cs` |
| 2 Request pipeline | 2.1 the full middleware order with both `UseWhen` predicates; 2.2 inside `GatewayAuthMiddleware`; 2.3 the action filter resolving the grant | `src/Program.cs:336-407`, `src/Middleware/GatewayAuthMiddleware.cs`, `src/Filters/RequireManagementLevelAttribute.cs` |
| 3 Authorisation | 3.1 claim-vs-grant type split; 3.2 the live check with its cache, suspension gate and fail-closed path; the three ProjectAdmin tuple shapes | `GrantedLevel.cs`, `LiveAuthorizationService.cs`, `KetoService.cs:96-123`, `Roles.cs` |
| 4 OIDC login | 4.1 password login end-to-end including MFA and where `ext.roles` is project-qualified; 4.2 the admin-console branch of the same consent handler; 4.3 the federated entry points | `AuthController.cs` (`GetLogin`, `CheckUserCredentials`, `InitiateMfa`, `CompleteLogin`, `GetConsent`), `HydraService.cs`, `SamlController`, `SocialLoginService` |
| 5 Introspection / authorize | 5.0 the full `/api/introspect` sequence; 5.1 the extra guards on `/api/authorize`; 5.2 what `ver` is for | `IntrospectionController.cs`, `PatService.cs`, `HydraService.ValidateJwtAsync` |
| 6 Data model | 6.1 an `erDiagram` of the tenancy graph; 6.2 the 19 policied tables grouped by how their predicate reaches `OrgId`; 6.3 how a scope reaches the database | `src/Data/Entities/*.cs`, `files/rls.sql`, `TenantScopeInterceptor.cs`, `AppConfig.cs:18-52` |
| 7 Keys | 7.1 HKDF root → six purposes → what each protects; 7.2 the `k<id>:` envelope with the legacy-prefix rule; 7.3 the rotation sequence and a table of the four things the sweep *cannot* cover; 7.4 the DataProtection read-side refusal | `AppConfig.cs:136-258`, `TotpEncryptionService.cs`, `KeyRotationService.cs`, `KeyRingProtection.cs`, `PasswordService.cs` |
| 8 Audit | 8.1 both producers and the three-step `SaveChangesAsync` hook; 8.2 the per-org chain and the advisory lock; 8.3 what `VerifyChainAsync` checks and who calls it; 8.4 the three limits | `RediensIamDbContext.cs:48-252`, `AuditChain.cs`, `AuditLogService.cs:68-79`, `AuditLogRetentionService.cs` |

Things deliberately **not** drawn, and said so at the foot of the document: the 184-route table
(`API.md` has it), webhook delivery (a subsystem, not part of the authorisation spine), and the
`instances` precedence rules (already a three-row table in `ARCHITECTURE.md`).

---

## 2. Render verification

Every fenced ` ```mermaid ` block was extracted to its own `.mmd` and rendered individually. A block
that only *looks* fine in a GitHub preview is exactly the failure this step exists to catch.

**Command:**

```bash
# extract each block to /tmp/.../mmd/dNN.mmd, then:
cd /tmp/.../mmd
for f in d*.mmd; do
  out=$(npx -y @mermaid-js/mermaid-cli -i "$f" -o "${f%.mmd}.svg" 2>&1)
  if echo "$out" | grep -qi "error"; then echo "FAIL  $f"; else echo "OK    $f"; fi
done
```

**Output (final run):**

```
27
OK    d01.mmd
OK    d02.mmd
...
OK    d27.mmd
PASS=27 FAIL=0
```

`mermaid-cli` resolved to the current `@mermaid-js/mermaid-cli` on npm under Node v24.4.1 / npm
11.7.0, rendering headless Chromium via puppeteer. 27 SVGs were produced.

### Two parse failures found and fixed

Both were in the same block (§5, the introspection sequence), both silent in a Markdown preview,
both caught only because the renderer was run:

| Failure | Cause | Fix |
|---|---|---|
| `Parse error on line 16 ... got 'INVALID'` | `Note over IC: 400, not active:false.` — a second `:` inside a sequence-diagram `Note` body terminates the note text | reworded to "not an inactive answer" |
| `Parse error on line 16` (again) | `the CALLER's request; answering ...` — `;` is a statement separator in the sequence grammar | replaced with a full stop |

Neither would have surfaced without rendering. That is the whole point of the check.

---

## 3. Where the code contradicted `ARCHITECTURE.md`

`ARCHITECTURE.md` was accurate on every structural claim I checked — the claim/grant split, the
default-deny gate, the audience gate, the `SelfGatedControllers` set, the 99 hand-written
`RecordAsync` calls, `VerifyChainAsync` having no production caller, the `k<id>:` envelope and its
legacy rule, the four-role Postgres split, and the nine `LegitimatelyUnscopedPaths`. **Three things
were stale**, all of them about deployment state, and all three were fixed in place because leaving
a new diagram beside prose that says the opposite is worse than either alone.

### 3.1 "RLS is off everywhere" — false

**Was:** `postgres.rls.enabled` is **`false`** in `values.yaml:302` and is not overridden in either
`values.dev.yaml` or `values.prod.yaml`. **RLS is off everywhere.**

**Is:** `values.dev.yaml` sets `postgres.rls.enabled: true`, with a long comment dating it to step
29. 19 tables carry `ENABLE` + `FORCE` + one `rediensiam_tenant` policy on the dev cluster.
`values.prod.yaml` does not override the `false` default. The line reference was also off by six —
`rls.enabled` is `values.yaml:308`, not `:302`.

`SECURITY.md` and `DEPLOYMENT.md` both already had this right ("Row-level security, live in dev",
`RLS applied to 19 tables`). Only `ARCHITECTURE.md` had missed the update.

**Fixed:** corrected the sentence and the line reference, and the transport table row
(`off / off / off` → `off / **on** / off`).

### 3.2 Dragonfly TLS "off in prod" — false in two documents

**Was**, in `ARCHITECTURE.md`: the transport table read `off / on / **off**`, the trust-boundary
table read "TLS on in dev, **off in prod**", and the prose read "It is on in dev and **not set in
`values.prod.yaml`**, so prod inherits `false`."

**Was**, in `SECURITY.md` §6: the control table read "**on in dev, off in prod**" and the qualifying
bullet read "`values.prod.yaml` sets no `dragonfly` block, so it inherits `enabled: false` from
`values.yaml:328`. … Prod simply has the flag off."

**Is:** `values.prod.yaml` sets `dragonfly.local.tls.enabled: true`, under a 20-line comment
describing the three-way hard cutover. The chart default at `values.yaml:333` is still `false`.

This is the exact drift `.security-hardening/README.md` calls out for `23-cache-hardening.md`,
except in the opposite direction: the report overstated the fix, and the docs then under-stated it
after prod values were set later. `SECURITY.md`'s **own risk register** (the "Dragonfly TLS in prod
is untested live" row) already said `values.prod.yaml` now sets it — so §6 and the risk register of
the same document disagreed with each other.

**Fixed** in both files, and phrased so the distinction survives: *the flag is set in both values
files; it has only ever been executed in dev.* Prod has never been deployed from this branch, so the
prod half is `helm template`-verified and reasoned from the dev cutover, not observed. The diagram
legend in `DIAGRAMS.md` carries a dedicated marking (`TLS dev only`) for exactly this state so no
edge can be read as proven.

### 3.3 `GetManagementLevel` "is private" — it is deleted

`ARCHITECTURE.md` gets this right ("made private and then deleted"). The stale claim is in the
**test** that guards it: `tests/.../StructuralDebtTests.cs:33` still says "the guard moved to the
compiler: `GetManagementLevel` is private", while the assertion two lines down checks that the
public static overload does not exist. The brief for this step also carried the "why is it private"
framing.

Verified: `grep -rn GetManagementLevel src/` returns **one comment** and no code. The diagram in
§3.1 shows it as deleted, in red, and says so explicitly rather than repeating the "private"
framing. `tests/` is outside this step's scope, so the test docstring was left alone — it is a
comment, not an assertion, and the assertion is correct.

---

## 4. Behaviour found while drawing that no document mentions

Not defects. Recorded because a diagram made them visible and the next reader will ask.

### 4.1 `GatewayAuthMiddleware` runs twice for `/admin/system/*`

`Program.cs` mounts it on two `UseWhen` branches:

- `protectedPrefixes` includes `"/admin/system"` (`:367`)
- the second branch matches `"/admin"` minus `/admin/config`, when the endpoint is a controller
  action or the method is not GET (`:380-385`)

`UseWhen` branches rejoin the main pipeline, so a request to `/admin/system/health` matches both and
the middleware executes twice in full. It is correct — introspection is idempotent — and the second
pass costs a Dragonfly cache hit, not a second Hydra round-trip. Drawn as-is in §2.1 with a note,
because a reader who traces the branches will otherwise assume the diagram is wrong.

### 4.2 `/account/*` is authenticated but is not a management surface

`protectedPrefixes` contains `/account` and `/auth/oauth2/link`; `ManagementPrefixes` does not.
So end-user self-service requires a bearer token but sees **neither** the audience gate **nor** the
default-deny gate. That is right — a tenant application's own token is precisely who should be
calling `/account/*` — but the two prefix lists are close enough to be misread as one. §2.1 states
the difference on the diagram.

### 4.3 The admin consent path is the one place a DB row still shapes a minted token

`AuthController.GetConsent`, admin-client branch: the **role list** comes from three Keto checks;
the **`org_id` and `project_id` scopes** stamped into the token come from `db.OrgRoles`
(`:654-662`). `ARCHITECTURE.md` says this in prose under "Honest limit"; §4.2 draws it with a note
on the exact interaction, because it is the concrete counter-example to "Keto is the only store
consulted anywhere".

---

## 5. What could not be represented faithfully

| Thing | Why not |
|---|---|
| **Whether any NetworkPolicy is enforced** | A NetworkPolicy is a declaration. Whether the CNI implements it is a property of the cluster, not of this repo, and cannot be read from a file. Every arrow in §1.4 is labelled a *permission*, with the caveat stated on the diagram rather than in a caption. |
| **Prod anything** | Production has never been deployed from this branch. Every prod column in §1.3 is what the chart *would* render, verified by `helm template` only. The legend distinguishes "set" from "executed" precisely so no prod row reads as observed. |
| **The `org_roles` / Keto divergence window** | A dual write with no reconciler and no outbox has a failure state — process killed between the tuple write and the row write — that is a *state*, not a flow. A box on §3.2 would have implied a recovery path that does not exist. It is stated as prose beneath the diagram instead. |
| **The ~200 hand-written tenant conjuncts** | They are the actual tenant-isolation mechanism in prod and they are un-diagrammable: 200 call sites, no shared chokepoint, no EF global query filter (`grep HasQueryFilter src/` → one comment, no code). §6.2 draws the *absence* — the "off" branch says what runs instead — rather than pretending there is a structure to draw. |
| **Argon2 pepper rotation as a completing process** | It never completes. Dormant accounts keep an old pepper indefinitely. Drawn in §7.3 as a table row explaining why it is a policy decision, not as a sequence with an end state. |
| **Route-level authority** | 184 routes. A diagram would be unreadable and would go stale on the next controller. `API.md` owns it; §"What is deliberately not drawn" says so. |

---

## 6. Files changed

| File | Change |
|---|---|
| `docs/DIAGRAMS.md` | **new** — 27 verified diagrams |
| `docs/ARCHITECTURE.md` | link in the intro and in *Where to go next*; RLS-off-everywhere corrected (§3.1); Dragonfly-TLS-off-in-prod corrected in the transport table, the trust-boundary table and the prose (§3.2) |
| `docs/SECURITY.md` | §6 control table and its qualifying bullet corrected for Dragonfly TLS (§3.2) |
| `README.md` | `docs/DIAGRAMS.md` row added to the documentation table |

No `src/`, `sdk/`, `frontend/` or `tests/` file was touched. No `deploy/` file was touched — the
chart was read, not edited. Nothing was committed.

---

## 7. If you are updating these diagrams

1. Edit `docs/DIAGRAMS.md`.
2. Re-run the extraction and render loop in §2. **Do not skip it.** Both failures found in this step
   were invisible in a Markdown preview and would have shipped a blank block.
3. If a diagram and the code disagree, the code wins and the diagram is a bug — same rule
   `ARCHITECTURE.md` states for itself.
4. If a control is implemented but disabled, or proven only in dev, say so **on** the diagram. The
   legend at the top of the document already has markings for both.
