# 38 — four residual code fixes: SAML tenant scope, the `/api/authorize` fail-open, health-endpoint exception text, SAML ordering

**Date:** 2026-08-01 · **Branch:** `security/hardening-2026-07-30` · **Base:** `eada439` (`v0.2.2`)
**Scope:** `src/Controllers/SamlController.cs`, `src/Controllers/IntrospectionController.cs`,
`src/Controllers/SystemHealthController.cs`, `src/Data/TenantScopeInterceptor.cs`,
`tests/**/Regression/{ApiSurfaceTests,HealthDetailRegressionTests,SamlScopeAndOrderingRegressionTests}.cs`
**Suite:** **1421 integration + 11 SDK passing, 0 failing, 0 skipped** · `dotnet build` **0 warnings**
**Not committed.**

Four items from `docs/SECURITY.md` §8. All four were real; none was already closed. One report claim
in the audit trail turned out to be wrong and is corrected under item 2.

`docs/SECURITY.md` was not edited — the replacement wording for each §8 row is at the bottom.

---

## 1. The SAML ACS is not tenant-scoped

### What was verified

`SamlController` had no reference to `TenantScopeInterceptor` at all: `grep PinScope src/` returned
`AuthController.cs` and nothing else, and `LegitimatelyUnscopedPaths` named the controller as a
whole. So every query a SAML login made — the IdP lookup, the user lookup, JIT provisioning, the
role check, the audit write — ran with `rediensiam.org_id = 'system'`, which is the value the RLS
policies read as *unscoped*. The finding as written is accurate.

**The two entry points do not have the same thing in hand, and the brief's instruction to check that
was the right one.**

| | `GET /auth/saml/start` | `POST /auth/saml/acs` |
|---|---|---|
| challenge in hand before the first read? | **yes** — `hydra.GetLoginRequestAsync` is the first statement in the action | **no** — the challenge arrives in `RelayState` |
| is that challenge a scope source? | yes: `client.metadata.org_id` is server-authored, exactly as `AuthController` uses it | **no.** `RelayState` is browser-controlled and, as `36-saml-destination-config.md` established, not covered by the assertion signature in the assertion-only-signing mode this SP accepts. The controller's own comment at `:72` already says it "cannot be trusted" |
| DB reads needed to know the tenant | **0** | **1** — the `saml_idp_configs` row, which is what determines the scope |

So they are treated separately, and neither invents a mechanism: both call
`TenantScopeInterceptor.PinToOrganisationAsync`, and both pass a value the server read back.

### What changed

`src/Controllers/SamlController.cs`. `TenantScopeInterceptor` injected (eighth constructor
parameter, with a scoped `#pragma warning disable S107` and a one-line reason — the alternative is a
DI aggregate for one service, which relocates the count rather than lowering it; the precedent is
`OrgAdminServices`). Two helpers, `PinScopeAsync` and `EnsureScopedToProjectAsync`, are the same
shape as `AuthController`'s and are deliberately a duplicate of six lines rather than a shared
abstraction extracted across two controllers in one change.

- **`Start`** — pins from `LoginChallengeProject.ResolveOrgOrNull(req)` before it reads anything, so
  even the `saml_idp_configs` row is fetched under the tenant's own scope. The IdP query gained
  `.Include(x => x.Project)` and is followed by `EnsureScopedToProjectAsync(idp.Project)`, which
  pins from the project row when the client carried no `org_id` and otherwise **verifies** the two
  agree, returning `400 project_org_mismatch` when they do not. Byte for byte the login path's
  arrangement.
- **`ParseSamlResponseAsync` (the ACS)** — pins from `idp.Project.OrgId` immediately after the IdP
  row is loaded. One line. Everything downstream runs scoped.

`src/Data/TenantScopeInterceptor.cs`: the `LegitimatelyUnscopedPaths` entry that named the whole
controller is replaced by one naming only the ACS's `saml_idp_configs` read — the read that decides
the scope and therefore cannot run under it, the same honest limit `EnsureScopedToProjectAsync`
already carries on the password path.

### What was deliberately not done

The ACS does **not** additionally cross-check the challenge client's `org_id`. It cannot disagree
with anything: the existing check at `:205` already requires `idp.ProjectId` to equal the
challenge's registered project, so the IdP's project *is* the challenge's project and there is no
second source to compare. Adding the comparison would be a check against itself.

