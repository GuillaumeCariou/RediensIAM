# Step 15a — Backend residuals

**Branch:** `security/hardening-2026-07-30` · **Working tree, not committed**
**Input:** `.security-hardening/14-finding-ledger.md` §5 (T-07a–d, T-26), §4 (P-08, M7),
§8 "Closed-but-untested" (P-03, P-04)
**Scope:** `src/`, `tests/`, plus `docs/INTEGRATION.md`. Nothing under `deploy/`, `sdk/` or
`frontend/` was touched — `git status` confirms the changes in those trees belong to the two
parallel agents, not to this step.

> Read §9 before trusting anything above it. Two findings here are **product decisions**
> implemented as security fixes because the task asked for all of them; one fix is deliberately
> narrower than the finding's wording; and T-26 came back with one real defect and a clean bill on
> the two things it was actually feared for. All of that is argued below, not asserted.

---

## 0. Suite

```
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true

Passed!  - Failed:     0, Passed:  1238, Skipped:     0, Total:  1238, Duration: 3 m 24 s - RediensIAM.IntegrationTests.dll (net10.0)
```

Baseline was **1221 passing**. 17 tests added (14 in `ResidualFindingsTests`, 3 in `SamlTests`) →
1238, all green, nothing skipped.

The first full run after the code changes was **11 failures**, and the numbers matter because they
are what told me two of the fixes were wrong in a way inspection had not:

| Failures | Cause | Resolution |
|---|---|---|
| 8 | `RequireMfa` now defaults true; fixtures were exercising password-only login | fixtures opt out explicitly (§2) |
| 3 | the P-08 org check refused an org id matching **no** row, turning three not-found branches into 403 | check narrowed to *suspended*, not *absent* (§4) |

---

## 1. T-07a — password floor 8 → 12, and the five paths that bypassed it

### What the finding said, and what was actually wrong

`12 §V2` records one line: `PasswordPolicy.cs:29` is 8, ASVS L2 §2.1.1 wants 12. Raising it is
one character. But before raising anything I grepped every site that writes a password hash, and
**five of them never consulted the policy at all**:

| Path | What it checked before |
|---|---|
| `POST /project/users` (`ProjectController.cs:250`) | `project.MinPasswordLength > 0 && …` — the project's own setting, never the floor. `MinPasswordLength` defaults to 0, so an unconfigured project accepted a one-character password |
| `POST /org/userlists/{id}/users` (`OrgController.cs:398`) | nothing |
| `POST /admin/userlists/{id}/users` (`SystemAdminController.cs:364`) | nothing |
| `POST /api/manage/userlists/{id}/users` (`ManagedApiController.cs:163`) | nothing |
| `PATCH /org/users/{uid}`, `PATCH /org/userlists/{id}/users/{uid}`, `PATCH /admin/users/{id}` (via `UserHelpers.ApplyUpdate`) | nothing |

`PasswordPolicy.cs`'s own class comment claims "Registration, admin-driven creation, invite
completion, password reset and self-service password change all write a password hash — every one
of them must run the same rules." Admin-driven creation did not, and a password written below the
floor is never re-evaluated afterwards. Raising the constant alone would have been a report I could
not have defended.

### What changed

- `src/Services/PasswordPolicy.cs:29-33` — `AbsoluteMinimumLength = 12`, with the ASVS reference
  in the doc comment.
- `src/Controllers/UserHelpers.cs:11-25` — new `PasswordFloorError(string?)`. Returns a
  `BadRequestObjectResult` carrying the existing `{"error":"password_too_short","min_length":12}`
  shape, or null. Null/empty password returns null: that is the invite path, which writes no hash.
- One `if` at each of the five sites above. `ProjectController.CreateUser` instead switched its
  open-coded check to `PasswordPolicyService.EffectiveMinimumLength(project)`, so it now takes
  `max(project setting, floor)` like every other caller.

No new service, no DI change, no constructor churn: the helper is static and the error shape
already existed.

### Test

`ResidualFindingsTests.PasswordFloor_IsTheAsvsL2Minimum` pins the constant.
`AdminCreatedUser_WithAPasswordBelowTheFloor_IsRefused` posts a 10-character password to
`POST /org/userlists/{id}/users`, asserts `password_too_short` + `min_length: 12`, **and asserts no
user row was written** — a check that would have failed before this step.
`AdminSetPassword_BelowTheFloor_IsRefusedAndNotPersisted` covers the `ApplyUpdate` half and asserts
the stored hash is unchanged.

