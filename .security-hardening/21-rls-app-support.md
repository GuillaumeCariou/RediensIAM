# Step 21 — the application half of RLS and cache TLS (step 18 items A-1 … A-4)

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Scope:** `src/` and `tests/` only
**Spec:** `.security-hardening/18-cnpg-tls-rls.md` §4
**Suite:** 1305 → **1335 passing, 0 failing, 0 skipped** (30 new)
**Not committed.** `deploy/`, repo-root scripts, `docs/`, `sdk/` and `frontend/` were not touched.

---

## Summary

| # | Item | Outcome |
|---|---|---|
| A-1 | `SET rediensiam.org_id` on connection open | **Done.** `src/Data/TenantScopeInterceptor.cs`, wired into the app's `DbContext` and into the pre-DI configuration provider |
| A-2 | Never `No Reset On Close=true` | **Done, as a startup failure rather than a prohibition.** `AppConfig.ConnectionString` also refuses `Multiplexing=true`, which breaks the same invariant and step 18 did not name |
| A-3 | Dragonfly TLS | **Application side done and pinned to the cluster CA. Still off, because it needs one chart change (a volume mount) that is outside this scope.** The callback is not a `return true` |
| A-4 | `IgnoreQueryFilters()` ↔ `'system'` alignment | **Done, and it is currently vacuous.** The model declares no global query filter, so there is nothing to bypass; a test pins that so it cannot stop being true silently |

Everything here is **inert until `postgres.rls.enabled` is turned on**, which was not done and is not this
scope's to do. `set_config` on a database with no policies is a no-op costing one round trip per
connection checkout.

---

## A-1 — the session scope

### What changed

**`src/Data/TenantScopeInterceptor.cs`** (new) — a `DbConnectionInterceptor` overriding
`ConnectionOpenedAsync` and `ConnectionOpened`. Wired in `src/Program.cs`:

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantScopeInterceptor>();
builder.Services.AddDbContext<RediensIamDbContext>((sp, options) =>
    options.UseNpgsql(appConfig.ConnectionString)
           .AddInterceptors(sp.GetRequiredService<TenantScopeInterceptor>()),
    ServiceLifetime.Scoped);
```

(`AddHttpContextAccessor()` moved up from line 115; it was already registered, it just now has to
exist before the `DbContext` registration reads for it.)

The statement issued is:

```sql
SELECT set_config('rediensiam.org_id', $1, false)
```

Three decisions inside that one line, each of which would have been a defect the other way:

1. **`is_local => false` is `SET`, not `SET LOCAL`.** Step 18 is explicit and it is the trap worth
   restating: most EF reads run outside an explicit transaction, where `SET LOCAL` does not error —
   it has no effect. That produces a control that returns zero rows in production while passing any
   test that happens to open a transaction. `TenantScopeInterceptorTests.Scope_Survives_Into_The_
   Transaction_EF_Opens_For_SaveChanges` asserts the value **outside** a transaction first, which is
   the assertion `SET LOCAL` fails.

2. **`set_config` with a bound parameter, not `SET … = '<value>'`.** The scope can only ever be the
   literal `system` or `Guid.ToString()`, so concatenation would have been safe today. It would not
   have been safe against the refactor that starts taking the value from somewhere else. There is a
   test that feeds `system'; DROP TABLE users; --` through the claim and checks `public.users` is
   still there.

3. **On connection open, not on `DbContext` construction.** Npgsql calls `Open()` on every rent from
   the pool, so this fires once per checkout — including for the connections EF opens itself for
   `SaveChanges` transactions and for migrations.

### How the scope is decided

```csharp
var claims = httpContextAccessor.HttpContext?.GetClaims();
return claims is not null && Guid.TryParse(claims.OrgId, out var orgId) && orgId != Guid.Empty
    ? orgId.ToString()
    : SystemScope;   // "system"
```

`GetClaims()` reads `HttpContext.Items["Claims"]`, which is what `GatewayAuthMiddleware` writes after
it has validated the token *and* passed the audience gate and the default-deny gate. So the scope is
never derived from an unvalidated token.

`Guid.Empty` maps to `'system'` rather than to a scope. That is deliberate: the `Guid.Empty`/`null`
conflation is the bug class the codebase already documents at `ServiceAccountController.cs:29-33`,
and mapping it to a real scope would make the all-zero UUID a tenant.

### The second `DbContext`

`src/Config/InstanceConfiguration.cs` builds its own `RediensIamDbContext` before DI exists, and it is
where `Migrate()` runs for the first time. It now carries the interceptor as well
(`new TenantScopeInterceptor(new HttpContextAccessor())` — no request is in flight there, so the scope
is `'system'`).

