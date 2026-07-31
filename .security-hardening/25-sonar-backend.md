# 25 — SonarQube maintainability, backend (`src/` + `tests/`)

Branch `security/hardening-2026-07-30`. Scope was `src/` and `tests/` only; `sdk/`, `frontend/`
and `deploy/` were another agent's and were not touched.

## Counts

Open maintainability issues on project `RediensIAM`, from the SonarQube API before and after:

| | Before | After |
|---|---|---|
| Whole project | 28 | 9 |
| In scope (`src/` + `tests/`) | 25 | 6 |
| Out of scope (`frontend/`, `sdk/`) | 3 | 3 |
| of which HIGH / CRITICAL (`csharpsquid:S3776`) | 8 | **0** |

All six that remain in scope are deliberate — reasoning in the last section.
Build warnings from `dotnet build` fell from 7 to 2 over the same change (the 2 being S2139 and
SCS0016, both listed as deliberate below).

## Suite

1345 passing at every checkpoint. Nothing was skipped, quarantined or rewritten.

| Checkpoint | Result |
|---|---|
| Baseline, before any edit | 1345 passed, 0 failed |
| After the seven S3776 extractions | 1345 passed, 0 failed |
| After the MEDIUM/LOW/INFO batch | 1345 passed, 0 failed |

The two S3776 batches were verified together because the extractions are independent of each
other and every one is a pure move — no call ordering, no condition and no early return changed
in any of them.

---

## HIGH — `csharpsquid:S3776` cognitive complexity (8 of 8 cleared)

The rule was over the limit in each case because these methods absorbed this audit's fixes. Every
extraction below moves a contiguous block into a private method called from exactly the point the
block used to occupy. No branch was merged, reordered, inverted or removed, and no short-circuit
changed sides.

### `src/Services/RedirectValidator.cs:29` — `TryReconstruct`, 16 → ~11

Extracted the trusted-origin scan into `IsTrustedOrigin(origin, trustedOrigins)`.

The loop is copied verbatim: blank entries are still `continue`d rather than matched (an unset
config value must not make everything trusted), the comparison is still `TrimOrigin(raw)` vs the
parsed origin under `OrdinalIgnoreCase`, and it still stops at the first match. The only change is
that `allowed = true; break;` became `return true;` and the post-loop `if (!allowed) return false;`
became `if (!IsTrustedOrigin(...)) return false;` at the same position — before the `UriBuilder`
reconstruction, so a rejected origin still never reaches the rebuild. The backslash rejection, the
relative-path short-circuit, the scheme allowlist and the CR/LF strip are untouched.

### `src/Services/PasswordService.cs:132` — `VerifyBackupCode`, 18 → ~3 (helper ~13)

Extracted the stored-format parsing into `TryResolveBackupCodeKey(rest, out storedHex, out keyForHash)`.

The body is the existing code unchanged, including the fail-closed branch that matters:
`if (pepperId < 1 || !TryGetPepperStrict(pepperId, out keyForHash)) return false;`. A code stored
under a pepper that is no longer configured still fails rather than falling back to the unpeppered
key — that is now a `return false` from the helper which the caller turns straight into `return
false`, which is the same observable result on the same input. The `keyId == "0"`, `"p"` and
numeric branches keep their order and their precedence, the legacy keyId-less format still resolves
to `DefaultBackupCodeKey`, and the `FixedTimeEquals` comparison stayed in the caller so the
constant-time path is unchanged. The `out` parameters are pre-assigned `""` / `[]` for definite
assignment; those values are only reachable on the `false` return, where the caller reads neither.

### `src/Controllers/WebhookController.cs:332` — `IsPrivateIp`, 19 → 3 (helpers 6 and 7)

Split the two address-family bodies into `IsPrivateIpv6(ip)` and `IsPrivateIpv4(bytes)`.

This is the SSRF reserved-range check, so it was moved character for character. The IPv4-mapped
IPv6 normalisation stays *above* the family test in the caller, which is the whole point of the
line — if it moved below, every private IPv4 range would be reachable again by writing it in
`::ffff:` form. `fc00::/7`, `2001:db8::/32`, CGNAT `100.64/10`, `192.0.0.0/24`, `198.18/15`,
`169.254`, `0.0.0.0/8` and the `>= 224` catch-all are all still there in the same order.

### `src/Controllers/OrgController.cs:151` — `UpdateProject`, 16 → ~8

Extracted the eight unconditional field copies (`Name` … `AllowedEmailDomains`) into
`ApplyProjectFields(project, body)`, called where they used to sit.

