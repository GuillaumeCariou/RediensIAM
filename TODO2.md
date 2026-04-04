# RediensIAM — Security Architecture Refactor

> Goal: remove the admin SPA and management API from the public internet. Expose only user-facing endpoints publicly. Provide a controlled programmatic surface for service accounts.

---

## Architecture Target

### Public ingress (internet-facing, port 80/443)
| Path | Backend | Notes |
|---|---|---|
| `/oauth2`, `/.well-known`, `/userinfo` | Hydra public | OIDC protocol |
| `/auth/*`, `/account/*` | Public port 5000 | Login flows, user self-service |
| `/login`, `/` | Public port 5000 | Login SPA |
| `/api/manage/*` | Public port 5000 | SA programmatic access — auth-gated, no SPA |
| ~~`/admin`~~ | ~~Admin port 5001~~ | **REMOVED from public ingress** |
| ~~`/internal`~~ | ~~Admin port 5001~~ | **REMOVED from public ingress** |

### Admin access (not internet-facing)
| Method | Who | URL |
|---|---|---|
| NodePort 30501 via SSH tunnel | Human admins | `http://localhost:30501/admin/` |
| `kubectl port-forward` | Dev/ops | `kubectl port-forward svc/rediensiam-admin 30501:5001` |
| Private ingress + IP allowlist | Production VPN | `https://admin.rediens.net/` |

### In-cluster (service-to-service)
- Other pods call admin APIs via `http://rediensiam-admin:5001/admin/...`

---

## Work Items

### 1. `deploy/rediensiam/templates/ingress.yaml`
- Remove the `/admin` path rule (→ admin service port 5001) from the public ingress
- Remove the `/internal` path rule from the public ingress
- Result: only Hydra paths + `/api/manage` + `/` remain on the public ingress

### 2. `/api/manage/*` — programmatic SA surface on public port

Define a minimal set of endpoints reachable from external service accounts over the public port. These are the operations an external app legitimately needs to call (e.g. a CI/CD pipeline provisioning a new project):

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/manage/organizations` | List orgs |
| `POST` | `/api/manage/organizations` | Create org |
| `GET` | `/api/manage/organizations/:id` | Get org |
| `POST` | `/api/manage/organizations/:id/projects` | Create project in org |
| `GET` | `/api/manage/organizations/:id/projects` | List projects in org |
| `POST` | `/api/manage/userlists` | Create user list |
| `POST` | `/api/manage/userlists/:id/users` | Add user to list (invite) |

Auth: Bearer token — either a super-admin PAT (`Authorization: Bearer <pat>`) or a `client_credentials` access token from a service account with `super_admin` role. Same auth as today, just gated to this new `/api/manage/` path prefix on the public port.

**Implementation approach:**
- Add a new controller `ManagedApiController.cs` (or thin forwarding layer) that exposes exactly these endpoints at `/api/manage/...`
- Internally reuses the same service layer as `SystemAdminController`
- Decorated with `[RequireManagementLevel(ManagementLevel.SuperAdmin)]` (same as admin endpoints)
- Add ingress rule: `/api/manage` → public service port 5000

### 3. `src/Program.cs`
- Remove the "Block admin/internal on public port" middleware (lines ~335-345) — the network enforces this now via ingress; no need for an app-level block
- Keep the two-port Kestrel setup (5000 + 5001) — admin port still used for in-cluster SA access and NodePort
- Add the `/api/manage` group to the public port (it needs to pass through the existing auth middleware stack)
- Ensure `/admin/config` endpoint stays reachable on admin port — the admin SPA fetches this on load

### 4. Admin SPA — `apiFetch` fix (implicit, no code change needed)
- Once the admin SPA is served from port 5001 directly (via NodePort or SSH tunnel), all `apiFetch('/admin/...')` relative calls automatically hit port 5001
- The 404 issue disappears because the requests never go through the public ingress
- No change to `api.ts` or `auth.ts` needed

### 5. `deploy/rediensiam/values.yaml`
- Add a comment to `service.admin.nodePort: 30501` explaining it is the only external access point for the admin SPA
- Optionally add a `values.prod-admin.yaml` template with the private ingress + IP allowlist for production

### 6. (Optional — Production) Private admin ingress
Create `deploy/rediensiam/templates/admin-ingress.yaml`:
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-admin-internal
  annotations:
    traefik.ingress.kubernetes.io/router.middlewares: "default-ip-allowlist@kubernetescrd"
spec:
  rules:
    - host: {{ .Values.adminIngress.host }}   # e.g. admin.rediens.net — private DNS only
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-admin
                port:
                  number: 5001
```
Guarded by `{{ if .Values.adminIngress.enabled }}`. Disabled in dev.

---

## Order of Implementation

1. **`ingress.yaml`** — remove `/admin` + `/internal` rules (immediate security fix)
2. **`ManagedApiController.cs`** — add `/api/manage/*` endpoints on public port
3. **`ingress.yaml`** — add `/api/manage` rule pointing to public service
4. **`Program.cs`** — remove blocking middleware
5. **`values.yaml`** — comments + optional admin ingress template
6. **Redeploy** (`deploy/deploy.sh --dev`) and verify:
   - `http://localhost/admin/` → 404 (not accessible from public)
   - `http://localhost:30501/admin/` → admin SPA loads correctly
   - `http://localhost:30501/admin/...` API calls → work (no more 404 on stats)
   - `http://localhost/api/manage/organizations` → requires Bearer token, returns data
   - `http://localhost/auth/login` → login SPA still works

---

## What Does NOT Change
- All login/auth flows (`/auth/*`) — untouched
- Admin SPA code — no changes to frontend
- `api.ts` / `apiFetch` — no path changes needed
- Org, project, account endpoints (`/org/*`, `/project/*`, `/account/*`) — untouched
- Service account PAT/JWT auth mechanism — untouched
- Hydra/Keto configuration — untouched
