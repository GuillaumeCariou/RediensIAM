# 36 — SAML `Destination` validation, `MigrateOnStartup`, dead configuration keys

Branch `security/hardening-2026-07-30`. Three changes, none of which alters an accepted login for a
correctly-behaving IdP. Files touched: `src/Services/SamlService.cs`,
`src/Controllers/SamlController.cs`, `src/Program.cs`, `src/Config/AppConfig.cs`,
`src/appsettings.json`, plus two new regression files.

---

## 1. SAML: validate `Destination` against the ACS URL

### The gap

`SamlService.BuildConfigAsync(SamlIdpConfig, string spEntityId, Uri acsUrl)` took `acsUrl` and never
read it. `SamlController` passed `AcsUrl` at both call sites (`Start`, `ParseSamlResponseAsync`) and
nothing anywhere compared a response's `Destination` against anything.

What was already protecting the flow, and still is: `config.AllowedAudienceUris.Add(spEntityId)`
makes ITfoxtec refuse an assertion whose `Audience` names a different service provider. That is the
primary control and it worked. The missing piece is the narrower one — a response legitimately
issued for *one endpoint of this SP* being relayed to *another endpoint of the same SP*, where the
audience is identical and therefore proves nothing.

### The ITfoxtec API, and how I established it exists

I decompiled the exact referenced assembly rather than trusting the docs or memory:

```
ilspycmd ~/.nuget/packages/itfoxtec.identity.saml2/4.17.0/lib/net10.0/ITfoxtec.Identity.Saml2.dll
```

(`ITfoxtec.Identity.Saml2` 4.17.0, `src/RediensIAM.csproj:26`.)

Four facts came out of that, each of which changed a decision:

1. **`Saml2Request.Destination` is a public `Uri`** — inherited by `Saml2Response` →
   `Saml2AuthnResponse`. Not merely documented: `Saml2Request.Read` assigns it.

2. **It is populated by `Read`, which both entry points call.**
   `Saml2Request.Read(string xml, bool validate, bool detectReplayedTokens)` contains

   ```csharp
   Destination = XmlDocument.DocumentElement.Attributes["Destination"].GetValueOrNull<Uri>();
   ```

   `Saml2Binding.ReadSamlResponse` calls `Read(..., validate: false, detectReplayedTokens: false)`;
   `Saml2PostBinding.UnbindInternal` calls `Read(..., validate: true, detectReplayedTokens: true)`.
   So `Destination` is available immediately after `ReadSamlResponse`, and `Unbind` re-parses the
   identical document to the identical value. Either placement reads the same bytes.

3. **Absent `Destination` does not throw.** `Attributes["Destination"]` returns `null` when the
   attribute is missing, and the extension is null-safe —
   `GetValueOrNull<T>(this XmlAttribute a) => GenericTypeConverter.ConvertValue<T>(a?.Value, a)`.
   So `Destination` is simply `null`. This matters: IdPs that omit the attribute work *today*, and
   requiring it would be a live-traffic behaviour change rather than a no-op.

4. **`Saml2Configuration` has no expected-destination field at all** (full public surface dumped:
   `Issuer`, `SingleSignOnDestination`, `SingleLogoutDestination`, `AllowedIssuer`,
   `AllowedAudienceUris`, certificates, …). `SingleSignOnDestination` is the *outbound* IdP SSO URL,
   not an inbound check. ITfoxtec therefore never validates `Destination` anywhere — the check
   could not have been configured on, and had to be written.

### What I added

`SamlService.DestinationMatches(Uri destination, Uri acsUrl)` — a pure static:

```csharp
Uri.Compare(destination, acsUrl, UriComponents.SchemeAndServer,
    UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0
&& string.Equals(destination.AbsolutePath.TrimEnd('/'),
    acsUrl.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
```

