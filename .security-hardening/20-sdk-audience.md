# Step 20 — SDK audience binding: closing the P-06 follow-up

**Branch:** `security/hardening-2026-07-30` · **Working tree, not committed**
**Scope:** `sdk/`, `docs/INTEGRATION.md`, `sdk/README.md`, `RediensIAM.slnx` (one line, to register a new test project)
**Closes:** the blocking follow-up flagged in [`19-api-surface.md`](19-api-surface.md) §1 "Not done" — *"No SDK was updated… this is a required follow-up and it is the largest cost left in this item."*

> **This is a breaking SDK change on top of a breaking server change.** Both halves are documented
> together in `sdk/README.md` under **"`aud` is now a required SDK option"**, because an integrator
> needs to see the whole migration in one place and neither half works alone.

---

## 0. Summary

| SDK | Change | Verified |
|---|---|---|
| `sdk/dotnet/RediensIAM.Client` | `RediensIamOptions.Audience`, required; `ver` enforced on both endpoints | build + **11 tests, new project** |
| `sdk/rust/rediensiam-client` | `Config::audience`, required; `ver` enforced on both endpoints | **12 tests** + 1 doctest |
| `sdk/typescript/rediensiam-web` | **No code change** — verified it never calls either endpoint | 12 tests + `tsc --noEmit` |

---

## 1. Did the server actually do what report 19 said?

I read `src/Controllers/IntrospectionController.cs` rather than trusting §1. **It matches**, on
every point the SDKs depend on:

| §1 claim | Code | Verdict |
|---|---|---|
| `aud` required on `/api/introspect` | `if (string.IsNullOrWhiteSpace(body.Aud)) return BadRequest(…)` | ✅ |
| `aud` required on `/api/authorize` | same guard, line 134 | ✅ |
| 400 body is `{"error":"audience_required","ver":1}` | `new { error = "audience_required", ver = ContractVersion }` | ✅ |
| `ver` on **every** answer including `{"active":false}` | `IntrospectionResult.Inactive = new(false)` — `Ver` is a defaulted positional parameter, so it is `1` | ✅ |
| `ver` on `{"allowed":false}` | `AuthorizationResult.Denied = new(false, null)`, same mechanism | ✅ |
| Bound when `aud` == `project_id`, == `org_id`, or ∈ OAuth2 audiences | `IsBoundToAudienceAsync` | ✅ |
| `aud` echoed on an active answer only | `Aud: body.Aud` on the active path; `Inactive` leaves it null | ✅ |

**One nuance §1 does not mention, and integrators can trip on it.** The two comparisons inside
`IsBoundToAudienceAsync` do not use the same string comparer:

```csharp
bool Matches(string? value) =>
    !string.IsNullOrEmpty(value) && value.Equals(aud, StringComparison.OrdinalIgnoreCase);
// …
|| subject.Audiences.Contains(aud, StringComparer.Ordinal)
```

Matching against `project_id` / `org_id` is **case-insensitive**; matching against the token's
OAuth2 `aud` claim list is **case-sensitive**. For the normal path this is invisible — both ids
are GUIDs and either comparer accepts either casing. It only bites the third path: a token
carrying a hand-minted, non-GUID `aud` claim must be matched byte-for-byte. Not a defect, and not
worth changing server-side from here, but it is the difference between "the id you configured"
working and "the audience string you configured" working. Noted rather than fixed — `src/` is out
of this step's scope.

No other discrepancy. §1 is accurate.

---

## 2. The option, per SDK

The audience is the resource server's **own identity** — the project id it fronts, or the
organisation id if it fronts a whole organisation.

| | C# | Rust |
|---|---|---|
| Option | `RediensIamOptions.Audience` | `Config::audience` |
| Type | `string`, `""` default | `String`, empty default |
| Required | yes | yes |
| Missing → | `ArgumentException` | `Error::Config` |
| Fails at | **construction** | **construction** |

### Why required, with no default

A default would be a guess about which tenant the service belongs to, and a wrong guess is P-06
under a new name: a deployment-scoped service-account credential resolving every tenant's token as
`active: true`. There is no value that is safe to assume, so there is no default.

### Why at construction

Consistent with the R-30 https check already in both SDKs — same place, same shape. A resource
server with no declared tenant is a deployment mistake, and it should stop the process at startup
with a message naming the fix, not turn into a 400 on every request once traffic arrives.

