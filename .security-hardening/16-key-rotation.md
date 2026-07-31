# Step 16 — S-10: key rotation

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30`
**Finding:** S-10 (`03-architecture-review.md` §7, `14-finding-ledger.md` §3) · **Chain:** C-3
**Scope touched:** `src/`, `tests/`, `deploy/`. Nothing under `sdk/` or `frontend/`.

---

## 0. The short version

Before this step, a ciphertext carried no key identifier. Every TOTP secret, webhook signing
secret, per-org SMTP password and social-provider client secret in every tenant was decryptable
only by the single current HKDF root. Rotating that root destroyed all of them at once, with no
migration path. `10-secrets-management.md` §4.7 documented this as a disaster procedure, which is
another way of saying the key was never going to be rotated — and that is why C-3 (registry
compromise → key exfiltration) had disclosure but no recovery.

Rotation is now maintenance:

| what | before | after |
|---|---|---|
| **HKDF root** | one key, no id, rotation = total loss | key ring, id in the envelope, incremental migration + sweep, old key retirable |
| **Argon2 pepper** | one pepper, no marker, rotation = every password broken | pepper ring, id on the hash, re-peppers on login, **long tail — not a clean cutover** |
| **Hydra system secret** | chart already supported a list; nothing said so at the app level | verified against current Ory docs, wired and documented; **one caveat about `secrets.cookie`** |

**The stored format changed.** Read §8 before deploying, and before rolling back.

---

## 1. The envelope, before and after

### Before

```
base64( nonce[12] ‖ tag[16] ‖ ciphertext[…] )
```

AES-256-GCM, 12-byte random nonce, 16-byte tag, key = `HKDF-SHA256(root, info=<purpose>)`.
Nothing in the stored value says which root produced it.

### After

```
[ "k" <keyId> ":" ]  base64( nonce[12] ‖ tag[16] ‖ ciphertext[…] )
     ↑ omitted entirely when keyId == 1
```

`src/Services/TotpEncryptionService.cs`. Two decisions carry the whole design:

**1. Absence of a prefix means key id 1, not "unknown".** Every ciphertext written before this
step is by definition under the one and only root, so reading it as key 1 is not a guess. This is
`TotpEncryption.LegacyKeyId` and it is the backward-compatibility rule everything else rests on.

**2. Key id 1 emits no prefix.** A deployment that has not rotated keeps writing byte-identical
values to what it wrote yesterday. The format change is *inert* until an operator actually names a
non-1 active key — which means the rollback window in §8 is only opened deliberately, not by
deploying this branch.

The prefix cannot collide with the body: the Base64 alphabet is `A–Z a–z 0–9 + / =` and contains
no `:`. So a stored value carries a key id **if and only if** it contains a `:`. That property is
load-bearing twice — in the parser, and in the SQL predicate the sweep uses (§3.2).

The key id is deliberately **not** bound as AES-GCM associated data. It does not need to be: it
selects a key, and selecting the wrong key fails the authentication tag. Tampering with the prefix
turns a valid ciphertext into a decryption failure, never into a different plaintext. Adding AAD
would only have created a second incompatible format for no gain.

---

## 2. The decrypt and encrypt paths

`KeyRing` (same file) holds `ActiveId` plus every configured key id → subkey.

- **Encrypt** — `TotpEncryption.Encrypt(ring, plaintext)` uses `ring.ActiveKey`, always. There is
  no overload that takes a bare `byte[]`; the old one was deleted rather than kept, so the
  compiler had to visit every one of the 20-odd call sites. That was the point of changing the
  type rather than adding a parameter.
- **Decrypt** — `TotpEncryption.Decrypt(ring, stored)` parses the key id, looks it up, and
  decrypts. An id that is **not configured** raises `CryptographicException` naming the id:

  > Ciphertext was encrypted under key id 2, which is not configured. Re-add it to
  > Security:EncryptionKeys — dropping a key that still has data under it is unrecoverable.

  Loud, not silent. The failure mode this replaces — a TOTP secret that quietly stops verifying —
  is indistinguishable from a user with a broken authenticator, and would have been debugged as
  one for days.

### Configuration

```
Security:EncryptionKeys = "2:<64 hex>,1:<64 hex>"
                           ↑ first entry is ACTIVE