This is not cosmetic. DDL is unaffected by RLS, but a future migration that **backfills data** would
otherwise write to tenant tables on a connection with no `rediensiam.org_id`. Under fail-closed
policies that migration does not error — it updates zero rows and reports success.

`src/Data/RediensIamDbContextFactory.cs` is untouched: it is `IDesignTimeDbContextFactory`, used by
`dotnet ef`, never by the running application.

---

## The honest limit — the paths that legitimately run unscoped

Step 18 §3 item 4 says it plainly and this report will not soften it: **login looks a user up by
e-mail before any tenant is known.** RLS protects tenant-scoped API traffic. It does not make the
login path tenant-safe, and a substantial share of requests legitimately run as `'system'`.

The list lives in code, as a greppable artefact, at
`TenantScopeInterceptor.LegitimatelyUnscopedPaths` — the same pattern as
`GatewayAuthMiddleware.SelfGatedControllers`, and for the same reason: a reviewer can audit one
array, not two hundred remembered facts.

**Before authentication — no token exists, so no organisation is known:**

| Path | Why |
|---|---|
| `AuthController` login (`LookupUserByCredentialsAsync`, `AuthController.cs:255-268`) | user found by `UserListId + Email`; the `Projects` row that supplies `AssignedUserListId` must itself be read unscoped |
| `AuthController` registration, password reset, e-mail verification | same shape — the caller is identified by an e-mail or a token, not by a tenant |
| `AuthController` / `SamlController` social and SAML callbacks | the remote IdP names a subject, not an organisation |
| `GatewayAuthMiddleware` → `PatService.IntrospectAsync` | a PAT is found by hash. This one runs *inside* the middleware, i.e. before `Items["Claims"]` is set, so it is unscoped by construction |

**Deployment-wide work that is not any one tenant's:**

| Path | Why |
|---|---|
| `Program.EnsureDbSchemaAsync` | EF migrations |
| `Program.BootstrapSuperAdminAsync` (`Program.cs:295-326`) | creates the `__system__` user list, whose `OrgId IS NULL` makes it invisible to every tenant scope by design |
| `InstanceConfigurationProvider` | the `instances` table — deployment-global, listed in `rls.sql` as such, no policy |
| `AuditLogRetentionService` (`AuditLogRetentionService.cs:35-54`) | enumerates every organisation, then sweeps the `OrgId IS NULL` rows |
| `WebhookDispatcherService` | drains one queue for all tenants |

**Cross-tenant by design, gated by authorisation rather than by scope:**

| Path | Why |
|---|---|
| `SystemAdminController` | SuperAdmin listings across organisations. Gated by `[RequireManagementLevel]` and a live Keto check, not by RLS |

**What this means for reporting S-5.** Anyone writing "S-5 closed" should write the sentence
underneath it: *RLS covers authenticated, org-bearing API traffic; the authentication surface itself
is protected by the application's own checks and by nothing at the database layer.* The control is
real and it is partial.

---

## A-2 — the connection reset

### The current DSN

Checked. `deploy/rediensiam/values.dev.yaml` / the gitignored secrets file produce a DSN of the form
`Host=…;Database=…;Username=iam_app;Password=…;SSL Mode=Require;Trust Server Certificate=true`.
**Neither `No Reset On Close` nor `Multiplexing` is set anywhere in the repository today.** So A-2 is
a guard against introduction, which is what step 18 asked for.

### The guard

`AppConfig.ConnectionString` no longer returns the raw value:

```csharp
public string ConnectionString => RequirePerCheckoutSessionState(config.GetConnectionString("Default") ?? throw …);
```

It throws on two flags:

- **`No Reset On Close=true`** — suppresses the `DISCARD ALL` Npgsql issues on pool return, which is
  the thing that clears `rediensiam.org_id`. One tenant's scope then serves whichever request rents
  the connection next.
- **`Multiplexing=true`** — step 18 does not name this one. It interleaves commands from different
  logical connections over one physical session, so there is no per-request session left to scope at
  all. Same root cause, same blast radius, one extra line.

A startup failure rather than a warning, because both are performance flags somebody adds
deliberately and the damage they do is silent and cross-tenant. `AppConfig.ConnectionString` is the
single accessor every consumer routes through — including `InstanceConfiguration`, which reads the
raw config key itself for its pre-DI bootstrap and is therefore the one path the guard does not sit
on. That is stated rather than fixed: it uses the same string, so a DSN that reaches it also reaches
`AppConfig` a few milliseconds later and fails the host.

---

## A-3 — Dragonfly TLS

### State

