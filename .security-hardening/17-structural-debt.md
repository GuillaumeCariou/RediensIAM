# Step 17 — Structural debt: S-1, S-8, S-3

**Branch:** `security/hardening-2026-07-30`
**Scope owned by this pass:** `src/Services/`, `src/Config/`, `src/Filters/`, `src/Middleware/`,
`src/Data/`, `tests/`. **`src/Controllers/` was not touched** — two other agents were editing
`src/Controllers/` + `docs/` and `deploy/` concurrently.
**Spec:** [`03-architecture-review.md`](03-architecture-review.md) §2.3, §1.4, §8 (S-1, S-8, S-3);
the refusals in [`05-backend-hardening.md`](05-backend-hardening.md) §S-1 and
[`08-auth-enhancement.md`](08-auth-enhancement.md) §4a/§4b.

| Finding | Status |
|---|---|
| **S-1** — no type distinguishes a verified authority from a claimed one | **Partial.** Type, producer and default-deny landed and enforced at runtime; the *compile* break is one line away and needs three controller edits this pass may not make. Demonstrated below with real compiler output. |
| **S-8** — dual authority on authorisation | **Done for the half that was asked.** One implementation, one store. The reconciler for the dual-write is **not** done — cost stated. |
| **S-3** — audit trail is remembered, not structural, and has no tamper-evidence | **Done.** Both halves: an EF `SaveChanges` hook that audits without a call site, and a per-org hash chain with a verifier. Database-level append-only remains the deploy agent's. |

**Suite: 1305 passed, 0 failed, 0 skipped** (baseline for this pass was 1274; +9 from this pass,
+22 from the concurrent controller pass). Full output in §5.

---

## 1. S-1 — a claimed level and a granted one are now different types

### 1.1 The async problem, solved by moving the resolution rather than the `await`

Both prior refusals were right about the mechanics and wrong about the only available shape.
`05-backend-hardening.md` §S-1 states the blocker precisely:

> `GetManagementLevel()` is a synchronous extension method on `TokenClaims`; `GrantedLevel` can
> only be produced by `LiveAuthorizationService`, which is `async`. So every current consumer …
> becomes an `await` at a call site that is currently a property read. Several of those sit inside
> LINQ expression trees passed to EF Core … which cannot contain an `await` at all.

That is only true if each consumer resolves its own grant. **It does not: the grant is resolved
once, in the filter that already runs before every one of them, and stashed for the request.**

```
RequireManagementLevelAttribute.OnActionExecutionAsync   (src/Filters/RequireManagementLevelAttribute.cs:30-49)
  claimed = GrantedLevel.ClaimedLevel(claims)      // which level to re-check — never the answer
  granted = await GrantedLevel.ResolveAsync(claims, live)   // the one await, once per request
  ClaimsExtensions.RecordGrantedLevel(claims, granted)
```

and every consumer thereafter reads

```csharp
HttpContext.GetGrantedLevel()     // GrantedLevel?, synchronous, already verified
```

A synchronous read means an EF `Where` can close over it exactly as it closes over
`ManagementLevel` today — `ServiceAccountController.IsCallerProjectListAsync`'s
`Level == ManagementLevel.SuperAdmin` inside a predicate becomes
`granted.Value == ManagementLevel.SuperAdmin` with no restructuring at all. The "async plumbing
change through six controllers" is not required; the three call sites in §1.4 are.

Rejected alternative: passing the org id into the query instead of the level. It works for the two
EF cases and does nothing for the eleven action bodies, so it is a smaller fix to a smaller problem.

### 1.2 The type

`src/Services/GrantedLevel.cs` — a `readonly struct` with a **private** constructor whose only
caller is `ResolveAsync`, lexically inside the type:

```csharp
public readonly struct GrantedLevel
{
    public ManagementLevel Value { get; }
    private GrantedLevel(ManagementLevel value) => Value = value;
    public bool IsAtLeast(ManagementLevel required) => Value <= required;

    internal static async Task<GrantedLevel?> ResolveAsync(TokenClaims claims, LiveAuthorizationService live)
    {
        var claimed = ClaimedLevel(claims);
        if (claimed == ManagementLevel.None) return null;
        return await live.IsStillGrantedAsync(claims, claimed) ? new GrantedLevel(claimed) : null;
    }

    internal static ManagementLevel ClaimedLevel(TokenClaims claims) { … }
}
```

