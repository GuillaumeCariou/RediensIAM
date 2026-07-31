# Step 19 — API surface: audience binding, authorize scoping, MFA downgrade guard, management parity

**Branch:** `security/hardening-2026-07-30` · **Working tree, not committed**
**Scope:** `src/Controllers/`, `docs/INTEGRATION.md`, `tests/`, `frontend/login/src/lib/sanitizeCss.ts`
**Findings addressed:** P-06 (5.0), P-05 (4.3), ledger §9 items 11 and 12, `typescript:S5852` ×4,
plus the product decision on `require_mfa` and the `/api/manage` parity gap.

> **Three wire-contract breaks in this step**, all on the resource-server surface. They are in
> §7. `aud` becoming mandatory on `/api/introspect` and `/api/authorize` is the fourth break of
> this release and the loudest one: an integration that does not send it stops working, by
> design.

---

## 0. Summary

| Item | Verdict |
|---|---|
| **P-06** — no audience binding | **Closed.** `aud` mandatory on both endpoints, `ver: 1` on every answer |
| **P-05** — `/api/authorize` object unscoped | **Closed.** Object scoped to the answer's tenant; unknown namespaces fail closed |
| **MFA disable guard** | **Implemented.** 409 + count, `confirm_mfa_downgrade` second call, audited. One shared guard across all four write paths |
| **`/api/manage` parity** | **Complete.** Not by adding endpoints — by deleting the duplicate controller and giving `SystemAdminController` a second route prefix. 7 endpoints → the whole `/admin` surface |
| **ReDoS in `sanitizeCss.ts`** | **3 of 4 exploitable; all 4 removed.** Measured: 4 KB of `type password ` took the old regex **69 seconds** |

---

## 1. P-06 — audience binding (5.0 Medium)

### What the finding was

`IntrospectionController` scoped an answer by *the caller's* organisation only
(`CallerOrgScope`). A service account on the `__system__` user list carries no `org_id` and so
stayed deliberately unscoped — and that is precisely the credential a multi-tenant gateway has to
hold. So one deployment-scoped gateway credential resolved **every** tenant's token in the
deployment as `active: true`, and the resource server behind it was expected to compare
`project_id` against its own configuration afterwards.

Nothing enforced that. No SDK had a field for it (`RediensIamOptions` and the Rust `Config` both
lack an expected-audience). `HasProjectRole` makes *role* checks safe by construction; nothing
made the *tenant* check safe by construction, so the safe path was not the default path.

### The design

The resource server declares, per request, which tenant it serves.

| Field | Where | Meaning |
|---|---|---|
| `aud` | request, **required** | the project id this resource server serves, or the organisation id if it fronts a whole organisation |
| `aud` | response | echo, on an active answer |
| `ver` | response | `1` — always present, including on `{"active": false}` and on the 400 |

A token is bound to `aud` when the value equals its `project_id`, equals its `org_id`, or appears
in its OAuth2 `aud` claim (`IsBoundToAudienceAsync`, `src/Controllers/IntrospectionController.cs`).
Both id forms are accepted because a service-account PAT carries no project id at all — the org
id is the only tenant it has. The comparison is fail-closed on emptiness: a token with neither id
matches no audience and can only be resolved by naming an explicit `aud` claim minted onto it.

A missing `aud` is **400 `{"error": "audience_required", "ver": 1}`**, not `{"active": false}`.
That is a deliberate departure from the RFC 7662 no-oracle rule elsewhere in this controller: a
missing parameter is a defect in the caller's own request, not a statement about anyone's token,
and answering "inactive" would let an un-migrated integration keep running while believing it had
merely been handed a dead token. **A resource server that declares no audience stops being
served.** That is stated as plainly as I could manage in `docs/INTEGRATION.md`.

An audience mismatch **is** `{"active": false}` — indistinguishable from expired or revoked, and
audited as `api.introspect.audience_mismatch` / `api.authorize.audience_mismatch`.