**The application side is done. TLS is still off, and it needs one chart change to turn on.**
`Program.cs:53` no longer calls `ConnectAsync(string)`.

### The certificate validation design

`src/Config/CacheTls.cs` (new). Two public entry points.

**`BuildOptions(connectionString, caBundlePath, log)`** decides whether to pin at all:

| Connection string | CA bundle at the path | Result |
|---|---|---|
| no `ssl=true` | irrelevant, not read | returned untouched. **This is today's deployment; the whole file is inert** |
| `ssl=true` | absent | .NET default validation left in place, and a `WARNING:` line naming the path. Not a downgrade — it is exactly what happens today, it just cannot trust a cluster root, and it fails loudly at startup |
| `ssl=true` | present, empty | `InvalidOperationException` at startup. An empty file would otherwise build a callback that rejects every certificate — a total cache outage whose cause is one blank key |
| `ssl=true` | present, has certs | pinned, plus a `Cache TLS: server certificate pinned to N root(s) from '<path>'` line so the operator has positive evidence the mount landed |

The path defaults to `/etc/cache-tls/ca.crt` (`AppConfig.CacheTlsCaFile`, overridable with
`Cache__TlsCaFile`). A default path rather than a configured value is what makes this automatic: the
chart mounts the cert-manager secret there and nothing has to be set at install time.

**`PinnedTo(roots)`** is the callback. Exactly what it accepts:

```csharp
if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
if (certificate is null) return false;

using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
using var verifier = new X509Chain();
verifier.ChainPolicy.TrustMode        = X509ChainTrustMode.CustomRootTrust;
verifier.ChainPolicy.CustomTrustStore.AddRange(roots);
verifier.ChainPolicy.RevocationMode   = X509RevocationMode.NoCheck;
verifier.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));  // serverAuth
if (chain is not null)
    foreach (var element in chain.ChainElements)
        verifier.ChainPolicy.ExtraStore.Add(element.Certificate);
return verifier.Build(leaf);
```

**What it trusts:** a server certificate that chains to a root in the mounted PEM bundle, is within
its validity window, carries the serverAuth EKU, and whose name matched the endpoint the connection
string names.

**What it rejects, each with a test:**

- a leaf issued by any other CA — the man in the middle who runs his own issuer;
- an unrelated self-signed certificate;
- a certificate **the cluster CA itself issued for a different service** — `RemoteCertificateNameMismatch`
  is outside the mask, so it stays fatal. Without that, any workload that can obtain a cert from the
  cluster issuer can impersonate the cache;
- an expired leaf;
- a client-authentication certificate presented as a server certificate;
- **a certificate the OS trust store already trusts, if it is not ours.** `SslPolicyErrors.None` is
  not a shortcut here. Pinning that also honours the public WebPKI is not pinning.

That last one is why StackExchange.Redis's own `ConfigurationOptions.TrustIssuer(path)` was
considered and not used. Its callback opens with `if (sslPolicyError == SslPolicyErrors.None) return true;`
— so the mounted root stops being a requirement the moment the cache moves to a publicly-certifiable
hostname. Its internal callback is also unreachable from a test, and for this particular change
"prove it rejects things" is the entire deliverable. The rest of `TrustIssuer`'s logic is sound and
this callback deliberately mirrors it (chain build, EKU, root identity).

**Stated ceilings:**

- `RevocationMode = NoCheck`. cert-manager publishes neither a CRL nor an OCSP responder; leaving
  revocation on fails every handshake. Short-lived certificates and rotation are the revocation story.
- `AddRange(roots)` under `CustomRootTrust` means the bundle's entries are trust anchors. With
  cert-manager's `selfsigned` issuer the published `ca.crt` *is* the leaf, so the chain is length 1
  and the pin is effectively to that certificate — it will need re-mounting on rotation. A real
  `Issuer`/`ClusterIssuer` with a stable CA makes this a normal root pin. There is a test for the
  length-1 case because that is the mode this deployment actually runs.

### Why it is still off, and what the operator (or the `deploy/` agent) must do

The CA is not mounted into the app pod. That is a chart change and outside this scope:

> **Required follow-up in `deploy/`:** mount the `rediensiam-dragonfly-tls` secret's `ca.crt` key at
> `/etc/cache-tls/ca.crt` in the app Deployment (one `volume` + one `volumeMount`, `readOnly: true`),
> gated on `dragonfly.local.tls.enabled` — the same condition that renders the `Certificate`.
> Then `dragonfly.local.tls.enabled: true` and `,ssl=true` on `cacheUrl` in the same `helm upgrade`.