```

Ordering follows Ory Hydra's `secrets.system` convention on purpose: an operator rotating this
deployment already has to internalise "first entry encrypts, all entries decrypt" for Hydra, and
two conventions in one runbook is how mistakes happen.

When `Security:EncryptionKeys` is unset, `Security:TotpSecretEncryptionKey` is key id 1 — exactly
the pre-rotation behaviour. Parsing is strict (`AppConfig.ParseRoots`): `id:hex`, ids positive and
unique, 64 hex characters each. Malformed input throws, and `Program.ValidateEncryptionKey` forces
the ring to parse at startup so it fails on boot rather than on the first TOTP decrypt.

Per-purpose HKDF separation is unchanged and now applies per root: each of the four ciphertext
purposes gets an independent subkey from *each* configured root. A webhook ring cannot read a TOTP
ciphertext even though both hold key ids 1 and 2 (proved by
`SubkeysStayIndependentPerPurpose_AcrossEveryRoot`).

### `DeviceFpKey` is deliberately not a ring

Device fingerprints are HMACs, not ciphertexts — they carry no key id and nothing can migrate
them. It follows the **active** root only. Retiring a root therefore invalidates the new-device
cache and every user gets one extra "new device" notification. That is the correct trade;
versioning a one-way fingerprint would buy nothing. It is noted here so it is not a surprise.

---

## 3. The re-encryption path

### 3.1 Why a sweep and not lazy-only

Encryption is *already* lazy: every write goes out under the active key, for free. That is not
enough, and it is worth being precise about why.

A TOTP secret is written once at enrolment and thereafter only read. A webhook secret is written
once at creation. An SMTP password is written when an org configures mail and then never again.
Under lazy-only migration these rows sit on the old key **forever**, which means the old key can
never be removed from the configuration, which means the compromised key is never actually
retired. Lazy-only produces a rotation that looks finished and isn't — the exact failure the brief
calls the worst possible outcome.

So: lazy on write (free, already done) **plus** an explicit sweep that finishes the job and, more
importantly, **tells the operator when it is finished**.

### 3.2 What the sweep is

`src/Services/KeyRotationService.cs`, exposed on the super-admin surface:

| endpoint | purpose |
|---|---|
| `GET /admin/key-rotation` | active key id, configured key ids, per-column pending counts, `total_pending` |
| `POST /admin/key-rotation/reencrypt` | re-encrypt every pending row under the active key; audited as `system.key_rotation.reencrypt` |

It covers all four encrypted shapes — that is the complete set, verified by grepping every
`TotpEncryption` call site in `src/`:

| column | key |
|---|---|
| `User.TotpSecret` | `TotpEncKey` |
| `Webhook.SecretEnc` | `WebhookEncKey` |
| `OrgSmtpConfig.PasswordEnc` | `SmtpEncKey` |
| `Project.LoginTheme` → `providers[].client_secret_enc` | `ThemeEncKey` |

**`total_pending == 0` is the only signal that the retired key may be dropped.** Nothing else is.

Two implementation details that are correctness, not style:

- **The SQL predicate is exact, not a superset.** The sweep pages with `Take(500)` and stops on an
  empty page, so a row the database returns but the application then filters out would silently
  stall the sweep short of the end — leaving cold rows behind while reporting success. Exactness
  comes from the no-`:`-in-Base64 property:
  - active id > 1 → pending ⇔ `NOT LIKE 'k{active}:%'`
  - active id = 1 → pending ⇔ `LIKE '%:%' AND NOT LIKE 'k1:%'`

  These are genuinely different predicates, not one negated. The first draft of this code used
  `NOT StartsWith(prefix)` for both and was inverted for the key-1 case; it was caught before it
  ran, but it is the kind of bug that would have reported "0 pending" during a rollback.

- **The sweep fails rather than drops.** If a row is under a key that is no longer configured, the
  decrypt throws and the sweep aborts. It never writes a row it could not read. Proved by
  `Sweep_RefusesToRun_WhenTheOriginalKeyIsMissing`.

`Project.LoginTheme` is a `jsonb` column, so its provider secrets cannot be filtered in SQL;
projects are loaded in full. Projects are one row per tenant application — this is the only table
where loading everything is acceptable, and it is commented as such.

### 3.3 Why operator-triggered rather than a background job

- The sweep must run when *every* replica already holds both keys. A startup hook would run during
  a rolling update, when half the pods are on the old configuration.
- N replicas running a background sweep race each other over the same rows.
- It must be observable and auditable — an operator needs to see `total_pending` fall to 0 and
  needs the run in the audit log.
- A CLI would need a second entrypoint and its own distribution of database credentials. The
  endpoint reuses the existing authn/authz/audit path and the `RequireManagementLevel(SuperAdmin)`
  filter.

The sweep is idempotent and re-runnable: each batch commits before the next is read, so an
interrupted run is simply started again.

---

## 4. Data volume caveat

`GET /admin/key-rotation` counts by pulling back the ciphertext column of every *pending* row and
parsing the key id in the application. Outside a rotation window the pending set is empty and the
query is trivial. Inside one it is bounded by the same set the sweep is about to rewrite. On a
deployment with a very large `Users` table this is a real query — run it deliberately, not on a
dashboard poll. It is not paginated; see §9.

---

## 5. The Argon2 pepper — what rotation actually means here

**It is not a clean cutover and this section does not pretend otherwise.**

A password hash cannot be re-derived without the plaintext password. There is no sweep, there
cannot be one, and any design that claims otherwise is either storing the password or lying.

### What was built

- `Security:Argon2Peppers = "2:<hex>,1:<hex>"`, first active, same convention as the key ring.
  Unset → `Security:Argon2Pepper` is pepper id 1; both unset → no pepper (id 0).
- The pepper id is appended to the PHC string as `$k={id}`:
  `$argon2id$v=19$m=…,t=…,p=…$<salt>$<hash>$k=2`.
- **The marker is omitted for pepper id 1 and for "no pepper".** Same reasoning as the ciphertext
  prefix: a deployment that has not rotated writes the exact string it wrote before. An unmarked
  hash reads as pepper id 1, which is precisely what it is.
- `PasswordService.NeedsRepepper(storedHash)` is true when the row is under a non-active pepper.
  Both password login paths — `AuthController.CheckCredentialsAsync` (tenant) and `AdminLogin`
  (console) — call `RepepperIfNeededAsync` immediately after a successful verify, the one moment
  the plaintext exists, and rewrite the hash.
- Backup codes already carried a key id (`sha256:{keyId}:{hex}`). `p` is kept as the marker for
  pepper id 1 so nothing already stored changes; new peppers write their numeric id. A code stored
  under a pepper that is no longer configured **fails closed** — it does not fall back to the
  unpeppered key.

### What that does not give you

- **Dormant accounts never migrate.** A user who does not sign in keeps a hash under the old
  pepper indefinitely. There is no marker sweep, no count that reaches zero, no "done".
- Therefore **the old pepper must stay in `Security:Argon2Peppers` for as long as you are
  unwilling to lock those accounts out.** Dropping it converts every un-migrated account into a
  password reset.
- Federated and passwordless accounts have no `PasswordHash` at all and are simply not in scope.
- The only bounded way to finish a pepper rotation is a policy decision: pick a date, drop the old
  pepper, and accept that everyone who has not signed in since must use the password-reset flow.
  A forced global reset is the same thing said honestly.

There is no per-row "which accounts are still on pepper 1" report. Adding one is cheap (§9) but it
would not change the shape of the answer — it would only tell you how many resets the cutover
costs, which is worth knowing before you pick the date.

---

## 6. The Hydra system secret

Checked against current Ory documentation rather than memory
(`docs/hydra/self-hosted/secrets-key-rotation.mdx`). Hydra's semantics:

> It's very important that the new key is the first entry in the list as only the first key is
> used for encryption while all keys from the list are used for decryption. Please note that
> existing data won't be automatically re-encrypted using the new key.

**The chart can already express this** — `values.yaml` passes `hydra.hydra.config.secrets.system`
as a list, and `deploy/deploy.sh` already emits it as a list with a comment stating the semantics.
No template change was needed. `10-secrets-management.md` §4.1 has the procedure and it is
correct; this step verified it rather than rewriting it.

**One thing that section does not say and should:** Hydra defaults `secrets.cookie` to
`secrets.system` when `cookie` is unset, and this deployment leaves it unset. Rotating the system
secret therefore also rotates cookie encryption. With the old secret retained in the list this is
non-destructive — decryption still works — but it is a second blast radius (login and consent
CSRF cookies) that the operator should know about before they prepend. If you want the two
lifecycles separated, set `secrets.cookie` explicitly as its own list.

Hydra does not re-encrypt existing rows, so the old secret must remain until everything encrypted
under it has expired. With the token TTLs this deployment sets (`refresh_token: 168h`), that is
**7 days plus a margin** before the tail entry can be dropped.

---

## 7. Runbook

### 7.1 Rotate the HKDF root

```bash
# 0. Precondition: confirm what you are starting from.
kubectl -n "$NS" port-forward svc/rediensiam-admin 5001:5001 &
curl -sS -H "Authorization: Bearer $SUPER_ADMIN_TOKEN" \
  http://localhost:5001/admin/key-rotation | jq
