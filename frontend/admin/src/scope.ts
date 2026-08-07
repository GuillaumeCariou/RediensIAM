/**
 * Where you are in the console, said once.
 *
 * The console has three levels — the deployment, an organisation, a project — and every level is
 * reachable by two different people: a super-admin browsing *into* a tenant, and that tenant's own
 * administrator. Those two produce different URLs for the same page:
 * `/system/organisations/:id/service-accounts` and `/org/service-accounts`.
 *
 * That is a property of the URL, not of the page, and the pages already know it — `useOrgContext`
 * exists precisely to resolve an id that may come from the path or from the token. What did *not*
 * know it was the navigation: `Sidebar.tsx` carried five lists (`systemNav`, `orgNav`,
 * `projectNav`, `buildSysOrgNav`, `buildSysProjNav`) for three levels, so every organisation
 * destination was written twice and every project destination twice more. Adding a page meant
 * remembering all the copies, and the copies had already drifted.
 *
 * This module is the single description: the levels, their destinations, and how a scope turns
 * into a path. Nothing else may build a console URL by concatenation.
 */

export type Level = 'deployment' | 'org' | 'project';

/**
 * The level, plus the ids that level needs. Ids are present when the URL carries them — a
 * super-admin browsing into a tenant — and absent when the caller's own token supplies them.
 */
export interface Scope {
  level: Level;
  orgId?: string;
  projectId?: string;
}

/** A destination within a level. `key` is the path segment; empty means the level's own page. */
export interface Destination {
  key: string;
  label: string;
  icon: string;
  /** Deployment-level entries a non-super-admin must never be offered. */
  superOnly?: boolean;
}

/**
 * Every destination the console has, by level, once.
 *
 * Order is the order they appear in the tree. The proposed design adds Impersonation and
 * deployment Settings here, and Integration and Login under a project; they are deliberately
 * absent until their pages exist, because a tree entry that leads nowhere is worse than a missing
 * one — it reads as a feature.
 */
export const DESTINATIONS: Record<Level, Destination[]> = {
  deployment: [
    { key: '',                 label: 'Overview',         icon: 'dashboard' },
    { key: 'organisations',    label: 'Organisations',    icon: 'building' },
    { key: 'admins',           label: 'Admins',           icon: 'shield',   superOnly: true },
    { key: 'users',            label: 'Users',            icon: 'users',    superOnly: true },
    { key: 'projects',         label: 'Projects',         icon: 'folder',   superOnly: true },
    { key: 'userlists',        label: 'User lists',       icon: 'list',     superOnly: true },
    { key: 'service-accounts', label: 'Service accounts', icon: 'bot',      superOnly: true },
    { key: 'email',            label: 'Email',            icon: 'mail',     superOnly: true },
    { key: 'impersonation',    label: 'Impersonation',    icon: 'user',     superOnly: true },
    { key: 'audit-log',        label: 'Audit log',        icon: 'log' },
    { key: 'metrics',          label: 'Metrics',          icon: 'chart' },
    { key: 'health',           label: 'Health',           icon: 'heart',    superOnly: true },
    { key: 'settings',         label: 'Settings',         icon: 'settings', superOnly: true },
  ],
  org: [
    { key: '',                 label: 'Overview',         icon: 'dashboard' },
    { key: 'projects',         label: 'Projects',         icon: 'folder' },
    { key: 'userlists',        label: 'User lists',       icon: 'list' },
    { key: 'admins',           label: 'Admins',           icon: 'shield' },
    { key: 'service-accounts', label: 'Service accounts', icon: 'bot' },
    { key: 'email',            label: 'Email',            icon: 'mail' },
    { key: 'audit-log',        label: 'Audit log',        icon: 'log' },
    { key: 'webhooks',         label: 'Webhooks',         icon: 'zap' },
    { key: 'settings',         label: 'Settings',         icon: 'settings' },
  ],
  project: [
    { key: '',                 label: 'Overview',         icon: 'dashboard' },
    { key: 'users',            label: 'Users',            icon: 'users' },
    { key: 'roles',            label: 'Roles',            icon: 'shield' },
    { key: 'service-accounts', label: 'Service accounts', icon: 'bot' },
    { key: 'authentication',   label: 'Authentication',   icon: 'key' },
    { key: 'settings',         label: 'Settings',         icon: 'settings' },
  ],
};

