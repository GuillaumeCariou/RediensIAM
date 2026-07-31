# Step 23 — closing R-15's cache half: transport TLS, and the key ring encrypted at rest

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Scope:** `src/`, `tests/`, `deploy/`
**Specs:** `.security-hardening/09-infra-security.md` §6.5 (runbook) · `18-cnpg-tls-rls.md` §2 (where it stopped) · `21-rls-app-support.md` A-3 (the app side)
**Suite:** 1335 → **1345 passing, 0 failing, 0 skipped** (10 new)
**Deployment:** `./deploy/verify-deployment.sh --dev` → **34 passed, 0 failed, 3 skipped**, 5 pods Running
**Not committed.**

---

## Summary

| # | Item | Outcome |
|---|---|---|
| 1 | Dragonfly TLS | **Done and live.** Server refuses cleartext, app connects over TLS 1.3 pinned to the mounted cluster CA. Observed on the wire, not read off a manifest |
| 2 | DataProtection key ring encrypted at rest | **Done and live.** Wrapped in AES-GCM under a purpose-derived HKDF subkey; an unprotected key is refused, not adopted |

Both were done in one image and one `helm upgrade`, because piece 1 is a hard cutover and piece 2
changes what the app writes into the cache that cutover restarts.

Step 18's blocker is closed. The sentence it ended on —

> `ConnectionMultiplexer.ConnectAsync(string)` cannot trust a self-signed certificate

— was fixed in step 21 (`src/Config/CacheTls.cs`); this step supplied the two things that were still
missing: the CA mount in the app Deployment, and the DSN half of the cutover.

---

## 1 — The TLS cutover

### What the cutover consists of

Three values that must move together, because any two of them without the third is a total cache
outage — and the cache holds the DataProtection key ring, so a cache outage is every session:

| Half | Where | Effect alone |
|---|---|---|
| `rediensiam.dragonfly.local.tls.enabled` | `values.dev.yaml` | Dragonfly gets `--tls` and **stops answering cleartext** |
| `,ssl=true` on `cacheUrl` | gitignored `values.secret.yaml`, written by `deploy.sh` | app negotiates TLS; against a non-TLS server it connects to nothing |
| `ca.crt` mounted at `/etc/cache-tls` | `templates/deployment.yaml` | without it `CacheTls` warns and falls back to the OS trust store, which cannot verify a cluster-issued certificate — so the connection fails at startup |

### The DSN half — `deploy/deploy.sh`

Postgres already had this shape: a `REQUIRE_SSL` flag read from the layered values files drives
`app_ssl`/`ory_ssl` inside `write_secrets_file`. The cache now has the equivalent `CACHE_TLS` →
`cache_ssl` → `cacheUrl`.

The one thing that could not be copied is the *reader*. `requireSsl` is a unique key name, which is
why the Postgres block greps for it and says so in a comment. `dragonfly.local.tls.enabled` is not
unique — there are three separate `tls:` blocks in these files and `enabled:` under the wrong one is
exactly how a check like this becomes a lie. So the block is cut out by indentation first and only
then matched:

```bash
cache_tls_in() {
  [ -f "$1" ] || return 1
  sed -n '/^[[:space:]]\{2\}dragonfly:/,/^[[:space:]]\{2\}[a-zA-Z]/p' "$1" \
    | sed -n '/^[[:space:]]*tls:/,/^[[:space:]]\{0,6\}[a-zA-Z]/p' \
    | grep -Eq '^[[:space:]]*enabled:[[:space:]]*true'
}
```

Verified against all three committed values files before use (`values.yaml` false, `values.dev.yaml`
true after the change, `values.prod.yaml` false). It is still a text reader over YAML, and it is
**backed by a template guard rather than trusted** — see below.

### The guards — `templates/dragonfly.yaml`

Three `fail`s, all gated on a non-empty `cacheUrl`, for the reason `postgres.yaml` states verbatim:
empty means the credential comes from the gitignored secrets file, which `helm lint` and
`helm template` on `values.yaml + values.<env>.yaml` alone never see. Every real deploy layers that
file, so every real deploy is judged.