**Why private and not `internal`.** `internal` is the obvious reading of §2.3(b) and it buys
nothing: controllers are in the same assembly, so an `internal` constructor is reachable from
exactly the code the finding is about. A private constructor plus a producer inside the type is
the only arrangement that holds without splitting the assembly, and splitting the assembly is a
larger change than the one being defended against. `NothingOutsideGrantedLevel_CanConstructOne`
asserts against a future widening.

`ClaimedLevel` is deliberately named for what it returns and is now the *only* place the
`ext.roles` → level mapping exists; the old body of `GetManagementLevel()` moved here.

### 1.3 The unverified path stops working

`ClaimsExtensions` (`src/Middleware/GatewayAuthMiddleware.cs:123-193`) keeps a
`ConditionalWeakTable<TokenClaims, StrongBox<GrantedLevel?>>`. `GatewayAuthMiddleware` marks the
caller's claims when it sets `ctx.Items["Claims"]`; the filter fills in the grant. Then:

```csharp
public static ManagementLevel GetManagementLevel(this TokenClaims claims)
{
    if (CallerGrants.TryGetValue(claims, out var box))
    {
        if (box.Value is not { } granted)
            throw new InvalidOperationException(
                "Unverified management level read for the calling token. …");
        return granted.Value;
    }
    return GrantedLevel.ClaimedLevel(claims);
}
```

- **The caller's own token, with no live check behind it → throws.** That is R-22's shape and the
  password-floor bypass's shape, and it is now a request that cannot succeed.
- **Somebody else's token → still answers.** `IntrospectionController` asks what a *third-party*
  token asserts in order to strip management roles from the response; that is a question about the
  token, not about the caller's authority, and it is legitimate. Every request builds its own
  `TokenClaims` instance (`HydraService.ValidateJwtAsync` deserialises from cache;
  `PatService` constructs), so identity is exact and nothing survives a request.

Test: `ReadingTheCallersManagementLevel_WithNoLiveCheckBehindIt_IsRefused` and
`ReadingWhatAnotherPartysTokenAsserts_StillWorks`.

**This is a runtime guarantee, not a compile-time one. Stated plainly so it is not read as more
than it is.** §1.4 is the compile-time half.

### 1.4 What must change in controllers — and the compile errors that prove it

Making `ClaimsExtensions.GetManagementLevel` non-public is a **one-line change** and is the whole
compile-time half of S-1. It was applied temporarily during this pass and reverted, because the
build must stay green and the three call sites are in files this pass does not own. The exact
compiler output:

```
src/Controllers/IntrospectionController.cs(100,28): error CS1061: 'TokenClaims' does not contain a definition for 'GetManagementLevel'
src/Controllers/ProjectController.cs(35,24):        error CS1061: 'TokenClaims' does not contain a definition for 'GetManagementLevel'
src/Controllers/ServiceAccountController.cs(36,46): error CS1061: 'TokenClaims' does not contain a definition for 'GetManagementLevel'
```

Three errors, no others. The follow-up pass:

| File / line | Today | Must become | Why |
|---|---|---|---|
| `src/Controllers/ServiceAccountController.cs:36` | `private ManagementLevel Level => Claims.GetManagementLevel();` | `private ManagementLevel Level => HttpContext.GetGrantedLevel()!.Value.Value;` | **R-22 itself.** The controller is `[RequireManagementLevel(ProjectAdmin)]`, so the grant is always present; the `!` is safe *because* the attribute is there, which is the invariant S-1 exists to make explicit. Nothing else in the file changes — `CanAccessAsync`'s `switch` and `IsCallerProjectListAsync`'s `Where` both keep working on a `ManagementLevel` local. |
| `src/Controllers/ProjectController.cs:35` | `if (Claims.GetManagementLevel() <= ManagementLevel.OrgAdmin)` | `if (HttpContext.GetGrantedLevel() is { } g && g.IsAtLeast(ManagementLevel.OrgAdmin))` | The `?project_id=` escalation branch: an OrgAdmin/SuperAdmin may target any project in their org. Deciding that from a claim is the same defect one tier down. |
| `src/Controllers/IntrospectionController.cs:100` | `var level = claims.GetManagementLevel();` | `var level = GrantedLevel.ClaimedLevel(claims);` | Legitimate — it is asked about the presented token, not the caller. Only the name changes, to one that says so. |