/**
 * The path prefix a scope's pages hang off.
 *
 * An id present in the scope means "a super-admin is browsing into this tenant", and produces the
 * `/system/...` shape; an id absent means the caller's own token names the tenant, and produces
 * the short shape. Both already exist as routes — this is where the choice between them is made,
 * instead of at every link.
 */
export function basePath(scope: Scope): string {
  if (scope.level === 'deployment') return '/system';

  if (scope.level === 'org') {
    return scope.orgId ? `/system/organisations/${scope.orgId}` : '/org';
  }

  return scope.orgId && scope.projectId
    ? `/system/organisations/${scope.orgId}/projects/${scope.projectId}`
    : '/project';
}

/**
 * The route patterns each level is mounted on, in React Router's syntax.
 *
 * Two per tenant level, because two audiences reach the same page by different URLs — and the
 * parameter names are load-bearing: `useOrgContext` and `useProjectContext` read `:id`, `:oid` and
 * `:pid` by name. `basePath` produces exactly these shapes with the ids filled in, and a test holds
 * the two in agreement so a rename here cannot silently orphan a page.
 */
export const ROUTE_BASES: Record<Level, string[]> = {
  deployment: ['/system'],
  org:        ['/org', '/system/organisations/:id'],
  project:    ['/project', '/system/organisations/:oid/projects/:pid'],
};

/** The URL of one destination within a scope. The only way to build a console path. */
export function hrefFor(scope: Scope, key: string): string {
  const base = basePath(scope);
  return key ? `${base}/${key}` : base;
}

/**
 * Reads the scope back out of a pathname.
 *
 * Deliberately ordered longest-shape-first: `/system/organisations/x/projects/y` is also a prefix
 * match for the organisation shape, and answering "org" there would put the tree on the wrong node.
 */
export function scopeFromPath(pathname: string): Scope {
  const sysProject = /^\/system\/organisations\/([^/]+)\/projects\/([^/]+)/.exec(pathname);
  if (sysProject) return { level: 'project', orgId: sysProject[1], projectId: sysProject[2] };

  const sysOrg = /^\/system\/organisations\/([^/]+)/.exec(pathname);
  if (sysOrg) return { level: 'org', orgId: sysOrg[1] };

  if (pathname.startsWith('/project')) return { level: 'project' };
  if (pathname.startsWith('/org')) return { level: 'org' };
  return { level: 'deployment' };
}

/**
 * Which destination of its level a pathname is on, or null when it is on none — a detail page such
 * as `/system/service-accounts/:id` sits under a destination without being it.
 *
 * Matching is exact on the full path, then longest-prefix, so `/org/userlists/:id` highlights
 * `userlists` rather than the level's own Overview, whose key is the empty string and whose path is
 * a prefix of everything.
 */
export function activeKey(scope: Scope, pathname: string): string | null {
  // A path belongs to exactly one level, and only that level may light a row for it.
  //
  // Without this, `/system/organisations/{id}/userlists` prefix-matches the DEPLOYMENT's
  // `organisations` destination as well as the tenant's `userlists`, and the tree lit both — three
  // rows once a project was open. A tree that says you are in two places says nothing, and it is
  // the same defect as a breadcrumb disagreeing with the tree, one component over.
  const here = scopeFromPath(pathname);
  if (here.level !== scope.level || here.orgId !== scope.orgId || here.projectId !== scope.projectId) {
    return null;
  }

  const candidates = DESTINATIONS[scope.level]
    .map(d => ({ key: d.key, href: hrefFor(scope, d.key) }))
    .sort((a, b) => b.href.length - a.href.length);

  const exact = candidates.find(c => c.href === pathname);
  if (exact) return exact.key;

  const prefixed = candidates.find(c => c.key !== '' && pathname.startsWith(`${c.href}/`));
  return prefixed ? prefixed.key : null;
}