Step 18's other finding about this cutover still stands and is not fixed by anything here: the
Dragonfly pod flips immediately while the Deployment keeps the old app pod serving, so the old pod
loses its cache. There is no version of this rollout that is invisible to users.

---

## A-4 — scope alignment

`IgnoreQueryFilters()` appears **nowhere** in `src/` or `tests/`, and neither does `HasQueryFilter`.
The model declares no global query filter, so there is no filter for `IgnoreQueryFilters()` to bypass
and the two lists agree trivially — the exemption set is empty.

That is a real answer, but it is only true today. `Model_Declares_No_Global_Query_Filter_So_The_
IgnoreQueryFilters_Exemption_Set_Is_Empty` reads the built EF model and fails the moment a query
filter is declared, with a message that names the obligation: every `IgnoreQueryFilters()` that
bypasses the new filter must correspond to an entry in
`TenantScopeInterceptor.LegitimatelyUnscopedPaths`, or a query bypasses one isolation layer and not
the other.

Step 18 says "A-1 and A-4 are the same piece of work as the EF global query filters". They have been
kept the same piece of work: the filter list and the scope list are now joined by a failing test
rather than by intent.

---

## Tests — what each one proves

`tests/RediensIAM.IntegrationTests/Tests/Security/TenantScopeInterceptorTests.cs` (14) — runs against
the fixture's real PostgreSQL container.

| Test | What it proves |
|---|---|
| `Scope_Is_Set_On_The_Connection_The_Request_Actually_Uses` | end to end through DI: claims → interceptor → `current_setting('rediensiam.org_id')` on the connection an EF query just used |
| `Scope_Survives_Into_The_Transaction_EF_Opens_For_SaveChanges` | the value is readable **outside** a transaction as well as inside — the assertion `SET LOCAL` fails |
| `Scope_Does_Not_Survive_Into_The_Next_Checkout_Of_The_Same_Connection` | `MaxPoolSize=1`, dirty the session, close, reopen, assert the backend PID is the same one *and* the scope came back empty. Without `DISCARD ALL` this reads the previous renter's UUID |
| `Dsn_That_Suppresses_The_Pool_Reset_Refuses_To_Start` | A-2: `No Reset On Close=true` throws |
| `Dsn_That_Multiplexes_Refuses_To_Start` | A-2: `Multiplexing=true` throws |
| `Ordinary_Dsn_Is_Accepted_Unchanged` | the guard is not eating normal DSNs |
| `Unscoped_Callers_Run_As_System` ×4 | no context / empty org / `Guid.Empty` / unparseable all map to `'system'` |
| `Unauthenticated_Request_Runs_As_System` | the login path: a request in flight with no claims |
| `Tenant_Caller_Runs_Scoped_To_Its_Own_Organisation` | the positive case |
| `System_Scope_Reaches_The_Database_As_The_Literal_The_Policies_Expect` | the string in the database is exactly `system`. A typo here is fail-closed, i.e. an outage |
| `Interceptor_Overwrites_A_Previous_Renters_Scope_Rather_Than_Inheriting_It` | even on a connection that arrived dirty, the current request's scope wins. Both layers have to fail to leak |
| `Scope_Value_Is_Bound_As_A_Parameter_Not_Concatenated` | `system'; DROP TABLE users; --` through the claim leaves `public.users` standing |
| `Model_Declares_No_Global_Query_Filter…` | A-4 |
| `The_Unscoped_Path_List_Is_A_Real_Artefact` | the honest-limit list has not been quietly emptied |

`tests/RediensIAM.IntegrationTests/Tests/Security/CacheTlsPinningTests.cs` (12) — no container, no
cluster; every certificate is generated in the test.

| Test | What it proves |
|---|---|
| `Leaf_Issued_By_The_Pinned_Root_Is_Accepted` | the callback is not merely a rejecter |
| `Self_Signed_Server_Certificate_Pinned_To_Itself_Is_Accepted` | cert-manager's `selfsigned` mode, chain length 1 |
| `Leaf_Issued_By_A_Different_Root_Is_Refused` | the MITM with his own CA — the case `return true` accepts |
| `Unrelated_Self_Signed_Certificate_Is_Refused` | the naive impostor |
| `Name_Mismatch_Is_Refused_Even_When_The_Root_Is_Ours` | a sibling workload's cert from the same issuer cannot impersonate the cache |
| `Missing_Certificate_Is_Refused` | `RemoteCertificateNotAvailable` |
| `Expired_Leaf_Is_Refused` | validity window still enforced |
| `Client_Authentication_Certificate_Cannot_Open_A_Server_Connection` | the serverAuth EKU requirement |
| `A_Certificate_The_Os_Store_Trusts_Is_Still_Refused_If_It_Is_Not_Ours` | **the differentiator.** No `SslPolicyErrors.None` shortcut |
| `Plaintext_Connection_String_Gets_No_Callback_And_No_File_Read` | today's deployment is unaffected |
| `Tls_Without_A_Mounted_Ca_Warns_And_Leaves_Default_Validation_In_Place` | a missing mount does not silently become "trust anything" |
| `An_Empty_Ca_Bundle_Refuses_To_Start` | a blank file fails at startup, not at every handshake |