| Condition | Message |
|---|---|
| TLS on, no `dragonfly.local.password` | pre-existing; Dragonfly refuses to start with TLS and no auth method |
| TLS on, `cacheUrl` has no `ssl=true` | *"…both must change in the SAME upgrade"* |
| TLS off, `cacheUrl` has `ssl=true` | *"…the cache serves no TLS and the app will not connect"* |

The third direction is new relative to the Postgres guard and the cache needs it: Postgres with
`ssl=on` still accepts cleartext clients, so a DSN ahead of the server is harmless there. Dragonfly
without `--tls` speaks no TLS at all, so a DSN ahead of the server is an outage.

Proven in all four directions:

```
=== A. tls ON + cleartext DSN (expect fail) ===
execution error at (rediensiam/templates/dragonfly.yaml:32:4): rediensiam.dragonfly.local.tls.enabled
is set but rediensiam.secrets.cacheUrl has no `ssl=true` — Dragonfly stops answering cleartext, so
both must change in the SAME upgrade (09-infra-security.md §6.5)

=== B. tls OFF + ssl=true DSN (expect fail) ===
execution error at (rediensiam/templates/dragonfly.yaml:35:4): rediensiam.secrets.cacheUrl asks for
`ssl=true` but rediensiam.dragonfly.local.tls.enabled is off — the cache serves no TLS and the app
will not connect (09-infra-security.md §6.5)

=== C. tls ON + ssl=true + no password (expect fail) ===
execution error at (rediensiam/templates/dragonfly.yaml:21:4): rediensiam.dragonfly.local.tls.enabled
needs rediensiam.dragonfly.local.password — Dragonfly refuses to start with TLS and no authentication method

=== D. happy path (expect --tls and the CA mount) ===  2
=== E. tls OFF + cleartext DSN, prod (expect no --tls) ===  0
```

That is what makes the shell reader acceptable rather than load-bearing: if it is ever wrong, the
deploy stops at template time with a message naming the fix, not at connection time with a cache
that answers nothing.

### The reuse path

`cache_ssl` only reaches a secrets file that *this run generates*. An existing one keeps whatever
`cacheUrl` it was written with, and the operator flipping the chart flag has no reason to know a
second, gitignored file has to move with it. `deploy.sh` now stops on that mismatch and prints the
edit — the password on that line is never reprinted:

```
  ┌─ R-15: cache TLS is on but this install's DSN is cleartext ────────
  │  Fix (edit in place, the password on that line is not reprinted here):
  │    sed -i 's|\(cacheUrl: "[^"]*:6379\)|\1,ssl=true|' <secrets file>
  └────────────────────────────────────────────────────────────────────
  ERROR: refusing to deploy a TLS cache with a cleartext DSN.
```

This is the path this cutover actually took: the dev secrets file predated the change, so the
`ssl=true` was applied to it by hand (mode 600 preserved, value never printed) before the deploy.

### The cutover as performed

```
$ bash deploy/deploy.sh --dev
…
──── [4/4] Deploy ───────────────────────────────
Release "rediensiam" has been upgraded. Happy Helming!
NAME: rediensiam        STATUS: deployed        REVISION: 18

 Pods:
   rediensiam-78fbd5bc96-pfgrx              Running
   rediensiam-7fc6978cc9-bfpvs              Terminating
   rediensiam-dragonfly-588d568655-jqpzm    Running
   rediensiam-hydra-75b7fc79b4-9v4tq        Running
   rediensiam-keto-754dc4c55d-792qd         Running
   rediensiam-postgres-0                    Running
```

```
$ kubectl get deploy rediensiam-dragonfly -o jsonpath='{.spec.template.spec.containers[0].args}'
["--logtostderr","--requirepass=$(DRAGONFLY_PASSWORD)","--tls",
 "--tls_cert_file=/etc/dragonfly-tls/tls.crt","--tls_key_file=/etc/dragonfly-tls/tls.key"]

$ kubectl logs -l app=rediensiam --tail=-1 | grep -i 'cache tls'
Cache TLS: server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
```

