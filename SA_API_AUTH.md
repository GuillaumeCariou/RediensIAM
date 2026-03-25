# Service Account & API Authentication — Analysis

## What exists today

### PAT (Personal Access Token) — solid
- **Generation**: `{prefix}{40 random bytes base64}`, stored as SHA256 hash (`PatGenerationService`)
- **Validation**: Redis-cached (5 min TTL), hash lookup → returns introspection response (`PatIntrospectionService`)
- **Introspection response**: `{ active, sub: "sa:{id}", org_id, project_id, roles[], is_service_account }`
- **Endpoint**: `POST /internal/tokens/introspect` (internal — only the gateway calls this)
- **Roles**: SAs carry roles just like users — system SAs via `ServiceAccountOrgRoles`, project SAs via `ProjectRoles`
- **Audit**: `LastUsedAt` updated fire-and-forget on every use
- **Expiry**: configurable per-token (`ExpiresAt`)

This is equivalent to **GitHub/Linear API keys** — simple, developer-friendly, battle-tested.

### Hydra JWK management on SAs — incomplete
There are endpoints to add/remove JWKs on service accounts (`AddSystemServiceAccountKey`, etc.)
but `ServiceAccount` has no `HydraClientId` field — each SA does **not** have its own OAuth2 client.
The JWK management purpose is unclear and appears to be a dead/partial feature.

---

## Gap analysis vs Zitadel

| Mechanism | Zitadel | RediensIAM | Notes |
|---|---|---|---|
| Static PAT | ✓ | ✓ | Well implemented |
| OAuth2 `client_credentials` per SA | ✓ | ✗ | Only exists per **project** (Hydra client) |
| Private key JWT auth | ✓ | Partial/broken | JWKs exist but no SA Hydra client |
| Short-lived access tokens | ✓ | ✗ | PATs are long-lived by default |
| Public token introspection (RFC 7662) | ✓ | ✗ | `/internal/tokens/introspect` is internal only |
| Scope-based access control | ✓ | ✗ | No OAuth2 scopes on SAs |

---

## Current use cases and verdict

**For internal system integrations (backend calling RediensIAM admin API):**
PATs work perfectly. No change needed.

**For customers who build APIs that need to accept tokens:**
They can't validate tokens themselves — introspection is internal.
This is the main gap.

**For customers who want machine-to-machine with short-lived tokens:**
Not supported. PATs are long-lived.

---

## Proposed options (if/when needed)

### Option A — Public token introspection (quick win, no architecture change)
Add a public `POST /oauth2/introspect` endpoint (RFC 7662).
Customers' downstream APIs can validate any token (PAT or Hydra JWT) themselves.
- No new entities, no Hydra changes
- Requires basic rate-limiting on the endpoint

### Option B — OAuth2 client per service account (full Zitadel pattern)
- At SA creation, also create a Hydra OAuth2 client → store `HydraClientId` on `ServiceAccount`
- SA authenticates with `client_id + client_secret` **or** private key JWT → gets short-lived JWT from Hydra
- Downstream APIs validate JWT using Hydra's JWKS endpoint — **no callback needed**
- Roles flow into JWT claims via Hydra's token hook (already exists for user login)
- Requires: `HydraClientId` on `ServiceAccount` entity + migration + SA creation wiring

### Option C — Extend existing Hydra project client
Use the project's existing Hydra client with a `client_credentials` grant for SA-scoped tokens.
Simpler than B but SAs share a single client — less auditable, no per-SA token isolation.

---

## Recommendation

**For now**: nothing needs to change — PATs cover all current use cases.

**Next step if needed**: implement **Option A** (public introspection) — one endpoint, no architecture change, unblocks customers who want to validate tokens themselves.

**Long term** (if customers request machine-to-machine short-lived tokens): **Option B** — give each SA its own Hydra client. The Hydra token hook infrastructure already exists, so only SA creation/deletion plumbing and the `HydraClientId` field need to be added.
