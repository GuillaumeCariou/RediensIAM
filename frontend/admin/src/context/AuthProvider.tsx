import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { isAuthenticated, startLogin, handleCallback, logout, getToken, restoreSession } from '../auth';
import { RETURN_TO_KEY } from './returnTo';
import { AuthContext, ROLE_SUPER_ADMIN, ROLE_ORG_ADMIN, ROLE_PROJECT_ADMIN, parseToken, type AuthState } from './AuthContext';

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
        const ok = await handleCallback();
        if (ok) {
          url.searchParams.delete('code');
          url.searchParams.delete('state');
          // The URL only. Where to go next is the router's business — see consumeReturnTo below:
          // the callback path matches no route, so the catch-all navigates, and a history entry
          // written here would be overwritten a tick later.
          globalThis.history.replaceState({}, '', url.toString());
        } else {
          await startLogin();
          return; // redirect in progress
        }
      } else {
        await restoreSession();
        if (!isAuthenticated()) {
          // Remembered before the redirect, because after it this page no longer exists. Path only
          // and same-origin by construction: it is read back into history.replaceState, and a value
          // that could name another origin would be an open redirect written by whoever can reach
          // this tab's storage.
          sessionStorage.setItem(RETURN_TO_KEY, globalThis.location.pathname + globalThis.location.search);
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
  }), [ready, authenticated, roles, orgId, projectId]);

  return (
    <AuthContext.Provider value={ctx}>
      {children}
    </AuthContext.Provider>
  );
}
