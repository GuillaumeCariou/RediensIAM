# Step 8 — Authentication and authorisation enhancement

**Branch:** `security/audit-2026-07-28` · **Working tree, not committed**
**Scope:** MFA coverage and enrolment policy, risk-based authentication, session management and
token rotation, RBAC/ABAC least privilege, and **I-06** (deferred here by step 5).
**Suite:** `dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true` —
**1198 passed, 0 failed, 0 skipped, 6 m 16 s** (baseline entering this step: 1185).

> **Build note.** Every command below was run with `-p:SonarQubeTargetsImported=true` to skip the
> stale `.sonarqube/` import at the repo root. Environmental, as step 5 recorded.

Nothing from steps 4, 5 or 6 was redone. **S-1 was not attempted** — step 5's cost analysis still
holds and nothing here changes it. **One behaviour change ships on by default** (`RequireAdminMfa`)
and **two endpoints gain a conditional request field**; both are in §Contract and behaviour changes.
No new wire-contract break in the `ext.roles` / introspection sense.

---

## Summary

| # | Item | Status | Landing point |
|---|---|---|---|
| 1a | MFA re-auth covered replacement/removal but not **addition** | Fixed | `AccountController.HasAnyFactorAsync` + 3 guards |
| 1b | WebAuthn registration asked for `userVerification: discouraged` while the assertion demands `required` | Fixed | `AccountController.WebAuthnRegisterBegin` |
| 1c | No MFA **policy** for the management console | Fixed | `AppConfig.RequireAdminMfa` + `AuthController.AdminLogin` |
| 1d | Step-up for privileged operations beyond MFA mutations | Assessed, not built | §1d |
| 2 | Risk-based authentication | Partly present; gap closed, rest judged a separate project | `CompleteMfaLoginAsync` / `AdminLogin` |
| 3a | Org **suspension** revoked nothing | Fixed | `SystemAdminController.SuspendOrg` |
| 3b | Management **role change** revoked nothing | Fixed | `LiveAuthorizationService.InvalidateAsync` |
| 3c | User **deactivation** revoked nothing | Fixed | `UserHelpers.ApplyUpdate` + both callers |
| 3d | Refresh-token rotation and reuse detection | Verified present (Hydra default), TTLs unset | §3d |
| 4a | Tier escalation super/org/project admin | Verified closed, left alone | §4a |
| 4b | Keto ↔ Postgres divergence on the management-grant paths | Two instances fixed, class still open | `SystemAdminController.AssignOrgAdmin` / `RemoveOrgAdmin` |
| 5 | **I-06** — `docs/ARCHITECTURE.md` stale in four places | Fixed | `docs/ARCHITECTURE.md` |

---

## 1. MFA coverage and enrolment

### Verified already present, left alone

- `RequireReauthAsync` and its four call sites from step 4 (`totp/confirm`, `backup-codes`,
  `DELETE mfa/phone`, `DELETE webauthn/credentials/{id}`) — including the anti-replay on the TOTP
  proof, the `mfareauth` rate-limit key, and the deliberate step-aside for accounts with neither a
  password nor TOTP.
- **`Project.RequireMfa` is a real, enforceable per-project policy**, not self-service: a user with
  no factor gets `{requires_mfa_setup: true}` and enrols through `/auth/mfa/setup/totp/*` before the
  login completes (`AuthController.InitiateMfaAsync`). Enrolment is refusal-free, which is the right
  shape.