### Tests — `tests/.../Tests/Regression/SamlScopeAndOrderingRegressionTests.cs`

Declared as a part of `SamlControllerTests` to reuse its IdP seeding and Start/response builders.

| Test | What it proves | Red before? |
|---|---|---|
| `Start_ChallengeClientNamingADifferentOrganisation_IsRefused` | the scope really comes from `client.metadata.org_id` and is really enforced: real active IdP, correct project, only the client's registered organisation wrong → `400 project_org_mismatch` | **yes** (was `302`) |
| `SamlLogin_OpensConnectionsUnderTheProjectsOrganisation` | the finding itself, measured rather than asserted — a full Start + ACS login must increment `iam_db_connection_scope_total{scope="org"}`, the counter `TenantScopeInterceptor` writes on every checkout. Before the pin it incremented zero times | **yes** |
| `The_Unscoped_Path_List_Names_Only_The_Read_That_Decides_The_Scope` | the greppable artefact keeps naming the one read that stays unscoped, and no longer claims the controller | **yes** |
| `Start_ChallengeWithNoOrgInClientMetadata_StillWorks` | **lockout guard, and it passes before the change too** — said plainly rather than counted as a regression test. Every project client minted before `org_id` was recorded there carries none, and the new `project_org_mismatch` branch must not turn those into an outage | no, by design |

The metric test is the one that measures the defect directly. RLS cannot be used for this: the
fixture's container runs as its bootstrap superuser, which bypasses row-level security even under
`FORCE`, so asserting isolation through the application's own `DbContext` would assert nothing —
the same reason `LoginScopeRegressionTests` drops to raw Postgres for its policy-level assertions.

---

## 2. `/api/authorize` skips the ownership check when both scopes are absent

### What was verified — and the correction

`IsObjectInScopeAsync` did `if (scope is null) return IsKnownNamespace(ns) || await RefuseAsync();`
— for a caller with no organisation and a subject token naming none, a *known* namespace
(`Organisations`, `Projects`, `UserLists`) passed with no ownership check performed at all. Keto was
then asked the question. Accurate as described.

**`75e9576`'s claim that the path is unreachable does not hold, and the code it cites contradicts
it.** The commit message says a token carrying neither `org_id` nor `project_id` "cannot bind to any
audience and `IsBoundToAudienceAsync` refuses it upstream". `IsBoundToAudienceAsync` has three
disjuncts, and the third is `subject.Audiences.Contains(aud)` — its own docstring says so
explicitly: *"a token whose `project_id` and `org_id` are both blank matches no audience at all
**and can only be introspected by naming an explicit `aud` claim on it**."* The commit's reasoning
holds only for the narrower statement it actually checked: **RediensIAM never mints such a token.**
That much is still true — `grep -rn "audience" src/ -i` finds no `grant_access_token_audience`
anywhere, and the three `CreateOAuth2ClientAsync` call sites do not set one.

But the audience comes from Hydra's introspection response, not from RediensIAM
(`HydraService.cs:331`, `Audiences = body.Aud ?? []`), and Hydra will honour
`grant_access_token_audience` written into its client store by any other route — the `hydra` CLI,
the admin API, a chart. So this is a **narrow live path gated on operator action outside
RediensIAM's own UI**, not dead code. Whether that reaches the bar for "reachable" is a judgement;
what is not a judgement is that the two halves of the fail-open were never both unreachable for the
same reason, and only the `System` half was closed.

### What changed

`src/Controllers/IntrospectionController.cs`:

```csharp
if (scope is null) return await RefuseAsync();
```

The refusal is audited as `api.authorize.object_out_of_scope` like every other one on this surface.
`IsKnownNamespace` had no other caller and was deleted with it — leaving it would have been an
unused private member and a build warning, and its whole job was to be the fail-open's fig leaf.
`IsOwnedByAsync` already answers `false` for an unknown namespace, so nothing else relied on it.

**No decision was needed.** §8 said the remainder "needs a decision about what a deployment-level
caller with an org-less token may legitimately ask". The answer falls out of the question: with no
tenant on either side there is no owner to compare the object against, and *nobody owns this* is not
*you own this*. Refusing is the only answer that is not an assumption.