### Why `ver`

`ver` is the anti-downgrade signal, and it is the reason this is worth more than a convention. A
RediensIAM that has *not* been upgraded silently discards the unknown `aud` form field and
answers exactly as before. A client that only sends `aud` therefore cannot tell an enforcing
server from an ignoring one — it would believe it was bound when it was not. Requiring `ver >= 1`
in the response makes that failure closed rather than silent.

### Migration

Documented in full under **"`aud` is mandatory — read this before you upgrade"** in
`docs/INTEGRATION.md`. Short form:

1. Each caller of `/api/introspect` and `/api/authorize` serves exactly one tenant — put that
   tenant's id in its configuration. A caller that serves several already knows which one each
   request is for.
2. Add `aud` to the request and **deploy the callers before the server**. The old server ignores
   the unknown field, so sending it early is safe and there is no window of 400s.
3. Assert `ver >= 1` on the response.

**Failure symptoms to expect:** `400 audience_required` (you skipped step 2), or — quieter and
worse — `{"active": false}` on a token you know is good, which means the `aud` you configured
names a different tenant than the token belongs to.

### Not done

- **No SDK was updated.** `sdk/dotnet`, `sdk/rust` and the browser SDK all still call
  `/api/introspect` without `aud` and will break against an upgraded server. `sdk/` is outside
  this step's scope. **This is a required follow-up and it is the largest cost left in this
  item** — see §8.
- `aud` is enforced at the introspection surface, not minted into tokens. Hydra still only sets
  the `aud` claim when asked. Making `aud` a *token* claim (rather than a request parameter) is
  the S-2 residual proper and is a Hydra-configuration change in `deploy/`.

---

## 2. P-05 — `/api/authorize` object scoping (4.3 Medium)

### What changed

`IsObjectInScopeAsync` (`src/Controllers/IntrospectionController.cs`) now runs before Keto is
asked. The scope is the same one introspection already uses — the caller's organisation — falling
back, for a deployment-level caller, to the organisation of the token being asked about (which
the audience binding above has just pinned to one tenant). That fallback is where P-05 and P-06
meet: without an audience there is no tenant to scope the object to.

| `namespace` | Check |
|---|---|
| `Organisations` | object id == scope |
| `Projects` | `Projects.Any(p => p.Id == object && p.OrgId == scope)` |
| `UserLists` | `UserLists.Any(l => l.Id == object && l.OrgId == scope)` |
| `System` | already refused for a scoped caller (pre-existing) |
| anything else | **refused** |

Refusals answer `{"allowed": false}` — the same shape as a genuine "no", so the endpoint cannot
be used to probe which objects exist — and write `api.authorize.object_out_of_scope`.

### The one judgement call

**Unknown namespaces now fail closed.** A namespace this deployment writes no objects into has no
ownership to check, and failing open there would reopen the finding under a new name. If a
deployment has added its own Keto namespaces and asks about them through `/api/authorize` with an
org-scoped credential, those calls now answer `false`. I could find no such namespace in this
repository (`Roles` defines exactly four and the app writes only those), but it is a behaviour
change a downstream deployment could feel, and it is called out in `docs/INTEGRATION.md`.

Note this is enumeration, not decision forgery: the subject of an authorize check is always the
presented token's user. The 4.3 rating was right. What it bought an attacker was one bit per
request about another tenant's relation graph.

---

## 3. MFA disable guard

### The flow

`PATCH … {"require_mfa": false}` on a project whose stored `RequireMfa` is `true` **and** whose
assigned user list contains at least one user holding a factor:

```json
409 {
  "error": "mfa_downgrade_requires_confirmation",
  "enrolled_user_count": 42,
  "consequence": "Disabling require_mfa stops enrolled second factors from gating logins for 42 user(s) in this project. Their factors are not deleted, but a stolen password alone becomes sufficient to sign in. Users are not notified.",
  "confirm_with": "confirm_mfa_downgrade"
}
```

