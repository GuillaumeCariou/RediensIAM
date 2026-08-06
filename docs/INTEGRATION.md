# Integrating with RediensIAM

How to plug an application into RediensIAM: log users in, and let your backend decide what they
may do. Written against the code, with `file:line` references — if this document and the code
disagree, the code wins and this document is a bug.

Companion reads: [`../sdk/README.md`](../sdk/README.md) (which SDK, and why),
[`API.md`](API.md) (every route, its required authority and where it is reachable),
[`ARCHITECTURE.md`](ARCHITECTURE.md) (how the internals fit together) and
[`SECURITY.md`](SECURITY.md) (what protects what, and what is still open).

> Source references below name **types and methods**, not `file:line`, for anything under
> `src/Controllers/` — those files move often enough that line numbers rot within a release.

---

## Rule zero — the API speaks `snake_case`

Every JSON request and response body on this API uses `snake_case` keys. This is not the ASP.NET
default; it is set explicitly (`src/Program.cs:115-120`):

```csharp
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
```

`PropertyNamingPolicy` applies to deserialization as well as serialization, so a C# property
`UserListId` is read from the key `user_list_id`. `PropertyNameCaseInsensitive` ignores **case**,
not **underscores** — so `userListId` does **not** bind.

This is the single most common integration mistake, and it fails **silently**: an unbound
optional field stays `null` and your request is accepted with a field you thought you set. Send
`grant_types`, never `grantTypes`.