# → {"active_key_id":1,"configured_key_ids":[1],"columns":[…],"total_pending":0}

# 1. Generate the new root. 64 hex characters, no exceptions.
NEW=$(openssl rand -hex 32)
OLD=$(kubectl -n "$NS" get secret rediensiam-secrets -o jsonpath='{.data.totp-key}' | base64 -d)

# 2. Set BOTH keys, new one FIRST, in values.secret.yaml / values.prod.secret.yaml:
#      rediensiam:
#        secrets:
#          encryptionKeys: "2:<NEW>,1:<OLD>"
#    Leave `encryptionKey` alone — it is ignored once encryptionKeys is non-empty, and
#    it is your written record of what key 1 was.

# 3. Deploy. Every replica must be on the new config before step 4.
./deploy/deploy-dev.sh --dev          # or the prod equivalent
kubectl -n "$NS" rollout status deploy/rediensiam
kubectl -n "$NS" logs deploy/rediensiam | grep 'Encryption key ring'
# → Encryption key ring: active key id 2, configured ids [2,1]; Argon2 pepper ids [1]

# 4. Verify: reads still work, writes now carry k2:.
curl -sS -H "Authorization: Bearer $SUPER_ADMIN_TOKEN" \
  http://localhost:5001/admin/key-rotation | jq
