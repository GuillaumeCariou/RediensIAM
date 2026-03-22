import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
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
    const payload = JSON.parse(atob(token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/')));
    const raw = payload.roles ?? payload.ext?.roles ?? [];
    const roles: string[] = typeof raw === 'string' ? raw.split(',').filter(Boolean) : Array.isArray(raw) ? raw : [];
    const orgId: string = payload.org_id ?? payload.ext?.org_id ?? '';
    const projectId: string = payload.project_id ?? payload.ext?.project_id ?? '';
    return { roles, orgId, projectId };
  } catch { return { roles: [], orgId: '', projectId: '' }; }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);
  const [roles, setRoles] = useState<string[]>([]);
  const [orgId, setOrgId] = useState('');
  const [projectId, setProjectId] = useState('');

  useEffect(() => {
    async function init() {
      const url = new URL(window.location.href);
      const code = url.searchParams.get('code');
      const state = url.searchParams.get('state');

      if (code && state) {
        const ok = await handleCallback(code, state);
        if (ok) {
          url.searchParams.delete('code');
          url.searchParams.delete('state');
          window.history.replaceState({}, '', url.toString());
        } else {
          await startLogin();
          return; // redirect in progress
        }
      } else {
        // Attempt to restore token from stored OIDC session (survives page reload)
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

  const handleLogout = () => {
    logout().then(() => startLogin());
  };

  return (
    <AuthContext.Provider value={{
      ready, authenticated, roles,
      isSuperAdmin: roles.includes('super_admin'),
      isOrgAdmin: roles.includes('org_admin') || roles.includes('super_admin'),
      isProjectManager: roles.some(r => r === 'project_manager' || r.startsWith('project_manager:')) || roles.includes('org_admin') || roles.includes('super_admin'),
      orgId,
      projectId,
      logout: handleLogout,
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() { return useContext(AuthContext); }
