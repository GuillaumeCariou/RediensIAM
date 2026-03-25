import { useParams, useSearchParams } from 'react-router-dom';
import { useAuth } from '@/context/AuthContext';

// ── Org context ────────────────────────────────────────────────────────────────
// Works for both org_admin (/org/*) and super_admin (/system/organisations/:id/*)
// URL params take priority over token claims so super_admin can manage any org.

export function useOrgContext() {
  const { id, oid } = useParams<{ id?: string; oid?: string }>();
  const { orgId: tokenOrgId } = useAuth();

  const orgId        = id ?? oid ?? tokenOrgId;
  const isSystemCtx  = !!(id ?? oid);
  const orgBase      = isSystemCtx ? `/system/organisations/${orgId}` : '/org';
  const userListBase = isSystemCtx ? '/system/userlists'              : '/org/userlists';

  // Link to a specific project's management page
  const projectUrl = (projId: string) =>
    isSystemCtx
      ? `/system/organisations/${orgId}/projects/${projId}`
      : `/project?project_id=${projId}`;

  return { orgId, isSystemCtx, orgBase, userListBase, projectUrl };
}

// ── Project context ────────────────────────────────────────────────────────────
// Works for project_manager (/project/*) and super_admin (/system/organisations/:oid/projects/:pid/*)

export function useProjectContext() {
  const { oid, pid } = useParams<{ oid?: string; pid?: string }>();
  const [searchParams] = useSearchParams();
  const { projectId: tokenProjectId } = useAuth();

  // Priority: URL path param (system ctx) > query param (org admin link) > token claim (project manager)
  const queryProjectId = searchParams.get('project_id') ?? undefined;
  const projectId   = pid ?? queryProjectId ?? tokenProjectId;
  const isSystemCtx = !!(oid && pid);
  const projectBase = isSystemCtx
    ? `/system/organisations/${oid}/projects/${pid}`
    : '/project';

  return { projectId, isSystemCtx, projectBase };
}