> One exception: `POST /api/introspect` is form-encoded per RFC 7662, and form binding does not
> use the JSON naming policy. See [Introspection](#introspection--the-backend-path).

---

## Two integration paths

| You are | You need | Read |
|---|---|---|
| A frontend (SPA, mobile web) logging users in | An OIDC client + `rediensiam-web` | [Frontend](#frontend--logging-users-in) |
| A backend validating incoming tokens | A service account + PAT + a backend SDK | [Backend](#introspection--the-backend-path) |

Most integrations need both: the SPA obtains a token, your API validates it.

---

## Frontend — logging users in

### Create a project, not a raw OAuth2 client

**Creating a project creates its OIDC client for you**, with a deterministic id
`client_<project_id>` (`OrgController.CreateProject`, `SystemAdminController.AdminCreateProject`).
The client is created as a public PKCE client (`token_endpoint_auth_method = "none"`) and carries
`metadata.project_id` / `metadata.org_id`, which is what ties issued tokens to a tenant.

This is the path you want. Pick the endpoint that matches your caller's level:

| Endpoint | Caller | Request record |
|---|---|---|
| `POST /org/projects` | OrgAdmin (`OrgController` class filter) | `CreateProjectRequest` |
| `POST /admin/organizations/{orgId}/projects` | SuperAdmin (`SystemAdminController` class filter) | `AdminCreateProjectRequest` |
| `POST /api/manage/organizations/{orgId}/projects` | SuperAdmin | **the same action** — see [Management API](#management-api--admin-and-apimanage-are-one-surface) |

```bash
PROJECT=$(curl -s -X POST "$IAM/admin/organizations/$ORG_ID/projects" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{
    "name": "Superadmin console",
    "slug": "superadmin",
    "redirect_uris": ["https://app.example.com/callback"]
  }')
PROJECT_ID=$(echo "$PROJECT" | python3 -c "import sys,json;print(json.load(sys.stdin)['id'])")
echo "client_id = client_$PROJECT_ID"
```

Your `client_id` is `client_<project_id>` — stable, and derivable from the project id in every
environment. That is the value `rediensiam-web` expects as `clientId`.

If Hydra is unreachable the project creation is rolled back and you get `502 hydra_unavailable`
(`OrgController.CreateProject`) — you never end up with a project that has no client.

### The escape hatch: `POST /admin/hydra/clients`

For a client that is **not** a project — a machine client, or an app whose id must be a
particular string — there is a raw endpoint (`SystemAdminController.CreateHydraClient`, SuperAdmin
only):

```json
{
  "client_id": "yandee-web",
  "client_name": "yandee-web",
  "grant_types": ["authorization_code", "refresh_token"],
  "redirect_uris": ["https://app.example.com/callback"],
  "scope": "openid profile email"
}
```

- `client_id` is **optional**. Omit it and Hydra mints a random UUID that you must capture and
  carry into every environment's build. Set it and you get a stable id everywhere.
- Allowed characters: ASCII letters, digits, `-`, `_`, `.`, max 64 — otherwise
  `400 invalid_client_id`. An id already in use gives `409 client_id_taken`.
- The prefixes `sa_` and `client_` are **reserved** and also rejected: they mark
  service-account and project clients respectively, and authorisation decisions key on them.
- `token_endpoint_auth_method` is **forced**: `private_key_jwt` when `grant_types` contains
  `client_credentials`, `none` otherwise. Do not send it.
- `response_types` is not part of the request record and is ignored; Hydra applies its default.

A client created this way has **no project metadata**, so tokens issued for it are not
project-scoped. Prefer a project unless you know you want that.

### Wire up the browser SDK

```ts
import { createRediensIam } from 'rediensiam-web';

const iam = createRediensIam({
  issuer: 'https://iam.example.com',        // browser-facing origin, must match Hydra's issuer
  clientId: `client_${PROJECT_ID}`,
  redirectUri: `${location.origin}/callback`,
});
```

`issuer` must be the origin the **browser** reaches, and it must equal the issuer Hydra is
configured with — the SDK fetches `{issuer}/.well-known/openid-configuration` before redirecting.
A mismatch here is the usual cause of "login does nothing".

Claims exposed by the SDK are decoded without signature verification. Render with them; never
protect with them.

### Changing redirect URIs later

There is no update endpoint. `/admin/hydra/clients` exposes GET, POST and DELETE only
(`SystemAdminController.{List,Create,Get,Delete}HydraClient`), and `UpdateOAuth2ClientScopeAsync`
(`HydraService.cs:190`) patches the scope alone. To add a redirect URI you must `DELETE` the
client and re-`POST` it with the same `client_id` — which is only painless because the id is
either `client_<project_id>` or one you pinned yourself. Sessions issued for the old client are
invalidated. **Known gap; plan your redirect URIs up front.**

---

## Introspection — the backend path

Your API must not decide privileges from a locally verified JWT. Local JWKS verification proves
the token was *issued*; it cannot see a role revoked, an account deactivated, an org suspended or
a token revoked since. The trade-off table is in [`../sdk/README.md`](../sdk/README.md#why-not-just-verify-the-jwt-locally).

> **Breaking change — `ext.roles` is now namespaced.** Tenant role names are emitted as
> `{project_id}/{name}` (`AuthController.cs`, `Roles.ProjectRoleClaim`). A role named `admin` in
> project `7f3…` appears as `7f3…/admin`, never as `admin`. Management role names
> (`super_admin`, `org_admin`, `project_admin`) remain bare and are now *reserved*: a tenant role
> cannot be created with one (`Roles.ProjectRoleNameError`), on either creation path.
>
> This closes the previous weakness — a `project_admin` could create a role literally named
> `super_admin` and obtain a signed token asserting it — and it closes the wider one behind it:
> two tenants both naming a role `admin` used to be byte-identical in every consumer's claims.
>
> **What you must change.** Any check of the form `roles.contains("admin")` now fails closed and
> must become `roles.contains(project_id + "/" + "admin")` — or, in the SDKs,
> `HasProjectRole(projectId, "admin")` / `info.has_project_role(project_id, "admin")`. `HasRole` /
> `has_role` still exist and now match management roles only. In the .NET SDK the qualified string
> is what lands in `ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` fails closed for every
> tenant instead of matching all of them — that is the intended behaviour, not a bug.
>
> Roles created before this change keep their stored names; only the claim's shape changed.
> Introspection still strips management roles it cannot confirm (`IntrospectionController.cs`), and
> introspecting remains preferable to local JWKS validation for every reason listed above.

### `act` — telling support traffic from the customer's own

Since 0.7.0 an introspection answer can carry an `act` object. It is **absent on every ordinary
token**, and present exactly when an operator opened a delegated session into the tenant:

```json
"act": { "sub": "usr_operator", "level": "super_admin", "mode": "read", "session": "7f3e…" }
```

Three consequences for your service, none optional:

1. **A consumer that cannot see `act` cannot tell an impersonated request from a genuine one.** Log
   both identities — the tenant *and* `act.sub` — or your audit trail says only "Acme did this".
2. **While `act.mode == "read"`, refuse mutating verbs.** The claim travels so every enforcement
   point sees the same value; enforcing it is still yours. `IsReadOnlyImpersonation` /
   `is_read_only_impersonation()` in the SDKs is the one-liner.
3. **A delegated token carries no roles at all** — `roles` is empty and `user_id` is null. Your
   usual role checks therefore grant it nothing, which is correct: *you* decide what a support
   session may see.

The full contract, and how to open a session, is in [`IMPERSONATION.md`](IMPERSONATION.md) — §12 is
written for exactly this audience.

### Get a service account and a PAT

Introspection callers authenticate as a service account
(`IntrospectionController.IsServiceAccountCaller`).
A plain user token is refused on purpose — otherwise the endpoint is an oracle any bearer could
use to probe token validity.

`user_list_id` is **required** (`CreateSaRequest`, `[JsonRequired]`), so fetch a
user list first:

```bash
curl -s "$IAM/admin/userlists" -H "Authorization: Bearer $ADMIN_TOKEN"
LIST_ID="<guid>"

SA=$(curl -s -X POST "$IAM/service-accounts" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d "{\"name\":\"my-gateway\",\"user_list_id\":\"$LIST_ID\"}")
SA_ID=$(echo "$SA" | python3 -c "import sys,json;print(json.load(sys.stdin)['id'])")

curl -s -X POST "$IAM/service-accounts/$SA_ID/pat" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"introspect"}'
# → { "id", "name", "token": "rediens_pat_…", "expires_at", "message": "store_this_token_shown_once" }
```

The PAT is shown once. An optional expiry is `expires_at` — `expiresAt` binds to nothing and
silently yields a token that never expires.

**No role assignment is needed.** Being a service account is the whole requirement; a gateway
that only validates tokens needs no roles at all. `POST /service-accounts/{id}/roles` exists for
service accounts that must *act*, not to unlock introspection.

### Call it

`POST /api/introspect` is `[FromForm]`, `application/x-www-form-urlencoded`, per RFC 7662:

```bash
curl -s -X POST "$IAM/api/introspect" \
  -H "Authorization: Bearer $SA_PAT" \
  --data-urlencode "token=$USER_TOKEN" \
  --data-urlencode "project_id=$PROJECT_ID"
```

An unusable token answers `{"active": false}` with a 200 — never an error status, so a caller
cannot distinguish malformed from revoked from expired.

### `project_id` is mandatory — read this before you upgrade

**Breaking change.** `project_id` names the tenant *your resource server serves*. Omit it and you
get `400 {"error": "project_id_required"}`. There is no grace period and no opt-out: **a resource
server that declares no tenant is no longer served.**

Why the break was worth making. Introspection previously answered for whatever token you handed
it, scoped only by *your* credential's organisation. A gateway holding a deployment-scoped
(`__system__`) service account — which is exactly what a multi-tenant gateway must hold — got
`active: true` for **every** tenant's token in the deployment, and was expected to compare
`project_id` against its own configuration afterwards. Nothing enforced that, nothing tested it,
and no SDK had a field for it. `has_project_role` made *role* checks safe by construction;
nothing made the *tenant* check safe by construction. Now the tenant check is the request.

| Field | Value |
|---|---|
| `project_id` (request) | the project id your service serves, **or** the organisation id if you front a whole organisation |
| `project_id` (response) | echo of what you sent, on an active answer |

A token is bound to `project_id` when the value equals its `project_id`, equals its `org_id`, or appears
in its OAuth2 `aud` claim. A token whose `project_id` and `org_id` are both empty matches no
audience and can only be introspected by naming an explicit OAuth2 `aud` claim minted onto it.

**Migration.** If you use a backend SDK, steps 2 and 3 are done for you — upgrade the SDK, set
the one new option, and jump to the symptom list. The raw-HTTP version:

1. Find every caller of `/api/introspect` and `/api/authorize`. Each one serves exactly one
   tenant; write that tenant's id into its configuration. If a caller genuinely serves several,
   it already knows which one each request is for — send that.
2. Add `project_id` to the request.
3. **Upgrade the server before pointing upgraded callers at it.** A server that predates this
   change does not reject the field — it discards it in silence and answers as it always did, and
   nothing in that answer says so. There is no client-side check that catches it, which is exactly
   why the order matters.

**Symptom you will see if you skip this:** every introspection returns
`400 project_id_required`, or — worse and quieter — an `{"active": false}` on a token you know is
good, which means the `project_id` you sent names a different tenant than the token belongs to.

> `token_type_hint` is **not** part of the request record. RFC 7662 §2.1 makes it an optional
> lookup hint the server may ignore and must not reject a token over, and RediensIAM identifies
> the token shape from its prefix in constant time — so there is nothing for a hint to optimise.
> Sending it is harmless (the field is discarded during model binding); expecting it to change
> the answer is not. The hint that matters is the one RediensIAM sends to Hydra itself
> (`HydraService.cs:302`), which is what stops a refresh token from authenticating an API call.

For an authorisation decision rather than a validity check, `POST /api/authorize` takes JSON and
keeps the policy in RediensIAM instead of in every gateway:

```json
{ "token": "…", "namespace": "Organisations", "object": "<org-id>",
  "relation": "org_admin", "project_id": "<project-or-org-id>" }
```

`project_id` is mandatory here too, on the same terms as introspection.

**Also breaking: `object` is now tenant-scoped.** The object must belong to the tenant the answer
is about — the caller's organisation, or, for a deployment-scoped caller, the organisation of the
token being asked about. `Organisations`, `Projects` and `UserLists` are checked against the
database; **any other namespace is refused**, because a namespace RediensIAM writes no objects
into has no ownership to check and failing open there is the same finding under a new name.

Refused requests answer `{"allowed": false}` — the same shape as a genuine "no", so the endpoint
cannot be used to probe which objects exist. Every refusal writes an audit row
(`api.authorize.object_out_of_scope`).

Why: the subject of an authorisation check is always the presented token's user, so an unscoped
`object` was never a way to forge a decision — but it was a way to read another tenant's relation
graph one bit per request. If you were asking about objects outside your own organisation, you
were relying on a bug.

In practice, use a backend SDK rather than raw HTTP — see
[`../sdk/README.md`](../sdk/README.md#c). Both SDKs cache positive answers briefly and never
cache negative ones.

> **The backend SDKs now send `project_id`, and require it.** `RediensIamOptions.ProjectId` (C#) and
> `Config::project_id` (Rust) are **required options with no default** — a client constructed
> without one throws at construction rather than 400-ing on its first request. Full migration in [`../sdk/README.md`](../sdk/README.md#project_id-is-now-a-required-sdk-option).
>
> The browser SDK is unaffected: it never calls these endpoints, because introspection needs a
> service-account credential and anything shipped to a browser is readable by anyone with
> devtools.

---

## Endpoint reference

The routes below are the ones an integrator uses. **The complete list of all 190 routes — with
required authority and whether each is reachable on the public hostname — is in
[`API.md`](API.md).**

Bodies are `snake_case`. Records cited so you can re-check against the source.

| Endpoint | Auth required | Body | Request record |
|---|---|---|---|
| `POST /org/projects` | OrgAdmin | `{name, slug, require_role_to_login?, redirect_uris?}` | `CreateProjectRequest` |
| `POST /admin/organizations/{id}/projects` | SuperAdmin | same | `AdminCreateProjectRequest` |
| `POST /admin/hydra/clients` | SuperAdmin | `{client_name, grant_types, redirect_uris, scope?, client_id?}` | `CreateHydraClientRequest` |
| `GET/DELETE /admin/hydra/clients/{id}` | SuperAdmin | — | — |
| `POST /service-accounts` | ProjectAdmin or above, plus per-object access | `{name, description?, user_list_id}` | `CreateSaRequest` |
| `POST /service-accounts/{id}/pat` | access to the SA | `{name, expires_at?}` | `GenerateSaPatRequest` |
| `POST /service-accounts/{id}/roles` | access to the SA | `{role, org_id?, project_id?}` | `AssignSaRoleRequest` |
| `POST /admin/organizations/{id}/admins` | SuperAdmin | `{user_id, role, scope_id?}` | `AssignOrgAdminRequest` |
| `POST /api/introspect` | service account | **form**: `token`, `project_id` (required) | two action parameters, not a record — see below |
| `POST /api/authorize` | service account | `{token, namespace, object, relation, project_id}` | `AuthorizationRequest` |
| `POST /admin/impersonate` | SuperAdmin **and** a service-account caller | `{org_id, project_id, mode, reason, ttl_seconds?}` | `OpenImpersonationRequest` |
| `GET /admin/impersonate` | same | — | — |
| `POST /admin/impersonate/{session}/revoke` | same | — | — |
| `PATCH /admin/projects/{id}` | SuperAdmin | see [MFA](#turning-require_mfa-off) | `AdminUpdateProjectRequest` |

⚠ `aud` was the name of `project_id` until 0.6.0. One value under two names is
what produced a deployment test whose only job was to check that two spellings agreed. There is no
compatibility shim: a caller still sending `aud` gets `400 project_id_required`.

⚠ `/api/introspect` binds **two action parameters**, not a record. Form binding resolves a record
through its constructor, so `[property: FromForm(Name = "project_id")]` lands where the binder never
looks — the field bound to nothing and every request answered `400 project_id_required`. If you
mirror this endpoint's shape in your own service, do not use a record.

`role` values are validated against `SystemAdminController.KnownManagementRoles` — `super_admin`,
`org_admin`, `project_admin`; anything else is `400 unknown_role`.

Route prefixes, all enforced by a class-level `[RequireManagementLevel]`: `/admin` and
`/api/manage` are SuperAdmin, `/org` is OrgAdmin, `/project` and `/service-accounts` are
ProjectAdmin. `/api/manage` is the same SuperAdmin surface as `/admin` under a second prefix — see
[Management API](#management-api--admin-and-apimanage-are-one-surface).

---

## Management API — `/admin` and `/api/manage` are one surface

`/api/manage` used to expose seven endpoints — create/list organisations and projects, create
user lists, add users. An external service could *create* a tenant and then had to stop:
suspending it, deleting it, updating its projects, managing roles, service accounts, PATs, Hydra
clients, webhooks and SMTP all lived only on `/admin/*`, which in practice meant an interactive
superadmin had to finish the job by hand.

They are now **the same surface**. `SystemAdminController` carries both route prefixes, so every
`/admin/x` is reachable at `/api/manage/x`, on the same action, behind the same class-level
`RequireManagementLevel(SuperAdmin)` filter — one token check, one live Keto re-check, whichever
prefix you used. `ManagedApiController` and its seven re-implementations are gone.

This matters more than the convenience. A duplicated handler is where an authorisation check goes
missing; the seven duplicates were also drifting (only the `/api/manage` copy refused a duplicate
email in a user list, and only it checked that the organisation existed before creating a project
under it — both are now on both prefixes).

| | |
|---|---|
| Auth | SuperAdmin, by PAT or `client_credentials` access token, identical on both prefixes |
| Route list | every route under `/admin` — `docs/` does not duplicate the list because it cannot drift: the pairing is asserted in `ApiSurfaceManagedParityTests` |
| Audit | every mutation writes an audit row; a machine credential has no session to correlate afterwards |

**Nothing you already call changes.** The seven original `/api/manage` routes keep their paths,
bodies and status codes. Two of them are now *stricter* on the `/admin` side rather than the
`/api/manage` side, which is the direction that closes a gap.

New on `/api/manage` (non-exhaustive, all pre-existing `/admin` behaviour):
`POST organizations/{id}/suspend`, `POST organizations/{id}/unsuspend`,
`DELETE organizations/{id}`, `PATCH organizations/{id}`, `PATCH projects/{id}`,
`DELETE projects/{id}`, `PUT projects/{id}/scopes`, `PUT|DELETE projects/{id}/userlist`,
`GET|POST|DELETE projects/{id}/roles`, `GET|POST|DELETE organizations/{id}/admins`,
`GET|PATCH users/{id}`, `POST users/{id}/unlock`, `GET|DELETE users/{id}/sessions`,
`GET|PUT|DELETE organizations/{id}/smtp`, `POST organizations/{id}/smtp/test`,
`GET|POST|DELETE hydra/clients`, `GET audit-log`, `GET metrics`, the export routes, the SAML
provider routes and the key-rotation routes.

`SystemHealthController` (`/admin/system/*`) and `AdminWebhookController` (`/admin/webhooks/*`)
carry the second prefix on the same terms, so `/api/manage/system/*` and `/api/manage/webhooks/*`
are live too.

**Not aliased:** `/service-accounts` and its PAT routes. They are not under `/admin`, they are
already reachable with a machine credential, and they run their own per-object authorisation —
a route alias there would be a place for that check to be skipped. Use the existing paths.

---

## Turning `require_mfa` off

`require_mfa` is opt-in at project creation and stays opt-in. Turning it **off** on a project
whose users have already enrolled a factor is a two-step call.

**First call** — `PATCH /admin/projects/{id}` (or `/api/manage/projects/{id}`, or
`/org/projects/{id}`, or `/project/info`) with `{"require_mfa": false}`:

```json
409 {
  "error": "mfa_downgrade_requires_confirmation",
  "enrolled_user_count": 42,
  "consequence": "Disabling require_mfa stops enrolled second factors from gating logins for 42 user(s) in this project. Their factors are not deleted, but a stolen password alone becomes sufficient to sign in. Users are not notified.",
  "confirm_with": "confirm_mfa_downgrade"
}
```

Nothing is applied — not the MFA change and not the rest of the body.

**Second call** — repeat it with the confirmation in the **same body**:

```json
{ "require_mfa": false, "confirm_mfa_downgrade": true }
```

This proceeds and writes an audit row `project.mfa_requirement_removed` carrying
`enrolled_user_count`.

Notes.

- `enrolled_user_count` counts users of the project's assigned user list holding a TOTP secret, a
  verified phone, or a WebAuthn credential — the same three factors that satisfy an MFA login.
- The count being `0` means there is nothing to downgrade, so no confirmation is asked for.
- The confirmation travels in the body, not a header or query flag, so it cannot be replayed onto
  a different request.
- Enabling `require_mfa` is unguarded — it is the safe direction.
- The guard is one shared function, so all four write paths behave identically. A guard on three
  of four paths is not a guard.

---

## Deployment notes that bite integrators

Recorded here because each one looks like an integration bug when you hit it. Current posture in
[`SECURITY.md`](SECURITY.md).

| Symptom | Cause | Status |
|---|---|---|
| Serving RediensIAM under `https://host/iam` breaks OIDC | the ingress serves at a host root; issuer and discovery URLs are absolute | **Still open** (finding R-27). Give it a dedicated host — `iam.example.com` |
| `/admin`, `/org`, `/project` or `/service-accounts` returns 403 from your gateway | the public host denies those prefixes at the ingress by design (finding P-04) | Working as intended. A machine caller wants `/api/manage/*`, which is the same super-admin surface and *is* served on the public host — see [Management API](#management-api--admin-and-apimanage-are-one-surface) |
| Pod in `CrashLoopBackOff` on a dev deploy | `App__TrustedProxies` was empty and the image runs as Production, so the app refuses to start rather than silently trust RFC1918 | **Fixed.** `values.yaml` now ships the k3s pod and service CIDRs (`10.42.0.0/16,10.43.0.0/16`). Override it for any other cluster |
| Admin console login does nothing | its own CSP `connect-src 'self'` blocked the cross-origin discovery fetch | **Fixed.** The header now names the issuer origin explicitly (`src/Program.cs:466-470`), resolved once at startup from the same value `/console/config` hands the SPA |

---

## Common mistakes, in the order people make them

1. **camelCase keys.** Silently unbound. See [Rule zero](#rule-zero--the-api-speaks-snake_case).
2. **Using `/admin/hydra/clients` when you wanted a project.** No project metadata, so no tenant
   scoping on issued tokens.
3. **Assuming a `client_id` you chose was honoured.** Before the `client_id` field existed, Hydra
   minted a UUID and ignored your intent. Check the response.
4. **`POST /service-accounts` with only `{"name": …}`.** `user_list_id` is required → 400.
5. **Assigning a role so a service account may introspect.** Unnecessary.
6. **Sending JSON to `/api/introspect`.** It is form-encoded.
6b. **Omitting `project_id`.** Required. 400, every time.
7. **Matching a bare tenant role name.** `ext.roles` carries tenant roles as
   `{project_id}/{name}`; `roles.contains("admin")` matches nothing. See the warning above.