**C# — this required restructuring one class.** `RediensIamClient` used a primary constructor,
which has no body to validate in. It is now an explicit constructor, and the three checks that
previously lived inline in `AddRediensIam` moved to `RediensIamOptions.Validated()`, which both the
client constructor and `AddRediensIam` call. Net effect:

- `new RediensIamClient(...)` now validates. Previously **only** the DI path did, so anyone
  constructing the client directly got no https check either. That was a pre-existing hole in
  R-30's coverage and it is closed as a side effect.
- `AddRediensIam` lost ~18 lines of duplicated validation.

**Rust** needed no restructuring — `RediensIamClient::new` already returned `Result` and already
validated `base_url` and `service_account_token`. One more check alongside them.

### What now goes on the wire

```
POST /api/introspect   token=…&token_type_hint=access_token&aud=<project-or-org-id>
POST /api/authorize    {"token":…,"namespace":…,"object":…,"relation":…,"aud":"<…>"}
```

---

## 3. `ver` handling

Both SDKs refuse any answer whose `ver` is absent or below 1.

| | C# | Rust |
|---|---|---|
| Constant | `RediensIamClient.RequiredContractVersion` (public) | `rediensiam_client::CONTRACT_VERSION` (public) |
| Failure | `InvalidOperationException`, message contains `ver=0, expected at least 1` | `Error::ServerTooOld { found: 0 }` |
| Applied to | `IntrospectAsync`, `AuthorizeAsync` | `introspect`, `authorize` |

**This is the part that makes sending `aud` worth anything.** An un-upgraded RediensIAM does not
reject the unknown `aud` form field — it silently discards it and answers exactly as it always
did. An SDK that only *sent* the audience could not distinguish an enforcing server from an
ignoring one and would report success while bound to nothing. `ver` is the only evidence the field
was read. Requiring it converts a silent wrong answer into a loud refusal.

Two supporting changes fell out of this:

- **An empty response body is now a fault, not an inactive token.** The .NET client previously did
  `?? TokenInfo.Inactive` on a null deserialisation, and `result?.Allowed ?? false` on authorize.
  Both now throw. A broken server saying nothing is not the same statement as "this token is
  dead", and the old code turned one into the other.
- **Fail-closed downstream is already correct.** `RediensIamAuthenticationHandler` catches every
  exception from `IntrospectAsync` and returns `AuthenticateResult.Fail` — so a downgraded server
  denies requests rather than admitting them. Verified by reading, not assumed.

New public wire fields on both `TokenInfo` types: `aud` (echo) and `ver`.

---

## 4. The browser SDK: checked, and deliberately unchanged

The task said to check whether `rediensiam-web` calls those endpoints, and that it should not.
**It does not.** `grep` over `sdk/typescript/rediensiam-web/src/` finds exactly one occurrence of
the word "introspect" in the whole SDK, and it is the doc comment explaining why the SDK doesn't
do it. There is no `/api/introspect` or `/api/authorize` call, so there is no audience to declare.

That is not an omission to fix later — it is the security property. Both endpoints require a
service-account credential, and a credential shipped to a browser belongs to anyone who opens
devtools. Adding an audience option here would imply a call that must never exist.

Code change: **one doc-comment paragraph**, stating why the mandatory `aud` does not reach this
SDK, so the next person doesn't read the silence as an oversight. No functional change, no new
test — a test asserting the absence of a method it never had would assert nothing.

---

## 5. Tests and what each proves

### Rust — `sdk/rust/rediensiam-client/src/lib.rs`, 5 new (12 total)

The three wire tests run a **one-shot loopback TCP listener** and assert on the bytes the client
actually wrote. This is deliberate: a mock handed to the client proves the client was configured,
not that the field left the process — and since an old server ignores a missing `aud` in silence,
reading the request is the only real evidence.

| Test | Proves |
|---|---|
| `audience_is_required_at_construction` | `new` returns `Error::Config` mentioning "audience"; the same config *with* an audience is `Ok` |
| `introspect_sends_the_audience` | `aud=proj-1` is in the form body on the wire |
| `authorize_sends_the_audience` | `"aud":"proj-1"` is in the JSON body on the wire |
| `an_answer_without_ver_is_refused` | `{"active":true}` (no `ver`) → `Error::ServerTooOld { found: 0 }`, not a trusted active answer |
| `an_authorize_answer_without_ver_is_refused` | `{"allowed":true}` (no `ver`) → refused, so a downgrade cannot manufacture an allow |

