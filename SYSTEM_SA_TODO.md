# System Service Accounts — Implementation Plan

## Context

System service accounts live in the root UserList (`org_id = NULL`, `immovable = true`).
They are created by a super_admin and can be granted the `super_admin` role via an OrgRole
row with `org_id = NULL`. They authenticate via PAT (Personal Access Token) or JWT Profile,
and their token carries `roles: ["super_admin"]`, `org_id: null`, `project_id: null`,
`is_service_account: true` — giving them identical access to a human super_admin.

The existing `GET /admin/service-accounts` endpoint already lists system SAs (filters where
`UserList.OrgId == null`). Everything else is missing.

---

## 1. Backend — AdminController.cs

Add the following endpoints. All require `IsSuperAdmin`.

### 1.1 Find or ensure the root UserList exists

The root UserList (`org_id = NULL`, `immovable = true`) must exist before creating SAs.
Add a private helper:

```csharp
private async Task<UserList> GetOrCreateRootListAsync()
{
    var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.OrgId == null && ul.Immovable);
    if (list != null) return list;
    list = new UserList { Name = "__system__", OrgId = null, Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
    db.UserLists.Add(list);
    await db.SaveChangesAsync();
    return list;
}
```

### 1.2 POST /admin/service-accounts

Create a system SA in the root UserList.

```csharp
[HttpPost("/admin/service-accounts")]
public async Task<IActionResult> CreateServiceAccount([FromBody] CreateSystemSaRequest body)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var rootList = await GetOrCreateRootListAsync();
    var sa = new ServiceAccount
    {
        UserListId = rootList.Id,
        Name = body.Name,
        Description = body.Description,
        Active = true,
        CreatedBy = GetActorId(),
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.ServiceAccounts.Add(sa);
    await db.SaveChangesAsync();
    await audit.RecordAsync(null, null, GetActorId(), "sa.created", "service_account", sa.Id.ToString());
    return Created($"/admin/service-accounts/{sa.Id}", new { sa.Id, sa.Name, sa.Description });
}
```

### 1.3 GET /admin/service-accounts/:id

Get a single system SA with its PAT list and assigned roles.

```csharp
[HttpGet("/admin/service-accounts/{id}")]
public async Task<IActionResult> GetServiceAccount(Guid id)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var sa = await db.ServiceAccounts
        .Include(sa => sa.PersonalAccessTokens)
        .Include(sa => sa.UserList)
        .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == null);
    if (sa == null) return NotFound();
    var roles = await db.OrgRoles
        .Where(r => r.UserId == id && r.OrgId == null)  // NOTE: OrgRole.UserId stores SA id for SAs
        // Actually see note in section 1.7 — role assignment for SAs may need a separate mechanism
        .Select(r => r.Role)
        .ToListAsync();
    return Ok(new
    {
        sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt,
        pats = sa.PersonalAccessTokens.Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt }),
        roles
    });
}
```

### 1.4 DELETE /admin/service-accounts/:id

```csharp
[HttpDelete("/admin/service-accounts/{id}")]
public async Task<IActionResult> DeleteServiceAccount(Guid id)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var sa = await db.ServiceAccounts
        .Include(sa => sa.UserList)
        .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == null);
    if (sa == null) return NotFound();
    db.ServiceAccounts.Remove(sa);
    await db.SaveChangesAsync();
    await audit.RecordAsync(null, null, GetActorId(), "sa.deleted", "service_account", id.ToString());
    return NoContent();
}
```

### 1.5 POST /admin/service-accounts/:id/pat

Generate a PAT. Returns the raw token **once** — never retrievable again.
Token format: `rediens_pat_<32 random bytes as hex>`.
Store SHA-256 hash.

```csharp
[HttpPost("/admin/service-accounts/{id}/pat")]
public async Task<IActionResult> GeneratePat(Guid id, [FromBody] GeneratePatRequest body)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var sa = await db.ServiceAccounts
        .Include(sa => sa.UserList)
        .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == null);
    if (sa == null) return NotFound();

    var raw = "rediens_pat_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();
    var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLower();

    var pat = new PersonalAccessToken
    {
        ServiceAccountId = id,
        Name = body.Name,
        TokenHash = hash,
        ExpiresAt = body.ExpiresAt,
        CreatedBy = GetActorId(),
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.PersonalAccessTokens.Add(pat);
    await db.SaveChangesAsync();
    // Return raw token — shown once only
    return Created($"/admin/service-accounts/{id}/pat/{pat.Id}", new
    {
        pat.Id, pat.Name, pat.ExpiresAt,
        token = raw   // <-- shown once, never stored
    });
}
```