Step 18 predicted the user-visible cost and it happened as described: the Dragonfly pod flips
immediately while the old app pod is still serving, so the old pod loses its cache for the ~30 s
before it terminates. Dragonfly runs with no PVC, so the cutover also emptied the key ring — every
session was invalidated. In dev that is free; in prod it is the price of this change and there is no
ordering that avoids it.

### Verifying the connection is actually encrypted — observed, not asserted

A manifest saying `--tls` and a log line saying "pinned" are both the application's own claims. The
following is the wire.

**The handshake.** `kubectl port-forward` to the Service, then plain `openssl s_client`:

```
$ openssl s_client -connect 127.0.0.1:16379 -servername rediensiam-dragonfly
Server certificate
subject=CN = rediensiam-dragonfly
issuer=CN = rediensiam-dragonfly
New, TLSv1.3, Cipher is TLS_AES_256_GCM_SHA384
Verify return code: 18 (self-signed certificate)
```

TLS 1.3, AES-256-GCM, and the certificate is cert-manager's — `subject == issuer` is the
`selfsigned` ClusterIssuer, i.e. the chain-of-length-1 case step 21 called out and wrote a test for.
`Verify return code: 18` is *openssl's* verdict against the OS trust store; it is not the app's.
The app uses `CacheTls.PinnedTo`, which trusts the mounted `ca.crt` and nothing else — that is the
difference between this and a `TrustServerCertificate` shortcut.

**Cleartext is refused, not merely unused.** The same port, plain RESP:

```
$ printf 'PING\r\n' | nc 127.0.0.1 16379
-ERR Bad TLS header, double check if you enabled TLS for your client.
```

This is the assertion that matters. The server does not answer cleartext at all, so the connection
the app is demonstrably using — it authenticates, it reads and writes — **cannot** be cleartext.
There is no configuration of the app that would make it cleartext and still work.

**A real round trip over that connection.** `AUTH` + `PING` + `KEYS *` inside the TLS tunnel:

```
+OK
+PONG
*1
$30
rediensiam:dataprotection:keys
```

The cache has exactly one key, written by the current app pod through this connection.

---

## 2 — The DataProtection key ring, encrypted at rest

### Why, given TLS

TLS ends at Dragonfly's socket. The key ring is then held in Dragonfly's memory and served to
anything that authenticates. It is the thing that mints session cookies. Encrypting it at rest
protects it against a class TLS does not touch: a memory dump of the cache, a snapshot, a rogue
client that has the AUTH password, or a Dragonfly compromise.

Out of the box ASP.NET Core writes this ring in **cleartext** and logs one warning.

### Design — `src/Config/KeyRingProtection.cs`

`.NET` has no `ProtectKeysWithSymmetricKey`; the four built-in `IXmlEncryptor`s are DPAPI ×2,
certificate, and null. The documented extension point is a custom `IXmlEncryptor`/`IXmlDecryptor`
pair, which is what this is — three small types and one builder extension.

**The key** is `AppConfig.DataProtectionKey`, one more entry in the existing HKDF table beside
`TotpEncKey`, `WebhookEncKey`, `SmtpEncKey` and `ThemeEncKey`:

```csharp
public Services.KeyRing DataProtectionKey => _dataProtectionKey ??= DeriveRing("rediensiam-dataprotection-v1");
```

Its own purpose string, so compromise of any other derived subkey does not yield the ability to mint
cookies. Versioned, because changing the purpose orphans every key already stored under the old one.
Nothing new has to be configured or rotated: the deployment already carries the root.

**The ciphertext** goes through `TotpEncryption.EncryptString`/`DecryptString` — the same AES-GCM
and the same key-id envelope every other secret in this codebase uses. That is not tidiness; it is
what makes root rotation (S-10 / step 16) apply to the key ring for free: every configured root can
decrypt, only the active one encrypts. There is a test for exactly that.