Called from `SamlController.ParseSamlResponseAsync`. A mismatch throws `AuthenticationException`,
which the existing `catch` turns into `400 saml_response_invalid` — the same opaque error every
other validation failure on that path returns, so the check leaks nothing about why it failed.

### Comparison rules, and why each edge case went the way it did

I verified .NET's normalisation empirically (throwaway console program) instead of assuming, because
`Uri.Equals` and `Uri.Compare` differ in exactly the places that matter here:

| Destination vs ACS `https://sp.example.com/auth/saml/acs` | `Uri.Equals` | chosen rule | verdict |
|---|---|---|---|
| `https://SP.EXAMPLE.COM/auth/saml/acs` (host case) | true | true | **accept** |
| `HTTPS://sp.example.com/...` (scheme case) | true | true | **accept** |
| `https://sp.example.com:443/...` (explicit default port) | true | true | **accept** |
| `http://sp.example.com:80/...` (explicit default port) | true | true | **accept** |
| `https://sp.example.com/auth/saml/acs/` (trailing slash) | **false** | true | **accept** |
| `https://sp.example.com/auth/../auth/saml/acs` (dot segments) | true | true | **accept** |
| `https://evil.example.com/...` | false | false | **refuse** |
| `https://sp.example.com/auth/saml/other` | false | false | **refuse** |
| `http://sp.example.com/...` (scheme downgrade) | false | false | **refuse** |
| `https://sp.example.com:8443/...` (different port) | false | false | **refuse** |
| `https://sp.example.com/auth/saml/ACS` (path case) | false | false | **refuse** |

- **Trailing slash** is the reason plain `Uri.Equals` is not enough — it is the one cosmetic
  difference `Uri` treats as significant, and an IdP admin pasting the ACS URL with a slash is
  routine. Trimmed on both sides.
- **Host and scheme case, and an explicitly written default port** are folded by
  `UriComponents.SchemeAndServer`. A genuinely different port (`:8443`) still mismatches, so this
  buys tolerance without giving up the port as a discriminator.
- **Path is compared ordinally**, so `/auth/saml/ACS` does not satisfy `/auth/saml/acs`. Our ACS
  path is a fixed literal that we hand the IdP ourselves in the `AuthnRequest`, so there is no
  legitimate source of case drift; being strict here costs nothing.
- **A query string on the Destination is ignored.** Our ACS location carries none, and the endpoint
  is already pinned by scheme, host, port and path.

### `Destination` absent — decision and justification

**Absent is accepted, and logged at Warning.** SAML 2.0 core §3.2.2 makes the attribute optional and
requires validation only *"if it is present"*. Two independent reasons, and the second is the one
that actually decides it:

1. Fact 3 above: responses without `Destination` are accepted today. Requiring it would break any
   working IdP that omits it — a real lockout for a control that is defence in depth.
2. **Requiring it would not close the bypass it appears to close.** It only helps against an
   attacker who can *strip* the attribute. From the decompiled `Saml2Request.ValidateXmlSignature`,
   our SP accepts a response whose document signature is absent as long as the assertion's signature
   is valid (`documentValidationResult == NotPresent && assertion == Valid` passes). In that
   assertion-only-signing mode the `<Response>` element is unsigned, so an attacker who can strip
   `Destination` can equally well *rewrite it to the correct value*. Mandating presence therefore
   buys no security in the only mode where absence can be forged, while costing compatibility in
   every mode.

### Honest limit of this control

State it plainly, because it bounds what the change is worth:

- **IdPs signing the response** (`AuthnResponseSignType = SignResponse`, which is the library
  default — enum value 0) put `Destination` inside the signature, since it is an attribute of the
  signed root element. There the check is solid: it cannot be altered without invalidating the
  signature.
- **IdPs signing only the assertion** leave the `<Response>` element and its `Destination`
  unprotected, and our SP accepts that shape. Against an attacker who has captured such a response,
  this check is bypassable by rewriting the attribute. It still stops naive relaying and misconfigured
  endpoints; it is not a hard control in that mode.