# → {"active_key_id":2,"configured_key_ids":[2,1],…,"total_pending":<N>}
#   N > 0 is expected and correct here — those are the cold rows.

# 5. Sweep.
curl -sS -X POST -H "Authorization: Bearer $SUPER_ADMIN_TOKEN" \
  http://localhost:5001/admin/key-rotation/reencrypt | jq
# → {"active_key_id":2,…,"total_pending":0}

# 6. Confirm independently. DO NOT skip: step 5's response is the same object as step 4's,
#    and a fresh GET proves it against the database rather than against the run that just wrote it.
curl -sS -H "Authorization: Bearer $SUPER_ADMIN_TOKEN" \
  http://localhost:5001/admin/key-rotation | jq '.total_pending, .columns'
# total_pending MUST be 0 and every column MUST be 0.

# 7. Only now drop key 1:
#      encryptionKeys: "2:<NEW>"
#    Redeploy. Take a database backup first — see §8.
```

Progress can also be read straight from the database if you prefer not to trust the endpoint:

```sql
SELECT count(*) FILTER (WHERE totp_secret LIKE 'k2:%') AS on_key2,
       count(*) FILTER (WHERE totp_secret NOT LIKE 'k%:%') AS on_key1_legacy
FROM users WHERE totp_secret IS NOT NULL AND totp_secret <> '';
```

### 7.2 Roll back mid-rotation

**Before step 7 (both keys still configured) — clean.** Put key 1 back at the front:

```yaml
encryptionKeys: "1:<OLD>,2:<NEW>"
```

Redeploy. Everything decrypts under either key; new writes go out under key 1 again. Running the
sweep now migrates *back* to key 1, and rows swept back carry no prefix, which is the original
format. `GET /admin/key-rotation` reports the key-2 rows as pending until you do — it does not
report 0. (`RollingBackToKey1_ReportsTheKey2RowsAsPending`.)

**After step 7 (key 1 dropped) — not a rollback, a restore.** There is nothing to roll back to;
the ciphertexts are under key 2 and key 1 no longer decrypts them. See §8.

**Rolling back the code itself** while `encryptionKeys` names key 2 is a data-loss event. See §8.

### 7.3 Rotate the Argon2 pepper

```bash
NEW=$(openssl rand -hex 32)
# values.secret.yaml:
#   rediensiam:
#     security:
#       argon2Peppers: "2:<NEW>,1:<OLD>"
```

Deploy. Existing hashes keep verifying; each successful login rewrites that user's hash under
pepper 2. **There is no completion signal and no sweep** — see §5. Keep pepper 1 listed until you
have decided to accept the reset cost for whoever has not signed in.

### 7.4 Rotate the Hydra system secret

`10-secrets-management.md` §4.1, unchanged, plus the `secrets.cookie` note in §6 above. Prepend,
never replace; keep the old entry for at least `refresh_token` TTL (7 days here) before trimming.

---

## 8. ⚠ Data format change — read before deploying, and before rolling back

**This step changes the format of data at rest.**

Stored ciphertexts may now carry a `k<id>:` prefix, and stored Argon2 hashes may now carry a
`$k=<id>` suffix. Code from before this branch does not understand either.

What is and is not safe:

| situation | safe? |
|---|---|
| Deploy this branch, do **not** set `encryptionKeys` / `argon2Peppers` | **Yes.** Active ids are 1; no prefix and no marker is written; stored bytes are identical to before. Rolling the code back is clean. |
| Deploy this branch, rotate to key 2, then **roll the code back** | **No.** Every value written or swept since the rotation carries `k2:`. Old code will `Convert.FromBase64String("k2:…")` and throw. TOTP verification, webhook signing, org SMTP and social login break for the affected rows. |
| Rotate to pepper 2, then roll the code back | **No.** Hashes carrying `$k=2` will be verified by old code against the *pepper-1* input and fail. Those users cannot log in. |
| Drop key 1 before `total_pending` reaches 0 | **No, and it is unrecoverable.** Those rows can never be decrypted again. Restore from backup or re-enrol every affected user. |

Consequences, stated plainly:

- **A rollback after new data is written is not clean.** There is no down-migration. Reverting the
  code after a rotation requires restoring the database to a point before the rotation, which
  discards everything written in between.
- **Take a database backup immediately before step 2 and before step 7 of §7.1.** The chart's
  nightly `pg_dumpall` is not a substitute for a deliberate pre-rotation snapshot.
- The blast radius of getting this wrong is every TOTP secret in every tenant. The design keeps
  the format inert until you rotate specifically so that deploying this branch is not itself the
  risky act — the risky act is step 7, and it is gated on a number you can check.

---

## 9. What is not done, and what it would cost

| gap | why it is not here | cost |
|---|---|---|
| **Per-user pepper-migration report** | There is no sweep to complete, so a count changes nothing structurally — but it would tell an operator how many password resets the pepper cutover costs before they commit to it. | ~2h: one query counting `PasswordHash` by `$k=` marker, one field on the status endpoint. |
| **Paginated / streaming status** | `GET /admin/key-rotation` pulls the pending ciphertext column into memory (§4). Fine at current scale, not fine at millions of TOTP users. | ~3h: exact SQL counts using the same `LIKE` predicates, no in-memory pass. |
| **Sweep progress / resume cursor** | Batches commit as they go, so an interrupted sweep is just re-run. A long sweep gives no progress until it returns. | ~4h: server-sent progress or a job row. |
| **Automatic rotation schedule** | Deliberately not built. Rotation writes to every encrypted row in the database; it should be an operator action with a backup taken first, not a cron job. | n/a — recommend against. |
| **`DeviceFpKey` versioning** | One-way HMAC; rotation costs each user one extra new-device email (§2). | n/a — recommend against. |
| **Hydra secret rotation automation** | Hydra's list already does the hard part; automating the prepend/trim is deploy tooling, not application code. | ~2h in `deploy.sh` if wanted. |
| **`secrets.cookie` split from `secrets.system`** | Currently unset, so it inherits `system` (§6). Splitting them is a values change plus a deploy. | ~1h, plus a Hydra restart. |

Nothing here is a half-finished rotation. The HKDF root rotation is complete end to end and has a
zero-valued completion signal. The pepper rotation is complete in the only shape it can have, and
§5 says exactly what it does not guarantee rather than implying it is equivalent.

---

## 10. Tests — what each one proves

`tests/RediensIAM.IntegrationTests/Tests/Regression/KeyRotationRegressionTests.cs`, 36 tests in
four classes.

### `KeyRotationEnvelopeTests` — the crypto invariants (11)

| test | proves |
|---|---|
| `PreRotationCiphertext_CarriesNoKeyIdPrefix` | a key-1 ring emits the *exact* legacy format — no prefix, no `:` — so the rest of these tests are testing real legacy data, not an approximation |
| `OldCiphertextWithNoKeyId_StillDecrypts_AfterKey2BecomesActive` | **required proof 3**: an old ciphertext with no key id still decrypts |
| `NothingChangesOnDisk_UntilAnOperatorActuallyRotates` | the §8 claim that deploying this branch without rotating is byte-inert |
| `ValueEncryptedUnderKey1_DecryptsAfterKey2BecomesActive` | **required proof 1**: key-1 value survives key 2 becoming current |
| `BothKeyGenerationsDecrypt_UnderTheSameRing` | multi-key decryption, both directions, one ring |
| `ValueWrittenAfterRotation_CarriesTheActiveKeyId` | single-key encryption: new writes are labelled `k2:` |
| `ValueWrittenAfterRotation_IsNotReadableByKey1Alone` | **required proof 2**: and it fails with a message naming the missing key id, not silently |
| `WrongKeyMaterialUnderTheRightKeyId_FailsAuthentication` | a key id is a label, not a proof — wrong bytes under the right id are rejected by the GCM tag, not returned as garbage |
| `Base64BodyBeginningWithK_IsNotMistakenForAKeyIdPrefix` | 400 random nonces; every Base64 body starting with `k` still parses as key 1 and round-trips. This is the collision the prefix scheme lives or dies on |
| `KeyIdsAboveSingleDigit_RoundTrip` | `k37:` parses — the scheme is not accidentally single-digit |
| `KeyRing_RejectsAnActiveIdItDoesNotHold` | a ring cannot be constructed in a state where encryption would throw at use time |

### `KeyRotationConfigTests` — the operator surface (10)

| test | proves |
|---|---|
| `NoEncryptionKeysConfigured_YieldsTheSingleLegacyKeyId` | back-compat of the configuration itself: unset ⇒ key id 1 ⇒ prefix-less writes |
| `FirstEntryIsActive_RestAreDecryptOnly` | the ordering convention the runbook depends on |
| `ARealRotation_MigratesAValueWrittenBeforeIt` | the whole §7.1 sequence at the config layer: write under key 1 → add key 2 → old value reads → new writes carry `k2:` → dropping key 1 makes the *un-swept* value permanently unreadable. That last assertion is the one that justifies gating step 7 on `total_pending == 0` |
| `SubkeysStayIndependentPerPurpose_AcrossEveryRoot` | HKDF purpose separation survives rotation — a webhook ring cannot read a TOTP ciphertext |
| `MalformedEncryptionKeys_…` (6 cases) | no `:`, id 0, non-numeric id, non-hex, wrong length, duplicate id → all throw naming the setting, at startup |

### `Argon2PepperRotationTests` — the honest half (7)

| test | proves |
|---|---|
| `WithNoPepper_HashIsUnmarkedAndVerifies` | pepper-less deployments are untouched, and are not falsely flagged for re-peppering |
| `WithTheLegacySinglePepper_StoredFormatIsUnchanged` | enabling pepper rotation changes nothing about hashes already in the database |
| `AfterRotation_OldHashStillVerifies_AndIsFlaggedForRepeppering` | nobody is locked out by a pepper rotation, and the login path is told to rewrite the row |
| `AHashUnderPepper2_IsNotVerifiableByPepper1Alone` | fail closed — dropping a pepper does not silently degrade to a different one |
| `AnUnmarkedHash_IsReadAsPepper1_NotAsPepperless` | the back-compat rule; the inverse would break every password in a peppered deployment |
| `BackupCodes_CarryThePepperIdAndSurviveRotation` | `sha256:p:` legacy marker still verifies, new codes carry the numeric id, and a code under a dropped pepper fails |
| `UnpepperedBackupCodes_KeepVerifying` | the `sha256:0:` path is unchanged |

### `KeyRotationSweepTests` — against real PostgreSQL (5)

Runs against a database of its own, created and migrated in `InitializeAsync` and dropped
afterwards. Not for tidiness: the sweep is deliberately global, so running it on the shared
collection database would re-encrypt the TOTP secrets every other test seeded under the fixture's
own root and break them. That is worth stating in the report because it is also true in
production — **the sweep is tenant-global by design.**

| test | proves |
|---|---|
| `Sweep_MigratesEveryEncryptedShape_AndPlaintextIsPreserved` | all four columns migrate, every plaintext survives byte-for-byte, `total_pending` goes from ≥4 to 0 |
| `AfterTheSweep_TheOldKeyCanBeDropped_AndSweptValuesStillRead` | the end state of the runbook actually works — key 1 removed entirely, swept values still read |
| `Sweep_IsIdempotent` | re-running after an interruption is safe |
| `RollingBackToKey1_ReportsTheKey2RowsAsPending` | §7.2: rollback does **not** report 0 pending, and sweeping the other way restores the prefix-less format |
| `Sweep_RefusesToRun_WhenTheOriginalKeyIsMissing` | it throws rather than dropping a value it cannot decrypt |

### `KeyRotationEndpointTests` — the wiring (3)

An endpoint that does not route would make the runbook worse than useless.

| test | proves |
|---|---|
| `Status_SuperAdmin_ReportsTheKeyRing` | `GET /admin/key-rotation` routes, returns the ring and all four columns |
| `Status_RegularUser_IsRefused` | a non-super-admin cannot read it |
| `ReEncrypt_WithNoRotationConfigured_IsANoOp` | running the sweep on a deployment that has not rotated rewrites nothing |

### Two bugs these tests caught before they shipped

1. `NeedsRepepper` returned true for every unmarked hash on a pepper-less deployment, because an
   unmarked hash reads as pepper 1 while the active pepper is 0. Every login would have rewritten
   its own hash forever. Caught by `WithNoPepper_HashIsUnmarkedAndVerifies`.
2. `string.Contains(char)` does not translate to SQL in EF Core; the status query threw at runtime
   on the rollback path. Caught by `RollingBackToKey1_ReportsTheKey2RowsAsPending` — which only
   exists because the rollback case was written as a test rather than as prose.

A third, an inverted SQL prefix predicate that would have reported "0 pending" during a rollback,
was caught by review before it ran (§3.2).

---

## 11. Suite

Baseline before this step: **1238 passing**.

```
$ dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
    -p:SonarQubeTargetsImported=true --nologo

Test run for .../RediensIAM.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:  1274, Skipped:     0, Total:  1274, Duration: 3 m 19 s - RediensIAM.IntegrationTests.dll (net10.0)
```

1274 = 1238 baseline + 36 new. Zero failures, zero skipped. The 1238 pre-existing tests were not
modified in substance — 6 test files had a local `byte[]` key variable wrapped in
`new KeyRing(1, …)` to follow the type change, which produces the identical legacy ciphertext, and
`TestFixture` gained a `PostgresConnectionString` accessor.

---

## 12. Effect on the ledger

| id | before | after |
|---|---|---|
| **S-10** | OPEN (deliberate) | **CLOSED** for the HKDF root; **CLOSED with a stated permanent limitation** for the Argon2 pepper (§5); **verified already-supported** for the Hydra system secret (§6) |
| **C-3** | PARTIAL — "recovery is still impossible" | **Recovery exists.** A compromised HKDF root can be retired without data loss via §7.1. The pepper can be rotated with a bounded, quantifiable reset cost. Hydra's secret was always rotatable. Entry narrowing (R-16, digest pinning) is unchanged — this closes the recovery half, not the entry half. |