**The wiring** uses `PostConfigure`, not `Configure`:

```csharp
builder.Services.PostConfigure<KeyManagementOptions>(options =>
{
    options.XmlEncryptor  = new RootKeyXmlEncryptor(appConfig.DataProtectionKey);
    options.XmlRepository = new EncryptedOnlyXmlRepository(options.XmlRepository ?? throw new …);
});
```

`PersistKeysToStackExchangeRedis` sets `XmlRepository` through `Configure`. `PostConfigure` runs
after every `Configure`, so this call may appear anywhere in the builder chain and the decorator
always wraps a real repository. Registering with `Configure` would have made the ordering of two
lines in `Program.cs` the difference between a protected ring and a wrapped null.

### The ordering trap, named

The decryptor is **not** resolved from DI. Its assembly-qualified type name is written into the
stored XML and DataProtection's activator instantiates it on first key-ring *read*, accepting only a
parameterless constructor or one taking `IServiceProvider`. Live proof, from the running cluster:

```xml
<encryptedSecret decryptorType="RediensIAM.Config.RootKeyXmlDecryptor, RediensIAM, Version=1.0.0.0, …">
```

A constructor taking `AppConfig` or `KeyRing` directly compiles, and passes any test that news the
type up by hand. It then fails on the deploy *after* the one that wrote the keys, at which point
every session is unreadable and the cause is a constructor signature. Hence:

- the round-trip test **restarts the host** rather than reusing it, so it exercises the activator;
- a second test asserts the constructor signature directly, so the first one's success is not a
  coincidence somebody can refactor away;
- and the same restart was done on the live cluster (below).

### An unprotected ring is not silently accepted

Encryption alone is one-way and buys much less than it looks like. An attacker who can write to the
cache cannot read the ring — but he can **append** a plaintext key of his own, which DataProtection
adopts and uses. `EncryptedOnlyXmlRepository` rejects any stored `<key>` element that does not
contain the encryptor's element:

```
DataProtection key '<id>' is stored unencrypted. Refusing to use it: an unprotected key in a shared
cache can be read — or planted — by anyone with access to that cache, and either way it mints session
cookies. If this is a ring written before key-ring protection was enabled, delete it
(DEL rediensiam:dataprotection:keys) and accept the one-time session loss.
```

It **throws rather than skipping**. A skipped key is a silent fallback to a smaller ring, which is
the exact failure mode this file exists to prevent, and the operator sees nothing.

`<revocation>` elements are exempt: they name a dead key id, carry no secret, and are correctly
stored in the clear. Rejecting everything unencrypted would have made key revocation an outage.
There is a test.

### Restart behaviour — verified on the live cluster

The ring written at cutover, read straight out of Dragonfly over the TLS connection:

```xml
<key id="7d62206a-def6-44fb-9725-4f283cbadc23" version="1">
  <creationDate>2026-07-31T15:59:25Z</creationDate> …
  <descriptor deserializerType="…AuthenticatedEncryptorDescriptorDeserializer, …">
    <descriptor>
      <encryption algorithm="AES_256_CBC" /><validation algorithm="HMACSHA256" />
      <encryptedSecret decryptorType="RediensIAM.Config.RootKeyXmlDecryptor, RediensIAM, …">
        <rediensiamEncryptedKey>«base64 AES-GCM ciphertext, elided»</rediensiamEncryptedKey>
      </encryptedSecret>
    </descriptor>
  </descriptor>
</key>
```

No `<masterKey>` element — which is where the raw AES-256 and HMAC-SHA256 key bytes sit when nothing
protects the ring. That element's presence is the whole finding, and it is gone.

Then the app pod was deleted and the ring re-read by its replacement, with Dragonfly untouched:

```
key ring id before restart: 7d62206a-def6-44fb-9725-4f283cbadc23  (pod rediensiam-78fbd5bc96-pfgrx)
new pod: rediensiam-78fbd5bc96-9zt2g
Cache TLS: server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
GET /health -> 200

LLEN rediensiam:dataprotection:keys  -> 1
<key id="7d62206a-def6-44fb-9725-4f283cbadc23"
grep -c masterKey -> 0
```

**Same key id, ring length still 1.** The new pod decrypted and adopted the existing protected ring
rather than failing or minting a fresh one — which is the property that decides whether a deploy
costs every user their session. The activator constructed `RootKeyXmlDecryptor` from the stored type
name and it found `AppConfig` in DI.

---

## What each of these does *not* fix

**The point of doing both.** Key-ring encryption on its own leaves the AUTH password crossing the
wire in cleartext on every connection. An on-path attacker reads it, authenticates as the
application, and — while he cannot read the ring — can `FLUSHALL`, delete the ring, or plant data.
That is a denial of service against every session and a foothold in the cache, and no amount of
at-rest encryption addresses it. Conversely TLS on its own leaves the ring in plaintext in
Dragonfly's memory and in anything that reads it. **After both**, the password is inside the tunnel
and the ring is ciphertext; each closes what the other cannot.

What remains true after both:

| Still exposed to | Why |
|---|---|
| **Compromise of the application pod** | the HKDF root is in the pod's environment. Anyone who can exec into the app, read its memory, or read the `rediensiam-secrets` Secret gets the root and therefore the ring. This protects against a *cache-only* compromise, which is a real and much larger attack surface — not against an app compromise |
| **A holder of the AUTH password** | one static credential, full admin, `FLUSHALL` included. Dragonfly is configured with `--requirepass`, not ACL users, so there is no read-only or restricted role to hand out. `dragonfly-lockdown` (NetworkPolicy) is what limits who can reach `:6379` at all |
| **A stolen cache certificate private key** | the cache's `tls.key` is a Secret in the same namespace. Someone who reads it can impersonate the cache to the app. Same trust root as everything else in the namespace |
| **Certificate revocation** | `RevocationMode = NoCheck`. cert-manager publishes neither CRL nor OCSP; leaving revocation on fails every handshake. Rotation is the revocation story |
| **Client authentication** | the app verifies the cache; the cache does not verify the app beyond the password. mTLS would need `--tls_ca_cert_file` on Dragonfly and a client certificate mounted in the app |
| **Sessions across a cache restart** | Dragonfly has no PVC. Any Dragonfly restart empties the ring and logs everyone out. Unchanged by this step, and it is what made the cutover's own session loss unavoidable |

---

## Tests — what each one proves

`tests/RediensIAM.IntegrationTests/Tests/Security/KeyRingProtectionTests.cs` (10 new).
Eight run in-process over a list-backed repository; one runs against the fixture's real Dragonfly
container; one is reflection over a constructor.

| Test | What it proves |
|---|---|
| `Stored_Key_Carries_No_Plaintext_Key_Material` | no `masterKey` element survives to storage — the finding itself |
| `Key_Ring_Round_Trips_Across_A_Restart` | a **new host** over the same stored bytes reads the ring. The activator path, i.e. the ordering trap |
| `Key_Ring_In_The_Real_Cache_Round_Trips_Across_A_Restart` | the same, through `PersistKeysToStackExchangeRedis` against a real Dragonfly, asserting the raw cached bytes contain no `masterKey` |
| `A_Ring_Written_Before_Protection_Was_Enabled_Is_Refused_Not_Adopted` | exactly what this deployment had: keys written with no encryptor. Refused, with the remedy in the message |
| `A_Plaintext_Key_Planted_Beside_A_Protected_Ring_Is_Refused` | the injection attack that at-rest encryption alone does not stop |
| `Revocation_Records_Are_Left_Alone` | the obvious over-tightening. Revocations are not secrets and must keep working |
| `A_Deployment_With_A_Different_Root_Cannot_Read_The_Ring` | the property TLS does not give: the dump is useless without the root |
| `Rotating_The_Root_Keeps_The_Existing_Ring_Readable` | `2:new,1:old` still decrypts a ring written under root 1 — root rotation is not a session massacre |
| `Protection_Without_A_Key_Repository_Refuses_To_Start` | no silent fall-back to the container's ephemeral filesystem while reporting "encrypted" |
| `The_Decryptor_Is_Constructible_The_Only_Way_DataProtection_Will_Construct_It` | pins the constructor signature, so the round-trip test's success is not accidental |