Then flip `public static ManagementLevel GetManagementLevel` to `private static` in
`src/Middleware/GatewayAuthMiddleware.cs:176` and delete the runtime throw with it. Line numbers
are as of this writing; `IntrospectionController` moved ~27 lines during this pass because it is
being edited concurrently, so re-locate by symbol.

### 1.5 S-1(a) — default deny

`GatewayAuthMiddleware` (`:65-101`). After the audience gate, a request on a management prefix
(`/admin`, `/org`, `/project`, `/service-accounts`, `/api`, `/internal`) that resolves to an MVC
action carrying no `[RequireManagementLevel]` is refused `403 no_authorisation_gate`.

- It lives in the middleware, not in `AddControllers(o => o.Filters.Add(...))`, because `Program.cs`
  is not owned by this pass and a global MVC filter needs a registration line there. `UseRouting()`
  has already run on both branches the middleware is mounted on (`Program.cs:355` precedes `:361`
  and `:373`), so the endpoint and its metadata are available.
- The `[PublicSurface]` marker of §2.3(a) would be a controller edit on every exempt controller.
  Instead the exemption set is a single array, `SelfGatedControllers`, in the middleware file —
  which is the *auditable artefact* §3.2 asks for, not a compromise on it. It contains exactly one
  entry: `Introspection`, whose gate is `IsServiceAccountCaller`, not a management level.
- Endpoints that are not MVC actions — the SPA fallback, `/admin/config`, static assets — resolve
  to no `ControllerActionDescriptor` and pass, unchanged.

This closes I-02's class: a new controller on `/admin` fails closed by construction rather than by
its author remembering an attribute. The follow-up pass should replace the array with
`[PublicSurface]` on `IntrospectionController` when it is next open.

### 1.6 Also fixed in passing (§2.3(c))

`LiveAuthorizationService.cs:46` — the cache key `authz:{userId}:{level}` did not carry the scope
the decision was made under. The value now carries `{orgId}/{projectId}`, so a `ProjectAdmin`
verdict decided for project A cannot be replayed for project B within the 30-second window. The
org half of this was already there; the project half is new and is required by §2.1 of S-8 below.

### 1.7 Residual risk on S-1

- **The compile break is not applied.** Until §1.4 lands, a *new* controller can still write
  `Claims.GetManagementLevel()` and it will compile. It will throw on the first request that
  reaches it, and if it is on a management prefix it will not reach it at all (§1.5) — so the path
  is dead, not silent. That is strictly weaker than a compile error and is the one thing this pass
  did not finish.
- **`GetGrantedLevel()` returns null on non-management surfaces.** `/account/*` has no
  `[RequireManagementLevel]` and does not need one; a future action there that wants a level must
  add the attribute rather than dereference a null.
- **§2.3(d), service accounts, is untouched.** `LiveAuthorizationService.cs:37` still returns
  `true` for `IsServiceAccount` on the grounds that `PatService` re-checks liveness per call. The
  review is right that "the account is live" is not "the role is granted": a service account's
  roles are read at cache fill and only `InvalidateServiceAccountAsync` refreshes them. A dedicated
  SA producer for `GrantedLevel` is not written. Cost: re-reading `ServiceAccountRole` rows on the
  resolve path plus a cache-key change in `PatService` — perhaps half a day, and it changes the
  latency profile of every service-account request, which is why it is not a rider on this pass.

---

## 2. S-8 — one authorisation question, one implementation, one store

### 2.1 What was there

Three resolutions of "what management level does this actor hold", reading two stores:

| Where | `project_admin` resolved as |
|---|---|
| `LiveAuthorizationService.CheckAsync` | Keto `Projects#manager` for **any** project **OR** an `org_roles` row **anywhere** — no org scope, no project scope |
| `KetoService.GetActorManagementLevelForOrgAsync` | an `org_roles` row scoped to the org |
| `KetoService.GetActorManagementLevelForProjectAsync` | Keto `Projects#manager` on that project |

Two of those consult a store the other does not, and nothing could ever notice them disagreeing.

