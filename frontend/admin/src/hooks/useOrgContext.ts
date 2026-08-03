import { useLocation, useParams, useSearchParams } from 'react-router';
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
  const orgBase      = isSystemCtx ? `/system/organisations/${orgId}` : '/org';
  const userListBase = isSystemCtx ? `${orgBase}/userlists`           : '/org/userlists';

  const projectUrl = (projId: string) =>
    isSystemCtx
      ? `/system/organisations/${orgId}/projects/${projId}`
      : `/project?project_id=${projId}`;

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
  const projectBase = isSystemCtx
    ? `/system/organisations/${oid}/projects/${pid}`
    : '/project';

  return { projectId, isSystemCtx, projectBase };
}