### Test fixtures updated (not weakened)

`NewP@ss!1`, `NewP@ss!2` → `NewP@ssw0rd!1`, `NewP@ssw0rd!2` (`AccountBranchCoverageTests`);
`NewP@ss1!` → `NewP@ssw0rd1!` (`InviteFlowTests`, 3 sites); `P@ssw0rd!1` → `P@ssw0rd!Adm1n`
(`SystemAdminBranchCoverageTests:487`); `P@ssword1!` → `P@ssword1!Long` (`OrgMoreCoverageTests`).
The floor was not lowered anywhere.

Three other short-password fixtures were **left alone deliberately**: `OrgBranchCoverageTests:221`,
`SystemAdminBranchCoverageTests:176` and `ProjectBranchCoverageTests:167,356` assert 404/`no_user_list`
on paths where the guard sits *after* the not-found check, so they still exercise the branch they
were written for. `AuthHardeningRegressionTests:212`'s comment ("passes the hardcoded 8") was
corrected — it was describing behaviour that no longer exists.

### Residual risk

`InstanceConfiguration`/`AppConfig` do not clamp `Project.MinPasswordLength` on the way in, so a
tenant can still store a *lower* number; it just no longer has any effect, because every reader now
goes through `EffectiveMinimumLength`. Existing accounts created under the old floor keep their
passwords — this is an entry check, not a re-hash campaign.

---

## 2. T-07b / T-07c — the two tenant defaults

`src/Data/Entities/Project.cs:17,34`:

```csharp
public bool RequireMfa { get; set; } = true;
public bool CheckBreachedPasswords { get; set; } = true;
```

Neither create path (`ProjectController`, `OrgController`, `SystemAdminController`,
`ManagedApiController`) ever writes either field — I checked, they only appear in the `PATCH`
handlers — so the entity initialiser *is* the policy. **Existing rows are untouched**; this changes
what a project created from now on starts with.

### Test

`ResidualFindingsTests.ANewProject_RequiresMfaAndChecksBreachedPasswords_ByDefault` reads both back
from the database after `CreateProjectAsync`.

### These are product decisions, and I am saying so

`RequireMfa = true` is not a bug fix. It changes what every new tenant's users experience at first
login: no factor enrolled means `requires_mfa_setup` and no session until they enrol. There is a
real argument that this belongs to whoever owns onboarding, not to a security step. I implemented
it because the task asked for all four findings fixed and because the console's own equivalent
(`AppConfig.RequireAdminMfa`) was already defaulted on in step 8 — leaving tenants weaker than the
console is the inconsistency `12 §V2` was pointing at. The opt-out is one `PATCH` field.

`CheckBreachedPasswords = true` is closer to a straight fix, and it is cheap: `BreachCheckService`
fails open on any HIBP outage (`BreachCheckService.cs:33-35`, I-05, deliberate), so the worst case
of the new default is an outbound k-anonymity lookup that returns nothing.

### Cost paid in the suite

Eight tests were exercising password-only login against a project that had never opted out. They
now opt out explicitly, one line each, with a comment saying why:
`LoginTests.ScaffoldAsync`, `WebAuthnLoginTests.ScaffoldAsync`,
`AuthMissingCoverageTests.Login_SendNewDeviceAlertThrows_…`. `AccountUnlockTests:195`'s comment
("default — no MFA enforcement") was corrected.

### Residual risk

`CheckBreachedPasswords` still fails open, so the default buys detection, not enforcement, during
an HIBP outage. That is I-05 and it stays deliberate.

---

## 3. T-07d — SMS enrolment when there is no SMS provider

`src/Program.cs:135` registers `StubSmsService`, whose `IsConfigured` is hard-coded false and whose
`SendOtpAsync` logs and returns. Step 12 recorded a partial mitigation: the login path
(`AuthController.cs:348`), the SMS step-up (`:447`) and registration (`:827`) all consult
`IsConfigured`.

**Enrolment did not.** `POST /account/mfa/phone/setup` stored a pending number, stored an OTP and
called the stub, then answered `{"sent": true}`. The user waits for a code that was never sent. On
a project with `RequireMfa` — now the default, see §2 — a phone factor that cannot deliver is a
lockout, not an inconvenience.