The load-bearing controls on this path remain `AllowedAudienceUris`, the pinned signing certificate,
and the single-use `InResponseTo` record.

### Placement: which call site, and where in the sequence

**The two call sites need different treatment, and only one gets the check.**

- `Start` (`SamlController.cs:57`) builds an outbound `AuthnRequest`. There is no response to
  validate; `AcsUrl` is already used there for `AssertionConsumerServiceUrl`. Unchanged.
- `ParseSamlResponseAsync` (`:152`) is the only receiving path, and the only place the check means
  anything.

Within the ACS, the check sits **after `ReadSamlResponse` and the status check, before
`GetAndDeletePendingAsync`**. That ordering is deliberate:

- The pending record is single-use. Validating after consuming it would let a misdirected response
  burn the `InResponseTo` of a legitimate login still in flight — a denial of service introduced by
  a defensive control. Pinned by a test.
- Checking before signature validation costs nothing, because `Unbind` still runs afterwards on the
  same document. An attacker who rewrites `Destination` to match only reaches a failed signature
  check instead of a failed destination check; the outcome is identical and the failure is earlier.

### The unused parameter

`BuildConfigAsync`'s `acsUrl` stays in the signature and stays unused by config construction —
because per fact 4 there is nowhere in `Saml2Configuration` to put it. It now carries a `<remarks>`
block saying exactly that and pointing at `DestinationMatches`, so the next reader does not re-file
it as a missing control. I did not retire the parameter: eight call sites live in
`tests/.../Tests/Auth/SamlTests.cs`, which is outside the file set I own. Retiring it is a clean
follow-up for whoever owns that file.

---

## 2. `Database:MigrateOnStartup` made real

`src/appsettings.json` shipped `"MigrateOnStartup": true` and **nothing read it** — zero occurrences
in `src/` other than the declaration. `Program.cs` called `db.Database.MigrateAsync()`
unconditionally, so an operator who set it to `false` still got migrations applied and had no signal
that their instruction was ignored.

**`AppConfig.MigrateOnStartup`** (`src/Config/AppConfig.cs`):

```csharp
public bool MigrateOnStartup => config.GetValue("Database:MigrateOnStartup", true);
```

Default `true` — byte-for-byte the previous behaviour, so honouring the key changes nothing until
someone sets it. `EnsureDbSchemaAsync` now takes `AppConfig` and returns early when it is false.

**When enabled:** unchanged. Same 12 attempts, same 5 s backoff, same fail-fast
`InvalidOperationException` wrapping on exhaustion.

**When disabled:** the app **starts**. Refusing to boot would make the switch useless — the point of
`false` is a deployment that migrates as a separate deliberate step. What it must not do is stay
quiet, because an un-migrated schema surfaces as unexplained 500s on whichever endpoint first touches
a missing column, a long way from the cause.

A `false` startup logs, at **Warning**:

```
Database:MigrateOnStartup is false — NO migrations were applied at startup. Pending migrations: {N}.
The schema is whatever already exists; apply migrations deliberately before treating this instance
as healthy.
```

`{N}` comes from `db.Database.GetPendingMigrationsAsync()`, which turns "did not migrate" into "did
not migrate, and you are N behind" — directly answering whether the database is actually stale. That
probe is wrapped in its own try/catch: if the database is unreachable the count logs as
`unknown — could not reach the database` and startup still proceeds, because only the diagnostic
failed and the operator's instruction not to migrate still stands. It deliberately does **not**
inherit the enabled path's fail-fast.

---

## 3. Four dead configuration keys removed

Each was checked individually before deletion — not just a grep for the literal key, but for the
bare property name across the whole repository (case-insensitive), and for any options class that
could bind the section indirectly. `grep -rn "GetSection\|Configure<\|Bind("` over `src/` returns
only `HostFilteringOptions`, `ForwardedHeadersOptions` and `KeyManagementOptions` — no `Cache` or
`App` section is bound to a POCO, so there is no indirect reader anywhere.

