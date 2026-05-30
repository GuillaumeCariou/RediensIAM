# Findings & Decisions — RediensIAM UI Overhaul

## Requirements
- Full pixel-perfect implementation of Claude Design prototype into the React/TS/Vite admin SPA
- Preserve existing functionality (auth, API calls, routing) — only replace visuals/UX
- All 3 role scopes: super_admin / org_admin / project_manager
- Complete login flow: password, MFA (TOTP/WebAuthn/SMS/backup), register, reset, invite
- Admin shell: collapsible scope-aware sidebar, scope breadcrumb topbar, ⌘K command palette
- 17 theme presets (stripe, jamm, void, aurora, sand, forest, ocean, desert, tundra, siberia × light/dark)
- Density + radius + role tweaks panel

## Target Codebase

### Stack
- React 18 + TypeScript + Vite 8
- Tailwind CSS v3 + shadcn/ui (Radix primitives)
- React Router v6 (basename `/admin`)
- Ory SDK for auth (`AuthContext`, `auth.ts`)

### Key Files
- `frontend/admin/index.html` — Vite HTML entry (UPDATED: Geist fonts, title)
- `frontend/admin/src/index.css` — CSS foundation (UPDATED: full OKLCH system + iam-* classes)
- `frontend/admin/tailwind.config.js` — (UPDATED: Geist font families)
- `frontend/admin/src/App.tsx` — Router, role-gated routes, Shell wrapper
- `frontend/admin/src/context/AuthContext.tsx` — Ory auth, `isSuperAdmin`, `isOrgAdmin`, `isProjectManager`
- `frontend/admin/src/context/ThemeContext.tsx` — theme state (needs `data-theme` on `<html>`)
- `frontend/admin/src/components/layout/Shell.tsx` — app shell (sidebar + topbar wrapper)
- `frontend/admin/src/components/layout/Sidebar.tsx` — existing sidebar (to be redesigned)
- `frontend/admin/src/components/layout/PageHeader.tsx` — page header component

### Page Map (existing → design equivalent)
| Route | Existing Component | Design Page |
|-------|-------------------|-------------|
| `/system` | `SystemDashboard` | `SysDashboard` |
| `/system/organisations` | `Organisations` | `OrgsList` |
| `/org` | `OrgDashboard` | `OrgOverview` |
| `/org/userlists` | `UserLists` | `UserLists` |
| `/project` | `ProjectDashboard` | `ProjOverview` |
| `/project/users` | `ProjectUsers` | `ProjUsers` |
| `/project/roles` | `ProjectRoles` | `ProjRoles` |
| `/project/roles` (permissions tab) | `ProjectRoles` | `ProjPermissions` |
| `/project/authentication` | `Authentication` | `ProjAuth` |
| `/system/audit-log` | `AuditLog` | `AuditLogPage` |
| `/system/metrics` | `SystemMetrics` | `MetricsPage` |
| `/system/health` | `SystemHealth` | `HealthPage` |
| `/org/webhooks` | `OrgWebhooks` | `Webhooks` |
| `/org/email` | `OrgEmail` | `OrgEmail` |
| `/system/email` | `SystemEmail` | `SystemEmail` |

## Design System Findings

### Typography
- `--font-sans: 'Geist'` — all UI text
- `--font-mono: 'Geist Mono'` — identifiers, tokens, IDs, monospace values

### Color Architecture
- OKLCH color space throughout
- Default preset: `stripe` (indigo primary, near-black sidebar)
- `--ia-accent` = primary action color (avoids collision with Tailwind's `--accent`)
- `--fg`, `--fg-muted`, `--fg-subtle` = text hierarchy
- `--surface`, `--surface-2` = card/elevated backgrounds
- `--bg`, `--bg-sunken` = page backgrounds
- `--iam-sidebar-*` = sidebar-specific palette

### CSS Class Conventions
- All new classes prefixed `iam-` to avoid Tailwind collision
- Existing shadcn components keep `hsl(var(--*))` vars — NOT changed
- New IAM components use OKLCH vars directly

### Key Visual Patterns
- **Scope chips**: `SYS` (red), `ORG` (accent/indigo), `PRJ` (green) mono labels
- **Relation tuples**: `namespace:object#relation@subject` in mono colored spans
- **Deny banner**: amber dashed border, warn colors — "No role = no access"
- **Token visual**: mock decoded JWT in mono on login right panel
- **Stat cards**: 28px bold value, sparkline top-right, uppercase label
- **Status dots**: 6px circles with glow for success
- **Permission matrix**: grid of checkboxes (role × permission)

### Sidebar Structure
```
System (super_admin only)
  Dashboard / Organisations / Admins / Users / Projects / User Lists / 
  Service Accounts / Email / Audit Log / Metrics / Health

Organisation · {name}
  Overview / Projects / User Lists / Admins / Service Accounts /
  Email / Audit Log / Webhooks / Settings

Project · {name}
  Overview / Users / Roles / Permissions / Service Accounts /
  Authentication / Settings
```

### Login Flow Screens
`login` → `mfa` → (success) → admin
`login` → `register` → (email OTP) → login
`login` → `reset` → (email OTP) → (new password) → login
`invite` link → `invite` screen → login

### MFA Methods
- TOTP: 6-digit OTP grid
- WebAuthn: passkey/YubiKey waiting UI
- SMS: code sent banner + OTP grid
- Backup: single text input `xxxx-xxxx-xxxx`

## Technical Decisions
| Decision | Rationale |
|----------|-----------|
| New components in `src/components/iam/` | Separates from shadcn ui/, easy to find |
| Keep existing page files, update their content | Less disruption, preserves routes |
| `ThemeContext` sets `data-theme` on `document.documentElement` | CSS theme vars activate via attribute selectors |
| Scope context via `useOrgContext` hook (already exists) | Avoid new context, extend existing |
| Login redesign wraps existing Ory redirect logic | Preserve auth security, only change visuals |

## Issues Encountered
| Issue | Resolution |
|-------|------------|
| — | — |

## Resources
- Design prototype files: `/tmp/design_extracted/rediensiam/project/`
- Admin SPA root: `/home/guille/Desktop/Workspace/RediensIAM/frontend/admin/`
- `data.jsx` — mock data shapes (reference for TypeScript interfaces)
- `styles.css` — full CSS source (already implemented in index.css)
- `admin-pages.jsx` — System + Org pages source
- `admin-pages2.jsx` — Project pages source
- `admin-shell.jsx` — Sidebar, Topbar, CommandPalette, ScopeBreadcrumb
- `login.jsx` — all login screen variants
- `tweaks.jsx` — TweaksPanel, PresetCard, PRESET_GROUPS