```
$ dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
      -p:SonarQubeTargetsImported=true --nologo

Passed!  - Failed: 0, Passed: 1345, Skipped: 0, Total: 1345, Duration: 3 m 46 s
```

1335 → 1345. No existing test was modified. `dotnet build` on `src` is clean: 7 warnings, all
pre-existing and unrelated.

---

## `helm`

```
$ helm lint     rediensiam -f rediensiam/values.yaml -f rediensiam/values.dev.yaml
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed

$ helm template t rediensiam -f rediensiam/values.yaml -f rediensiam/values.dev.yaml    → OK

$ helm lint     rediensiam -f rediensiam/values.yaml -f rediensiam/values.prod.yaml
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed

$ helm template t rediensiam -f rediensiam/values.yaml -f rediensiam/values.prod.yaml   → OK
```

The dev render passes *because* the three cutover guards are gated on a non-empty `cacheUrl`. Before
that gate the dev render failed on the pre-existing password guard — `dragonfly.local.password`
lives in the gitignored secrets file, so `values.yaml + values.dev.yaml` alone can never satisfy it.
Gating on `cacheUrl` is the rule `postgres.yaml` already stated for its own DSN guard and it is
applied here verbatim: the guard keeps every bit of its force on the deploy path, where the secrets
file *is* layered, and stops failing a configuration that is correct.

---

## `verify-deployment.sh`

**V-26 is new** — three sub-assertions, because any one alone is satisfiable while the traffic is
still cleartext or encrypted against anything that answers:

- **`/server`** — Dragonfly's args contain `--tls`. This is the one that makes the others
  unnecessary to trust: a server that refuses cleartext cannot be talking cleartext to a working client.
- **`/dsn`** — the app's `cache-url` carries `ssl=true`. Read from the Secret; the password on that
  line is never read and never printed, same discipline as V-20/V-21/V-23.
- **`/pin`** — the CA is mounted at `/etc/cache-tls`, **and** the running pod logged the pinning
  line. That second half is evidence from the process rather than from the manifest: `CacheTls`
  prints it only after it has actually loaded roots out of the mounted file. A future "fix" that
  reached for a trust-anything callback would pass `/server` and `/dsn` and fail here.

```
$ ./deploy/verify-deployment.sh --dev
═══════════════════════════════════════════════════════════════
 RediensIAM control verification — dev — 2026-07-31T18:02:09+02:00
 namespace default · release rediensiam · public host iam.localhost
═══════════════════════════════════════════════════════════════
  …
  PASS  V-23/dsn  app, hydra and keto DSNs all request TLS
  PASS  V-24      cache requires a password (48 chars)
  PASS  V-26/server Dragonfly runs with --tls (cleartext is refused, not merely unused)
  PASS  V-26/dsn  app cache DSN requests TLS (ssl=true)
  PASS  V-26/pin  app pinned the cache certificate — server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
  --    V-25      postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)
───────────────────────────────────────────────────────────────
 34 passed · 0 failed · 3 skipped
 All asserted controls are live.
```

31 → 34 passed, **0 failed**. Pods:

```
rediensiam-78fbd5bc96-9zt2g            Running
rediensiam-dragonfly-588d568655-jqpzm  Running
rediensiam-hydra-75b7fc79b4-9v4tq      Running
rediensiam-keto-754dc4c55d-792qd       Running
rediensiam-postgres-0                  Running
```

---

## Files changed