Two pre-existing tests were updated to supply an audience (they construct `Config` and would
otherwise now fail) via a shared `config()` helper.

One incidental finding: `RediensIamClient` has no `Debug` impl, so `expect_err` does not compile
on `Result<RediensIamClient, _>`. That is **correct and was left alone** — a derived `Debug` would
print `service_account_token`. The test uses `let Err(err) = … else { panic!(…) }` instead, with a
comment saying why.

### C# — `sdk/dotnet/RediensIAM.Client.Tests/AudienceBindingTests.cs`, new project, 11 tests

No test project existed for the .NET SDK. One was added and registered in `RediensIAM.slnx`.

| Test | Proves |
|---|---|
| `Introspect_sends_the_audience` | `aud=proj-1` in the form body a stub `HttpMessageHandler` received |
| `Authorize_sends_the_audience` | `"aud":"proj-1"` in the JSON body |
| `Client_without_an_audience_cannot_be_constructed` | direct `new RediensIamClient(...)` throws `ArgumentException` naming `Audience` |
| `Registration_without_an_audience_fails_at_startup` | the DI path `AddRediensIam` throws too — both entry points, not just the documented one |
| `BaseUrl_must_be_https_except_on_loopback` (×5) | R-30 survived the move into `Validated()` — the regression guard for the refactor in §2 |
| `Introspection_answer_without_ver_is_refused` | `{"active":true}` → `InvalidOperationException` |
| `Authorization_answer_without_ver_is_refused` | `{"allowed":true}` → `InvalidOperationException` |

### None of these are tautological

Each asserts on an artefact that disappears if the change is reverted: delete the `aud` pair from
the request and the `Contains` assertions fail; delete `RequireContract` / `require_contract` and
the refusal tests get a success where they demand an error.

---

## 6. Actual build and test output

**Rust** — `cd sdk/rust/rediensiam-client && cargo test`

```
running 12 tests
test tests::cache_key_is_a_sha256_digest ... ok
test tests::inactive_has_no_roles ... ok
test tests::cache_key_is_not_the_token ... ok
test tests::audience_is_required_at_construction ... ok
test tests::role_and_tenant_helpers ... ok
test tests::requires_base_url_and_token ... ok
test tests::base_url_must_be_https_except_on_loopback ... ok
test tests::tenant_roles_do_not_match_across_projects ... ok
test tests::authorize_sends_the_audience ... ok
test tests::an_answer_without_ver_is_refused ... ok
test tests::an_authorize_answer_without_ver_is_refused ... ok
test tests::introspect_sends_the_audience ... ok

test result: ok. 12 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out

   Doc-tests rediensiam_client
test src/lib.rs - (line 7) - compile ... ok
test result: ok. 1 passed; 0 failed
```

No warnings. The doctest passing matters: it compiles the `Config { … }` literal in the crate
header, so the documented usage cannot drift from the required option.

**TypeScript** — `cd sdk/typescript/rediensiam-web && npm test`

```
ℹ tests 12
ℹ pass 12
ℹ fail 0
```

`npm run typecheck` (`tsc -p tsconfig.json --noEmit`) — no output, exit 0.

> `npx tsc` fails in this repo with *"This is not the tsc command you are looking for"* because
> `node_modules` was absent; `npm install` (1 package, `typescript`, 0 vulnerabilities) fixes it and
> `npm run typecheck` is the script that works. The generated `package-lock.json` was **deleted**
> afterwards — the repo does not track one and `node_modules/` is already gitignored, so nothing
> was left behind.

**.NET** — `cd sdk/dotnet/RediensIAM.Client && dotnet build -p:SonarQubeTargetsImported=true`

```
  RediensIAM.Client -> …/bin/Debug/net10.0/RediensIAM.Client.dll

Build succeeded.
    4 Warning(s)
    0 Error(s)
```

The 4 warnings are pre-existing and unrelated — `NU1510` twice each for
`Microsoft.Extensions.Caching.Memory` and `Microsoft.Extensions.Http` ("will not be pruned"),
emitted once at restore and once at build. Not introduced here.