A fact that decided the direction: **nothing in this codebase writes `Projects:{id}#manager`.**
Every reference (`LiveAuthorizationService`, `KetoService`, `AuthController:645,1070`) is a read.
The tuple that grants `project_admin` is the one `AssignManagementRoleAsync` writes —
`Organisations:{orgId}#project_admin@user:{id}` or `…@user:{id}|project:{scope}` — and no check
was reading it. The DB fallback existed to paper over that mismatch.

### 2.2 What it is now

`KetoService.IsManagementLevelGrantedAsync(actorId, level, orgId, projectId)` is the single
implementation, in `src/Services/KetoService.cs:76-142`. It reads the tuples the grant paths
actually write:

- `SuperAdmin` → `System:rediensiam#super_admin`
- `OrgAdmin` → `Organisations:{orgId}#org_admin`, and the claim must name the org
- `ProjectAdmin` → `Projects:{projectId}#manager`, **or** `Organisations:{orgId}#project_admin` for
  the bare subject, **or** the same for the `|project:{id}` scoped subject

`ResolveManagementLevelAsync` is that, tried most-privileged-first; both
`GetActorManagementLevelForOrgAsync` and `GetActorManagementLevelForProjectAsync` are now one-line
delegations, and `LiveAuthorizationService.CheckAsync` calls
`keto.IsManagementLevelGrantedAsync(...)` with the org and project from the claims.

**Keto is the single authority for management level.** It is the store every grant path writes
*first* (`AssignManagementRoleAsync:206`, and `AssignOrgAdmin` since step 8 §4b), so the failure
modes are asymmetric in the right direction: a row without a tuple is a failed grant that does not
work; a tuple without a row is a grant whose bookkeeping lagged and still does. `org_roles` keeps
holding the scope, the display name and the provenance. It is no longer consulted as an answer —
`LiveAuthorizationService` no longer references `db.OrgRoles` at all.

**R-22 residual 3 is closed by this.** "project_admin somewhere" no longer satisfies
"project_admin here": the check names a scope or it fails.

### 2.3 What newly fails

- `KetoStub.DenySubject` had to be widened (`tests/…/Infrastructure/KetoStub.cs:96-110`). It
  stubbed a denial for one exact `subject_id` and left `user:{id}|project:{pid}` allowed — a state
  real Keto cannot be in, since a missing grant is missing for both subject forms. Two tests
  (`ProjectCoverageTests.AssignRole_WhenNoKetoManagementRights_Returns403`,
  `…RemoveRole_…`) went green→red→green across that fix; the production behaviour they assert is
  unchanged.
- New: `WhenTheAuthorisationStoreRefusesAnActor_BothResolversRefuse` — with Keto refusing the
  subject and an `org_roles` project_admin row present in the very same organisation, both
  resolvers now answer `None`. **This test fails on the pre-change code**: the old
  `LiveAuthorizationService` asked a list endpoint that `DenySubject` does not cover, and fell back
  to the row.
- New: `AProjectAdminGrantInAnotherOrganisation_DoesNotAuthoriseThisOne` — an unscoped
  `project_admin` claim is refused. Also fails on the pre-change code.

### 2.4 Residual risk on S-8 — the reconciler is not written

The dual-write is unchanged: tuple first, row second, compensating tuple delete in a `catch`. That
covers a *thrown* exception and not a *killed process*, so a crash between the two still leaves a
tuple with no row on all four sites (`AssignProjectRoleAsync`, `AssignDefaultRoleAsync`,
`AssignManagementRoleAsync`, `AssignOrgAdmin`). Under the new arrangement that orphan is now
**authoritative** — it grants — where before it was one of two opinions. That is a deliberate
trade: one detectable state beats two undetectable ones, and the ordering means the orphan is a
grant that was *intended*, not a phantom.

**What closing it costs.** A hosted service enumerating `Organisations`/`Projects` tuples from
Keto's `GET /relation-tuples` (paged) against `org_roles`/`user_project_roles`, emitting an audit
row per orphan, plus the page-token handling and a `KetoStub` that can list. A day, plus a policy
decision on whether it deletes or only reports. It is a component, not a guard, and it wants its
own review — the same conclusion step 8 §4b reached, now with the dependency it was waiting on
(a single entry point) in place.

Also unaddressed, and named here so it is not lost: §1.4 item 2 — the `Projects:{id}#role:{name}`
tenant-role tuples are still written and still read by nothing.

---