- **The WebAuthn *assertion* is complete and correct** (`AuthController.WebAuthnOptions` /
  `WebAuthnVerify`): `UserVerification = Required`, the credential lookup is scoped to the user
  pending MFA (so another account's authenticator cannot satisfy the factor), the ownership callback
  is scoped the same way, and the signature counter is persisted and advanced. Nothing to add.
- Backup codes, TOTP anti-replay, SMS rate limiting, and the session-ID rotation on MFA completion.

### 1a — Adding a factor was unguarded (the R-24 residual)

**The gap.** Step 4 guarded replacing and removing a factor. Adding one was free: a stolen access
token could `POST /account/mfa/webauthn/register/complete` or `POST /account/mfa/phone/verify` and
enrol the attacker's own authenticator *beside* the victim's. That factor then satisfies MFA on
every future login, and — exactly like the takeover step 4 closed — it survives the victim's
remediation, because `ChangePassword` revokes sessions but never touches enrolled factors. The same
hole existed for TOTP on a passkey-protected account, because the guard was `user.TotpEnabled`
rather than "has any factor".

**What changed.** `src/Controllers/AccountController.cs`:

- new `HasAnyFactorAsync(User)` — TOTP, verified phone, or any registered credential;
- `ConfirmTotp`'s condition changed from `user.TotpEnabled` to `await HasAnyFactorAsync(user)`;
- the same guard added to `VerifyPhone` and to `WebAuthnRegisterComplete`.

**Why there.** One predicate, three call sites, next to the existing `RequireReauthAsync` — the
rule is now uniform across all seven factor mutations: *mutating a factor on an account that has one
requires proving one; the first enrolment on an account with none does not.* On the passkey path the
guard runs **before** `GetAndDeletePendingAsync`, so a refusal does not destroy the pending
registration and the user does not have to re-tap their authenticator after the prompt.

Also folded in there: `WebAuthnRegisterComplete` loaded the user twice and tolerated a null on the
second load. It now loads once and 404s.

**Regression tests** (`Tests/Regression/AuthEnhancementRegressionTests.cs`):
`PhoneVerify_OnAnAccountWithAFactor_WithoutReauth_IsRefusedAndNotPersisted` (asserts 401 **and**
that `Phone`/`PhoneVerified` are untouched), `..._WithCurrentPassword_Succeeds`,
`PhoneVerify_AsTheFirstFactor_StillNeedsNoProof`, and
`PasskeyRegistration_OnAnAccountWithAFactor_IsRefusedBeforeTheAttestationIsRead` — which posts a
deliberately invalid attestation and asserts **401 `reauthentication_required` rather than 400
`attestation_failed`**, proving the guard runs first, then retries with the proof and asserts the
pending registration survived.

**Residual risk.** Unchanged from step 4: an account with no password and no TOTP (passkey-only,
social-only) has nothing to re-authenticate against, so `RequireReauthAsync` steps aside for it and
every factor mutation on such an account is still bearer-token-only. Closing it needs a WebAuthn
re-auth assertion — a new flow, not a guard. Still recorded as follow-up.

### 1b — WebAuthn registration was not asking for user verification

**The gap, measured not assumed.** `WebAuthnRegisterBegin` passed `AuthenticatorSelection.Default`.
Running the new options test against the pre-change build prints the options RediensIAM actually
emitted:

```
authenticatorselection:{residentkey:preferred,requireresidentkey:false,userverification:discouraged}
```

`discouraged` — not merely "preferred". Meanwhile the assertion demands `Required`. So the two ends
of the same factor disagreed: a credential enrolled by an authenticator that honours `discouraged`
is a factor the user can never actually use, and one that verifies anyway does so by luck of its own
configuration rather than by policy. As a second factor, possession of the key is not the point.

**What changed.** `UserVerification = UserVerificationRequirement.Required` on registration.
`ResidentKey` is pinned at `Preferred` — the library's own default — deliberately: nothing here
consumes a discoverable credential (the assertion always supplies `allowCredentials` and there is no
passwordless entry point), but platform authenticators create and sync them anyway and forcing
`Discouraged` would degrade passkey enrolment for no security gain.

**Regression test.** `PasskeyRegistrationOptions_DemandUserVerification` — asserts the emitted
options carry `userVerification: required`, written naming-policy-agnostically because the app sets
snake_case globally while Fido2NetLib's `[JsonPropertyName]` attributes emit camelCase. **Confirmed
failing on the pre-change build** (output above is that failure).

**Residual risk.** Credentials registered before this change were enrolled under `discouraged`. If
such an authenticator does not verify the user, its assertions will now fail — the user must
re-register the passkey. That is the correct direction (the credential was not a second factor) but
it is a visible change for existing passkey holders. Nothing sweeps or flags them; an operator
wanting to be proactive should notify users with `webauthn_credentials` rows created before this
release.

### 1c — The management console had no MFA policy

**The gap.** A tenant project can demand a second factor. RediensIAM's own console — where
`super_admin` lives — asked for one only when the account happened to have a factor
(`AdminLogin`: `if (hasMfa) …` and nothing else). The most privileged accounts in the deployment
were password-only by default, and no switch existed to change that.

**What changed.** `AppConfig.RequireAdminMfa` (`Security:RequireAdminMfa`, **default true**), and a
five-line branch in `AuthController.AdminLogin` that mirrors the tenant path: set
`mfa_setup_required`, open the pending-MFA session, and answer `{requires_mfa_setup: true}`.

**Why that is the whole change.** The login SPA already handles `requires_mfa_setup` generically
(`frontend/login/src/pages/Login.tsx`), and `/auth/mfa/setup/totp/start|confirm` already handle an
empty project id — `CompleteMfaLoginAsync` has an explicit `projectId == ""` branch for exactly the
admin case. Nothing else was needed on either side. **The admin is never refused, only enrolled.**

**Regression tests.** `AdminLogin_WithNoFactor_SendsTheAdminThroughEnrolment` asserts
`requires_mfa_setup`, the absence of `redirect_to`, **and** that Hydra's login-accept was never
called. `Tests/Auth/AdminLoginTests` gained
`AdminLogin_SuperAdmin_WithAFactor_IsChallengedForIt`.

**Two existing tests were rewritten to the new contract, not weakened.**
`AdminLogin_SuperAdmin_Returns200WithRedirectTo` asserted that credentials alone complete an admin
login — precisely the behaviour being removed — and is now
`AdminLogin_SuperAdmin_WithNoFactor_RequiresEnrolment`.
`AdminLogin_OrgAdminNotSuperAdmin_Returns200` asserted only `200`, which `requires_mfa_setup` also
is, so it silently stopped testing its own name; it now asserts the enrolment body explicitly and is
renamed `..._PassesTheRoleCheck`. It was the only other test the default affected — one failure
across 1195 cases.

**Residual risk.** With the default on, the `RequireAdminMfa=false` path (credentials → immediate
`redirect_to`) has no test coverage, because the flag is read from process configuration and the
fixture builds one host for the whole collection. The branch is four lines and is the pre-existing
behaviour; noted rather than worked around with a second test host.

### 1d — Step-up beyond MFA mutations: assessed, deliberately not built

The instruction names "an org admin assigning `super_admin`" as the example. That specific operation
**cannot happen** — both assignment paths now refuse it (§4b), and the real `super_admin` grant is
`System:rediensiam#super_admin`, written only by adding a user to the `__system__` list under a
`SuperAdmin` filter. So the named case is closed by a guard rather than by a step-up.

A general step-up (re-prove a factor before *any* `RequireManagementLevel(SuperAdmin)` mutation) is
the right long-term control and is **not** built here. It needs a proof-of-recency signal in the
session — an `amr`/`auth_time` claim, or a server-side "re-authenticated at" record keyed by the
management subject — because the console's bearer token carries no such thing today, and
`RequireReauthAsync` works only on `/account` where the target is the caller themselves.
**What it would take:** put `auth_time` into the admin consent session, expose it on `TokenClaims`,
add a `[RequireRecentAuth(minutes)]` filter that 401s with a challenge the console answers through
the existing `ReauthDialog`, and pick the mutation set it applies to. Roughly a day plus a UX
decision about which operations are worth the friction. Half-building it — a step-up on one endpoint
— would be the "installing a second auth stack" failure the brief warns about.

---

## 2. Risk-based authentication

**What exists, verified.** `AuthController.CheckNewDeviceAsync` is a real new-device signal:
HMAC-SHA256 of `user-agent | /24 subnet` under a dedicated HKDF subkey, remembered in Dragonfly for
`Security:NewDeviceCacheDays` (90), and an email alert on first sight. It is per-user opt-out
(`User.NewDeviceAlertsEnabled`). There is no impossible-travel check, no IP reputation, no device
binding, and no risk-driven step-up: the signal alerts, it does not gate.

**The gap that was worth closing, and was.** The check was called from `CompleteLoginAsync` only —
the tenant password path that completes *without* MFA. Every login that passed a second factor, and
**every admin-console login**, produced no alert at all. That is exactly backwards: the accounts
most worth alerting on were the ones excluded. `CheckNewDeviceAsync` is now called from
`CompleteMfaLoginAsync` (covering TOTP, SMS, backup-code, passkey and enrolment completions, tenant
and admin alike) and from `AdminLogin`'s direct-accept path. Two lines.

While there: both new call sites snapshot `Ip` and the user-agent header into locals **before** the
detached `Task.Run`. The pre-existing call in `CompleteLoginAsync` reads `HttpContext` from inside
the lambda, i.e. after the response may have completed; the new ones do not repeat that.

**Judgement on the rest: it is a larger project, not a hardening item — do not half-build it.**
A credible RBA needs a risk *engine* (signal collection, scoring, thresholds), a per-tenant policy
for what each score does, an outcome channel other than email (step-up challenge, deny, silent
allow), and geo-IP data with the privacy and data-residency consequences that carries for a
multi-tenant IdP under GDPR. It also needs somewhere to put device identity, which today is a
90-day cache entry with no user-visible device list and no "this wasn't me" action. Bolting
impossible-travel onto `CheckNewDeviceAsync` would produce a control that fires on every VPN user,
has no enforcement path, and cannot be tuned per tenant — worse than the honest absence.

**Recommended sequencing if it is wanted:** persist devices as rows (not cache entries) with a
user-facing list and a revoke action; add `auth_time`/`amr` to the token (§1d needs it too); then
add scoring with step-up as the only outcome. Each of those three is independently useful, which is
the test of whether a project is decomposable. It is.

---

## 3. Session management and token rotation

### 3a — Suspending an organisation revoked nothing

`Organisation.Active` is consulted **at login only** (`AuthController` returns
`organisation_suspended` on the login path). Every access token already issued to that org's users
kept working for its full lifetime, at RediensIAM and at every external resource server.
`DeleteOrg` already revoked per user; `SuspendOrg` did not — so the reversible action was the
unsafe one.

**What changed.** `src/Controllers/SystemAdminController.cs` — `SuspendOrg` now calls a new
`RevokeOrgSessionsAsync(orgId)` that revokes `{orgId}:{userId}` for every user in the org's user
lists, best-effort per user so one unreachable subject does not leave the rest signed in. Same shape
as the loop `DeleteOrg` already had.

**Regression test.** `SuspendingAnOrganisation_RevokesEverySessionInIt`.

**Residual risk.** Users in the org who also hold a management role have a *console* session under
the bare `{userId}` subject; that is not revoked here, because it is not scoped to the suspended org
and killing it would sign them out of unrelated organisations. Their management authority is still
live-checked against Keto on every privileged request. Also: `RevokeOrgSessionsAsync` is O(users) in
Hydra calls, done inline. For a very large org the suspend request will be slow. An outbox is the
right answer if that becomes real; it is not today.

### 3b — A revoked management role kept working at every resource server (A / R-22)

Every management-role mutation already called `LiveAuthorizationService.InvalidateAsync(userId)`,
which dropped the 30-second decision cache. That bounds the window **on RediensIAM's own surface
only**. The token still carries the old `ext.roles`, and a resource server validating it locally
against JWKS honours the revoked role until the token expires. Step 2's finding A is precisely this.

**What changed.** `InvalidateAsync` now also revokes the user's management-console Hydra sessions
(subject = the bare user id, which is what `AdminLogin` accepts the login with), wrapped in
try/catch so a Hydra outage logs rather than fails a grant change that is already committed.

**Why there.** All five callers — `OrgController.AssignOrgListManager` /
`UpdateOrgListManager` / `RemoveOrgListManager`, `SystemAdminController.AssignOrgAdmin` /
`RemoveOrgAdmin` — are management-role mutations and every one of them already calls it. Extending
the single method changed zero call sites and cannot be forgotten by the next one. Tenant sessions
(`{orgId}:{userId}`) are deliberately untouched: they carry project roles, not management ones.

A grant also revokes, not just a revocation. That is deliberate and is the smaller half of the
argument: today, granting `org_admin` to a signed-in admin appears not to work until their token
expires, because the claim set is fixed at issuance. Forcing a fresh token is both the security
behaviour and the correct one.

**Regression test.** `RemovingAManagementRole_RevokesTheTargetsConsoleSessions`, using a new
`HydraStub.SessionsRevokedFor(subject)` helper.

**Residual risk.** Revocation is best-effort. If Hydra is unreachable the grant change still commits
and only the cache drop protects this deployment — the fail-open direction, chosen because failing
the request would misreport a committed change. The log line is the detection.

### 3c — Deactivating a user revoked nothing

`User.Active` is also login-only. `UserHelpers.ApplyUpdate` returned "did the password change?" and
both callers revoked on that alone, so `PATCH …/users/{id} {"active": false}` left every session
live.

**What changed.** `ApplyUpdate` now returns `(PasswordChanged, Deactivated)`; both callers
(`SystemAdminController.UpdateUser`, `OrgController.ApplyUserUpdate`) revoke on either and write a
`user.deactivated` audit row on the second. Fixing the shared helper rather than the two call sites
is what makes the next caller correct by default.

**Regression tests.** `DeactivatingAUser_RevokesTheirSessions` (revocation **and** the audit row)
and `ReactivatingAUser_DoesNotRevokeSessions` — the pair is what proves the condition is
"deactivation", not "any `active` field present".

### 3d — Refresh-token rotation and reuse detection: verified, not changed

Hydra owns this and RediensIAM does not override it: `deploy/rediensiam/values.yaml` sets only
`strategies.access_token: jwt` and the serve/CORS block — no `oauth2.refresh_token_rotation`, no
`ttl.*`. So the deployment runs Hydra's defaults, which **do** rotate refresh tokens on use and
invalidate the whole chain when a rotated token is replayed. Rotation and reuse detection are
therefore present, and re-implementing either in `src/` would be exactly the "second auth stack" the
brief warns against.

What is *not* set is any explicit TTL, so access and refresh token lifetimes are Hydra's defaults
rather than a stated policy — and the refresh lifetime is the real bound on §3b's residual, since
`RevokeSessionsAsync` kills the consent session and hence the refresh chain, but an access token
already minted survives to its own `exp`. Setting `ttl.access_token` short and
`ttl.refresh_token` explicitly is a `deploy/` change. **`deploy/` is untouched here — carry to
step 9/10.**

Also verified and left alone: `PatService` invalidates every cached introspection of a service
account by enumerating its PAT hashes from the database (complete, not best-effort), re-checks the
account, the organisation and the PAT expiry on every cache hit, and both service-account role
mutations invalidate. The PAT side of revocation propagation is in good shape.

---

## 4. RBAC / ABAC and least privilege

### 4a — Escalation between the three tiers: traced, found closed, left alone

`ManagementLevel` is `SuperAdmin=1 < OrgAdmin=2 < ProjectAdmin=3`. Every path that grants or acts on
a management role was read:

| Path | Rule | Verdict |
|---|---|---|
| `KetoService.AssignManagementRoleAsync` | `targetRank < actorLevel` → refuse; `ProjectAdmin` actors additionally confined to their own `ScopeId` | closed |
| `OrgController.AssignOrgListManager` | explicit `cannot_grant_super_admin`, then the above | closed |
| `OrgController.UpdateOrgListManager` | reimplements the level check instead of routing through `AssignManagementRoleAsync`, and does **not** re-run `ValidateProjectAdminScopeAsync` | **not reachable** — `OrgController` is `[RequireManagementLevel(OrgAdmin)]`, so no `ProjectAdmin` can call it. Recorded below |
| `SystemAdminController.AssignOrgAdmin` / `RemoveOrgAdmin` | `[RequireManagementLevel(SuperAdmin)]` at class level | closed |
| `ServiceAccountController.ValidateRoleAssignment` | `targetLevel < Level` → refuse; `ProjectAdmin` may assign only `project_admin`, only in its own project | closed |
| `AddUserToList` granting `System#super_admin` for the `__system__` list | `SuperAdmin`-gated | closed |

The tiers themselves are **live-verified**, not claimed: `RequireManagementLevelAttribute` reads the
claimed level and then re-checks *that level* against Keto — `System:rediensiam#super_admin` for
SuperAdmin, `Organisations:{claims.OrgId}#org_admin` for OrgAdmin (org-scoped, correctly). So the
snapshot reads in `ServiceAccountController.Level` and `ProjectController.IsSuperAdmin` are safe
today by virtue of the filter that ran before them — which is S-1's entire point: safe by flow, not
by type. **S-1 remains the standing debt and was not attempted;** step 5's cost analysis is
unchanged by anything in this step.

Two observations recorded rather than changed:

- `LiveAuthorizationService`'s `ProjectAdmin` branch falls back to
  `db.OrgRoles.Any(r => r.UserId == userId && r.Role == project_admin)` with **no org and no project
  scope** — "project_admin somewhere" satisfies "project_admin here". It is not an escalation today
  because per-project scoping is done correctly by each controller and because a pure `project_admin`
  token carries `org_id = ""`, but it is S-8's "two authorities for one question" verbatim and it is
  where a future controller will get it wrong.
- Because `GetConsent` derives `org_id` from the caller's first **`org_admin`** row, a user who is
  only `project_admin` gets `org_id = ""` and therefore fails `ProjectController`'s
  `p.OrgId == CallerOrgId` scoping for every project. Fail-closed, so not a security item, but the
  `project_admin` tier appears to be functionally unusable on `/project/*`. Flagged for whoever owns
  that behaviour; not changed here, because "make project_admin work" is a feature decision, not a
  hardening one.

### 4b — Keto ↔ Postgres divergence on the management-grant paths

Step 3 flagged the dual-write with a best-effort compensating delete and no reconciler. Two concrete
instances were fixed; the class is not closed.

**`POST /admin/organizations/{id}/admins` accepted `role: "super_admin"`.** It wrote
`Organisations:{orgId}#super_admin@user:X` — a tuple **no policy anywhere reads**, since every
super-admin check is against `System:rediensiam` — plus an `org_roles` row that resolves to nothing.
The console then listed a `super_admin` who had no `super_admin`. Fail-closed, but it is a grant
that reads as real and is not, which is the worst kind of authorisation state to have in a UI.
`/org/admins` already refused it; this endpoint now does too (`403 cannot_grant_super_admin`), so
no `org_roles` row with `Role = 'super_admin'` can be created through the API at all.

**Write ordering was inverted on both endpoints of that pair.** `AssignOrgAdmin` committed the DB
row and *then* wrote the tuple (a failing Keto write left a committed grant with no tuple);
`RemoveOrgAdmin` deleted the row and *then* the tuple (a failing Keto delete left the **tuple**
alive with no row naming it — R-01's "admin of some org that no longer exists" orphan, and the
fail-**open** direction). Both now do the Keto side first, with a compensating tuple delete on the
assign path, matching the order `KetoService.AssignManagementRoleAsync` already established. Every
failure mode is now "row without tuple" — the grant stops working and the row is the evidence for
cleanup.

**Regression tests.** `AssigningSuperAdminAsAnOrgRole_IsRefused` (403 **and** no row written) and
`AssigningAnOrgRole_WhenKetoRefusesTheTuple_LeavesNoGrantBehind`, using a new
`KetoStub.FailTupleWrites()` helper.

**Residual risk — the class is still open.** There is still no reconciler and no outbox. A crash
between the tuple write and the row write, on any of the four dual-write sites
(`AssignProjectRoleAsync`, `AssignDefaultRoleAsync`, `AssignManagementRoleAsync`, `AssignOrgAdmin`),
still leaves a tuple with no row — the compensating delete only covers a *thrown* exception, not a
killed process. Ordering makes every *handled* failure fail closed, which is the cheap half. The
expensive half is S-8: pick one authority per question, make the other a projection, and add a
periodic reconciler that reports tuples with no backing row. **What it would take:** a background
service enumerating `Organisations`/`Projects` tuples against `org_roles`/`user_project_roles` and
emitting an audit row per orphan — a day, plus a decision on whether it deletes or only reports.
Not attempted here; it is a component, not a guard.

---

## 5. I-06 — `docs/ARCHITECTURE.md` (resolved)

Step 5 deferred this as a docs item. The document was stale in **four** places, three of them
under-claiming and one over-claiming a weakness that no longer exists:

1. The "Gap — read before relying on principle 3" banner still said
   `RequireManagementLevelAttribute` authorises purely from `ext.roles` and that
   `KetoService.CheckAsync` is not on that path. Replaced with what the code does — claimed level,
   then live Keto re-check, 30-second cache — **and** with the boundary of that guarantee: it does
   not extend to a resource server validating the JWT locally, which is why role changes and org
   suspensions now revoke sessions (§3). The banner's two dangling references to files that do not
   exist in this tree went with it.
2. The PAT cache line said the cache is "**not** invalidated on SA deactivate or org suspend".
   `PatService.IntrospectAsync` re-checks both on every cache hit. Corrected.
3. The MFA section said WebAuthn uses `UserVerification = Preferred, not Required` and that "the
   assertion is also not bound server-side to the user pending MFA (SEC-05)". The first was wrong in
   a new way after §1b, the second has been false since the assertion was scoped to `uid`. Both
   corrected, and the section gained the enrolment-policy and factor-mutation rules.
4. The test-count line (`1093 tests… 1059 pass; the 34 in Tests/Regression/ fail by design`) has
   been wrong since step 4. Updated to this step's real number.

The "Security controls — quick reference" table also gained the four controls it never listed: the
live Keto re-check, session-revocation propagation, MFA re-authentication, and the new-device alert.

**Residual risk.** The document is still maintained by hand, which is the root cause I-06 names and
which no edit fixes. Step 3's advice — regenerate it from the code — stands.

---

## Contract and behaviour changes

Neither of these is a break of the `ext.roles` / introspection kind. They are flagged separately
because the brief requires it, and because both are visible to a client.

### A. `Security:RequireAdminMfa` defaults to **on** — a deliberate behaviour change

An administrator with no second factor no longer completes a console login on a password alone. They
receive `200 {"requires_mfa_setup": true}` and the login SPA takes them through TOTP enrolment
before the login finishes. **Nobody is locked out**; the enrolment path is the one tenant projects
have used since before this audit, and it is exercised by the suite.

Set `Security__RequireAdminMfa=false` to restore the previous behaviour. It is a security regression
and should be temporary.

Who this affects: any automation that scripts `POST /auth/login` against `client_admin_system`. The
product ships none — the console is interactive — but a deployment with its own tooling must check.
**Step 10 should not need to set anything**; the default is the intended production posture.

### B. Two `/account` endpoints gain a conditional `reauth` field

| Endpoint | New requirement |
|---|---|
| `POST /account/mfa/phone/verify` | `{"code": …, "reauth": {"current_password": …}}` **when the account already has any MFA factor** |
| `POST /account/mfa/webauthn/register/complete` | `{"response": …, "device_name": …, "reauth": {…}}` under the same condition |

`POST /account/mfa/totp/confirm` did not change shape, but its guard widened from "has TOTP" to
"has any factor", so an account with only a passkey now needs the proof it previously did not.

Refusal is the same `401 {"error":"reauthentication_required","methods":[…]}` step 4 introduced, so
a client that already handles it for the other four endpoints needs no new error handling.

**The admin console was updated in the same change**, unlike step 4 — `frontend/admin/src/api.ts`
(`verifyPhone`, `completeWebAuthnRegistration` take an optional `reauth`) and
`AccountPage.tsx` (both flows wrapped in the existing `useReauth().guard`, which omits the proof
first and prompts only if the backend asks). `npm run build` in `frontend/admin` succeeds. The login
SPA is unaffected — it does not manage factors.

---

## Deliberately left, with cost

1. **S-1 — `GrantedLevel` + default-deny.** Unchanged from step 5's analysis: it is an async
   plumbing change through six controllers, including EF expression trees that cannot contain an
   `await`. §4a re-confirms it is the right change and that nothing in this step is a fragment of
   it. Estimate remains a day plus a full test pass, as its own commit.
2. **A general step-up for privileged mutations (§1d).** Needs an `auth_time`/recency signal that
   does not exist in the admin token. Cost and shape written out in §1d.
3. **Risk-based authentication beyond the new-device signal (§2).** Judged a separate project, with
   a three-step decomposition given. Not half-built, deliberately.
4. **A Keto/Postgres reconciler (§4b).** The ordering fixes make handled failures fail closed; the
   crash window and the missing reconciler are S-8 and remain open.
5. **`LiveAuthorizationService`'s unscoped `project_admin` DB fallback (§4a).** Removing it is
   S-8's first half and needs the missing tuples written by migration. Not a surgical change.
6. **Token TTLs and refresh-token policy in Hydra's chart (§3d).** `deploy/` belongs to steps 9/10.
7. **Passkey-only / social-only accounts can still mutate factors with a bearer token alone.**
   Step 4's residual, unchanged; needs a WebAuthn re-auth assertion flow.
8. **Pre-existing passkeys registered under `userVerification: discouraged` (§1b).** No sweep, no
   operator notification. An operator can find them by `created_at`.

---

## Files changed

**Backend**
`src/Config/AppConfig.cs` (`RequireAdminMfa`),
`src/Controllers/AccountController.cs` (`HasAnyFactorAsync` + 3 guards, WebAuthn `UserVerification`),
`src/Controllers/AuthController.cs` (admin MFA policy, new-device on every login path),
`src/Controllers/OrgController.cs` (revoke on deactivation),
`src/Controllers/SystemAdminController.cs` (`RevokeOrgSessionsAsync`, `super_admin` refusal,
dual-write ordering, revoke on deactivation),
`src/Controllers/UserHelpers.cs` (`ApplyUpdate` reports deactivation),
`src/Services/LiveAuthorizationService.cs` (revoke console sessions on role change)

**Frontend**
`frontend/admin/src/api.ts`, `frontend/admin/src/pages/account/AccountPage.tsx`

**Tests**
`tests/…/Tests/Regression/AuthEnhancementRegressionTests.cs` *(new, 12 cases)*,
`tests/…/Infrastructure/HydraStub.cs` (`SessionsRevokedFor`),
`tests/…/Infrastructure/KetoStub.cs` (`FailTupleWrites`),
`tests/…/Tests/Auth/AdminLoginTests.cs` (two tests rewritten to the new contract, one added)

**Docs**
`docs/ARCHITECTURE.md` (I-06)

No file under `deploy/` was modified.

---

## Test output

```
dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
    -p:SonarQubeTargetsImported=true --nologo

Passed!  - Failed:     0, Passed:  1198, Skipped:     0, Total:  1198, Duration: 6 m 16 s
         - RediensIAM.IntegrationTests.dll (net10.0)
```

Baseline entering this step: 1185. The 13 net new cases are the 12 in
`AuthEnhancementRegressionTests` plus `AdminLogin_SuperAdmin_WithAFactor_IsChallengedForIt`; no
existing test was removed and none was weakened — the two rewritten `AdminLoginTests` cases assert
more than they did, not less (see §1c).

Frontend: `npm run build` in `frontend/admin` — ✓ built in 906 ms. The login SPA and the SDKs were
not touched.

**Intermediate run, recorded because it is the evidence for §1c's blast-radius claim.** The full
suite was run once with `RequireAdminMfa` defaulted on and no test updates:
`Failed: 1, Passed: 1194, Total: 1195` — the single failure being
`AdminLoginTests.AdminLogin_SuperAdmin_Returns200WithRedirectTo`, i.e. exactly the one assertion
that encoded "credentials alone complete an admin login".