### Test — `tests/.../Tests/Regression/ApiSurfaceTests.cs`

`Authorize_WithNoTenantOnEitherSide_IsRefusedInsteadOfPassedThrough`, placed beside the `System`-half
test from `75e9576`. A `__system__` service-account gateway; a subject token registered through
`HydraStub.RegisterTokenForClient` with `orgId: null, projectId: null` and an explicit
`aud` — the exact shape `IsBoundToAudienceAsync` admits and nothing else does. Keto is set to
`AllowAll`, so a `true` can only have come from the controller declining to ask the ownership
question. Asserts `allowed == false` and that the refusal left an audit row.

**Red before the change** (`allowed: true`), green after. Unlike its `System`-namespace neighbour,
this one is a genuine regression test and does not need the disclaimer that one carries.

---

## 3. `GET /admin/system/health` returns raw `ex.Message`

### What was verified

Two branches, both as described: `Probe` (`:222`) returned `ex.Message` as the component `detail`
for every HTTP probe — Hydra admin, Hydra public, Keto read, Keto write — and the `Err` helper
(`:245`) did the same for every in-process check — PostgreSQL, Dragonfly, SMTP. The route is
`SuperAdmin`-only, and the SMTP username beside it was already redacted with a comment naming the
reason (*"this response also lands in browser history and audit metadata"*), which is the same
reason that applies to an exception message from a failed connection: a hostname, a port, a DSN
fragment, a certificate subject.

### What changed

`src/Controllers/SystemHealthController.cs`. `ILogger<SystemHealthController>` injected (sixth
parameter, well inside S107). Two stable codes, `probe_failed` and `check_failed`, kept distinct so
an operator can tell an outbound HTTP probe from an in-process check without reading the log. Both
branches now log the exception at Warning with the URL or the component name, and return the code.
`Err` stopped being `static` to reach the logger; `Ok` did not.

### Tests — `tests/.../Tests/Regression/HealthDetailRegressionTests.cs`

Both assert the substance rather than the spelling — that the text the exception carried does not
appear anywhere in the response body.

| Test | What it proves | Red before? |
|---|---|---|
| `Health_ProbeFailure_ReturnsACodeRatherThanTheExceptionText` | `HydraStub.SetHealthFailure()` → `Hydra (admin)` detail is `probe_failed`, and the raw body does not contain `EnsureSuccessStatusCode`'s message | **yes** |
| `Health_CheckFailure_DoesNotEchoTheHostPortOrCertificateInTheException` | an `IEmailService` whose `CheckConnectivityAsync` throws `"Connection to smtp-relay.internal.corp:587 failed: certificate CN=mail.internal.corp not trusted"` → detail is `check_failed` and the body contains none of it | **yes** |

The second test's message is deliberately shaped like a real one, so the test fails for the reason
the finding exists rather than on a string comparison that happens to differ.

---

## 4. SAML pending state consumed before signature validation (I-10)

### What was verified

The order was `ReadSamlResponse` → status → `Destination` → **`GetAndDeletePendingAsync`** →
`Unbind`. `Unbind` is where the signature is validated: per the decompilation recorded in
`36-saml-destination-config.md`, `Saml2Binding.ReadSamlResponse` calls
`Read(..., validate: false, detectReplayedTokens: false)` and `Saml2PostBinding.UnbindInternal`
calls `Read(..., validate: true, detectReplayedTokens: true)`. So the record was spent before
anything about the document had been checked, and any unauthenticated caller who could guess or
replay a request id could destroy a legitimate in-flight login with a document signed by nobody.

**It reorders, and the ITfoxtec facts that make it safe are already established rather than assumed.**
Fact 2 of that report: `Read` re-parses the identical bytes to the identical values, so
`InResponseTo` and `Destination` — both read before the move — come out the same afterwards. Nothing
between the two positions depends on the pending record.

### What changed

`src/Controllers/SamlController.cs`: `httpRequest.Binding.Unbind(...)` moved up to sit immediately
after the `Destination` check and before `GetAndDeletePendingAsync`. Exactly the shape step 36
applied to `Destination`, applied to the signature. No other line moved; the IdP-binding and
challenge-binding checks still follow the consume, because they need the record in hand.

### What is left open, and why — stated rather than papered over