### What changed

`src/Controllers/AccountController.cs:330-335` — the same guard the other three paths use, before
anything is stored:

```csharp
if (!smsService.IsConfigured)
    return StatusCode(503, new { error = "sms_provider_not_configured" });
```

I chose the guard over pretending the factor works, exactly as the task framed it. Writing a real
SMS provider is out of scope and would be a new dependency, a new secret and a new billing
relationship.

### Test

`ResidualFindingsTests.PhoneEnrolment_WithNoRealSmsProvider_IsRefused`. The test fixture's SMS stub
reports `IsConfigured => true` so the other tests can drive SMS flows; it is now a settable property
(`TestFixture.cs:543-547`) and the test flips it to reproduce production, asserts 503 +
`sms_provider_not_configured`, and asserts no message was recorded. Restored in a `finally`.

### Residual risk

Users who already enrolled a phone under the old behaviour still have `PhoneVerified = true` and
will be offered SMS at login — where `IsConfigured` refuses it, so they fall back to another factor
or are stuck if it is their only one. **No migration was written for that population.** Cost: one
query (`SELECT id FROM users WHERE phone_verified`) and a decision about whether to clear the flag;
I did not take that decision unilaterally because it removes a factor from live accounts.

---

## 4. P-08 residual — a suspended org's system-list admin

### The hole

`11b §4` closed session revocation and named what it did not close: `Organisation.Active` is
consulted at login only (`AuthController.cs:140,229`), and `AdminLogin` consults no organisation at
all. An `org_admin` whose grant was made by `AssignOrgAdmin` on a user *outside* the org
(`UserList.OrgId == null`) therefore logs straight back in, and the token they get carries the Keto
tuple `Organisations:{orgId}#org_admin`, which suspension never removed. For them, suspension was a
forced re-login.

### The fix, and why it is at that location

`src/Services/LiveAuthorizationService.cs:76-84`, at the top of `CheckAsync`:

```csharp
if (level != ManagementLevel.SuperAdmin && Guid.TryParse(orgIdClaim, out var claimedOrg)
    && await db.Organisations.AnyAsync(o => o.Id == claimedOrg && !o.Active))
    return false;
```

`CheckAsync` is where `RequireManagementLevelAttribute` and `IntrospectionController` both land, so
one condition covers every management request regardless of what minted the token. Putting it at
login would have missed the whole point — the attack *is* a fresh login.

**The super_admin carve-out is structural, not a special case.** `GetManagementLevel` returns the
caller's highest level; a platform `super_admin` is `ManagementLevel.SuperAdmin`, passes
`level > minimum` on an `OrgAdmin`-gated controller, and is re-checked as `SuperAdmin` — a branch
that takes no organisation. `SystemAdminController` (which owns `/suspend` and `/unsuspend`) is
`[RequireManagementLevel(ManagementLevel.SuperAdmin)]` at class level. So unsuspension cannot be
locked out by this check. That is the risk `11b §4` refused to take without a test; the test is
below.

Two deliberate widenings and one deliberate narrowing:

- **Widened to `ProjectAdmin`.** `CheckAsync`'s ProjectAdmin branch has no org filter at all
  (`db.OrgRoles.AnyAsync(r => r.UserId == userId && r.Role == ProjectAdmin)`), so a system-list
  `project_admin` in a suspended org was the identical hole. Same condition, same line.
- **Cache scope widened.** `IsStillGrantedAsync` previously carried `claims.OrgId` in the cached
  value for `OrgAdmin` only; every non-super_admin verdict is now org-dependent, so the scope is
  `level == SuperAdmin ? "" : claims.OrgId` (`:45-49`). Without this a `project_admin`'s cached
  verdict would leak across orgs.
- **Narrowed to "suspended", not "absent".** An org id that matches no row is *allowed through* to
  the controller, which answers 404. Denying it turned two existing branch-coverage tests
  (`OrgBranchCoverageTests.GetOrgInfo_OrgNotFound_Returns404`,
  `UpdateOrgSettings_OrgNotFound_Returns404`) and
  `ServiceAccountBranchCoverageTests.ListServiceAccounts_ProjectAdminInvalidProjectId_Returns403`
  into 403s — I measured this, it was 3 of the 11 failures on the first full run. Turning every
  not-found into a filter-level forbidden is a different change with a different rationale, and
  P-08 is about suspension. Recorded here rather than done quietly.

