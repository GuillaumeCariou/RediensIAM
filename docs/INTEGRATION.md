# Integrating with RediensIAM

How to plug an application into RediensIAM: log users in, and let your backend decide what they
may do. Written against the code, with `file:line` references — if this document and the code
disagree, the code wins and this document is a bug.

Companion reads: [`../sdk/README.md`](../sdk/README.md) (which SDK, and why), and
[`ARCHITECTURE.md`](ARCHITECTURE.md) (how the internals fit together).

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
`client_<project_id>` (`OrgController.cs:105-118`, and the same three lines in
`SystemAdminController.cs:489-500` and `ManagedApiController.cs:117-128`). The client is created
as a public PKCE client (`token_endpoint_auth_method = "none"`) and carries
`metadata.project_id` / `metadata.org_id`, which is what ties issued tokens to a tenant.

This is the path you want. Pick the endpoint that matches your caller's level:

| Endpoint | Caller | Request record |
|---|---|---|
| `POST /org/projects` | OrgAdmin (`OrgController.cs:19`) | `CreateProjectRequest` (`:934`) |
| `POST /admin/organizations/{orgId}/projects` | SuperAdmin (`SystemAdminController.cs:16`) | `AdminCreateProjectRequest` (`:1124`) |
| `POST /api/manage/organizations/{orgId}/projects` | SuperAdmin (`ManagedApiController.cs:18`) | machine-to-machine variant |

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
(`OrgController.cs:119-126`) — you never end up with a project that has no client.

### The escape hatch: `POST /admin/hydra/clients`

For a client that is **not** a project — a machine client, or an app whose id must be a
particular string — there is a raw endpoint (`SystemAdminController.cs:866`, SuperAdmin only):

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
(`SystemAdminController.cs:855, 866, 896, 904`), and `UpdateOAuth2ClientScopeAsync`
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

### Get a service account and a PAT

Introspection callers authenticate as a service account (`IntrospectionController.cs:109-111`).
A plain user token is refused on purpose — otherwise the endpoint is an oracle any bearer could
use to probe token validity.

`user_list_id` is **required** (`ServiceAccountController.cs:345`, `[JsonRequired]`), so fetch a
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

`POST /api/introspect` (`IntrospectionController.cs:23,40`) is `[FromForm]`,
`application/x-www-form-urlencoded`, per RFC 7662:

```bash
curl -s -X POST "$IAM/api/introspect" \
  -H "Authorization: Bearer $SA_PAT" \
  --data-urlencode "token=$USER_TOKEN"
```

An unusable token answers `{"active": false}` with a 200 — never an error status, so a caller
cannot distinguish malformed from revoked from expired.

> `token_type_hint` is **not** part of the request record. RFC 7662 §2.1 makes it an optional
> lookup hint the server may ignore and must not reject a token over, and RediensIAM identifies
> the token shape from its prefix in constant time — so there is nothing for a hint to optimise.
> Sending it is harmless (the field is discarded during model binding); expecting it to change
> the answer is not. The hint that matters is the one RediensIAM sends to Hydra itself
> (`HydraService.cs:302`), which is what stops a refresh token from authenticating an API call.

For an authorisation decision rather than a validity check, `POST /api/authorize` takes JSON and
keeps the policy in RediensIAM instead of in every gateway:

```json
{ "token": "…", "namespace": "Organisations", "object": "<org-id>", "relation": "org_admin" }
```

In practice, use a backend SDK rather than raw HTTP — see
[`../sdk/README.md`](../sdk/README.md#c). Both SDKs cache positive answers briefly and never
cache negative ones.

---

## Endpoint reference

Bodies are `snake_case`. Records cited so you can re-check against the source.

| Endpoint | Auth required | Body | Source |
|---|---|---|---|
| `POST /org/projects` | OrgAdmin | `{name, slug, require_role_to_login?, redirect_uris?}` | `OrgController.cs:90,934` |
| `POST /admin/organizations/{id}/projects` | SuperAdmin | same | `SystemAdminController.cs:474,1124` |
| `POST /admin/hydra/clients` | SuperAdmin | `{client_name, grant_types, redirect_uris, scope?, client_id?}` | `SystemAdminController.cs:866,1130` |
| `GET/DELETE /admin/hydra/clients/{id}` | SuperAdmin | — | `SystemAdminController.cs:896,904` |
| `POST /service-accounts` | any management level | `{name, description?, user_list_id}` | `ServiceAccountController.cs:92,345` |
| `POST /service-accounts/{id}/pat` | access to the SA | `{name, expires_at?}` | `ServiceAccountController.cs:181,346` |
| `POST /service-accounts/{id}/roles` | access to the SA | `{role, org_id?, project_id?}` | `ServiceAccountController.cs:250,348` |
| `POST /admin/organizations/{id}/admins` | SuperAdmin | `{user_id, role, scope_id?}` | `SystemAdminController.cs:1103` |
| `POST /api/introspect` | service account | **form**: `token` | `IntrospectionController.cs:40` |
| `POST /api/authorize` | service account | `{token, namespace, object, relation}` | `IntrospectionController.cs:78` |

`role` values are validated against `KnownManagementRoles`
(`SystemAdminController.cs:38` — `super_admin`, `org_admin`, `project_admin`); anything else is
`400 unknown_role`.

Route prefixes: `/admin` is SuperAdmin-only at the class level
(`SystemAdminController.cs:16`), `/org` is OrgAdmin-only (`OrgController.cs:19`), `/api/manage`
is SuperAdmin-only (`ManagedApiController.cs:18`).

---

## Deployment notes that bite integrators

These are open issues, recorded here because each one looks like an integration bug when you hit
it. Detail in [`2026-07-28-findings-securite-deploiement.md`](2026-07-28-findings-securite-deploiement.md).

| Symptom | Cause | Workaround |
|---|---|---|
| Pod in `CrashLoopBackOff` on a dev deploy | `App__TrustedProxies` must be set explicitly in Production, and the image runs as Production; `values.yaml`'s comment claiming an RFC1918 fallback is wrong | `--set rediensiam.app.trustedProxies="10.42.0.0/16,10.43.0.0/16"` (k3s pod CIDRs) |
| Admin console login does nothing | its own CSP `connect-src 'self'` blocks the cross-origin discovery fetch when the console is served on a NodePort | widen `connect-src` to the issuer origin and rebuild, or drive the API with curl |
| Serving RediensIAM under `https://host/iam` breaks OIDC | the ingress serves at a host root; issuer and discovery URLs are absolute | give it a dedicated host (`iam.example.com`) |

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
7. **Matching a bare tenant role name.** `ext.roles` carries tenant roles as
   `{project_id}/{name}`; `roles.contains("admin")` matches nothing. See the warning above.
