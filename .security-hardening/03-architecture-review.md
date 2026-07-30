# Step 3 — Architecture Review

**Target:** RediensIAM — multi-tenant OIDC identity provider
**Branch:** `security/audit-2026-07-28`
**Inputs:** [`01-vulnerability-scan.md`](01-vulnerability-scan.md) (R-01…R-32, I-01…I-11),
[`02-threat-model.md`](02-threat-model.md) (TB-1…TB-8, C-1…C-9, T-N1…T-N6),
[`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md), [`../docs/INTEGRATION.md`](../docs/INTEGRATION.md)
**Method:** read the code against the documented intent; judge structure, not instances.

> **What this document is not.** Steps 1 and 2 catalogued 32 findings and 6 additional threats.
> This document adds no finding IDs. Every defect named here is already named there; the question
> asked is *why this class of defect keeps appearing*, and what shape the system would have to
> take for it to stop. Where a control is well designed I say so and stop — the scan's conclusion
> that the crypto and injection posture is solid is correct and I found nothing to add to it.
>
> **Line-number note.** `src/Controllers/SystemAdminController.cs` moved ~25 lines around the
> `POST /admin/hydra/clients` handler since the scan. Symbols cited here were re-located by name.
> T-N1 is closed by the reserved-prefix guard; it survives in this document only as an example of
> a *class*, not as an open issue.

---

## 0. The one-sentence assessment

RediensIAM's components are the right components and its cryptography is genuinely good; what it
lacks is a **spine** — there is no single place where "who is this, what may they do, and whose
tenant is this" is decided, so each of those three questions is answered independently in every
controller, and each independent answer is a chance to get it wrong. Every high-severity finding
in steps 1 and 2 except the deployment-layer ones (R-16, R-06, R-02) is an instance of that one
structural fact.

Concretely, the same authorisation question is answered in four different places with four
different levels of rigour:

| Where | What it checks | Live? |
|---|---|---|
| `src/Filters/RequireManagementLevelAttribute.cs:20-48` | claimed level ≤ required, then Keto | **yes** |
| `src/Controllers/ServiceAccountController.cs:28` (`Level => Claims.GetManagementLevel()`) | claimed level only | **no** (R-22) |
| `src/Controllers/IntrospectionController.cs:53-56` | claimed management level, strips on failure | yes, three names only (T-N3) |
| An external RS after local JWKS validation | nothing | **no** (R-23, C-1) |

Four implementations of one decision is not a bug list. It is the absence of a choke point.

---

## 1. Trust boundaries and service decomposition

### 1.1 What is correctly placed

- **Hydra and Keto are used, not reimplemented.** `KetoService` writes JSON tuples
  (`src/Services/KetoService.cs:30-38`), never string-concatenated relations, so there is no
  relation injection; tenant role names are prefixed into `role:{name}`
  (`:138`, `:168`, `:274`) which keeps them structurally distinct from `manager`, `org_admin`,
  `super_admin`. That prefix is a real namespacing decision made in the right place and it holds.
- **The audience gate is the correct boundary object.** `GatewayAuthMiddleware.IsManagementAudience`
  (`src/Middleware/GatewayAuthMiddleware.cs:65-76`) asks "was this token minted for the management
  surface?", which is a question about the *token*, not about its contents. This is the control
  that contains R-23 inside RediensIAM, and it is architecturally the right shape: a token issued
  for `client_{projectId}` cannot reach `/admin` no matter what its claims say.
- **Fail-closed defaults are consistent.** Keto unreachable → deny
  (`src/Services/LiveAuthorizationService.cs:50-56`); Keto non-2xx → deny (`KetoService.cs:25`);
  missing `App:TrustedProxies` in Production → refuse to start (`src/Program.cs:412-417`); Hydra
  unreachable during project creation → `502` and roll back (`src/Controllers/OrgController.cs:119-126`).
  These are deliberate and correct.
- **`automountServiceAccountToken: false`** (`deploy/rediensiam/templates/deployment.yaml:18`) —
  the pod has no Kubernetes API identity it doesn't need. Correct, and rarer than it should be.

### 1.2 The public/admin port split is a deployment boundary described as a trust boundary

`docs/ARCHITECTURE.md:42` and its trust-boundary table present `:5000` and `:5001` as separate
boundaries. In the code they are one process, one DI container, one middleware chain, one Postgres
role, one set of secrets. The separation is enforced by predicates on the local port
(`src/Program.cs:308` for Swagger, `:348-349` for `/metrics`) and by path prefixes
(`:326` `protectedPrefixes`, `:334-338` the `/admin` branch).

That is defence in depth, and it is worth keeping. It is not a trust boundary, and treating it as
one is what produced R-05 (NodePort on all interfaces exposes both), I-02 (a `/admin` GET without
an `Authorization` header skips the middleware entirely, safe only because every controller
currently carries a class attribute), and the unauthenticated `/swagger` + `/metrics` on that port.

**Judgement:** either make it real or stop claiming it.
- *Make it real* — a second `Deployment` running the same image with `IAM_SURFACE=admin`, its own
  Service (ClusterIP), its own NetworkPolicy, its own Postgres role, and no public ingress. Cost:
  one Helm template and a startup switch that refuses to map public routes. Benefit: R-05 becomes
  structurally impossible rather than a values-file setting, and the `/metrics` and `/swagger`
  exposure questions disappear.
- *Or* — document `:5001` as convenience only and treat the audience gate as the sole boundary,
  which means it must become default-deny (§2).

The current middle position is the worst of the two: operators reason about it as isolation, and
the code does not deliver isolation.

### 1.3 The boundary that does not exist: claim issuance

There is no component that owns "what goes into an issued token". Role names travel from
`db.UserProjectRoles` into Hydra's consent session in one expression with nothing in between:

```
src/Controllers/AuthController.cs:663-677
    var roles = await db.UserProjectRoles… .Select(r => r.Role.Name).ToListAsync();
    var session = new { access_token = new { org_id, project_id, user_id, roles } };
    await hydra.AcceptConsentAsync(consent_challenge, session, req.RequestedScope);
```

A controller action is doing the job of a claims policy. There is no allowlist, no reserved-name
check, no namespacing, no size bound, and no place to add one that would apply everywhere — the
admin branch a few lines above (`:620-652`) builds a *different* session object with its own
rules. R-23 is not a missing `if`; it is a missing component. T-N3 survives fixing R-23 for
exactly the same reason: with no claims assembler, "namespace the role" has nowhere to live.

**This is the single highest-value structural addition in the document.** See §4.

### 1.4 Is the backend doing authorisation work that belongs in Keto?

Partly — and worse, it is doing it *in both places*, which is the failure mode neither store can
detect.

- `LiveAuthorizationService.CheckAsync` (`src/Services/LiveAuthorizationService.cs:63-87`) resolves
  `ProjectAdmin` as *"Keto says manager of some project **OR** `db.OrgRoles` has a row"*.
  `KetoService.GetActorManagementLevelForOrgAsync` (`:89-98`) does the same mixed resolution.
  Two authoritative stores for one decision means neither is authoritative.
- Grants are dual-written with a best-effort compensating delete
  (`KetoService.cs:206-221`, `:139-152`): tuple first, row second, `catch` → delete the tuple. If
  the process dies between the write and the catch, the tuple survives with no row. That is
  precisely the shape of R-01 (the orphaned `org_admin` tuple), which was closed by adding
  `DeleteAllOrgTuplesAsync` (`:61-65`) — a *cleanup* for one instance of a *class* that the
  dual-write still generates. There is no reconciler and no outbox.
- The `ProjectAdmin` Keto check is object-less: `HasAnyRelationAsync(Projects, manager, subject)`
  (`:82`) — "manager of *something*". Per-project scoping is then done by each controller
  (`src/Controllers/ProjectController.cs:54,65`). So the one component designed to answer scoped
  relation questions is being used as a boolean, and the scoping is back in application code
  (R-22 residual 3).
- Conversely, tenant role tuples *are* written to Keto (`Projects:{id}#role:{name}`, `:138`) and
  **nothing ever checks them**. The data is in the right store; the decisions are made from a
  string list in a JWT instead. `/api/authorize` (`src/Controllers/IntrospectionController.cs:74-88`)
  is the endpoint that would use them properly — and it forwards caller-supplied namespace/object/
  relation unmodified (T-N6).

**Judgement.** Pick one authority per question and make the other a projection:
1. **Management authority (super/org/project admin): Keto is authoritative.** Delete the
   `db.OrgRoles` fallback from `LiveAuthorizationService.cs:83` and `KetoService.cs:95` and write
   the missing tuples in a migration. `org_roles` stays as the display/scoping record, not as an
   answer. This removes the "two truths" problem and makes `InvalidateAsync` sufficient.
2. **Per-project tenant roles: Postgres is authoritative** (it holds `Rank`, `Description`, the
   project FK), and the Keto tuples become a projection maintained by an outbox — or are dropped
   entirely, since nothing reads them today.
3. **Resource decisions: Keto, via `/api/authorize`, scoped** (§3.4).

Either direction is defensible. What is not defensible is the current arrangement, where a grant
can exist in one store and not the other and no code path will ever notice.

---

## 2. The claims-vs-live-authorisation split

### 2.1 Diagnosis: "live check on privileged paths" is a convention, and conventions decay

The mechanism is sound. `RequireManagementLevelAttribute` reads the claimed level, refuses if it
is insufficient, then re-verifies against Keto with a 30-second cache
(`src/Filters/RequireManagementLevelAttribute.cs:31-44`, `LiveAuthorizationService.cs:27,41-60`).
The comment at `LiveAuthorizationService.cs:9-19` states the invariant precisely. It works.

It is also **opt-in**, and it has already been forgotten once. Seven controller-level usages exist:

```
src/Controllers/OrgController.cs:19          OrgAdmin
src/Controllers/ProjectController.cs:14      ProjectAdmin
src/Controllers/WebhookController.cs:18,171  OrgAdmin / SuperAdmin
src/Controllers/ManagedApiController.cs:18   SuperAdmin
src/Controllers/SystemAdminController.cs:16  SuperAdmin
src/Controllers/SystemHealthController.cs:27 SuperAdmin
```

`ServiceAccountController` is not in that list (`src/Controllers/ServiceAccountController.cs:18-24`
declares only `[ApiController]` and `[Route]`) and instead reads
`private ManagementLevel Level => Claims.GetManagementLevel();` (`:28`) — the token snapshot — on
every action, including the one that mints a non-expiring credential on a `__system__` service
account. That is R-22, and it is the controller that *terminated* the R-01 chain. The most
security-conscious controller in the codebase is the one that forgot.

The same shape appears one layer down: `GatewayAuthMiddleware` runs on `/admin` only when an
`Authorization` header is present or the verb is not GET (`src/Program.cs:334-338`), safe today
only because every `/admin` controller carries an attribute (I-02).

Three independent "safe because everyone remembered" statements is a design, and it is the wrong
one for an IdP.

### 2.2 The root cause is a type, not a filter

```csharp
// src/Middleware/GatewayAuthMiddleware.cs:87-93
public static ManagementLevel GetManagementLevel(this TokenClaims claims)
{
    if (claims.Roles.Contains(Roles.SuperAdmin))   return ManagementLevel.SuperAdmin;
    …
}
```

`GetManagementLevel()` is a public extension method that turns unverified token content into a
`ManagementLevel` — the *same type* that `LiveAuthorizationService` produces a verified answer
about. Nothing in the type system distinguishes "the token claims SuperAdmin" from "Keto confirms
SuperAdmin thirty seconds ago". `ServiceAccountController.cs:28` is a one-line call that reads as
correct at every review, because the value it produces is indistinguishable from the checked one.

`TokenClaims.Roles` (`src/Models/TokenClaims.cs:8`) is a bare `List<string>` with the same problem
one level further out.

### 2.3 Structural answer

Two changes, both small, that convert the convention into an invariant:

**(a) Default-deny at the pipeline.** Register the management filter globally and require an
explicit opt-out rather than an explicit opt-in:

```csharp
// Program.cs — AddControllers(o => o.Filters.Add<RequireManagementLevelAttribute>(...))
// default: ManagementLevel.None → deny unless the action/controller declares otherwise
```

with a `[PublicSurface]` marker for `AuthController`, `SamlController`, `/health`, `/admin/config`
and the account self-service routes. A new controller then fails closed by construction. This
closes I-02's class and would have closed R-22 the day `ServiceAccountController` was written.

**(b) A verified-level type that cannot be constructed without a live check.**

```csharp
public readonly record struct GrantedLevel   // no public ctor
{
    internal GrantedLevel(ManagementLevel l) => Value = l;
    public ManagementLevel Value { get; }
}
// LiveAuthorizationService is the only producer:
public Task<GrantedLevel?> ResolveAsync(TokenClaims claims);
```

Make `GetManagementLevel()` `internal` to the filter and the introspection strip. Every access
decision then takes a `GrantedLevel`, and `ServiceAccountController.cs:39-44`'s
`Level switch { SuperAdmin => true, … }` becomes a compile error until it asks for a real one.
This is the smallest change that makes the *class* of defect unrepresentable rather than merely
absent. It also removes the need to remember which controllers are "privileged" — every controller
that wants a level must obtain a checked one.

**(c) While there:** `LiveAuthorizationService.cs:41`'s cache key is `authz:{userId}:{level}` but
the `OrgAdmin` branch (`:74-75`) is a function of `claims.OrgId`, which is not in the key. Put the
org in the key. One line, closes R-22 residual 2 permanently rather than relying on "`org_id` is
minted server-side today".

**(d) Service accounts.** `LiveAuthorizationService.cs:36` short-circuits `IsServiceAccount` to
`true` on the grounds that `PatService` re-checks liveness per call. That is now **true** —
`PatService.IntrospectAsync` re-validates on cache hit (`src/Services/PatService.cs:57-68`,
`IsStillLiveAsync` at `:155-175`), which is better than `docs/ARCHITECTURE.md:70` claims (I-06).
But "the account is live" is not "the role is granted": a SA's roles come from
`ServiceAccountRole` rows read at cache-fill (`:101-124`) and only `InvalidateServiceAccountAsync`
(`:139-148`) refreshes them. Under the `GrantedLevel` design, the SA path needs its own producer
that re-reads roles, not a `return true`.

---

## 3. Tenant isolation as a structural property

### 3.1 Diagnosis: isolation is a per-query habit

`OrgController.cs` alone contains 81 occurrences of `OrgId`, almost all of them a manually appended
predicate: `… && p.OrgId == OrgId`, `… && ul.OrgId == orgId`, `… && u.UserList.OrgId == OrgId`
(`src/Controllers/OrgController.cs:139,201,278,297,480,533,561,…`). `ProjectController` and
`WebhookController` do the same with their own derived scope
(`ProjectController.cs:26,54,65`; `WebhookController.cs:28,35,82,97,…`).
`RediensIamDbContext.OnModelCreating` (`src/Data/RediensIamDbContext.cs:29-32`) applies entity
configurations and **no query filters**.

So tenant isolation is roughly 200 independent opportunities to forget a conjunct, in a product
whose entire value proposition is that boundary. The threat model is right that TB-8 "has no
network representation" — it also has no *code* representation. There is no type, no filter, no
constraint, no test that can fail; there are only conjuncts.

The codebase already documents one instance of the resulting bug class, in a comment:

```
src/Controllers/ServiceAccountController.cs:29-33
// Guid.Empty, never null. A null here compared equal to UserList.OrgId IS NULL — the
// __system__ list — so a token whose org_id failed to parse gained access to the most
// privileged service accounts in the deployment.
```

A parse failure produced access to the deployment's most privileged objects, and the fix was a
convention ("every other controller already uses `Guid.Empty`") applied by hand in each file.

### 3.2 Can it be made a schema property? Yes, and the platform already provides it

**Phase 1 — EF Core global query filters (native, ~30 lines).** Add a scoped `ITenantContext`
populated by `GatewayAuthMiddleware` alongside `ctx.Items["Claims"]`, and in `OnModelCreating`
attach `HasQueryFilter(e => tenant.IsUnscoped || e.OrgId == tenant.OrgId)` to every org-owned
entity (`Project`, `UserList`, `OrgRole`, `Webhook`, `OrgSmtpConfig`, `AuditLog`, and `User` via
`UserList`). The filter applies to every LINQ query in the process, including ones written next
year.

The system paths that legitimately cross tenants — bootstrap (`Program.cs:280-297`), the retention
sweep (`src/Services/AuditLogRetentionService.cs:35-54`), SuperAdmin listings
(`SystemAdminController`), the login/consent path before a tenant is known — then need an explicit
`IgnoreQueryFilters()`. **That is the point:** a greppable list of ~15 deliberate exceptions is an
auditable artefact; 200 hand-written conjuncts are not. Reviewers get a rule they can enforce:
*a new `IgnoreQueryFilters()` requires justification; a missing `&& OrgId ==` is now impossible.*

**Phase 2 — Postgres Row-Level Security.** Filters live in the ORM and therefore do not survive
raw SQL (`SystemAdminController.cs:172`, `SystemHealthController.cs:63,71`) or a future service
that talks to the same database. RLS with `SET LOCAL app.org_id` per request survives both. It
also forces the prerequisite that C-4 needs anyway: **the app, Hydra and Keto must stop sharing
the `iam` Postgres role** (`deploy/deploy.sh:118,126,134`). Do the role split for R-15/C-4, and
RLS becomes cheap.

**Phase 3 — typed identifiers.** `readonly record struct OrgId(Guid Value)` and `ProjectId`
likewise. This kills the `Guid.Empty`/`null` conflation class above outright, and removes the
`Guid.TryParse(Claims.OrgId, …)` boilerplate repeated in five controllers
(`OrgController.cs:42`, `ProjectController.cs:26`, `WebhookController.cs:28`,
`ServiceAccountController.cs:33`, `AccountController.cs:104`). Lowest priority of the three;
highest long-term leverage on readability.

### 3.3 What this does *not* fix — and why T-N3 needs §4

Query filters scope what the backend *reads*. They say nothing about what the token *asserts*.
T-N3 (tenant A's role `admin` and tenant B's role `admin` are byte-identical in a downstream
`ClaimsPrincipal`) lives entirely outside the database. It is a *claims* problem, not a *query*
problem, and it is fixed in §4 or not at all. Any remediation plan that treats "tenant isolation"
as one work item will fix the queries, declare victory, and leave C-1's second path open.

### 3.4 Tenant scoping of the introspection surface

`IntrospectionController` has no tenant concept at all: `IsServiceAccountCaller()` (`:109-111`)
asks only "is this a service account", and `ResolveAsync` (`:93-107`) resolves any token the
deployment ever issued (T-N6). Structurally the fix is not a check inside the controller — it is
that the *caller's* tenant is available (`Caller.OrgId`, set by `PatService` at `:116`) and simply
unused. Under phase 1 above, `ResolveAsync`'s Hydra path should compare the resolved token's
`org_id` to the caller's and answer `{"active": false}` on mismatch — an RFC 7662-legal answer
that leaks nothing. `/api/authorize` should derive the namespace/object from the *token*, not from
the caller's request body, or at minimum reject `Namespace == "System"` and any object the caller's
org does not own.

---

## 4. The token and claim contract as a public API

### 4.1 It is a public API and it is not treated as one

`ext.roles` is consumed by resource servers RediensIAM cannot inventory, cannot patch, and does
not know exist (`docs/INTEGRATION.md:230` documents no update path; `02-threat-model.md` §10 makes
the same point). That makes the claim set a **published interface with unbounded consumers and no
versioning**. It is currently assembled inline in a controller action (§1.3), has no schema, no
`ver` field, and no compatibility policy.

The product also ships the sink alongside the source: the .NET SDK maps every returned role onto
`ClaimTypes.Role` (`sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs:109`), making
`[Authorize(Roles = "super_admin")]` the natural idiom, and the Rust SDK's own doc-comment example
is `info.has_role("super_admin")` (`sdk/rust/rediensiam-client/src/lib.rs:18`).

### 4.2 The contract, stated

**Must be present and authoritative:**
`iss`, `sub` (opaque, globally unique — the current `orgId:userId` compound at
`AuthController.cs:631-641` is fine but must stay opaque to consumers), `aud`, `exp`, `iat`, `jti`,
`scope`, `org_id`, `project_id`.

**`aud` must become mandatory and per-resource-server.** `TokenClaims.Audiences`
(`src/Models/TokenClaims.cs:18`) is documented as "often empty — Hydra only sets it when
requested". An audience-less bearer token is a bearer token for *everything*; it is what makes
C-1's blast radius the set of all relying parties rather than one. Requesting an audience per RS
and having the SDKs reject tokens whose `aud` excludes them is the single change that bounds every
token-theft chain in step 2.

**Must never be present:**
- Bare tenant-controlled strings in a claim whose name invites comparison to a constant. Either
  namespace them (`{project_id}/{name}`) or move them out of `roles` into
  `tenant_roles: [{project_id, name}]`. This is T-N3's only structural fix.
- Management role names (`super_admin`, `org_admin`, `project_admin`) in any token minted for a
  `client_{projectId}` client — the tenant-facing token should be *incapable* of expressing
  RediensIAM's own management authority, not merely unlikely to. Reserve the names at role
  creation (R-23) **and** filter them at assembly, because reservation only protects roles created
  after the fix.
- Anything the issuer cannot re-verify at introspection time. The strip at
  `IntrospectionController.cs:53-56` is the right instinct applied to three names; the assembler
  should apply it to all.

**Must be versioned:** add `ver` to the claim set now, while there is one version. Without it,
every future change to `ext.roles` is a silent breaking change to an unknown number of consumers.

### 4.3 Making a downstream RS safe by default

RediensIAM cannot patch its relying parties, so the only lever it has is what the SDKs make easy:

1. **`has_role(name)` must not compile.** Change the signature to
   `has_role(project_id, name)` / `HasRole(ProjectId, string)` across all three SDKs. A method that
   cannot be called without naming a tenant cannot produce T-N3.
2. **Stop mapping to `ClaimTypes.Role` unqualified.** Emit `{project_id}/{name}`
   (`sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs:109`), so
   `[Authorize(Roles="admin")]` fails closed for every tenant instead of matching all of them.
3. **Validate `aud` in the SDKs and refuse tokens that omit it.**
4. **Introspection response shape.** `IntrospectionResult.Roles`
   (`src/Controllers/IntrospectionController.cs:64,130`) should carry objects, not strings. Ship as
   an additive field with a deprecation window, since it is a public wire contract.
5. **Build the RS inventory that step 2 says does not exist.** Hydra already holds the client list
   and RediensIAM already owns service-account registration; requiring an owner contact on
   `POST /admin/hydra/clients` and on service-account creation turns "which relying parties read
   `ext.roles`?" from unanswerable into a query. This is a prerequisite for *responding* to C-1,
   independent of fixing it.
6. **The documentation warning (`docs/INTEGRATION.md:150-158`) is good and should stay** — it is
   honest, precise, and cites the code. It is also a control with a human in the loop and every
   integrator who onboarded before it existed is unprotected. Treat it as a stopgap with an expiry
   date, not as mitigation.

---

## 5. Zero-trust posture

### 5.1 What the cluster currently assumes

The deployment's security model is **positional**: a caller is trusted because of where it sits on
the network. Concretely, inside the cluster:

| Hop | Authentication | Encryption |
|---|---|---|
| app → Hydra admin `:4445` | **none** — NetworkPolicy is the entire control | none |
| app → Keto read `:4466` / write `:4467` | **none** — Keto's write API is unauthenticated by design | none |
| app → Postgres `:5432` | password, shared `iam` role for app+Hydra+Keto (`deploy/deploy.sh:118,126,134`) | **none** — `sslmode=disable` on the Hydra/Keto DSNs, unset (Npgsql `Prefer`) for the app (R-15) |
| app → Dragonfly `:6379` | `--requirepass` (`deploy/rediensiam/templates/dragonfly.yaml:50`) | none |
| ingress → app | — | cleartext path live (R-02) |

Three consequences follow directly:
- **Keto's write API is reachable by anything that can reach the pod.** The `keto-lockdown` policy
  (`deploy/rediensiam/templates/network-policies.yaml:80-95`) permits `podSelector: app={{ .Release.Name }}`
  to both 4466 and 4467. Any workload that acquires the app's pod labels — or any CNI that fails
  open — can write `System:rediensiam#super_admin@user:…`. There is no second factor on the most
  security-critical write path in the deployment.
- **One Postgres role for three components** is the precondition that makes C-4/R-14 practical: a
  write primitive in *any* of app, Hydra or Keto rewrites the `instances` row and thereby the trust
  anchors.
- **`AllowedHosts: "*"`** is justified in-line by "Traefik handles host filtering at ingress; app
  is not directly internet-exposed" (`deploy/rediensiam/templates/deployment.yaml:36-38`). That is
  positional trust stated explicitly, and it is false while `type: NodePort` exposes 5000/5001 on
  every node interface (R-05, R-17).

### 5.2 What the NetworkPolicies actually assume

Reading `deploy/rediensiam/templates/network-policies.yaml` as a specification, it assumes:

1. **The CNI enforces NetworkPolicy at all.** Unverified (step 2 §11 flags this too). If it does
   not, every "locked down" row in `docs/ARCHITECTURE.md:128-141` is decorative.
2. **The `default` namespace is trusted.** `:49-53` admits the entire `default` namespace to ports
   5000 **and 5001**, and `deploy/deploy.sh:22` sets `NAMESPACE=default`. Every pod in the release
   namespace can reach the admin API. This is a namespace-wide trust grant to the most privileged
   surface, written as an ingress rule.
3. **Anything not selected by a policy is fine.** There is no namespace-wide default-deny; the
   policies are per-pod allowlists. A new pod with no policy is unrestricted in both directions.
4. **RFC1918 is the whole private address space.** The egress exception list (`:37`, `:42`) covers
   `10/8`, `172.16/12`, `192.168/16`, `169.254/16` and omits `100.64.0.0/10` — the CGNAT range this
   deployment uses for its Tailscale mesh (`deploy/rediensiam/values.prod.yaml:6-7`). The webhook
   validator blocks that range in application code (`src/Controllers/WebhookController.cs:282-316`,
   correctly, including IPv4-mapped IPv6); the network layer does not (R-10).
5. **DNS egress to anywhere is harmless** (`:32`). It is the standard exfiltration channel; worth
   restricting to `kube-dns` by podSelector.

### 5.3 Recommended zero-trust design

**Phase 1 — no mesh required, closes the most.**
- `sslmode=verify-full` on all three DSNs plus a mounted CA; TLS on Dragonfly (R-15).
- **Separate Postgres roles**: `iam_app`, `iam_hydra`, `iam_keto`, each owning its own schema, no
  cross-schema grants. This is the highest-leverage single change in the deployment layer: it
  breaks C-4's "a write primitive anywhere is a write primitive everywhere" and is a prerequisite
  for RLS (§3.2 phase 2).
- Namespace-wide `default-deny` ingress **and** egress policy, with the existing policies as the
  allowlist on top; drop the `default`-namespace rule at `:49-53` and scope admin ingress to the
  ingress controller's pod selector only.
- `type: ClusterIP` + a real ingress for the admin surface, sequenced with the CSP fix (C-7).
- Add `100.64.0.0/10` to the egress exception list.
- `seccompProfile: RuntimeDefault` at pod level (R-32) — one line, CIS 5.7.2.

**Phase 2 — service identity.** Adopt Linkerd (mTLS by default, minimal configuration) or Istio
with `PeerAuthentication: STRICT` and `AuthorizationPolicy` allowing exactly:
`app → hydra:4444,4445`, `app → keto:4466,4467`, `app → postgres:5432`, `app → dragonfly:6379`,
`hydra → postgres`, `keto → postgres`, everything else denied. Workload identity then replaces pod
labels as the authorisation subject, which is what makes Keto's unauthenticated write API
acceptable — today it is not.

**Phase 3 — split the Keto read and write identities.** The process holds both `Keto:ReadUrl` and
`Keto:WriteUrl` (`src/Services/KetoService.cs:12-17`). Under a mesh, the read path and the write
path should be distinct workload identities (or at minimum distinct service accounts on a split
admin deployment, §1.2), so that a compromise of the token-validation path cannot mint grants.

**Phase 4 — supply chain.** Digest-pinned base images, `npm ci --ignore-scripts`, a registry with
authentication and TLS, cosign signatures, and an admission policy that verifies them (R-16, C-3).
This is the only chain that reaches both root secrets in one step; nothing in phases 1-3 touches it.

---

## 6. Data classification matrix

Sensitivity: **S4** catastrophic across all tenants · **S3** tenant-wide · **S2** per-user ·
**S1** operational.
"At rest" describes the *application* layer; the Postgres PVC
(`deploy/rediensiam/templates/postgres.yaml:93-100`) inherits whatever the storage class provides
and no encryption is configured or asserted anywhere in the chart — treat volume-level encryption
as **unknown/absent** for every Postgres row below.

| # | Category | Where | Sens. | At rest | In transit | Retention | Who can read | Deviation from intent |
|---|---|---|---|---|---|---|---|---|
| 1 | HKDF root (`Security:TotpSecretEncryptionKey`) | env ← K8s Secret (`deploy/rediensiam/templates/secret.yaml:11`), derived at `src/Config/AppConfig.cs:79-88` | **S4** | plaintext in Secret (etcd encryption unverified); world-readable in `values.secret.yaml` | n/a | none — no rotation path | app process, anyone with Secret read, anyone with the values file | **R-06/R-07.** Single root for every tenant; no per-tenant separation; rotation would require re-encrypting every TOTP secret and has **no implementation** |
| 2 | Argon2 pepper | env ← Secret (`secret.yaml:14`) | **S4** | as above | n/a | none | as above | Same class as #1; no rotation path (versioned backup-code format proves the team knows the problem) |
| 3 | Hydra signing key + system secret | Hydra store; secret at `values.secret.yaml:19` | **S4** | `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS` on the `--dev` path | cleartext to its DB (`deploy/deploy.sh:126`) | none | Hydra, DB reader | **R-06.** `Program.cs:371-373` validates only the TOTP placeholder, not this one |
| 4 | Password hashes | `users.password_hash` (`src/Data/Entities/User.cs:12`) | S2 | Argon2id + optional pepper — **correct** | cleartext on 5432 (R-15) | lifetime of account | app; any DB reader (shared `iam` role) | Argon2 cost parameters are mutable from the same DB row that holds them (T-N5) |
| 5 | TOTP secrets | `users.totp_secret` (`User.cs:17`) | S2 | AES-256-GCM under HKDF subkey `rediensiam-totp-secret-v1` — **correct** | cleartext on 5432 | lifetime of factor | app; DB reader (ciphertext) | Overwritable with a bearer token alone and unlogged (R-24 + T-N2) |
| 6 | Backup codes | `backup_codes.code_hash` (`BackupCode.cs:7`) | S2 | HMAC-SHA256, versioned `sha256:{keyId}:{hex}` — **correct and rotation-aware** | cleartext on 5432 | until used | app; DB reader | Reissued silently by the same unauthenticated-in-practice call as #5 |
| 7 | WebAuthn credentials | `webauthn_credentials.public_key` (`WebAuthnCredential.cs:8`) | S1 | plaintext — **correct**, it is a public key | cleartext on 5432 | lifetime of credential | app; DB reader | None. `UserVerification = Preferred` is an authn-strength issue, not a data one |
| 8 | PATs | `personal_access_tokens.token_hash` (`PersonalAccessToken.cs:8`) | **S3** | SHA-256 of a 40-byte random token — acceptable (no salt needed at that entropy) | cleartext on 5432; **shown once** in an API response | `ExpiresAt` nullable → **default is never** | app; DB reader (hash only); holder | **R-22/C-5.** Non-expiring by default on the most privileged accounts; `expiresAt` binds to nothing (`docs/INTEGRATION.md:184-185`) |
| 9 | PAT introspection cache | Dragonfly `pat:{sha256}` (`src/Services/PatService.cs:53,128`) | S3 | plaintext in Redis, password-auth only, no TLS | cleartext on 6379 | `Cache:PatTtlMinutes` (default 5) | app; anyone reaching Dragonfly | TTL is settable from the mutable instance row (T-N5) — extending it neuters revocation |
| 10 | Sessions + DataProtection keys | Dragonfly `rediensiam:dataprotection:keys` (`src/Program.cs:42-45`) | **S3** | plaintext | cleartext on 6379 | key lifetime | app; anyone reaching Dragonfly | DataProtection keys unencrypted at rest = forgeable session cookies for anyone who reads the cache |
| 11 | OAuth2 access/refresh tokens | Hydra's Postgres schema | **S4** (aggregate) | per Hydra | **cleartext** (`sslmode=disable`) | per Hydra config | Hydra; DB reader via shared `iam` role | R-15 exposes the entire token store to a pod-network observer |
| 12 | Audit log | `audit_logs` (`AuditLog.cs`), incl. IP + user-agent | **S3** | plaintext, mutable, deletable | cleartext on 5432 | `orgs.audit_retention_days ?? Audit:RetentionDays` (`src/Services/AuditLogRetentionService.cs:42-47`) | app; org admins via API; DB reader | **T-N4** — no floor, `0`/negative purges the org's entire history within 24 h. **T-N2** — MFA mutations never written at all. No append-only guarantee, no WORM export |
| 13 | End-user PII | `users` (email, phone, display name, metadata) (`User.cs:9-25`) | S2 | plaintext | cleartext on 5432 | **no policy** — rows persist until an admin deletes (`OrgController.cs:577`, `SystemAdminController.cs:160,383`) | app; org/project admins; DB reader | No retention or erasure schedule; hard delete only, no anonymisation path — GDPR Art. 17 is a manual operation |
| 14 | Social account links | `user_social_accounts` (`UserSocialAccount.cs`) | S2 | plaintext (provider + provider user id + email) | cleartext on 5432 | with the user | app; DB reader | Unlink is unaudited (T-N2) |
| 15 | Per-org SMTP credentials | `org_smtp_configs.password_enc` (`OrgSmtpConfig.cs:11`) | S3 | AES-256-GCM under `rediensiam-smtp-password-v1` — **correct** | cleartext on 5432 | with the org | app; DB reader (ciphertext) | Host/port unvalidated → probe oracle (R-10); errors returned verbatim (`OrgController.cs:747-761`) |
| 16 | Social provider client secrets | project `login_theme` JSON, `client_secret_enc` (`src/Services/TotpEncryption.cs:35-49`) | S3 | AES-256-GCM under `rediensiam-theme-secret-v1`; stripped from API responses — **correct** | cleartext on 5432 | with the project | app; DB reader (ciphertext) | None at the data layer |
| 17 | Webhook signing secrets | `webhooks.secret_enc` (`Webhook.cs:9`) | S3 | AES-256-GCM under `rediensiam-webhook-secret-v1` — **correct** | cleartext on 5432 | with the webhook | app; DB reader (ciphertext) | No rotation path |
| 18 | SAML IdP trust anchors | `saml_idp_configs.certificate_pem` (`SamlIdpConfig.cs:10`) | **S3** | plaintext — acceptable (it is a public cert), **but it is a trust anchor** | cleartext on 5432 | with the config | app; SuperAdmin via `SystemAdminController.cs:1069,1095`; DB reader | Writable as ordinary data; changes are not fingerprinted into the audit record. Whoever edits this column controls who may assert an identity for that project |
| 19 | Instance runtime config | `instances` row (`src/Data/Entities/Instance.cs`, emitted at `src/Config/InstanceConfiguration.cs:114-149`) | **S4** | plaintext, unsigned, no checksum | cleartext on 5432 | permanent | app; **any of three components via the shared `iam` role** | **R-14 + T-N5.** Holds `Hydra:AdminUrl`, `Keto:*Url`, `App:TrustedProxies`, Argon2 costs, lockout, PAT TTL, audit retention. See §7 |
| 20 | Keto tuple store | Keto's Postgres schema | **S4** | plaintext | cleartext | permanent | Keto; **anyone who reaches :4467** | The authorisation ground truth has no authentication in front of its write API (§5.1) |
| 21 | Tenant branding / `custom_css` | project `login_theme` | S1 | plaintext | cleartext on 5432 | with the project | public (rendered on the login page) | **R-09** — no server-side validation on the org route (`OrgController.cs:162`); currently masked by a CSP bug (C-6) |

**Cross-cutting deviations:**
- No data category is encrypted in transit inside the cluster (R-15 for Postgres; Dragonfly has no
  TLS at all). Rows 9, 10, 11 and 20 are the ones where that matters most.
- The three highest-sensitivity categories (1, 2, 3) have **no rotation implementation**, which
  makes C-3 unrecoverable rather than merely severe.
- Categories 12 and 13 have retention semantics controlled by, respectively, the tenant under
  investigation and nobody at all.

---

## 7. Secrets and trust-anchor management

### 7.1 The config-as-data model, judged

The Zitadel-style instance row (`docs/ARCHITECTURE.md:77-124`) solves a real problem: fleet-wide
atomic reconfiguration without synchronised env vars. The implementation is clean — a stock
`ConfigurationProvider` (`src/Config/InstanceConfigurationProvider`), secrets deliberately excluded
(`Instance.cs:8-9`), an explicit `RECONFIGURE_FROM_ENV` gate, a `ConfigVersion` counter, and a
degrade-to-env fallback with a loud warning (`InstanceConfiguration.cs:55-59`).

**The model is right for operational configuration and wrong for two of the three things currently
in the row.** The values need splitting by *what a wrong value does*, not by *whether it is a
secret*:

| Class | Examples | Correct substrate | Why |
|---|---|---|---|
| **Trust anchors** | `Hydra:AdminUrl` (`InstanceConfiguration.cs:124`), `Keto:ReadUrl`/`WriteUrl` (`:126-127`), `App:TrustedProxies` (`:119`), `App:PublicUrl` (issuer identity, `:116`) | **env / mounted file only. Never the DB.** | These define *who the process believes*. A process must not be able to learn who to trust from data it can itself write. `KetoService.CheckAsync` fails closed on a non-2xx (`KetoService.cs:25`) — which defends against Keto being **down**, not against Keto being **someone else**. An attacker's endpoint answers `200 {"allowed":true}` |
| **Security parameters** | Argon2 cost ×3, `MaxLoginAttempts`, `LockoutMinutes`, `Cache:PatTtlMinutes`, `Audit:RetentionDays` (`:139-147`) | DB is acceptable **with compiled-in clamps** | Operators legitimately tune these. But a value outside a safe range is a control failure, and the code currently accepts any integer. `Math.Clamp` at read time in `AppConfig` (floors: Argon2 t≥2/m≥19MiB/p≥1, lockout attempts ≤10, PAT TTL ≤15 min, audit retention ≥90 days) removes most of T-N5's blast radius and all of T-N4's, in about ten lines |
| **Operational config** | SMTP host/from-name, invite expiry, OTP TTL, cache instance name | DB — as today | A wrong value here is an outage, not a bypass |

### 7.2 Specific structural defects in the current model

- **The row wins over env** (`src/Program.cs:17` layers it last), so the deployment manifest is a
  *record* of intent, not the *source* of it. An operator reading `values.yaml` cannot know what
  the pod will do. That inversion is fine for SMTP and fatal for `Keto:ReadUrl`.
- **No integrity and no provenance.** The row has no signature, no checksum, no allowlist, and no
  writer identity. `ConfigVersion` increments only on the `RECONFIGURE_FROM_ENV` path
  (`InstanceConfiguration.cs:47`), so a direct `UPDATE` changes behaviour and leaves the version
  unchanged — the one field that could have detected tampering does not move.
- **Config load is unaudited.** Nothing writes an audit record when the instance row is loaded or
  when its values differ from the previous boot. A trust-anchor change is invisible in the audit
  log by construction.
- **Three components share the writer.** The row is protected only by Postgres credentials that
  Hydra and Keto also hold (`deploy/deploy.sh:118,126,134`).

### 7.3 Recommended shape

1. **Move the four trust anchors out of `Instance` entirely** — drop them from `ApplyEnv`
   (`InstanceConfiguration.cs:85-88`, `:81`) and from `ToDict` (`:119`, `:124-127`), and let the
   env/appsettings layer supply them as it already does in the chart
   (`deploy/rediensiam/templates/deployment.yaml:51-60`). This is a *deletion*, and it closes
   R-14's high-impact half and most of C-4.
2. **Clamp the security parameters** at read time in `AppConfig`, with the bound logged when it
   fires. Closes T-N5's remaining half and T-N4.
3. **Validate anchors at startup regardless** — `Hydra:AdminUrl` and `Keto:*Url` must resolve to a
   configured in-cluster hostname allowlist; refuse to start otherwise. Same fail-closed philosophy
   as `ConfigureForwardedHeaders` (`Program.cs:412-417`), which is already the right pattern.
4. **Audit the config load** — one `audit.RecordAsync` on boot when `ConfigVersion` or any emitted
   value differs from the last recorded hash. Cheap, and it converts an invisible change into a
   detectable one.
5. **SAML certificates** (`SamlIdpConfig.cs:10`): keep them in the DB — they are per-tenant
   federation trust and correctly scoped to SuperAdmin (`SystemAdminController.cs:1069,1095`) — but
   record the certificate SHA-256 fingerprint in the audit entry on every create/update so a
   silent anchor swap is reconstructable.
6. **Rotation, for the categories that have none.** Rows 1-3 and 17 of the matrix need a documented
   and *implemented* procedure. Minimum viable: a `keyId` prefix on ciphertexts (the backup-code
   format at `docs/ARCHITECTURE.md:188` already does this correctly — copy it), plus a background
   re-encrypt pass. Without this, C-3's recovery plan is "re-enrol every MFA user in every tenant
   by hand".

---

## 8. Structural changes, ranked by findings prevented

Ranked by how many *current* findings each would have prevented, and how many *future* ones of the
same class it forecloses. These are structural changes; the point-fixes for individual findings
belong in step 4 and are not repeated here.

| # | Change | Prevents / would have prevented | Effort | Notes |
|---|---|---|---|---|
| **S-1** | **Default-deny authorisation + a `GrantedLevel` type that only `LiveAuthorizationService` can construct** (§2.3) | **R-22** (all three residuals), **I-02**, **C-5**, T-N1's *class*; makes R-23's internal containment structural rather than incidental | M | Highest ratio of class-elimination to diff size. `GetManagementLevel()` becomes `internal`; `ServiceAccountController.cs:28` stops compiling until fixed |
| **S-2** | **A claims-assembly component + the token contract of §4** (namespaced tenant roles, reserved management names, mandatory `aud`, `ver`, SDK signature changes) | **R-23**, **T-N3**, **C-1**, C-8's impact, the `has_role`/`ClaimTypes.Role` sinks (`ServiceCollectionExtensions.cs:109`, `lib.rs:18`) | M-L | The only fix that closes *both* paths to C-1. Requires a deprecation window because `ext.roles` is a published wire contract |
| **S-3** | **Audit as a cross-cutting concern** — an action filter (or outbox) that records every mutating request on `/account`, `/org`, `/project`, `/service-accounts`, `/api`, plus an append-only/WORM export | **T-N2** (all 7 unlogged MFA mutations), **T-N4** detection, **T-N6**, and R-24's *effective* severity | S-M | `AuditLogService` already uses its own DbContext scope (`src/Services/AuditLogService.cs:15-33`) — genuinely good design, just not invoked from enough places. A filter fixes "someone forgot" the same way S-1 does |
| **S-4** | **Trust anchors out of the mutable row + clamps on security parameters** (§7.3) | **R-14**, **T-N5**, **T-N4**, most of **C-4** | S | Largely a deletion. Best effort-to-benefit ratio in the document |
| **S-5** | **Tenant scope as a schema property** — EF global query filters, then per-component Postgres roles + RLS, then typed `OrgId` (§3.2) | The `ServiceAccountController.cs:29-33` bug class, R-22 residual 2, T-N6's enforcement point, the C-4 precondition (shared `iam` role), R-15's blast radius | M-L | Phase 1 is ~30 lines and converts 200 hand-written conjuncts into ~15 auditable `IgnoreQueryFilters()` calls |
| **S-6** | **Zero-trust network baseline** — namespace default-deny, TLS on Postgres and Dragonfly, ClusterIP + real ingress for admin, CGNAT egress block, seccomp (§5.3 phase 1) | **R-15**, **R-05**, **R-10** (network half), **R-17**, **R-32**, R-19's exposure | M | Sequence with the CSP fix — C-7 says fixing R-26 first turns a broken admin surface into a working exposed one |
| **S-7** | **Supply-chain integrity** — authenticated TLS registry, digest pinning, `npm ci --ignore-scripts`, cosign + admission policy | **R-16**, **C-3**, R-21's build-time reachability | M | The only chain reaching both root secrets in one step; unaffected by every change above |
| **S-8** | **Single authority per authorisation question** — remove the `db.OrgRoles` fallback from `LiveAuthorizationService.cs:83` / `KetoService.cs:95`, add a reconciler or outbox for the dual-write (§1.4) | R-01's *class* (orphaned tuples), R-22 residual 3, drift between Keto and Postgres that no current code path can detect | M | Do after S-1, which gives the single entry point this change needs |
| **S-9** | **Split the admin surface into its own deployment/identity, or stop documenting it as a boundary** (§1.2) | R-05 structurally, I-02's reachability, `/swagger` + `/metrics` exposure | M | Either resolution is acceptable; the current ambiguity is not |
| **S-10** | **Key rotation implementations** for the HKDF root, the Argon2 pepper and the Hydra system secret (§7.3 item 6) | Does not prevent a finding — it makes **C-3** and **C-1** *recoverable* | M | The backup-code versioned format is the pattern to copy |

**If only three ship:** S-1, S-4, S-2 — in that order. S-1 and S-4 are small and each closes a
whole class; S-2 is the only thing that closes C-1, which is the finding this product cannot
detect and cannot remediate at its relying parties.

---

## 9. What is well designed, stated plainly

Not a courtesy section — these are load-bearing, several threats in step 2 are rated Medium rather
than High only because of them, and any remediation that weakens one re-rates the threat it
contains.

- **Cryptography.** Argon2id with an optional HMAC pepper (`src/Services/PasswordService.cs:118-131`);
  AES-256-GCM under per-purpose HKDF subkeys (`src/Config/AppConfig.cs:79-88`) so compromise of one
  purpose does not cross to another; `CryptographicOperations.FixedTimeEquals` on every secret
  comparison; versioned backup-code format (`sha256:{keyId}:{hex}`) that makes pepper rotation
  *detectable* — the only place in the codebase that anticipated rotation, and the pattern the
  others should copy.
- **Injection posture.** No SQL injection (the three raw-SQL sites are parameterised or constant),
  no unsafe deserialization, no command execution, no path traversal, no `dangerouslySetInnerHTML`
  or `innerHTML` in either SPA or the browser SDK, no tokens in `localStorage`, no secrets in git
  history. Keto tuples are written as JSON (`KetoService.cs:30-38`), so there is no relation-string
  injection either.
- **The `role:` prefix on Keto relations** (`KetoService.cs:138,168,274`) — tenant strings cannot
  collide with the structural relations. This is exactly the namespacing discipline §4 asks for in
  the *token*, already applied correctly in the *tuple store*. The fix for T-N3 is to do in one
  place what this code already does in the other.
- **The audience gate** (`GatewayAuthMiddleware.cs:46-52,65-76`) — a boundary keyed on what the
  token *is*, not what it *says*.
- **Fail-closed everywhere it matters**, including the trusted-proxy startup throw
  (`Program.cs:412-417`) which R-25 makes inconvenient and which must stay.
- **`AuditLogService` uses its own DbContext scope** (`src/Services/AuditLogService.cs:15-33`) so a
  caller's uncommitted state cannot ride along into the audit record. Subtle, correct, and rare.
- **`PatService` re-checks liveness on the cache-hit path** (`src/Services/PatService.cs:57-68`,
  `:155-175`) — the cache skips the join, never the decision. This is better than
  `docs/ARCHITECTURE.md:70` claims (I-06: the document is stale in the *safe* direction here).
- **`LoginChallengeProject`** treats a caller-supplied `project_id` as a cross-check and never as a
  source (`src/Services/LoginChallengeProject.cs:26-39`) — the correct instinct, and the one §4
  generalises.
- **`docs/INTEGRATION.md`'s honesty.** A document that says "if this and the code disagree, the code
  wins and this document is a bug" (`:5`) and then writes out its own open weakness in full
  (`:150-158`) is a genuine security asset. `docs/ARCHITECTURE.md` is less reliable — it is stale in
  both directions (`:11-17` warns about a control that now exists; `:70` understates another) — and
  should be regenerated from the code, not edited.

---

## 10. Assumptions and limits of this review

- **Source-derived only.** No running instance, no cluster, no `helm template` output rendered
  against real values. Whether the CNI enforces NetworkPolicy at all is unverified and materially
  changes §5.
- **Hydra and Keto subchart internals were not unpacked** (`deploy/rediensiam/charts/*.tgz`) —
  unchanged blind spot from steps 1 and 2. Their default authentication posture is assumed from the
  upstream documentation, not read.
- **Postgres volume encryption is unknown**, not absent. The chart configures none; the storage
  class may. The matrix in §6 treats it as absent, which is the conservative reading.
- **etcd encryption-at-rest for Kubernetes Secrets is unverified**, which is why rows 1-3 of the
  matrix say "plaintext in Secret".
- **No code was modified.** Every recommendation above is a proposal; nothing in this branch changed.

---

*Next step: 04 — mitigation design and remediation sequencing. The structural changes S-1…S-10 are
the frame; the point-fixes for R-nn/T-Nn hang off them. Note the ordering hazards recorded in
step 2 §7 (C-6: fix R-09's server-side validation with or before R-26; C-7: fix R-05 in the same
change) — they apply to S-6 directly.*
