import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { isAuthenticated, startLogin, handleCallback, logout, getToken } from '../auth';

interface AuthState {
  ready: boolean;
  authenticated: boolean;
  roles: string[];
  isSuperAdmin: boolean;
  isOrgAdmin: boolean;
  isProjectManager: boolean;
  logout: () => void;
}

const AuthContext = createContext<AuthState>({
  ready: false, authenticated: false, roles: [],
  isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false, logout: () => {},
});

function parseRoles(token: string | null): string[] {
  if (!token) return [];
  try {
    const payload = JSON.parse(atob(token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/')));
    const raw = payload.roles ?? payload.ext?.roles ?? [];
    if (typeof raw === 'string') return raw.split(',').filter(Boolean);
    return Array.isArray(raw) ? raw : [];
  } catch { return []; }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);
  const [roles, setRoles] = useState<string[]>([]);

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
          setAuthenticated(true);
          setRoles(parseRoles(getToken()));
        } else {
          await startLogin();
        }
      } else if (!isAuthenticated()) {
        await startLogin();
      } else {
        setAuthenticated(true);
        setRoles(parseRoles(getToken()));
      }
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
      logout: handleLogout,
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() { return useContext(AuthContext); }
