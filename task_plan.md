# Task Plan: RediensIAM Full UI Overhaul

## Goal
Implement the full RediensIAM UI design (from Claude Design prototype) into the existing React/TypeScript/Vite admin SPA, pixel-perfect, using the new OKLCH design system.

## Current Phase
Phase 10 — COMPLETE

## Phases

### Phase 1: Discovery & Design Import
- [x] Fetch and extract Claude Design bundle
- [x] Read README, chat transcript, all prototype files
- [x] Understand codebase structure (React/TS/Vite/Tailwind/shadcn)
- **Status:** complete

### Phase 2: Design System Foundation
- [x] `index.html` — Geist + Geist Mono fonts, title "RediensIAM"
- [x] `tailwind.config.js` — Geist font families registered
- [x] `src/index.css` — full OKLCH token system (17 themes), all `iam-*` utility classes
- **Status:** complete

### Phase 3: Core Primitives & Shared Components
- [x] `IamAvatar` + `IamAvatarStack` — initials + hue-based color
- [x] `IamChip` — default/success/warn/danger/accent tones + mono variant
- [x] `IamDot` — status dot (success/warn/danger/muted)
- [x] `Spark` — SVG sparkline polyline
- [x] `StatCard` — stat card with label/value/sub/spark
- [x] `IamTuple` — relation tuple display (ns:obj#rel@subj)
- [x] `HealthRow` — service health status row
- [x] `ActivityChart` — 24h login activity bar chart
- [x] `IamDialog` — modal using iam-dialog-* CSS classes
- [x] `PageHeader` — updated to iam-page-header style, accepts actions array
- [x] Barrel export `src/components/iam/index.ts`
- **Status:** complete

### Phase 4: Login SPA Redesign
- [x] `Login.tsx` — two-column layout, TokenVisual right panel, SSO, show/hide pw
- [x] `MfaChallenge.tsx` — 4-method picker, OTP grid cells with focus traversal
- [x] `MfaSetup.tsx` — 3-step stepper (QR/secret, verify, backup codes)
- [x] `Register.tsx` — strength meter, OTP grid verify step
- [x] `PasswordReset.tsx` — 3-step (email, OTP grid, new password)
- [x] `SetPassword.tsx` — invite accept + set password
- [x] `App.tsx` — OAuthError updated to new design
- [x] `index.html` — Geist fonts, title RediensIAM
- [x] `src/index.css` — full OKLCH token system + all design classes
- **Status:** complete

### Phase 5: Admin Shell
- [x] `Sidebar.tsx` — iam-* CSS, collapsible sections, brand mark, user footer popover, no Tailwind/shadcn
- [x] `Topbar.tsx` — SYS/ORG/PRJ scope chips from URL, cmd-search button
- [x] `CommandPalette.tsx` — ⌘K global shortcut, search filter, keyboard nav, role-gated items
- [x] `Shell.tsx` — iam-screen layout, Topbar + Sidebar + CommandPalette wired
- [x] `ScopeContext.tsx` — orgName/projectName context for pages to populate breadcrumb
- [x] `App.tsx` — ScopeProvider added
- **Status:** complete

### Phase 6: System Pages
- [ ] `SystemDashboard` — 4-stat grid + activity chart + health mini + orgs table
- [ ] `Organisations` — table with plan chip, user/project counts, status
- [ ] `SystemUsers` — cross-tenant users table
- [ ] `SystemProjects` — all projects table
- [ ] `SystemUserLists` — system-level user lists
- [ ] `SystemServiceAccounts` — SA table with last-used
- [ ] `SystemEmail` — email provider config
- [ ] `AuditLog` — events table with action coloring, actor/target, IP
- [ ] `Metrics` — login p95, MFA enrollment, uptime stats
- [ ] `SystemHealth` — service health rows (Hydra, Keto, PG, Dragonfly, Oathkeeper)
- [ ] `SystemTheming` — full theme preset grid with PresetCard
- **Status:** pending

### Phase 7: Org Pages
- [ ] `OrgOverview` — stat cards + recent audit + projects list
- [ ] `UserLists` — table + members panel slide-out
- [ ] `Projects` (org) — project cards/table
- [ ] `OrgAdmins` — admins table
- [ ] `OrgServiceAccounts` — SA table
- [ ] `OrgEmail` — email override config
- [ ] `OrgAuditLog` — org-scoped audit events
- [ ] `OrgWebhooks` — webhooks table with success/fail counts
- [ ] `OrgSettings` — org metadata + danger zone
- **Status:** pending

### Phase 8: Project Pages
- [ ] `ProjectDashboard` — project stat cards + recent auth activity
- [ ] `ProjectUsers` — users table with roles chips, MFA status, last seen
- [ ] `ProjectRoles` — roles list with member count + default badge
- [ ] `ProjectPermissions` — permission matrix (role × permission checkboxes)
- [ ] `ProjectServiceAccounts` — SA table + API key management
- [ ] `Authentication` — provider toggles + MFA config + self-reg toggle + deny banner
- [ ] `ProjectSettings` — slug + require-role toggle + danger zone
- **Status:** pending

### Phase 9: Theming System
- [ ] `TweaksPanel` — theme preset grid (4-col), density/radius/role/switcher toggles
- [ ] `TweaksButton` — fixed FAB bottom-right
- [ ] `PresetCard` — mini preview with sidebar + content strips
- [ ] Theme persistence — `localStorage` via `ThemeContext`
- [ ] Wire `data-theme` attribute to `<html>` on theme change
- **Status:** pending

### Phase 10: Final Verification & Build
- [ ] `npm run build` passes with no errors
- [ ] TypeScript strict mode — no type errors
- [ ] All routes render without crash
- [ ] Dark/light themes toggle correctly
- [ ] Scope switching works (system → org → project)
- **Status:** pending

## Key Questions
1. Does the login redesign replace the existing Ory Hydra login flow or live alongside it?
2. Should new components coexist with existing shadcn ones or fully replace them?
3. Is `ThemeContext` already wired to `data-theme` on `<html>`, or needs updating?

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| `iam-*` prefix for all new CSS classes | Avoids collision with Tailwind/shadcn utility classes |
| Keep shadcn HSL vars alongside OKLCH vars | Existing components keep working during migration |
| Phase-by-phase implementation | Allows incremental testing; don't break working features |
| New components in `src/components/iam/` | Clean separation from existing shadcn ui components |
| Geist via Google Fonts CDN | Matches prototype; no npm package needed |

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| — | — | — |

## Notes
- Design source files at `/tmp/design_extracted/rediensiam/project/` (7 JSX files)
- Target: `/home/guille/Desktop/Workspace/RediensIAM/frontend/admin/`
- Update phase status: pending → in_progress → complete
- Re-read this plan before major decisions