### Tests

`ResidualFindingsTests.SuspendedOrg_SystemListOrgAdmin_LosesManagementAccess` — the admin lives in a
system list (`UserList.OrgId == null`), holds an `OrgRoles` grant on the tenant, gets 200 on
`GET /org/info`, the org is suspended, the cache is flushed, and the same client gets **403**.
`SuspendedOrg_SuperAdmin_CanStillUnsuspendIt` — a `super_admin` suspends and then unsuspends through
the real endpoints, both 200, and `Organisation.Active` is read back true. Both halves of 11b §4's
stated risk are now covered.

### Residual risk

- **Up to 30 s of lag.** Suspension does not drop the live-authorisation cache, so an admin who made
  a request in the preceding `CacheTtlSeconds` window keeps their verdict until it expires. That is
  the documented revocation bound for every other grant change in this system
  (`LiveAuthorizationService.cs:27-28`) and I did not special-case suspension. Closing it means
  calling `InvalidateAsync` per admin inside `RevokeOrgSessionsAsync`, which would duplicate the
  Hydra revocation already in that loop; ~5 lines if the 30 s is judged too long.
- Unchanged from 11b: `RevokeSessionsAsync` revokes Hydra login/consent sessions, not access tokens
  already issued. A resource server that does not introspect still honours them to expiry.
- The Keto tuple itself still exists after suspension. Nothing reads it while the org is inactive,
  but a tuple-store dump still shows the grant.

---

## 5. M7 — `token_type_hint`

**Decision: remove it.** `src/Controllers/IntrospectionController.cs:173` no longer declares
`TokenTypeHint`.

The reasoning, in full, because either option was defensible and the ledger asked for a decision
rather than a preference:

RFC 7662 §2.1 makes `token_type_hint` a *lookup optimisation*. The server MAY ignore it and — this
is the part that decides it — MUST NOT reject a token because the hint was wrong; if the hint does
not find the token, the server has to search the other types anyway. RediensIAM has exactly two
token shapes and `ResolveAsync` distinguishes them in constant time from the PAT prefix
(`:129`). So a correct implementation of "honour the hint" is indistinguishable from doing nothing.
Binding it would have added a field, a code path and a test to produce no behavioural difference.

Removing it is not a wire change: form model binding never mapped `token_type_hint` onto
`TokenTypeHint` (naming mismatch — the snake_case JSON policy does not apply to form binding), so
the value was already discarded. Both backend SDKs keep sending it and keep working, unchanged. See
§8 for the one place this *is* visible.

`docs/INTEGRATION.md:216-221` was rewritten from "the server does not read it" (documenting the
lie) to a statement of what the contract now is and why sending it is harmless.

### Residual risk

None functional. If a future RediensIAM grows a token type that is expensive to probe, the hint
becomes worth having again and the record is the place to put it back.

---

## 6. P-03 — the theme-key walk, which nothing exercised

`LoginThemeValidator.cs:67-72` walks every theme key other than `custom_css`/`logo_url` and refuses
values containing `;{}()<>"'\`\\` or longer than 120 characters. The ledger's point was precise: no
test anywhere named `theme_value_invalid_character`, so the entire walk was dead weight as far as
the suite was concerned, and a refactor restoring the early `return` after `custom_css` would
re-arm chain C-6 with everything green.

### Tests added

| Test | Covers |
|---|---|
| `ProjectInfo_ThemeValueCarryingACssPrimitive_IsRefused` (theory ×4: `url(…)`, `#fff;background:url(…)`, `<script>`, `attr(…)`) | the character set, on the project route, plus **not persisted** |
| `ProjectInfo_OverlongThemeValue_IsRefused` | the 120-character ceiling — the other half of `IsUnsafeThemeValue`, which the character test alone leaves uncovered |
| `OrgProjectUpdate_ThemeValueCarryingACssPrimitive_IsRefused` | the org route reaches the same validator |
| `ProjectInfo_OrdinaryThemeValue_IsStillAccepted` | `#123456` and `rebeccapurple` still store — the guard is a filter, not a wall |

All four assert the error code `theme_value_invalid_character` by name, which is what the ledger
said was missing.

### Residual risk (unchanged from 11b §6, restated because it is still true)