Nothing is applied — not the MFA change and not the rest of the body. The guard runs before any
field is written.

Repeat with `{"require_mfa": false, "confirm_mfa_downgrade": true}` → the update proceeds and
`AuditLogService` records `project.mfa_requirement_removed` with `enrolled_user_count` in the
metadata.

### The confirmation contract, and why it is shaped that way

- **Field name:** `confirm_mfa_downgrade`, boolean, in the request body. The 409 names it in
  `confirm_with` so a client does not have to have read the docs to recover.
- **In the body, not a header or query flag.** It travels in the same payload as the change it
  authorises, so it cannot be replayed onto a different request or left switched on in a client's
  default header set.
- **Not a nonce or a token.** A challenge/response would need server-side state and an expiry for
  a control whose threat is "an operator did not realise", not "an attacker forged a
  confirmation" — an attacker who can PATCH the project can also send `true`. The value here is
  the interruption and the audit row, and a boolean delivers both. `# ponytail: plain boolean, no
  server-side challenge — upgrade to a signed nonce only if the threat becomes a confused-deputy
  one rather than an operator-error one.`
- **`enrolled_user_count` counts** users of the project's assigned user list who hold a TOTP
  secret, a verified phone, **or** a WebAuthn credential — the same three factors
  `AccountController.HasAnyFactorAsync` counts, so the number quoted is the number of people
  actually protected. Count `0` → no confirmation asked for, because there is nothing to
  downgrade. No assigned user list → count `0`.
- **Enabling stays unguarded.** It is the safe direction. The opt-in default at project creation
  was **not** changed.

### Where it lives — and why that mattered

`src/Controllers/MfaDowngradeGuard.cs`, one static function, called from **all four** write paths
that reach `Project.RequireMfa`:

| Path | Handler |
|---|---|
| `PATCH /admin/projects/{id}` | `SystemAdminController.AdminUpdateProject` |
| `PATCH /api/manage/projects/{id}` | *same action* (see §4) |
| `PATCH /org/projects/{id}` | `OrgController.UpdateProject` |
| `PATCH /project/info` | `ProjectController.UpdateInfo` |

The ticket named the setting, not a route. Grepping for `RequireMfa =` found three assignment
sites; a guard on the one route someone happened to test is not a guard. `ConfirmMfaDowngrade`
was added to `AdminUpdateProjectRequest`, `UpdateProjectRequest` and `UpdateProjectInfoRequest`
as an optional trailing parameter, so no existing caller's body changes shape.

---

## 4. `/api/manage` parity

### What I did instead of adding 50 endpoints

`ManagedApiController` and `SystemAdminController` had **identical class-level authorisation** —
both `[RequireManagementLevel(ManagementLevel.SuperAdmin)]`, which is one filter that reads the
token level *and* re-checks it live against Keto. The only real difference was the route prefix
and the fact that seven handlers had been copy-pasted.

So: **`SystemAdminController` now carries `[Route("admin")]` and `[Route("api/manage")]`, and
`ManagedApiController.cs` is deleted.**

Every `/admin/x` is reachable at `/api/manage/x`, on the same action, through the same filter
instance, writing the same audit rows. There is no duplicated handler to review, because there is
no duplicated handler. The brief's requirement — *"every endpoint you add must route through the
same live authorisation path as its `/admin` twin"* — is satisfied by construction rather than by
review, which is the only version of that requirement I trust.

### The endpoint table

Because the routes are literally the same action, a per-endpoint table would be a table of
`x → x`. What follows is every route now newly reachable at `/api/manage`, and the action it
shares. **None of these is a new handler; all are the pre-existing `/admin` handler.**