`cd sdk/dotnet/RediensIAM.Client.Tests && dotnet test -p:SonarQubeTargetsImported=true`

```
Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11, Duration: 119 ms
```

`dotnet sln RediensIAM.slnx list` confirms the solution still parses with the new project.

---

## 7. The combined migration note

Written into **`sdk/README.md` → "`aud` is now a required SDK option"**, linked from the top of
that file and from `docs/INTEGRATION.md`. It carries both halves: what the server broke, what the
SDK broke, and the ordering between them.

The ordering is the part integrators get wrong, so it is stated explicitly:

1. **Upgrade the SDK and set the option.** One line per service.
2. **Deploy your services, then the server** — in that order. An old server ignores the `aud` a
   new SDK sends, so there is no window of 400s; a new server rejects an old SDK immediately.
3. **Nothing to do in the browser.**

Step 2 has a wrinkle report 19 could not have: because the new SDK *requires* `ver`, an upgraded
SDK pointed at an un-upgraded server **refuses every answer**. That is correct — it is the client
failing closed rather than believing it is bound — but it means the intermediate state is safe to
sit in only in the sense that it denies rather than leaks. The README says so plainly: do not run
that combination for longer than the rollout takes.

A symptom table maps each failure back to the step that was skipped:

| Symptom | Cause |
|---|---|
| Throws at startup naming `Audience` / `audience` | Step 1 not done for that service |
| `400 audience_required` | Un-upgraded SDK, or raw-HTTP caller, against an upgraded server |
| `ver=0, expected at least 1` | Upgraded SDK against an un-upgraded server — step 2 out of order |
| `{"active": false}` on a token you know is good | The configured `aud` names a different tenant than the token belongs to |

The last row is the quiet failure, and it is quiet on purpose: an audience mismatch is
indistinguishable from expired or revoked, because the endpoint must not confirm that another
tenant's token exists.

**`docs/INTEGRATION.md`** had a warning block saying the shipped SDKs do not send `aud` and telling
integrators to drop to raw HTTP. That block is now **false** and was replaced with the current
state plus a link to the migration. Its migration list also gained a line saying steps 2 and 3 are
handled for you if you use an SDK.

---

## 8. Left, with its cost

**One audience per client. A gateway fronting several tenants needs several clients.**
Report 19 §1 allows for a caller that "genuinely serves several" tenants and sends the right one
per request. The SDKs do not support that: the audience is bound at construction, which is exactly
what makes the missing-audience failure a startup failure. Such a gateway must build one client
per tenant. **Cost:** N clients, N caches, the same credential in each — workable, and the caches
being separate is arguably right since entries are audience-scoped answers. **Upgrade path:** a
per-call overload (`IntrospectAsync(token, audience, ct)` / `introspect_with_audience`) if a real
multi-tenant gateway appears. Not added now: it is speculative API surface, and the required
option is what the task specified and what every single-tenant caller needs.

**C# signals a downgraded server with `InvalidOperationException`, Rust with a typed variant.**
Rust callers can `matches!(err, Error::ServerTooOld { .. })`; C# callers must match on the message
to distinguish it from other faults. **Cost:** asymmetry between the two SDKs, and a C# caller who
wants to alert specifically on "server too old" cannot do it cleanly. **Upgrade path:** a
`RediensIamException` type. Not added: no caller today branches on it, and every consumer that
matters — the ASP.NET handler — already treats all exceptions as fail-closed, which is the
behaviour that counts.

**A new .NET test project now exists and the solution builds it.** `RediensIAM.Client.Tests` is
registered in `RediensIAM.slnx`, so any CI step that builds or tests the solution picks it up.
**Cost:** one more project in the build graph (xunit + `Microsoft.NET.Test.Sdk`, all already in the
local NuGet cache — restore worked offline). Unavoidable: the .NET SDK had no test harness at all,
and the three required proofs cannot be made without one.

**The server-side casing nuance in §1 is documented here, not fixed.** See §1. `src/` is out of
scope, and it only affects hand-minted non-GUID `aud` claims.

**Not attempted:** minting `aud` into tokens at Hydra. That is the S-2 residual proper, it lives in
`deploy/`, and report 19 §1 already assigns it there.