`login_theme.providers[]` nested values are still unvalidated — `Login.tsx:229` renders
`<img src={p.logo_url}>` from them. Fixing that decides the fate of `data:` provider icons, which is
a wire-contract call and belongs with whoever owns the login page. Stored hostile themes written
before step 11b were not migrated.

## 6b. P-04 — no test added, and why

The ledger suggests a `helm template` assertion at ~10 lines. `deploy/` is out of this step's scope
and a third agent is working in it; adding a chart assertion from here would collide. **Left
undone.** Cost of leaving it: P-04's only proof remains a live `curl`, so a chart edit that dropped
`adminOnlyPaths` would not be caught by any suite. The `helm template` assertion is the right fix
and it belongs in the `deploy/` agent's scope.

---

## 7. T-26 — SAML XML processing, assessed

The ledger's instruction was explicit: assess XXE, signature wrapping and certificate validation;
do not leave it silently unassessed a second time. Method: decompiled
`ITfoxtec.Identity.Saml2 4.17.0` (the exact assembly restored into this build) with `ilspycmd` and
read the parse and signature paths, then wrote tests against the running ACS for the two attack
classes.

**One real defect found and fixed. XXE and signature wrapping are clean, with evidence.**

### 7.1 XXE — clean

`ITfoxtec.Identity.Saml2.Util.XmlUtil.ToXmlDocument(this string)` — the single entry point every
SAML message string goes through (`Saml2Request.Read:2344` → `xml.ToXmlDocument()`):

```csharp
using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null
});
XmlDocument xmlDocument = new XmlDocument();
xmlDocument.XmlResolver = null;
```

`DtdProcessing.Prohibit` refuses the document outright the moment a `<!DOCTYPE` appears, which
closes external-entity retrieval *and* internal entity expansion (billion laughs) in one move; the
null resolver is belt and braces. The two other `ToXmlDocument` overloads (`XDocument`, `XElement`)
set the same pair. RediensIAM adds nothing of its own here and does not need to.

Evidence I generated: `SamlControllerTests.Acs_ResponseCarryingADoctype_IsRefused` takes a validly
signed response, prepends `<!DOCTYPE Response [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>`,
re-encodes and posts it. Result: 400 `saml_response_invalid`, and no user provisioned.

### 7.2 Signature wrapping — clean, and better defended than most

`Saml2SignedXml` (decompiled, `CheckSignature`/`AssertReferenceValid`/`AssertTransformChainValid`)
enforces, in this order:

1. exactly one `Reference` in `SignedInfo`, else refuse;
2. `SignedInfo.CanonicalizationMethod` must equal the configured method (exclusive C14N by default);
3. `SignedInfo.SignatureMethod` must equal the configured algorithm (**rsa-sha256** by default —
   not SHA-1);
4. **the reference URI must dereference to the very element being validated** —
   `if (Element != GetIdElement(Element.OwnerDocument, idValue)) throw`. This is the canonical XSW
   mitigation: the signature cannot cover one subtree while the SP reads another;
5. the transform chain may contain only enveloped-signature and the configured canonicalization —
   no XPath, no XSLT.

Above that, `Saml2Request.ValidateXmlSignature` selects `Signature` as a **direct child** of the
element under validation (not `//`), and throws if there is more than one. And
`Saml2AuthnResponse.GetAssertionElementReference` requires **exactly one** `Assertion` element in
the whole document, which kills the "append a forged assertion" family before signature checking
even starts. The claims are then read from `assertionElement.OuterXml` — the same element that was
validated, cached in `assertionElementCache`, not re-selected.

Ordering is also right: `Saml2AuthnResponse.Read` calls `base.Read` (which throws
`InvalidSignatureException` on a bad signature) *before* `ReadClaimsIdentity`.

Evidence I generated: `SamlControllerTests.Acs_ResponseWithASecondWrappedAssertion_IsRefused` keeps
the genuine signed assertion — so the signature still verifies — and appends a deep clone. Result:
400 `saml_response_invalid`.

Also verified while I was in there, since the ledger's `CertificateValidationMode.None` lead touches
the same config object:

- `AudienceRestricted` defaults true and `BuildConfigAsync` puts the SP entity id in
  `AllowedAudienceUris` (`SamlService.cs:31`) → `TokenValidationParameters.ValidateAudience` is on.