| `/api/manage/…` | Shared action on `SystemAdminController` |
|---|---|
| `GET key-rotation` | `KeyRotationStatus` |
| `POST key-rotation/reencrypt` | `KeyRotationReEncrypt` |
| `PATCH organizations/{id}` | `UpdateOrg` |
| `POST organizations/{id}/suspend` | `SuspendOrg` |
| `POST organizations/{id}/unsuspend` | `UnsuspendOrg` |
| `DELETE organizations/{id}` | `DeleteOrg` |
| `GET users`, `GET users/{id}`, `PATCH users/{id}` | `ListUsers`, `GetUser`, `UpdateUser` |
| `POST users/{id}/unlock` | `UnlockUser` |
| `GET users/{id}/sessions`, `DELETE users/{id}/sessions` | `GetUserSessions`, `RevokeUserSessions` |
| `GET userlists`, `GET userlists/{id}`, `GET userlists/{id}/users` | `ListAllUserLists`, `GetUserList`, `ListUsersInList` |
| `DELETE userlists/{id}/users/{uid}` | `RemoveUserFromList` |
| `GET organizations/{id}/admins`, `POST …/admins`, `DELETE …/admins/{roleId}` | `ListOrgAdmins`, `AssignOrgAdmin`, `RemoveOrgAdmin` |
| `GET projects` | `AdminListAllProjects` |
| `PATCH projects/{id}` | `AdminUpdateProject` |
| `DELETE projects/{id}` | `AdminDeleteProject` |
| `GET projects/{id}/scopes`, `PUT projects/{id}/scopes` | `AdminGetProjectScopes`, `AdminUpdateProjectScopes` |
| `PUT projects/{id}/userlist`, `DELETE projects/{id}/userlist` | `AdminAssignUserList`, `AdminUnassignUserList` |
| `GET projects/{id}/stats` | `AdminGetProjectStats` |
| `GET projects/{id}/roles`, `POST projects/{id}/roles`, `DELETE projects/{id}/roles/{rid}` | `AdminListRoles`, `AdminCreateRole`, `AdminDeleteRole` |
| `GET email/overview` | `GetEmailOverview` |
| `GET|PUT|DELETE organizations/{id}/smtp`, `POST organizations/{id}/smtp/test` | `GetOrgSmtp`, `UpsertOrgSmtp`, `DeleteOrgSmtp`, `TestOrgSmtp` |
| `GET audit-log`, `GET audit-log/export` | `GetAuditLog`, `ExportAuditLog` |
| `GET metrics` | `GetMetrics` |
| `GET|POST hydra/clients`, `GET|DELETE hydra/clients/{id}` | `ListHydraClients`, `CreateHydraClient`, `GetHydraClient`, `DeleteHydraClient` |
| `GET organizations/{id}/export/users`, `GET organizations/{id}/export/audit-log` | the export actions |
| `GET|POST projects/{id}/saml-providers`, `PATCH|DELETE projects/{projectId}/saml-providers/{providerId}` | the SAML provider actions |
| `…/system/*` — every route of `SystemHealthController` | same actions, second prefix on that class |
| `…/webhooks/*` — every route of `AdminWebhookController` | same actions, second prefix on that class |

Two controllers besides `SystemAdminController` served `/admin/*` and were both already
`[RequireManagementLevel(SuperAdmin)]`: `SystemHealthController` (`admin/system`) and
`AdminWebhookController` (`admin/webhooks`). Both got the same treatment — a second `[Route]`, no
new handler. **I missed both on the first pass** and the routing-table parity test caught them;
webhooks are named explicitly in the brief as an `/admin`-only gap.

The seven routes `/api/manage` already had keep their paths, bodies and status codes. One new
route was added to `SystemAdminController` because only the deleted controller had it:
`GET organizations/{id}/projects` → `AdminListOrgProjects`.

### Two bugs the fold exposed

Folding two copies together forced a diff of them, and the copies had drifted. Both gaps were on
the `/admin` side, and both are now fixed there:

1. **`POST /admin/userlists/{id}/users` accepted a duplicate email** in the same user list. Only
   the `ManagedApi` copy had the uniqueness check. Two rows with the same email in one list is a
   login lookup that cannot tell them apart. Now 409 `email_already_exists` on both prefixes.
