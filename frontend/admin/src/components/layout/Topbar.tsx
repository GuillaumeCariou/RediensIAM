import { useEffect } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { getOrg, getProjectInfo } from '@/api';
import { basePath, scopeFromPath } from '@/scope';
import { useAuth } from '@/context/AuthContext';
import { useScope } from '@/context/ScopeContext';
function Kbd({ children }: Readonly<{ children: React.ReactNode }>) {
  return <kbd className="iam-kbd">{children}</kbd>;
}

interface TopbarProps {
  onCmdK: () => void;
}

export default function Topbar({ onCmdK }: Readonly<TopbarProps>) {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const { isSuperAdmin, isOrgAdmin } = useAuth();
  const { orgName, projectName, setOrgName, setProjectName } = useScope();

  // One reading of the URL, shared with the router, the tree and the pages — see scope.ts. This
  // was the seventh place in the console re-deriving "where am I" from a regex of its own, and a
  // breadcrumb that disagrees with the tree is a breadcrumb that lies about where you are.
  const here      = scopeFromPath(pathname);
  const urlOrgId  = here.orgId ?? '';
  const urlProjId = here.level === 'project' ? (here.projectId ?? '') : '';

  const showSys  = isSuperAdmin || pathname.startsWith('/system');
  const showOrg  =
    (isSuperAdmin && urlOrgId !== '') ||
    (!isSuperAdmin && (isOrgAdmin || pathname.startsWith('/org')));
  const showProj =
    (isSuperAdmin && urlProjId !== '') ||
    (!isSuperAdmin && pathname.startsWith('/project'));

  /**
   * The names behind the ids in the URL.
   *
   * Resolved here, once, rather than published by each page: `setOrgName` and `setProjectName`
   * existed on the scope context and **nothing ever called them**, so `orgName` was permanently
   * empty and the id-slice below — written as a transient fallback while a name loads — was the
   * only thing an operator ever saw. Nine organisation pages and six project pages each
   * remembering to announce their own title is the arrangement that produced that; the topbar is
   * the one component that needs the answer, so it is the one that asks.
   *
   * Failures are swallowed on purpose: a breadcrumb is not worth an error state, and the id slice
   * is a truthful thing to show when the name cannot be had.
   */
  useEffect(() => {
    if (!urlOrgId) { setOrgName(''); return; }
    let current = true;
    getOrg(urlOrgId)
      .then((o: { name?: string }) => { if (current) setOrgName(o?.name ?? ''); })
      .catch(() => { if (current) setOrgName(''); });
    return () => { current = false; };
  }, [urlOrgId, setOrgName]);

  useEffect(() => {
    if (!urlProjId) { setProjectName(''); return; }
    let current = true;
    getProjectInfo(urlProjId)
      .then((p: { name?: string }) => { if (current) setProjectName(p?.name ?? ''); })
      .catch(() => { if (current) setProjectName(''); });
    return () => { current = false; };
  }, [urlProjId, setProjectName]);

  const resolvedOrgName  = orgName  || (urlOrgId  ? urlOrgId.slice(0, 12)  : 'Organisation');
  const resolvedProjName = projectName || (urlProjId ? urlProjId.slice(0, 12) : 'Project');

  return (
    <div className="iam-topbar">
      <div className="iam-scope-breadcrumb">
        {showSys && (
          <button className="iam-scope-chip" onClick={() => navigate('/system')}>
            <span className="iam-scope-kind iam-scope-kind-sys">SYS</span>
            <span>RediensIAM</span>
          </button>
        )}

        {showOrg && (
          <>
            <span className="iam-scope-sep">›</span>
            <button className="iam-scope-chip"
              onClick={() => navigate(basePath({ level: 'org', orgId: isSuperAdmin ? urlOrgId : undefined }))}>
              <span className="iam-scope-kind iam-scope-kind-org">ORG</span>
              <span>{resolvedOrgName}</span>
            </button>
          </>
        )}

        {showProj && (
          <>
            <span className="iam-scope-sep">›</span>
            <button className="iam-scope-chip"
              onClick={() => navigate(basePath(isSuperAdmin
                ? { level: 'project', orgId: urlOrgId, projectId: urlProjId }
                : { level: 'project' }))}>
              <span className="iam-scope-kind iam-scope-kind-proj">PRJ</span>
              <span>{resolvedProjName}</span>
            </button>
          </>
        )}
      </div>

      <div className="iam-topbar-spacer" />

      <button className="iam-cmd-search" onClick={onCmdK}>
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
          <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
        </svg>
        <span>Search anywhere…</span>
        <span className="iam-cmd-k">
          <Kbd>⌘</Kbd><Kbd>K</Kbd>
        </span>
      </button>

    </div>
  );
}