- `AllowedIssuer` is set to `idp.EntityId` and enforced in `Saml2Request.Read` before anything else.
- Replay: `DetectReplayedTokens` is false (library default) and no `TokenReplayCache` is configured,
  so the library performs no replay detection — but RediensIAM does its own, and it is stronger:
  `SamlController.cs:163` consumes the pending `InResponseTo` record single-use from Redis, and
  binds it to both the IdP and the login challenge (`:171-184`).

### 7.3 Certificate validation — one real defect, fixed

`SamlService.cs:29` sets `CertificateValidationMode = X509CertificateValidationMode.None`. The
decompiled `Saml2CertificateValidator.Validate` is a `switch` in which `None` is `case 0: break;` —
**a complete no-op**.

The comment in `SamlService.cs` defends this on the grounds that the signing certificates are
supplied explicitly, so chain trust is not the trust anchor — the pin is. That reasoning is sound
and I did not change the mode: switching to `ChainTrust` would break every self-signed enterprise
IdP, which is the normal case in SAML.

**But `None` also switches off the validity-period check, and the two configuration paths disagreed
about that.** `ApplyMetadataAsync` filters `descriptor.IdPSsoDescriptor.SigningCertificates.Where(c
=> c.IsValidLocalTime())` (`SamlService.cs:61`). `ApplyExplicitConfig` — the path a tenant uses when
it pastes a PEM instead of a metadata URL — did not check anything. Consequence: an IdP signing
certificate that expired years ago kept authenticating assertions at RediensIAM for ever. Expiry is
one of the two reasons an IdP rotates a signing key; the other is compromise, and a compromised key
is usually retired by letting it expire.

Fixed at `src/Services/SamlService.cs:93-105` — the explicit branch now refuses a certificate
outside `NotBefore`/`NotAfter` with a message naming the window and the remedy. Same branch, same
throw style as the two guards already there (missing PEM, non-HTTPS `SsoUrl`), so it surfaces the
same way: `saml_response_invalid` at the ACS, and a refusal at `/auth/saml/start`.

Test: `SamlServiceUnitTests.BuildConfigAsync_ExpiredExplicitCertificate_IsRefused` builds a
certificate that expired yesterday and asserts the throw.

Severity, honestly: **low**. It needs an IdP whose signing key has expired *and* an attacker who
holds that key. It is a hygiene defect, not the XXE or wrapping hole T-26 was filed for.

### 7.4 What T-26 leaves open, precisely

- **Encrypted assertions were not assessed.** `DecryptMessage` and `Saml2EncryptedXml` are dead code
  in this deployment — `SamlIdpConfig` has no decryption certificate field and
  `Saml2Configuration.DecryptionCertificates` is never populated, so `DecryptMessage` returns
  immediately. If encrypted-assertion support is ever added, the decrypt-then-validate ordering in
  `Saml2Request.Read` needs its own look: `DecryptMessage()` runs **between** the two signature
  validations.
- **No fuzzing.** I tested two specific attack shapes and read the code for the third. A malformed-XML
  fuzz run over the ACS is a different exercise; ~half a day with a corpus, and I did not do it.
- **`I-10` unchanged.** `ReadSamlResponse` is `Read(validate: false)` — confirmed in the decompiled
  binding, `Saml2Binding.ReadSamlResponse:338` — so `Status`, `InResponseTo` and the pending-record
  consumption at `SamlController.cs:156-164` all happen on an **unverified** document, before
  `Unbind` validates. The pending record is consumed either way, which is the unauthenticated
  in-flight-login DoS `05 §Deliberately left` accepted. Still deliberate, still open, and it needs an
  unguessable request id to exploit.

---

## 8. Wire-contract changes

Three contracts have already broken in this release, so every visible change is listed, including
the ones I judge harmless.

