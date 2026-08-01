import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { isAuthenticated, startLogin, handleCallback, logout, getToken, restoreSession } from '../auth';

interface AuthState {
  ready: boolean;
  authenticated: boolean;
  roles: string[];
  isSuperAdmin: boolean;
  isOrgAdmin: boolean;
  isProjectManager: boolean;
  orgId: string;
  projectId: string;
  logout: () => void;
}

/** Must match src/Config/Roles.cs exactly — these strings come from the token's ext.roles. */
export const ROLE_SUPER_ADMIN   = 'super_admin';
export const ROLE_ORG_ADMIN     = 'org_admin';
export const ROLE_PROJECT_ADMIN = 'project_admin';

const AuthContext = createContext<AuthState>({
  ready: false, authenticated: false, roles: [],
  isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false,
  orgId: '', projectId: '', logout: () => {},
});

interface ParsedToken {
  roles: string[];
  orgId: string;
  projectId: string;
}

function parseToken(token: string | null): ParsedToken {
  if (!token) return { roles: [], orgId: '', projectId: '' };
  try {
    const payload = JSON.parse(atob(token.split('.')[1].replaceAll('-', '+').replaceAll('_', '/')));
    const raw = payload.roles ?? payload.ext?.roles ?? [];
    let roles: string[];
    if (typeof raw === 'string') roles = raw.split(',').filter(Boolean);
    else if (Array.isArray(raw)) roles = raw;
    else roles = [];
    const orgId: string = payload.org_id ?? payload.ext?.org_id ?? '';
    const projectId: string = payload.project_id ?? payload.ext?.project_id ?? '';
    return { roles, orgId, projectId };
  } catch { return { roles: [], orgId: '', projectId: '' }; }
}

export function AuthProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [ready, setReady] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);
  const [roles, setRoles] = useState<string[]>([]);
  const [orgId, setOrgId] = useState('');
  const [projectId, setProjectId] = useState('');

  const initStarted = useRef(false);
  useEffect(() => {
    // StrictMode runs effects twice in dev — guard so handleCallback isn't called with
    // a code/state pair that's already been consumed (which would loop into startLogin).
    if (initStarted.current) return;
    initStarted.current = true;
    async function init() {
      const url = new URL(globalThis.location.href);
      const code = url.searchParams.get('code');
      const state = url.searchParams.get('state');

      if (code && state) {
        const ok = await handleCallback(code, state);
        if (ok) {
          url.searchParams.delete('code');
          url.searchParams.delete('state');
          globalThis.history.replaceState({}, '', url.toString());
        } else {
          await startLogin();
          return; // redirect in progress
        }
      } else {
        await restoreSession();
        if (!isAuthenticated()) {
          await startLogin();
          return; // redirect in progress
        }
      }

      const parsed = parseToken(getToken());
      setAuthenticated(true);
      setRoles(parsed.roles);
      setOrgId(parsed.orgId);
      setProjectId(parsed.projectId);
      setReady(true);
    }
    init();
  }, []);

  /**
   * Logs out and nothing else. Do not chain startLogin here: signoutRedirect navigates to
   * Hydra's logout endpoint, which then redirects back via post_logout_redirect_uri. Calling
   * startLogin races that navigation and can leave the Hydra session cookie alive, which is a
   * silent re-login.
   */
  const handleLogout = () => {
    logout();
  };

  const ctx = useMemo<AuthState>(() => ({
    ready, authenticated, roles,
    isSuperAdmin: roles.includes(ROLE_SUPER_ADMIN),
    isOrgAdmin: roles.includes(ROLE_ORG_ADMIN) || roles.includes(ROLE_SUPER_ADMIN),
    /**
     * The backend emits `project_admin` (src/Config/Roles.cs). This used to test for
     * `project_manager`, a name the API never emits, so a user whose only role was
     * project_admin fell through to the "No access" screen and could never reach the console.
     */
    isProjectManager: roles.some(r => r === ROLE_PROJECT_ADMIN || r.startsWith(`${ROLE_PROJECT_ADMIN}:`))
      || roles.includes(ROLE_ORG_ADMIN) || roles.includes(ROLE_SUPER_ADMIN),
    orgId,
    projectId,
    logout: handleLogout,
  }), [ready, authenticated, roles, orgId, projectId]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <AuthContext.Provider value={ctx}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() { return useContext(AuthContext); }