Required usings: `System.Security.Cryptography`

### 1.6 GET /admin/service-accounts/:id/pat

List PATs — names and expiry only, never the raw token or hash.

```csharp
[HttpGet("/admin/service-accounts/{id}/pat")]
public async Task<IActionResult> ListPats(Guid id)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var pats = await db.PersonalAccessTokens
        .Where(p => p.ServiceAccountId == id)
        .Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt })
        .ToListAsync();
    return Ok(pats);
}
```

### 1.7 DELETE /admin/service-accounts/:id/pat/:pat_id

```csharp
[HttpDelete("/admin/service-accounts/{id}/pat/{patId}")]
public async Task<IActionResult> RevokePat(Guid id, Guid patId)
{
    if (!IsSuperAdmin) return StatusCode(403);
    var pat = await db.PersonalAccessTokens.FirstOrDefaultAsync(p => p.Id == patId && p.ServiceAccountId == id);
    if (pat == null) return NotFound();
    db.PersonalAccessTokens.Remove(pat);
    await db.SaveChangesAsync();
    return NoContent();
}
```

### 1.8 Role assignment for system SAs

System SAs get their roles the same way human super_admins do — via an OrgRole row.
The difference: the SA's `Id` is stored in `OrgRole.UserId`, and `OrgRole.OrgId = null`.

**Check**: Does `OrgRole.UserId` foreign-key to `User.Id` in the DB schema? If it does, SAs
cannot be stored there directly. Read `RediensIamDbContext.cs` and the OrgRole EF config to confirm.

**If FK constraint blocks it**: add a `ServiceAccountOrgRole` table (same structure, just
`ServiceAccountId` instead of `UserId`). Then update `InternalController.cs` to read from
it during PAT introspection.

**If no FK**: reuse OrgRole with `UserId = sa.Id`. Simpler — prefer this if possible.

Role endpoints to add:

```
POST   /admin/service-accounts/:id/roles   { role: "super_admin" }
DELETE /admin/service-accounts/:id/roles/:roleId
GET    /admin/service-accounts/:id/roles
```

### 1.9 PAT introspection (InternalController.cs)

The existing PAT introspection endpoint must also resolve system SA PATs. Check
`InternalController.cs` — specifically the PAT lookup and the role resolution logic.
Currently it probably looks up roles from `UserProjectRole` or `OrgRole` scoped by `OrgId`.
For system SAs: roles come from the root OrgRole rows (`OrgId = null`).

Verify the introspection returns:
```json
{
  "active": true,
  "sub": "sa:<id>",
  "org_id": null,
  "project_id": null,
  "roles": ["super_admin"],
  "is_service_account": true
}
```

---

## 2. Frontend — api.ts

Add to the existing `api.ts`:

```ts
// ── System Service Accounts ───────────────────────────────────────
export async function createSystemServiceAccount(body: { name: string; description?: string }) {
  return (await apiFetch('/admin/service-accounts', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getSystemServiceAccount(id: string) {
  return (await apiFetch(`/admin/service-accounts/${id}`)).json();
}
export async function deleteSystemServiceAccount(id: string) {
  return apiFetch(`/admin/service-accounts/${id}`, { method: 'DELETE' });
}
export async function generateSystemPat(saId: string, body: { name: string; expires_at?: string }) {
  return (await apiFetch(`/admin/service-accounts/${saId}/pat`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function listSystemPats(saId: string) {
  return (await apiFetch(`/admin/service-accounts/${saId}/pat`)).json();
}
export async function revokeSystemPat(saId: string, patId: string) {
  return apiFetch(`/admin/service-accounts/${saId}/pat/${patId}`, { method: 'DELETE' });
}
export async function listSystemSaRoles(saId: string) {
  return (await apiFetch(`/admin/service-accounts/${saId}/roles`)).json();
}
export async function assignSystemSaRole(saId: string, role: string) {
  return (await apiFetch(`/admin/service-accounts/${saId}/roles`, { method: 'POST', body: JSON.stringify({ role }) })).json();
}
export async function removeSystemSaRole(saId: string, roleId: string) {
  return apiFetch(`/admin/service-accounts/${saId}/roles/${roleId}`, { method: 'DELETE' });
}
```