| File | Change |
|---|---|
| `src/Config/KeyRingProtection.cs` | **new** — `RootKeyXmlEncryptor`, `RootKeyXmlDecryptor`, `EncryptedOnlyXmlRepository`, `ProtectKeysWithRootKey` |
| `src/Config/AppConfig.cs` | `DataProtectionKey` — one more purpose in the existing HKDF table |
| `src/Program.cs` | `.ProtectKeysWithRootKey(appConfig)` on the DataProtection builder |
| `tests/…/Security/KeyRingProtectionTests.cs` | **new** — 10 tests |
| `deploy/deploy.sh` | `CACHE_TLS` reader, `cache_ssl` in `write_secrets_file`, reuse-path guard |
| `deploy/rediensiam/templates/dragonfly.yaml` | cutover guards in both directions; existing password guard moved under the `cacheUrl` gate |
| `deploy/rediensiam/templates/deployment.yaml` | CA mount at `/etc/cache-tls` (applied before this step, unchanged here) |
| `deploy/rediensiam/values.dev.yaml` | `dragonfly.local.tls.enabled: true` |
| `deploy/rediensiam/values.yaml` | comment now describes the mechanism instead of the old blocker |
| `deploy/verify-deployment.sh` | V-26 |

---

## What is left, with its cost

| Item | Why | Cost |
|---|---|---|
| **Prod is still on a cleartext cache** | `dragonfly.local.tls.enabled` is `false` in `values.yaml` and `values.prod.yaml`; cert-manager is not this chart's to install | one flag plus the DSN edit `deploy.sh` now prints. ~15 min, plus the unavoidable session loss |
| **Any environment upgrading onto this build with a *surviving* unprotected ring will 500 on the session path** | `EncryptedOnlyXmlRepository` refuses it, by design. Dev did not hit this because the TLS cutover restarted Dragonfly, which is memory-only, so the old ring was gone anyway | one command as a pre-step: `DEL rediensiam:dataprotection:keys`. The remedy is in the exception message. Making it silent would be the failure this whole file prevents |
| **The pin is to the leaf, not to a CA** | cert-manager's `selfsigned` issuer publishes `ca.crt == tls.crt`, so the chain is length 1 | a real `Issuer`/`ClusterIssuer` with a stable CA. ~4 h, already costed in step 18, and it closes this and Postgres `verify-full` together |
| **Certificate renewal requires an app restart** | `CacheTls` loads the CA once at startup. cert-manager renews at ⅔ of lifetime; the projected Secret updates but the loaded roots do not | a `FileSystemWatcher` reload, or accept a restart at renewal. The former is ~1 h and adds a moving part; the latter is a calendar entry. **Not doing this silently expires the connection in ~60 days** |
| **No revocation checking** | cert-manager serves no CRL/OCSP | rotation is the mitigation |
| **No mTLS to the cache** | Dragonfly authenticates the app by password only | `--tls_ca_cert_file` plus a client certificate. ~2 h; the marginal gain over `--requirepass` + NetworkPolicy is small |
| **`verify-deployment.sh` does not assert the ring is encrypted** | reading it needs the cache password, and the script's discipline is that a password is never read and never printed | would need a purpose-built read-only check inside the app (e.g. a `/health/detail` field). ~1 h. Today the property is covered by tests and by the live evidence in this report, not by the drift check |
| **Dragonfly ACLs** | one credential, full admin including `FLUSHALL` | Dragonfly supports ACL users; splitting the app's credential from an admin one is ~1 h and would bound the blast radius of a leaked DSN |

### Credential note

No secret was printed. `values.secret.yaml` was edited in place to add `,ssl=true` with its mode
(600) preserved and its contents never displayed; the temporary copy taken before the edit was
`shred`ed afterwards. The cache password was read into a shell variable to open the verification
connection and filtered out of every captured output. The one ciphertext blob quoted above is the
key ring under AES-GCM and is elided in this document. Nothing was written to a tracked file.