| Key | Evidence it was dead |
|---|---|
| `Cache:ProjectTtlMinutes` | `ProjectTtl` appears once in the entire repo: the declaration. `AppConfig` exposes only `PatCacheTtlMinutes` from `Cache:PatTtlMinutes`. |
| `Cache:JwksTtlMinutes` | `JwksTtl` appears once in the entire repo: the declaration. |
| `App:FrontendUrl` | `FrontendUrl` appears once in the entire repo: the declaration. `AppConfig` reads `App:PublicUrl`, `App:Domain`, `App:AdminSpaOrigin` — never this. |
| `App:LoginPath` | `LoginPath` appears once in the entire repo: the declaration. |

Removed from `src/appsettings.json`. `src/appsettings.Development.json` contains only
`Security:TotpSecretEncryptionKey` and needed no change.

**Chart references: none.** The repo-wide case-insensitive sweep (which covered `deploy/`, `docs/`,
`frontend/`, `sdk/`, `tests/`) found no occurrence of any of the four outside `src/appsettings.json`
— so there is nothing for the `deploy/` owner to clean up. Env-var spellings (`App__FrontendUrl`
etc.) were included in the same sweep and are likewise absent.

**Left alone, confirmed live:** `Security:Argon2Pepper` (`AppConfig.Argon2Pepper`, feeds
`Argon2PepperRing`), `IAM_PUBLIC_PORT` / `IAM_ADMIN_PORT` / `IAM_ADMIN_PATH` (`PublicPort`,
`AdminPort`, `AdminPath`), `AllowedHosts`, and `Logging:*`. All are pinned by a test so a future
sweep cannot take them out quietly.

---

## Behaviour change for existing IdP integrations

One change can alter what a currently-working integration accepts.

**What changes:** a SAML response carrying a `Destination` attribute that does not resolve to this
deployment's ACS URL is now refused with `400 saml_response_invalid`. Previously it was accepted.

**Who could be affected.** The ACS URL is computed as `{App:PublicUrl}/auth/saml/acs`, and the
`Destination` an IdP sends is whatever it has configured as the SP's ACS. These agree unless the
deployment's `App:PublicUrl` does not match the URL the IdP was actually configured with. Realistic
ways that happens:

- **`App:PublicUrl` set to an internal or cluster-internal URL** while the IdP is configured with
  the public ingress hostname. Terminating TLS at the ingress and running the app on `http://`
  internally makes this likely: the IdP sends `https://iam.example.com/auth/saml/acs`, the app
  computes `http://rediensiam:5000/auth/saml/acs`, and scheme *and* host mismatch.
- **A deployment reachable on several hostnames** (vanity domain, legacy domain, NodePort plus
  ingress) where the IdP holds one and `App:PublicUrl` holds another.
- **A port that appears in one and not the other** — note an explicitly written `:443`/`:80` is
  tolerated, so only a genuinely different port breaks.

Host case, scheme case, default-port notation and trailing slashes are all tolerated and will not
cause a regression.