An attacker who controls **any registered active IdP** can still sign a response of their own, name
that IdP in `RelayState`, echo a guessed `InResponseTo` and burn the record: the checks that bind
the response to the IdP and challenge it was *issued* for run after the consume, and they cannot run
before it because they read the record.

Closing that would mean peeking at the record, validating, then deleting — which gives up the atomic
single-use property that stops a valid captured response being redeemed twice under a race. That is
a worse trade: replay of a genuine assertion is a session, the residual denial of service is a
retry. **The residual is narrowed, not eliminated** — from *any unauthenticated caller with any
garbage document* to *a caller holding an active IdP's signing key who has also guessed an
unguessable request id*. This is the same finding, one order of magnitude smaller, and it should be
recorded as still open rather than closed.

### Test

`Acs_ResponseSignedByTheWrongKey_DoesNotConsumeThePendingRequest`. A response that is well-formed,
correctly addressed, echoes the right `InResponseTo` and is signed with a freshly generated
certificate the SP has never been told about → `400 saml_response_invalid`; the genuine response for
the *same* pending request must then still complete with a `302`.

**Red before the change**: the forged response consumed the record, and the real login came back
`400 saml_no_pending_request`. Green after.

---

## Behaviour changes an operator would notice

Three, all on surfaces an operator reads directly.

### A. `GET /admin/system/health` no longer prints the failure reason

**What changes.** A failed component's `detail` is now `probe_failed` (Hydra admin, Hydra public,
Keto read, Keto write) or `check_failed` (PostgreSQL, Dragonfly, SMTP) instead of the exception
message. `status`, `latency_ms` and `stats` are unchanged, `NotConfigured` details are unchanged, and
the admin console renders the new string in the same place with no code change — it treats `detail`
as opaque text (`frontend/admin/src/pages/system/SystemHealth.tsx`).

**What an operator must do differently.** The reason moved to the application log, at **Warning**,
as `System health probe of {Url} failed` or `System health check {Component} failed` with the
exception attached. Anyone who was diagnosing a dependency outage from the health page now needs
`kubectl logs`. That is the trade: the page is `SuperAdmin`-only but its response reaches browser
history and audit metadata, where a DSN fragment or a certificate subject outlives the incident.

### B. `GET /auth/saml/start` can now return `400 project_org_mismatch`

**What changes.** A SAML login start is refused when the OAuth2 client on the login challenge is
registered to a different organisation than the IdP's project belongs to. Previously it proceeded.

**Who could be affected.** Only a deployment whose Hydra client store was edited outside RediensIAM:
every client this application mints writes `project_id` and `org_id` together, from the same row.
Clients registered before `org_id` was recorded there carry no `org_id` at all and are unaffected —
they take the fallback and are pinned from the project row (`Start_ChallengeWithNoOrgInClientMetadata_StillWorks`
holds that shut).

**Symptom if it happens.** `400 {"error":"project_org_mismatch"}` from `/auth/saml/start`. The fix is
to correct the client's `metadata.org_id` in Hydra to the organisation that owns the project.

### C. The SAML login path now runs under its tenant's RLS scope

**What changes.** Queries made by `/auth/saml/*` publish `rediensiam.org_id = <org uuid>` instead of
`'system'`. With **RLS off** (the chart default, and `values.prod.yaml`) this is invisible: the
session setting is written and no policy reads it.

**With RLS on** it is load-bearing, and there is one thing to check: `saml_idp_configs` and every
table the ACS touches after the pin must carry a policy that resolves through to the organisation.
`deploy/rediensiam/files/rls.sql` is outside this work stream and was not modified; a table reachable
from this path with a policy that does not admit its own organisation would present as
`saml_idp_not_found` or `user_not_provisioned` on a login that previously worked. Dev has run RLS on
since `29-rls-prod-tls.md`, so the suite and the dev cluster are the first place this would show.

---

## Suite

```
$ dotnet build RediensIAM.slnx -p:SonarQubeTargetsImported=true
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test RediensIAM.slnx -p:SonarQubeTargetsImported=true
Passed! - Failed: 0, Passed:   11, Skipped: 0, Total:   11 - RediensIAM.Client.Tests.dll (182 ms)
Passed! - Failed: 0, Passed: 1421, Skipped: 0, Total: 1421 - RediensIAM.IntegrationTests.dll (3 m 30 s)
```