---

## Suite output

```
$ dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
      -p:SonarQubeTargetsImported=true --nologo

Test run for …/RediensIAM.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed: 0, Passed: 1335, Skipped: 0, Total: 1335, Duration: 3 m 36 s
```

1305 → 1335. No pre-existing test was modified, and none needed to be: the interceptor adds one
`set_config` round trip per connection checkout and changes no query result while `pg_policies` is
empty.

`dotnet build` on both projects is clean — 7 warnings on `src`, all pre-existing and unrelated
(`IntrospectionController` XML comments, two Sonar `S1144`s, `S3267`, `S2139`, `SCS0016`).

---

## Can RLS be enabled now?

**Yes — the application half is in place.** The precondition step 18's runbook opens with is now
satisfiable and, more importantly, checkable.

The runbook in `18-cnpg-tls-rls.md` §3 is unchanged and still authoritative. The two things it says
that are worth repeating because skipping them is silent:

1. **`ALTER ROLE iam_backup BYPASSRLS`, once, as superuser.** Not optional. `pg_dump` sets
   `row_security = off`, which *errors* for a role that cannot bypass. Without it the nightly backup
   stops, and it stops loudly — which is better than the alternative but is still a stopped backup.
2. **Step 0 — check the precondition rather than assume it.** From the app pod, under load:
   ```sql
   SELECT setting FROM pg_settings WHERE name = 'rediensiam.org_id';
   ```
   This build makes that query return `system` or an org UUID on every connection the app holds. An
   empty result means the deployed image predates this change — stop.

What the operator must run, in order: the `BYPASSRLS` grant → a pre-enable dump → deploy this build →
verify step 0 → `postgres.rls.enabled: true` → `kubectl logs job/rediensiam-rls` must end with
`RLS applied to 19 tables` → the functional query in §3 → a post-enable dump.

**`postgres.rls.enabled` was not changed. It is still `false`, it is a chart flag, and turning it on
is a separate operator decision.**

---

## What is left, with its cost

| Item | Why still open | Cost |
|---|---|---|
| **Dragonfly TLS still off** | the cluster CA is not mounted into the app pod; that is a chart change and `deploy/` is another agent's scope | ~15 min of chart work (one volume + one volumeMount, gated on `dragonfly.local.tls.enabled`), then the atomic cutover step 18 describes |
| **RLS still off** | correct — it is a chart flag and an operator decision, and it needs the `BYPASSRLS` grant first | §3's runbook, ~1 h |
| **RLS does not protect the login path** | login resolves a user by e-mail before any tenant exists; the `Projects` row it needs must itself be read unscoped | not fixable at this layer. Would need a tenant-bearing pre-auth route (host- or path-derived org) — a product change, days not hours, and out of proportion to the finding |
| **`InstanceConfiguration` bypasses the A-2 guard** | it reads `ConnectionStrings:Default` directly, before DI, to bootstrap | ~10 min to route it through the same helper. Low value: it uses the same string, so a bad DSN fails the host moments later via `AppConfig` |
| **Pin is to the leaf under `selfsigned`** | cert-manager's `selfsigned` issuer publishes `ca.crt == tls.crt` | the fix is a real `Issuer`/`ClusterIssuer` with a stable CA — the same ~4 h already costed in step 18 for Postgres `verify-full`, and it would close both |
| **No revocation checking on the cache certificate** | cert-manager serves no CRL/OCSP | rotation is the mitigation; a real CDP endpoint is infrastructure work nobody has asked for |
| **A-4 is currently vacuous** | the model declares no query filter | nothing to do. The test fails the day that changes, which is the point |
| **Background work runs as `'system'`** | `AuditLogRetentionService` and `WebhookDispatcherService` are genuinely cross-tenant | by design. If either is ever narrowed to one tenant it must set the scope explicitly rather than inherit `'system'` |

### Credential note

No secret was read or written. The DSN values quoted are shapes, not contents; the gitignored
`values.secret.yaml` was not opened. The certificates in `CacheTlsPinningTests` are generated per
test run and never leave memory except for a temp PEM the test deletes in a `finally`.