The security-relevant lines stayed in the method body and in their original order: the
`MfaDowngradeGuard.CheckAsync` gate before any mutation, `ApplyDefaultRoleAsync` and its
`invalid_default_role` return, the `LoginThemeValidator.Validate` call before `ApplyLoginTheme`
(this route's fix — the org path used to reach `ApplyLoginTheme` with no validation at all), and
the `IpAllowlist` / `CheckBreachedPasswords` assignments after it. Only inert `if (x != null) a = b`
copies moved.

### `src/Controllers/OrgController.cs:629` — `UpdateOrgListManager`, 19 → ~11

Three extractions, all mechanical:

- the `targetLevel` switch expression → `ManagementLevelForRole(roleName)`, a pure mapping with
  the same four arms and the same `ManagementLevel.None` default;
- the scope-in-org check → `ValidateScopeIsInOrgAsync(newScopeId, currentScopeId, orgId)`,
  keeping the guard `newScopeId != null && newScopeId != currentScopeId` so an unchanged or absent
  scope is still not re-queried, and still returning `BadRequest project_not_in_org`;
- the twice-repeated Keto subject ternary → `KetoSubject(userId, scopeId)`.

The three authorisation guards above them are untouched and still run in the same order:
`cannot_modify_own_role`, `cannot_grant_super_admin`, and the `KnownManagementRoles` allowlist that
closed the bypass where this endpoint skipped `AssignManagementRoleAsync` entirely. The
`actorLevel == None || targetLevel < actorLevel` comparison is unchanged and still reads the level
from `keto.GetActorManagementLevelForOrgAsync`, not from claims.

`KetoSubject` is called at exactly the two points the ternaries were evaluated — once before the
mutation for the delete, once after it for the write — so the old and new tuples are still built
from the pre- and post-mutation `ScopeId` respectively. `Guid?` interpolates identically to `Guid`
for a non-null value, so the emitted strings are byte-identical.

### `src/Controllers/ProjectController.cs:106` — `ApplyProjectFields`, 16 → 10 (helper 6)

Split the six password-policy copies into `ApplyPasswordPolicyFields(project, body)`, called from
where they were. This is the password floor, so it is worth being explicit: `MinPasswordLength`
still goes through `Math.Max(0, …)`, the five `Require*` flags still copy only when
`HasValue`, and the call sits between `AllowedEmailDomains` and the `ClearEmailFromName` /
`EmailFromName` if-else, which is where those lines were. The if-else pair was kept together in the
parent — splitting an `else if` across methods is exactly the kind of edit that changes a guard.

### `src/Program.cs` — top-level file, 20 → ~9

The composition root, so only two blocks moved and both are leaves with no ordering role:

- the `HostFilteringOptions` `PostConfigure` body → `ReplaceWildcardAllowedHosts(o, cfg)`. The
  registration stays at the same line, so it is still a `PostConfigure` and still runs after
  configuration binding — an operator-supplied `AllowedHosts` list still wins, because the
  `if (!o.AllowedHosts.Contains("*")) return;` early exit moved with it.
- the two production-URL warnings → `WarnOnNonHttpsProductionUrls(logger, cfg)`, called from
  inside the existing `if (app.Environment.IsProduction())`. Same two messages, same levels
  (`LogError` for `PublicUrl`, `LogWarning` for `AdminSpaOrigin`), same order.

Nothing else in the file was touched. `AddDataProtection().PersistKeysToStackExchangeRedis(…)
.ProtectKeysWithRootKey(appConfig)` is in the same place in the same chain; the middleware
pipeline order (`AppExceptionMiddleware` → `UseForwardedHeaders` → security headers → Swagger →
metrics → session → CORS → static → `UseRouting` → the two `GatewayAuthMiddleware` branches →
`MapControllers`) is unchanged, including the `UseWhen` that has to sit after `UseRouting` to tell
an API call from an SPA navigation.

---

## MEDIUM / LOW / INFO fixed

| File | Rule | Change |
|---|---|---|
| `src/Controllers/AuthController.cs` | S1144 | Deleted the unused `breachCheck` bundle-unwrap property. Nothing read it; `BreachCheckService` is still injected and still used via `PasswordPolicyService`. |
| `src/Controllers/AuthController.cs` | S1192 | `mfa_setup_required` (×4) → `private const string MfaSetupRequired`. Same string reaches the session key and the metric label. |
| `src/Services/SocialLoginService.cs` | S1192 | `userinfo_endpoint` (×4) → `private const string UserInfoEndpoint`. Same discovery-document property name and same `EnsureSafeEndpointAsync` label. |
| `src/Middleware/GatewayAuthMiddleware.cs` | S1144 | Deleted the dead private `GetManagementLevel` extension. It was already unreachable — the S-1 fix pointed its three callers at `GetGrantedLevel` / `GrantedLevel.ClaimedLevel`. Its rationale (why there is no general claims→level reader, R-22, P-01) was folded into the `GetGrantedLevel` doc comment so it survives; the stale duplicate doc block above it went with it. `StructuralDebtTests` asserts no *public* `GetManagementLevel` exists and stays green — a deleted method satisfies that as well as a private one. |
| `src/Data/RediensIamDbContext.cs` | S3267 | The audit-log tamper check became `FirstOrDefault(e => e.State is Modified or Deleted)` + `if (tampered != null) throw`. It threw on the first offending entry before and throws on the first offending entry now; the message is unchanged. (The intermediate `foreach … .Where(…)` form tripped S1751 — "loop that never iterates twice" — so it went to `FirstOrDefault`, which is what the loop actually was.) |
| `src/Controllers/IntrospectionController.cs` | CS1587 ×2 | XML doc comments sat on two positional record parameters, where they are not valid. Folded into the record's `<summary>`. Documentation only — the record's shape and serialisation are untouched. (`<param>` tags were tried first and traded 2 warnings for 8 × CS1573, so the prose form won.) |
| `src/Program.cs` | CA1873 | Wrapped the key-ring startup log in `if (logger.IsEnabled(LogLevel.Information))`, so the two `string.Join` calls are not evaluated when Information is off. Identical output when it is on. |
| `src/Services/KeyRotationService.cs` | CA1873 | Same guard on the sweep-completion log. |
| `src/Services/LoginThemeValidator.cs` | CA1870 | `ForbiddenValueChars` went from `const string` to a cached `SearchValues<char>`. `IndexOfAny(SearchValues<char>)` has the same semantics as `IndexOfAny(ReadOnlySpan<char>)` — first index of any listed char — so the same theme values are refused. The character set `;{}()<>"'`\` is unchanged, including the `(` and the backslash that are the ones that matter. |
| `tests/…/Security/KeyRingProtectionTests.cs` | CA1859 | Test helper `Dumped()` returns `List<XElement>` instead of `IEnumerable<XElement>`. |
| `tests/…/Regression/FlowStateRegressionTests.cs` | SYSLIB1045 | The SAML `AuthnRequest` ID regex moved to a `[GeneratedRegex]` partial method. Same pattern, compile-time generated. |

---

## Deliberately left (6)

### `src/Services/KeyRotationService.cs` — CA1847 ×3, `Contains(":")` → `Contains(':')`

**Left.** These three calls are inside EF Core `IQueryable` predicates that are translated to SQL,
not executed in .NET, and the comment block directly above them documents why the predicate has to
be *exact* rather than a superset: the sweep pages with `Take(BatchSize)` and stops on an empty
page, so a row the database returns and the application then filters out stalls the re-encryption
sweep short of the end. Swapping the overload changes what the provider translates. The upside is
an INFO-level micro-optimisation on an expression that never runs as .NET code; the downside is a
key-rotation sweep that silently stops early. Not worth it.

### `src/Program.cs:231` — S2139, log-and-rethrow

**Left.** This is the fail-fast on database migration failure after 12 attempts. It logs
`LogCritical` with the full exception *and* rethrows, which is precisely what the rule objects to —
but here both halves are wanted: the log is the operator-facing diagnostic, and the rethrow is what
aborts startup instead of serving traffic against a half-migrated schema. Satisfying the rule means
either swallowing the exception (the app comes up broken) or wrapping it in a new exception type
(changes what escapes startup, and adds nothing the `LogCritical` message does not already say).

### `src/Services/ControllerServices.cs:78` — S107, 8-parameter constructor

**Left.** `OrgAdminServices` is a DI bundle that exists *to* satisfy S107 on the controllers that
take it; its own constructor is a list of injected services, not a call signature anyone writes by
hand. Splitting it into two bundles to get from 8 to 7 moves the count without reducing anything,
and adds an indirection to every consuming controller.

### `src/Controllers/IntrospectionController.cs:72` — SCS0016, CSRF

**Left — false positive.** The analyser fires on `[HttpPost]` + `[FromForm]` with no antiforgery
token. CSRF requires ambient credentials; this endpoint has none. `Introspect` returns 403
`service_account_required` unless `IsServiceAccountCaller()` passes, which reads a bearer token, and
a browser will not attach one cross-site. The form encoding is not a choice either — RFC 7662
specifies form-encoded parameters. Adding antiforgery here would break every conforming resource
server for no gain.

## Out of scope, still open (3)

`frontend/admin/src/components/layout/CommandPalette.tsx` (typescript:S6819 ×2) and
`sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs` (S1075). Another agent owns those
trees; untouched.