## 3. S-3 — audit as a property of persistence

### 3.1 Coverage: the hook, not more call sites

T-N2 was closed by adding call sites, which is the defect S-3 names. The floor is now
`RediensIamDbContext.SaveChangesAsync` (`src/Data/RediensIamDbContext.cs:48-217`) — the one path
every mutation already takes.

`RecordUnloggedSecurityMutationsAsync` walks the change tracker and writes an `entity.*` audit row
for:

| Entity | Trigger |
|---|---|
| `User` | any of `PasswordHash`, `TotpSecret`, `TotpEnabled`, `WebAuthnEnabled`, `Phone`, `PhoneVerified`, `Email`, `EmailVerified`, `Active` is modified → `entity.users.credential_changed`, metadata naming the columns |
| `BackupCode`, `WebAuthnCredential`, `UserSocialAccount` | insert / update / delete |
| `SamlIdpConfig` | insert / update / delete — §7.3 item 5's "a silent anchor swap must be reconstructable" |
| `Instance` | insert / update — §7.2's "config load is unaudited" |

Design notes:

- **Additive, not a replacement.** The ~98 hand-written `RecordAsync` calls stay; they carry intent
  (`user.password.reset`) a change tracker cannot infer. The `entity.` prefix keeps the two
  separable in a query. Some events now produce both rows. That is the intended shape: the hook is
  the floor, and a floor you can also step on deliberately is fine.
- **In the caller's transaction.** The row is part of the same `SaveChanges`, so the mutation and
  its record commit or roll back together. `AuditLogService`'s own-scope design (which is correct,
  and step 3 §9 is right to praise it) solves a different problem — not letting a caller's
  uncommitted state ride along — and is untouched.
- **Rows carry the subject's org.** One batched lookup resolves `user → UserList.OrgId` so the rows
  land on the tenant's chain and are visible at `/org/audit-log`. Without that they would sit on
  the deployment-wide chain, invisible to the tenant whose user was taken over — which is the
  entire reason T-N2 mattered.
- **Actor** comes from `new HttpContextAccessor().HttpContext` (the backing store is a static
  `AsyncLocal`; a DbContext is built from options and has no route to application services). A
  background service or a startup path has no request and records a null actor, which is the honest
  answer.
- No webhook dispatch and no Prometheus increment on these rows — deliberately, to avoid a webhook
  storm on high-frequency columns.

Test: `OverwritingAnAuthenticationFactor_IsAuditedWithNobodyCallingTheAuditService` writes a TOTP
secret straight through the DbContext, with no controller and no audit call in the path, and finds
the row.

### 3.2 Tamper-evidence: a per-organisation hash chain

`src/Data/AuditChain.cs` + `RediensIamDbContext.ChainAsync`. Two new columns
(`PrevHash`, `Hash`, migration `20260731132739_AuditLogHashChain`) and one index `(OrgId, Id)`.

- `Hash = SHA256(prevHash ‖ canonical(row))` over every field including `Metadata`. Metadata is
  canonicalised by sorting keys and re-serialising each value through JSON, because `jsonb` does
  not preserve key order and returns `JsonElement` where a `string` went in. `CreatedAt` is
  truncated to microseconds — Postgres `timestamptz` precision — or a row would not verify against
  its own re-read.
- **Chains are per organisation** because retention purges are per organisation. A purge shortens
  an org's chain from the front, which stays verifiable; one global chain would be left with holes
  scattered through it and be indistinguishable from tampering.
- **Concurrency.** `pg_advisory_xact_lock` per org, taken in sorted key order so two transactions
  touching the same pair of orgs cannot deadlock. Without it two concurrent writers read the same
  predecessor and both claim it, and a fork is not distinguishable from a forgery. `ponytail:`
  audit writes for one org serialise; the upgrade path if that ever contends is to move the audit
  insert onto its own short transaction, not to widen the lock.
- Rows written before this migration carry an empty hash and are skipped by the verifier — they are
  *unverifiable*, which is not the same as *tampered with*.

`AuditLogService.VerifyChainAsync(orgId)` returns the id of the first row that does not link, or
null. It is detection, not prevention; the application holds credentials that could rewrite the
table, so all it can do is make the result inconsistent.