---

## 3. Frontend — SystemServiceAccounts.tsx (rewrite)

**File**: `frontend/admin/src/pages/system/SystemServiceAccounts.tsx`

Current state: probably just a list with no actions. Rewrite to:

- Table columns: Name, Description, Status, Last used, (actions)
- Row click → navigate to `/system/service-accounts/:id`
- `[+ New Service Account]` button → dialog with Name + Description fields
- `[···]` dropdown per row: Delete (with AlertDialog confirm)

```tsx
// Rewrite SystemServiceAccounts.tsx
// Uses: listServiceAccounts, createSystemServiceAccount, deleteSystemServiceAccount
// Navigate on row click to /system/service-accounts/:id
```

---

## 4. Frontend — SystemServiceAccountDetail.tsx (new)

**File**: `frontend/admin/src/pages/system/SystemServiceAccountDetail.tsx`
**Route**: `/system/service-accounts/:id`

Layout (two sections):

### Section 1 — SA card
- Name, description, status badge (Active/Inactive), Created at
- `[Disable]` / `[Enable]` toggle button
- `[Delete]` button (with AlertDialog — navigates back on confirm)

### Section 2 — Assigned Roles
- Table: Role name, Granted at
- `[+ Assign Role]` button → dialog with a Select showing `super_admin` as the only option
  (system SAs can only hold `super_admin`)
- `[···]` per row: Remove role (AlertDialog)

### Section 3 — Personal Access Tokens
- Table: Name, Expires, Last used, Created
- `[+ Generate PAT]` button → dialog:
  - Name field (required)
  - Expiry date field (optional)
  - On submit: show the raw token in a read-only input with a copy button
  - Warning: "This token will not be shown again."
  - After closing the token display dialog, reload PAT list
- `[Revoke]` button per row (AlertDialog confirm)

---

## 5. App.tsx

Add import and route:

```tsx
import SystemServiceAccountDetail from './pages/system/SystemServiceAccountDetail';

// Inside <Routes>:
<Route path="system/service-accounts/:id" element={<SystemServiceAccountDetail />} />
```

---

## 6. Entities to verify

Before implementing, read these files to confirm field names and FK constraints:

- `src/Entities/PersonalAccessToken.cs` — confirm fields: `Id`, `ServiceAccountId`, `Name`,
  `TokenHash`, `ExpiresAt`, `LastUsedAt`, `CreatedBy`, `CreatedAt`
- `src/Entities/OrgRole.cs` — confirm whether `UserId` has a FK to `Users` table only,
  or if it can hold any Guid (SA id)
- `src/Data/RediensIamDbContext.cs` — check EF config for OrgRole FK constraints
- `src/Controllers/InternalController.cs` — understand current PAT introspection logic
  and what needs to change to support system SA PATs

---

## 7. Order of implementation

1. Read the 4 files listed in section 6
2. Implement backend endpoints (sections 1.1–1.8), deciding on OrgRole FK approach
3. Update InternalController if needed (section 1.9)
4. Add api.ts functions (section 2)
5. Rewrite SystemServiceAccounts.tsx (section 3)
6. Create SystemServiceAccountDetail.tsx (section 4)
7. Update App.tsx (section 5)
8. Run deploy script, fix any errors
9. Commit

---

## 8. Key invariants to preserve

- Raw PAT token is **returned once and never stored** — only the SHA-256 hash is persisted
- System SAs are identified by their UserList having `OrgId = null`
- The root UserList is created on-demand if it doesn't exist yet (`GetOrCreateRootListAsync`)
- `super_admin` is the **only** role a system SA can hold — no project or org scoping
- Introspection must return `org_id: null` and `project_id: null` for system SAs