2. **`POST /admin/organizations/{id}/projects` never checked the organisation existed.** Only the
   `ManagedApi` copy did. Now 404 on both.

### Not covered by parity

- **Service accounts and PATs are not aliased.** `ServiceAccountController` is
  `[Route("service-accounts")]` at **ProjectAdmin** level, not under `/admin` at all, and is
  *already* reachable by a machine credential — it is not gated on an interactive user token and
  it accepts a PAT. It also runs its own per-object `CanAccessAsync` authorisation, which is
  exactly the kind of thing a route-prefix change must not be allowed to skip past. **I
  deliberately did not re-route it.** The parity gap the brief describes — "can create a tenant
  but cannot manage one" — was about the `/admin`-only surface, and that gap is closed. If a
  caller wants `/api/manage/service-accounts` as a *name*, that is a cosmetic alias and should be
  argued for on its own rather than smuggled in with an authorisation-sensitive controller.
  (Webhooks were on the `/admin`-only list and **are** now mirrored — see the table above.)
- **`/org/*` is not mirrored.** It is a lower privilege tier with a different filter
  (`OrgAdmin`). A SuperAdmin machine credential can already do everything `/org` can, on
  `/api/manage`.

---

## 5. ReDoS in `frontend/login/src/lib/sanitizeCss.ts`

### Assessment per regex — measured, not estimated

I ran each flagged pattern in isolation on Node 24 against the input shape that defeats it. Times
are for a single `replaceAll`.

| Line | Pattern | Shape | 1 KB | 2 KB | 4 KB | Growth | Verdict |
|---|---|---|---|---|---|---|---|
| **29** | `[^{}]*type[^{}]*password[^{}]*\{[^{}]*\}` | `type password ` ×n | 291 ms | 4 435 ms | **69 372 ms** | ×16 per doubling (quartic) | **Exploitable, catastrophic** |
| **30** | `[^{}]*password[^{}]*type[^{}]*\{[^{}]*\}` | `password type ` ×n | 308 ms | 4 374 ms | **69 624 ms** | ×16 per doubling | **Exploitable, catastrophic** |
| **32** | `[^{}]*attr\s*\([^)]*\)[^{}]*\{[^{}]*\}` | `attr()` ×n | 37 ms | 281 ms | **2 206 ms** | ×8 per doubling (cubic) | **Exploitable** |
| **25** | `@[a-zA-Z-]+[^;{]*(?:;\|\{[^{}]*(?:\{…\}[^{}]*)*\})` | `@a` ×n | 1 ms | 2 ms | **6 ms** | ~×3 (quadratic, small constant) | **Not meaningfully exploitable** |

Reading these honestly:

- **Lines 29 and 30 are the real finding.** Three unanchored `[^{}]*` runs with two literals
  between them, on input containing no `{`, make the engine enumerate every placement of `type`
  and `password` from every start position. `LoginThemeValidator` caps `custom_css` at 20 000
  characters, which is **five times** the 4 KB that already took 69 seconds — extrapolating the
  measured ×16-per-doubling gives hours, not seconds. The payload contains no `(`, no `@`, no
  `\`, no `/*` and no `<`, so **it passes the server-side validator cleanly**. A tenant admin
  writes one theme; every user of that tenant who loads the login page freezes their browser's
  main thread on a page that has not authenticated anybody yet. That is the whole DoS.
- **Line 32 is exploitable** on the same reasoning with a smaller exponent. Its payload does
  contain `(`, which `ForbiddenValueChars` refuses in *other* theme keys but which
  `ValidateCss` refuses only as part of `attr(` / `url(` / `image-set(` / `expression(` — so
  `attr(` specifically is caught by the server today. It is reachable through a theme persisted
  **before** the validator existed (served verbatim; the validator runs on write, not on read)
  and through any regression in it — which is this file's stated reason to exist.
- **Line 25 is not a DoS on its own.** Quadratic with a tiny constant: ~150 ms extrapolated at
  the 20 KB cap. Noticeable jank on an uncapped legacy theme, nothing more. I fixed it because
  the rewrite removed it for free, not because it was dangerous. Saying otherwise would be
  inflating the finding.

Two facts that bound all four: the 20 000-character server cap applies only to themes written
*through* the validator, and the validator refuses `@` outright — so line 25's payload cannot be
stored today at all, and lines 29/30's payload can.

### The fix

All four regexes are gone. Rules 3, 5 and 6 are now one left-to-right scan (`dropUnsafeRules` +
`blockEnd`) using `indexOf`, which cannot backtrack, so the cost is bounded by the input length
whatever shape the input has. Brace-less at-rules (`@import …;`) are swept by
`/@[^;{}]*;?/g`, which always consumes at least the `@` and has an optional tail, so it can
never backtrack either.

Three things the rewrite also fixed or preserved, checked against the old behaviour:

- **Nesting is followed to the matching brace.** A rule filter that stopped at the first `}`
  would have dropped `@media … {` and then left `.a { … }` behind as a top-level rule — a
  sanitiser that stops sanitising. `blockEnd` counts depth. Verified against
  `@media screen { .a{…} .b{…} } .keep{…}` → only `.keep` survives.
- **`attr(` detection was widened.** The old pattern only looked for `attr(` *before* the `{`,
  i.e. in the selector — so `.x { content: attr(value) }`, which is the usual shape of the
  attack, went straight through. The scan tests the whole rule.
- **`url()` neutralisation moved ahead of the rule filter**, so a `url()` inside a rule that
  survives the filter is still rewritten to `url(about:blank)`.

### Verification

Measured from the TypeScript source directly (`node --experimental-strip-types`), 11 behaviour
assertions and 5 timing probes:

```
OK   "input[type=password]{background:url(http://evil/)}" -> ""
OK   "@import url(http://evil/);"                         -> ""
OK   "@media screen { .a { color: red } }"                -> ""
OK   "@media screen { .a{…} .b{…} } .keep{color:green}"   -> " .keep{color:green}"
OK   ".btn { color: red }"                                -> ".btn { color: red }"
OK   ".x { content: attr(value) }"                        -> ""
OK   "input[type=\"password\"] { background: var(--surface) }" -> ""
OK   "div[password][type] { color: red }"                 -> ""
OK   ".a{background:url(https://evil/?x)}"                -> ".a{background:url(about:blank)}"
OK   "/* input[type=password]{x} */ .ok{color:blue}"      -> " .ok{color:blue}"
OK   ".a{…} input[type=password]{…} .b{…}"                -> ".a{…} .b{…}"
type/password 20KB  -> 0 ms      (was: extrapolated hours)
attr( 20KB          -> 0 ms      (was: ~275 s extrapolated)
@a 20KB             -> 0 ms
mixed 200KB         -> 0 ms
nested braces 200KB -> 2 ms
ALL BEHAVIOUR CHECKS PASS
```

**No vitest file was added.** `frontend/login` has `vitest` as a devDependency but no `test`
script and no existing test files, and this step's frontend scope was exactly one file — adding a
test runner configuration would have meant editing `package.json`. The checks above were run
against the shipped source, not a copy, but they live in a scratchpad and are not in the
repository. **This is a real gap and it is listed in §8.**

### Not fixed

`sanitizeCss`'s own header claims rule 5 covers hex-escaped selectors ("covers …, hex-escaped,
…"). It does not, and did not before this change either: step 2 replaces `\74 ` with a **space**
rather than decoding it to `t`, so `input[\74 ype=password]` becomes `input[ ype=password]` and
no longer contains the word `type`. The rule then does not fire. This is fail-*open* in the
sanitiser but the construct is refused outright server-side (`css_escapes_not_allowed` on any
backslash), so the composed control holds. I left the behaviour alone rather than redesign the
escape handling inside a ReDoS ticket; the misleading comment is worth a follow-up.

---

## 6. Test results

```
$ dotnet test -p:SonarQubeTargetsImported=true --nologo
Passed!  - Failed:     0, Passed:  1305, Skipped:     0, Total:  1305, Duration: 3 m 29 s
```

Baseline at the start of this step was **1274**. I added **22**; the other nine are the
concurrent structural agent's, landed in the same tree.

New tests, all in `tests/RediensIAM.IntegrationTests/Tests/Regression/ApiSurfaceTests.cs`:

| Class | Tests | Covers |
|---|---|---|
| `ApiSurfaceIntrospectionTests` | 8 | `aud` required on both endpoints (400 + `ver`), system gateway resolves only the audience it declares, org-id audience accepted, mismatch audited; object out of tenant refused while Keto says allow, system-gateway object scoped to the subject's tenant, unknown namespace fails closed |
| `ApiSurfaceMfaDowngradeTests` | 6 | 409 with count/consequence/`confirm_with` and nothing applied; confirmed call proceeds and audits; zero enrolled users is unguarded; enabling is unguarded; the `/org` path is guarded identically; the `/api/manage` path is guarded identically |
| `ApiSurfaceManagedParityTests` | 8 | every `/admin` controller action has an `/api/manage` twin on the same action; non-SuperAdmin refused on newly reachable routes (3 cases); unauthenticated refused; full tenant lifecycle driven from `/api/manage`; mutations write audit rows; duplicate-email refused on both prefixes |

Existing tests updated for the breaking contract (not new tests — the same assertions, now
sending `aud`): `Tests/Api/IntrospectionTests.cs` (8 call sites),
`Tests/Regression/PentestFindingsTests.cs` (2), `Tests/Regression/BackendHardeningRegressionTests.cs` (4).

`BackendHardeningRegressionTests.Introspect_FromASystemServiceAccount_IsStillUnscoped` kept its
assertion but its meaning narrowed, and I amended its doc comment to say so: "unscoped" now means
*any tenant it names*, not *every tenant at once*.

### Two things the parity test caught that I had missed

Writing the routing-table assertion was worth more than the endpoints it covers, because it
failed twice on routes I had not thought about:

1. **`/admin/config`** — the minimal-API bootstrap the admin SPA fetches before it has a token.
   Not a controller action, not a management endpoint; excluded from the assertion with the
   reason written down.
2. **`/admin/system/health` and `/admin/webhooks`** — two *other* controllers
   (`SystemHealthController`, `AdminWebhookController`), both already
   `[RequireManagementLevel(SuperAdmin)]`. Webhooks are named in the brief as an `/admin`-only
   gap and I had missed them entirely by only looking at `SystemAdminController`. Both now carry
   the second route prefix on the same terms.

Frontend: `npx tsc --noEmit` and `npx eslint src/lib/sanitizeCss.ts` both clean.

---

## 7. Wire-contract changes

Three, all on the resource-server surface, all breaking.

### 7.1 `aud` is required on `POST /api/introspect`

- **Before:** `token` (form) was the only field.
- **After:** `token` **and** `aud`. Missing → `400 {"error":"audience_required","ver":1}`.
- **Who breaks:** every current caller, including all three shipped SDKs.
- **Migration:** add `aud` to callers first (old server ignores it), then upgrade the server.
- **Response additions:** `aud` (echo, active answers only) and `ver: 1` (always).

### 7.2 `aud` is required on `POST /api/authorize`

Same terms. Missing → 400.

### 7.3 `object` is tenant-scoped on `POST /api/authorize`

- **Before:** `object` reached Keto unchecked.
- **After:** must belong to the tenant the answer is about. Unknown Keto namespaces are refused.
- **Who breaks:** a caller asking about objects outside its own organisation — which was the
  finding — and any deployment using custom Keto namespaces through this endpoint.
- **Shape of the refusal:** `{"allowed": false}`, indistinguishable from a genuine "no".

### Not breaking

- **`confirm_mfa_downgrade`** is additive and only read on a true→false transition with enrolled
  users. Existing PATCH bodies are unaffected.
- **`/api/manage`'s seven original routes** keep their paths, bodies and status codes.
- **Two `/admin` routes became stricter** (409 on duplicate email, 404 on missing organisation).
  Strictly speaking a behaviour change; both were bugs and both directions close a gap.

---

## 8. Left undone, with cost

| # | Item | Why it is not done | Cost |
|---|---|---|---|
| 1 | **All three SDKs still omit `aud`** (`sdk/dotnet`, `sdk/rust`, browser). They will 400 against an upgraded server | `sdk/` is outside this step's scope | ~0.5 d. **This is the blocking follow-up** — the server change is not shippable without it |
| 2 | **`ManagedApiServices` is now unused.** `src/Services/ControllerServices.cs:99` and its registration at `src/Program.cs:123` | `src/Services/` and `src/Program.cs` belong to other agents this round | 2 lines, deletion only |
| 3 | **No vitest file for `sanitizeCss`.** The checks in §5 are real but live in a scratchpad | Adding a test runner means editing `frontend/login/package.json`, outside the one-file frontend scope | ~1 h: add `"test": "vitest run"` and the 11 assertions above |
| 4 | **`aud` is a request parameter, not a token claim.** Hydra still only mints `aud` on request | The S-2 residual proper is a Hydra configuration change in `deploy/` | ~0.5 d |
| 5 | **`sanitizeCss` header comment claims hex-escaped selectors are covered.** They are not (§5) | Redesigning escape handling inside a ReDoS ticket is scope creep; the server refuses backslashes outright so the composed control holds | ~1 h to either decode properly or correct the comment |
| 6 | **`/service-accounts` is not aliased under `/api/manage`** | `ServiceAccountController` is `/service-accounts` at **ProjectAdmin**, not under `/admin`, and already machine-credential reachable. It runs its own per-object `CanAccessAsync` that a prefix change must not bypass. (Webhooks *were* an `/admin`-only gap and **are** now on `/api/manage` — see §4) | Cosmetic. Argue for it separately |
| 7 | **Unknown Keto namespaces now fail closed on `/api/authorize`** | Deliberate. Failing open is the finding under a new name | Documented; revisit only if a deployment reports a custom namespace |

### Required changes in other agents' files

None. Nothing in this step needed an edit to `src/Services/`, `src/Config/`, `src/Filters/`,
`src/Middleware/`, `src/Data/` or `deploy/`. Item 2 above is a cleanup, not a dependency.

---

## 9. Honesty notes

- **The parity claim rests on one property**, asserted in
  `ApiSurfaceManagedParityTests.EveryAdminRoute_HasAManagedTwinOnTheSameAction`: every `/admin/*`
  route in the application's own routing table has an `/api/manage/*` twin resolving to the same
  action. That is a stronger statement than fifty hand-written endpoint tests, and it cannot pass
  if anyone reintroduces a duplicated handler. The 403-for-non-SuperAdmin half is sampled rather
  than exhaustive **because the filter is one instance on one class** — it is not possible for it
  to differ per route. If you disagree with that reasoning, the exhaustive version is a `[Theory]`
  over the same route table and costs an hour.
- **P-06's fix does not make the *token* audience-bound**, only the introspection *answer*. A
  resource server that validates JWTs locally against JWKS still sees no `aud` and is unaffected
  by any of this. That path was already the discouraged one for other reasons, but the finding is
  only closed for callers who introspect.
- **Line 25's ReDoS is not exploitable** and I have said so rather than counting four fixes as
  four vulnerabilities.
- The 69-second measurement in §5 is a single `replaceAll` at 4 KB on this machine. The
  extrapolation to the 20 KB cap is arithmetic on the observed growth rate, not a measurement — I
  did not sit through the 20 KB run.
