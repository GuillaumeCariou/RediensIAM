import { useLocation, useNavigate } from 'react-router';
import { useAuth } from '@/context/AuthContext';
import { useScope } from '@/context/ScopeContext';
import { useTheme } from '@/context/ThemeContext';
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
  const { orgName, projectName } = useScope();
  const { dark, toggleDark } = useTheme();

  const sysProjMatch = /^\/system\/organisations\/([^/]+)\/projects\/([^/]+)/.exec(pathname);
  const sysOrgMatch  = /^\/system\/organisations\/([^/]+)/.exec(pathname);
  const urlOrgId     = sysOrgMatch?.[1] ?? '';
  const urlProjId    = sysProjMatch?.[2] ?? '';

  const showSys  = isSuperAdmin || pathname.startsWith('/system');
  const showOrg  =
    (isSuperAdmin && urlOrgId !== '') ||
    (!isSuperAdmin && (isOrgAdmin || pathname.startsWith('/org')));
  const showProj =
    (isSuperAdmin && urlProjId !== '') ||
    (!isSuperAdmin && pathname.startsWith('/project'));

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
              onClick={() => navigate(isSuperAdmin && urlOrgId ? `/system/organisations/${urlOrgId}` : '/org')}>
              <span className="iam-scope-kind iam-scope-kind-org">ORG</span>
              <span>{resolvedOrgName}</span>
            </button>
          </>
        )}

        {showProj && (
          <>
            <span className="iam-scope-sep">›</span>
            <button className="iam-scope-chip"
              onClick={() => navigate(isSuperAdmin && urlOrgId && urlProjId
                ? `/system/organisations/${urlOrgId}/projects/${urlProjId}`
                : '/project')}>
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

      <button
        className="iam-btn iam-btn-ghost iam-btn-icon"
        onClick={toggleDark}
        aria-pressed={dark}
        title={dark ? 'Switch to light theme' : 'Switch to dark theme'}
      >
        {dark ? (
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <circle cx="12" cy="12" r="5"/>
            <path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/>
          </svg>
        ) : (
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/>
          </svg>
        )}
      </button>
    </div>
  );
}