**What an operator must check before rolling this out.** For each configured SAML IdP, confirm that
`App:PublicUrl` + `/auth/saml/acs` is *string-for-string the ACS URL registered at the IdP*, modulo
the tolerated differences above. `GET /auth/saml/metadata` prints the exact value the app will
compare against, in `AssertionConsumerService/@Location` — compare that against the IdP's SP
configuration. If they differ, fix `App:PublicUrl` (or the IdP's registered ACS); do not expect the
mismatch to keep passing.

**Symptom if missed:** every SAML login fails with `400 saml_response_invalid`, and the ACS logs
`SAML ACS validation failed` with an inner
`Destination '<what the IdP sent>' does not name this ACS endpoint` — which names both sides of the
mismatch directly.

IdPs that send no `Destination` are unaffected; they log a warning naming the IdP id and continue.

---

## Tests

Two new files, 35 tests.

`tests/RediensIAM.IntegrationTests/Tests/Regression/SamlDestinationRegressionTests.cs` — 16 tests.
Declared as a part of the existing `partial class SamlControllerTests` to reuse its IdP seeding and
Hydra stubbing rather than duplicate them.

- `DestinationMatches_CosmeticDifferences_AreAccepted` (6 cases) — host case, upper-case host,
  scheme case, explicit `:443`, trailing slash, dot segments.
- `DestinationMatches_RealDifferences_AreRefused` (5 cases) — different host, **different endpoint
  on the same host** (the attack this exists for), scheme downgrade, different port, path case.
- `Acs_ResponseDestinedForAnotherEndpoint_IsRefused` — full flow, correctly signed and otherwise
  entirely valid, `Destination` pointing at `/auth/saml/somewhere-else` → `400
  saml_response_invalid`.
- `Acs_MisdirectedResponse_DoesNotConsumeThePendingRequest` — pins the ordering decision: after a
  misdirected response is refused, a correctly addressed response echoing the *same* `InResponseTo`
  still succeeds.
- `Acs_ResponseDestinedForThisEndpoint_StillSucceeds` — the control is not a lockout.
- `Acs_DestinationWithDefaultPortAndTrailingSlash_IsAccepted` — normalisation end to end, not only
  in the pure helper (`http://LOCALHOST:80/auth/saml/acs/`).
- `Acs_ResponseWithNoDestination_IsAccepted` — the absence decision, pinned.

One test-construction note worth recording, since it is a real property of the library:
`CreateSecurityToken` derives the SubjectConfirmation `Recipient` from `Destination` and throws
`ArgumentNullException` on null, so a response with no `Destination` cannot be emitted directly. The
absent-case test builds a normal response, switches to assertion-only signing, and removes the
attribute from the unsigned `<Response>` root afterwards — which is exactly the shape described in
"honest limit" above, and confirms our SP accepts it.

`tests/RediensIAM.IntegrationTests/Tests/Regression/ConfigKeyRegressionTests.cs` — 19 tests, no
fixture (pure configuration binding, so no database or Redis).

- `MigrateOnStartup_WhenUnset_DefaultsToMigrating` — the default did not move.
- `MigrateOnStartup_IsHonoured` (3 cases: `false`, `False`, `true`) — the actual defect.
- `RetiredKeys_AreNotDeclared` (4 cases) — the four removed keys stay gone.
- `LiveKeys_AreStillDeclared` (6) and `LiveTopLevelKeys_AreStillDeclared` (5) — the other side of
  the sweep, so a future cleanup cannot quietly take out `Security:Argon2Pepper`, the `IAM_*` port
  keys, `AllowedHosts` or `Logging`.

The key tests assert against the `appsettings.json` copied into the test output — the same file the
application loads, not a fixture copy.

---

## Suite

```
dotnet build RediensIAM.slnx -p:SonarQubeTargetsImported=true
Build succeeded.
    0 Warning(s)
    0 Error(s)

dotnet test RediensIAM.slnx -p:SonarQubeTargetsImported=true
Passed! - Failed: 0, Passed:   11, Skipped: 0, Total:   11  RediensIAM.Client.Tests.dll
Passed! - Failed: 0, Passed: 1381, Skipped: 0, Total: 1381  RediensIAM.IntegrationTests.dll  (3 m 46 s)
```

**1381 integration tests** — the 1346 baseline plus the 35 added here — and 11 SDK tests, **0
failures, 0 build warnings**.

Two notes on running it: a `--no-incremental` solution build can emit spurious `MSB3030` copy errors
while other agents are building concurrently (it deletes `src/bin` under a parallel project's feet);
a plain build is clean. And the stale `.sonarqube/` at the repo root still requires
`-p:SonarQubeTargetsImported=true` on every invocation.

Not committed, per instruction.