**1421 integration tests** — the 1413 baseline plus 8 added here (5 SAML, 2 health, 1 authorize) —
and 11 SDK tests. **0 failures, 0 build warnings.** Both baselines hold.

Red-before-green was verified per item by stashing only the source files for that item and rerunning
the same filter:

| Stashed | Result |
|---|---|
| `IntrospectionController.cs`, `SystemHealthController.cs` | `Failed: 3, Passed: 0` — the three new tests for items 2 and 3 |
| `SamlController.cs`, `TenantScopeInterceptor.cs` | `Failed: 4, Passed: 38` of 42 `SamlControllerTests` — the four new tests for items 1 and 4 that are regression tests; the fifth is the lockout guard named above and passes both ways |

The full suite was run after items 2+3 (1416 passing) and again after items 1+4 (1421 passing), not
once at the end.

The stale `.sonarqube/` at the repo root still requires `-p:SonarQubeTargetsImported=true` on every
invocation. `sonar-scan.sh` was already modified in the working tree before this work began and was
not touched.

---

## Wording for `docs/SECURITY.md` §8

Not applied — `docs/SECURITY.md` was not edited. Three rows are replaced, one is deleted, and the
one-paragraph summary at the top needs one clause changed.

**§8 — replace the `SAML ACS` row with:**

| **SAML ACS pins from the IdP's project row, not from its challenge** | Low | `SamlController.Start` pins from `client.metadata.org_id` at zero reads, exactly as `AuthController` does. The ACS cannot: its challenge arrives in `RelayState`, which is browser-controlled and outside the assertion signature, so it pins from `idp.Project.OrgId` — one read of `saml_idp_configs`, which is the read that decides the scope and is what `LegitimatelyUnscopedPaths` now names | The residue is that one row. Everything after it — user lookup, JIT provisioning, role check, audit write — runs scoped |

**§8 — replace the `/api/authorize` row with:**

| **`/api/authorize` refuses an object it cannot attribute** | — | **Closed.** `IsObjectInScopeAsync` no longer returns true when neither the caller nor the subject token names a tenant; with no owner to compare against it refuses and audits. `75e9576` closed the `System` half and recorded the rest as unreachable — that was wrong: `IsBoundToAudienceAsync`'s third disjunct admits a token with an explicit `aud`, so any Hydra client carrying `grant_access_token_audience` reaches it. RediensIAM mints none, but Hydra honours one written into its store directly | Move to the closed list |

**§8 — replace the `GET /admin/system/health` row with:**

| **`GET /admin/system/health` returns error codes, not exception text** | — | **Closed.** Both branches answer `probe_failed` / `check_failed` and log the exception at Warning server-side. The SMTP username was already redacted | Move to the closed list. Note in §6 or `DEPLOYMENT.md` that diagnosing a dependency outage now means reading the log |

**§8 — replace the `SAML pending state` row with:**

| **SAML pending state is consumed after signature validation, before IdP binding** | Low | `ReadSamlResponse` → status → `Destination` → **`Unbind`** → `GetAndDeletePendingAsync` → IdP/challenge binding. The unauthenticated half of I-10 is closed: a garbage document no longer burns an in-flight login. What remains is that a caller holding **any registered active IdP's signing key** can sign a response, name that IdP in `RelayState` and burn a guessed request id, because the binding checks need the record in hand | Closing it means peek-validate-delete, giving up the atomic single-use that stops a valid captured response being redeemed twice. Replay of a genuine assertion is a session; this residual is a retry. Still requires an unguessable request id |

**§2 / the one-paragraph summary** — this sentence is now wrong:

> The tenant login path now runs under its own organisation's scope rather than unscoped — the admin
> console, the token-keyed endpoints and the SAML ACS still do not, and §2 says why.

should read:

> The tenant login path now runs under its own organisation's scope rather than unscoped, and the
> SAML path with it — the admin console and the token-keyed endpoints still do not, and §2 says why.

**Row count** — §8's opening still says "Four things are known-open and named at the bottom" in the
summary paragraph; two of the four named here move to closed and two remain (narrowed), so whoever
edits that file should recount rather than take this note's arithmetic on faith.