| # | Surface | Change | Breaks a client if… |
|---|---|---|---|
| 1 | `POST /org/userlists/{id}/users`, `POST /admin/userlists/{id}/users`, `POST /api/manage/userlists/{id}/users`, `POST /project/users` | new **400** `{"error":"password_too_short","min_length":12}` | it seeds users with passwords under 12 characters. Error shape is the existing one |
| 2 | `PATCH /org/users/{uid}`, `PATCH /org/userlists/{id}/users/{uid}`, `PATCH /admin/users/{id}` | same 400 on `new_password` | same |
| 3 | `POST /account/mfa/phone/setup` | new **503** `{"error":"sms_provider_not_configured"}` when no real SMS provider is wired — which is **every current deployment** | it treats the endpoint as always-succeeding. This is the point of the fix |
| 4 | project reads (`GET /project/info`, `GET /org/projects/{id}`, `GET /admin/projects/{id}`) | **newly created** projects come back with `require_mfa: true` and `check_breached_passwords: true`. Existing projects unchanged | a client assumed new projects start permissive. Login on such a project answers `requires_mfa_setup` |
| 5 | every `[RequireManagementLevel]` endpoint | **403** `{"error":"forbidden","detail":"role_no_longer_granted"}` for a non-super_admin whose org is suspended | intended (P-08) |
| 6 | `POST /api/introspect` | `token_type_hint` removed from the request record. **Not a runtime change** — it never bound — but the **generated OpenAPI schema no longer lists it**, so a client generated from `/swagger` will stop emitting it | a codegen client is regenerated and something downstream expected the field. Nothing in `sdk/` reads it |
| 7 | `/auth/saml/start` and `/auth/saml/acs` for an IdP configured with an explicit expired PEM | now refuse instead of authenticating | a tenant is running on an expired IdP certificate. That is the defect being fixed; it needs an operator to upload the current certificate |

Items 3 and 4 are the ones worth telling users about before a deploy.

---

## 9. Left undone, with cost

| Item | Why | Cost of leaving it |
|---|---|---|
| **P-04 chart assertion** (§6b) | `deploy/` is another agent's scope this step | P-04's only proof stays a live `curl`; a chart edit dropping `adminOnlyPaths` passes the suite |
| **Cache invalidation on suspension** (§4) | The 30 s TTL is the documented bound for every other revocation; special-casing one is a judgement call | A suspended org's admin keeps working for up to 30 s. ~5 lines in `RevokeOrgSessionsAsync` |
| **Migration for already-enrolled phone factors** (§3) | Removes a live factor from real accounts; needs an operator decision | Users whose only factor is an undeliverable phone are stuck at login. One query supplied in §3 |
| **`providers[]` nested theme values** (§6) | Decides the fate of `data:` provider icons — a wire call, and the login page is another agent's tree | Tenant-controlled beacon on the login page. Same class as P-03, low severity |
| **SAML fuzzing and encrypted-assertion review** (§7.4) | Half a day and a corpus; encrypted assertions are unreachable in this deployment | Unknown-unknowns in the XML surface stay unknown. The two named attack classes are covered |
| **I-10** (§7.4) | Pre-existing, explicitly deliberate in `05` | Unauthenticated in-flight login DoS, needs an unguessable id |
| **`MinPasswordLength` not clamped on write** (§1) | Every reader now takes the max with the floor, so a low stored value is inert | Cosmetic: the API echoes back a number smaller than what it enforces |

---

## 10. Files changed

| File | Finding |
|---|---|
| `src/Services/PasswordPolicy.cs` | T-07a |
| `src/Controllers/UserHelpers.cs` | T-07a (`PasswordFloorError`) |
| `src/Controllers/ProjectController.cs` | T-07a |
| `src/Controllers/OrgController.cs` | T-07a (×2) |
| `src/Controllers/SystemAdminController.cs` | T-07a (×2) |
| `src/Controllers/ManagedApiController.cs` | T-07a |
| `src/Data/Entities/Project.cs` | T-07b, T-07c |
| `src/Controllers/AccountController.cs` | T-07d |
| `src/Services/LiveAuthorizationService.cs` | P-08 |
| `src/Controllers/IntrospectionController.cs` | M7 |
| `src/Services/SamlService.cs` | T-26 |
| `docs/INTEGRATION.md` | M7 |
| `tests/…/Tests/Regression/ResidualFindingsTests.cs` | **new** — P-03, P-08, T-07a–d (14 tests) |
| `tests/…/Tests/Auth/SamlTests.cs` | T-26 (3 tests) |
| `tests/…/Infrastructure/TestFixture.cs` | settable `StubSmsService.IsConfigured` |
| `tests/…/Tests/{Account,Auth,Org,System,Regression}/…` (8 files) | fixture updates for the new floor and the new MFA default |

`tests/…/Tests/Regression/PentestFindingsTests.cs` was **not** edited — step 11's proofs stand
unmodified. Nothing was committed.
