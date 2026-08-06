import { useLocation, useParams, useSearchParams } from 'react-router';
import { basePath, hrefFor } from '@/scope';
import { useAuth } from '@/context/AuthContext';

// ── Org context ────────────────────────────────────────────────────────────────

/**
 * Works for both org_admin (/org/*) and super_admin (/system/organisations/:id/*).
 *
 * The `id ?? oid ?? tokenOrgId` order below matters: URL params must win over the token claim,
 * otherwise a super_admin is pinned to their own org and cannot manage anyone else's.
 */
export function useOrgContext() {
  const { id, oid } = useParams<{ id?: string; oid?: string }>();
  const { orgId: tokenOrgId } = useAuth();
  const { pathname } = useLocation();

  const orgId        = id ?? oid ?? tokenOrgId;
  // Read from the path, not from the presence of a param. `org/userlists/:id` bound :id to the
  // USER LIST id, so every org admin opening one of their own lists was treated as being in the
  // system context and sent to the super-admin-only routes — a 403 on every request, swallowed by
  // the page's catch, leaving a titled page with an empty table. The param is now :listId, and
  // this no longer depends on anyone remembering that.
  const isSystemCtx  = pathname.startsWith('/system');
  // Built by scope.ts, not here. These were the fifth and sixth places in the console assembling a
  // path out of string pieces; every one of them had to be found and edited together whenever a
  // URL shape moved, and the sidebar's copy had already drifted from the router's.
  const orgBase      = basePath({ level: 'org', orgId: isSystemCtx ? orgId : undefined });
  const userListBase = hrefFor({ level: 'org', orgId: isSystemCtx ? orgId : undefined }, 'userlists');

  // The query form is not a URL shape scope.ts knows, and deliberately: an org admin has one
  // project route and reaches every project through it, which is what `?project_id=` carries.
  const projectUrl = (projId: string) =>
    isSystemCtx
      ? basePath({ level: 'project', orgId, projectId: projId })
      : `${basePath({ level: 'project' })}?project_id=${projId}`;

  return { orgId, isSystemCtx, orgBase, userListBase, projectUrl };
}

// ── Project context ────────────────────────────────────────────────────────────

/**
 * Works for project_manager (/project/*) and super_admin
 * (/system/organisations/:oid/projects/:pid/*).
 *
 * The `pid ?? queryProjectId ?? tokenProjectId` order below is the precedence the three entry
 * points need: URL path param (system context) beats the ?project_id query param (the link an
 * org_admin follows) beats the token claim (a project_manager's own project). Reordering it
 * silently sends an admin to the wrong project.
 */
export function useProjectContext() {
  const { oid, pid } = useParams<{ oid?: string; pid?: string }>();
  const [searchParams] = useSearchParams();
  const { projectId: tokenProjectId } = useAuth();

  const queryProjectId = searchParams.get('project_id') ?? undefined;
  const projectId   = pid ?? queryProjectId ?? tokenProjectId;
  const isSystemCtx = !!(oid && pid);
  const projectBase = basePath({ level: 'project', orgId: oid, projectId: pid });

  return { projectId, isSystemCtx, projectBase };
}