Tests: `EditingAnAuditRowBehindTheApplicationsBack_IsDetectable` (raw `UPDATE` → the tampered id),
`DeletingAnAuditRowFromTheMiddle_IsDetectable` (raw `DELETE` → the orphaned successor's id).

### 3.3 Append-only at the application layer

`RejectAuditLogTampering` throws if any `AuditLog` entry reaches `SaveChanges` in `Modified` or
`Deleted` state. The one sanctioned deletion — `AuditLogRetentionService`'s `ExecuteDeleteAsync` —
never enters the change tracker and is unaffected, which is exactly the line that should be drawn.
Test: `RewritingAnAuditRowThroughTheApplication_IsRefused`.

### 3.4 Residual risk on S-3

- **No `VerifyChainAsync` caller.** The method exists and is tested; nothing runs it. It wants
  either a `GET /admin/audit-log/verify` (a `SystemAdminController` route — not this pass's file)
  or a daily pass in `AuditLogRetentionService` writing an audit row on a break. The latter is
  ~15 lines and could have gone here; it is not in, because a verifier that reports into the log it
  is verifying wants a design decision (a break is exactly when the log is untrustworthy) and this
  pass should not make it silently.
- **Database-side append-only is not here.** The application refusing to issue an UPDATE is a
  guard against its own bugs, not against a writer with the credentials. The real control is no
  `UPDATE`/`DELETE` grant on `audit_log` to the app role — which needs the per-component Postgres
  role split, and that is the deploy agent's S-6/RLS work. The chain is what covers the gap in the
  meantime: it makes a rewrite *detectable* without needing it to be *impossible*.
- **A rewriter who recomputes the chain wins.** The hashes are unkeyed, so anyone who can write the
  table can rewrite it consistently from the edit forward. Making that infeasible needs either a
  key the app does not hold or an off-box anchor (periodic export of the chain head, or a WORM
  sink). §6 row 12's "no WORM export" is not closed. Cheapest real upgrade: HMAC the chain under a
  key the app writes with but a separate verifier holds.
- **Nested metadata values** canonicalise by `JsonSerializer.Serialize`, which is stable for the
  scalars this codebase stores and not guaranteed for nested objects. No current call site stores
  one.
- **Duplicate rows.** Events covered by both a hand-written call and the hook now write two rows.
  Noise, not risk; the `entity.` prefix makes them filterable.

---

## 4. Files changed

**Owned by this pass, modified:**
`src/Services/GrantedLevel.cs` (new) · `src/Services/LiveAuthorizationService.cs` ·
`src/Services/KetoService.cs` · `src/Services/AuditLogService.cs` ·
`src/Filters/RequireManagementLevelAttribute.cs` · `src/Middleware/GatewayAuthMiddleware.cs` ·
`src/Data/AuditChain.cs` (new) · `src/Data/RediensIamDbContext.cs` ·
`src/Data/Entities/AuditLog.cs` · `src/Data/Configurations/RemainingConfigurations.cs` ·
`src/Data/Migrations/20260731132739_AuditLogHashChain.cs` (new) ·
`tests/…/Infrastructure/KetoStub.cs` ·
`tests/…/Tests/Regression/StructuralDebtTests.cs` (new, 9 tests)

**Not touched:** anything under `src/Controllers/`, `docs/`, `deploy/`, or `src/Program.cs`.

## 5. Suite

```
Passed!  - Failed:     0, Passed:  1305, Skipped:     0, Total:  1305, Duration: 3 m 37 s
```

Two runs were taken. The first showed 14 failures; 12 were the concurrent `src/Controllers/` pass's
in-flight introspection work — confirmed by disabling this pass's default-deny and observing them
persist unchanged — and 2 were the `KetoStub.DenySubject` narrowing in §2.3, fixed here. The final
run is green with no failures from any pass.

## 6. What a follow-up pass should pick up, in order

1. **The three controller edits in §1.4, then the one-line flip.** This is the whole remaining cost
   of S-1 and it is under an hour. Until it lands, S-1 is enforced at runtime and not by the
   compiler, and this document should not be read as saying otherwise.
2. **A caller for `VerifyChainAsync`** (§3.4) — the chain is only worth its cost if something looks.
3. **The Keto ↔ Postgres reconciler** (§2.4) — a day, and a policy decision.
4. **The service-account `GrantedLevel` producer** (§2.3(d) of the review, §1.7 here).
