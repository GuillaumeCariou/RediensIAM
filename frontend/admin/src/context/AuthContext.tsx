import { createContext, useContext } from 'react';


export interface AuthState {
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

export const AuthContext = createContext<AuthState>({
  ready: false, authenticated: false, roles: [],
  isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false,
  orgId: '', projectId: '', logout: () => {},
});

interface ParsedToken {
  roles: string[];
  orgId: string;
  projectId: string;
}

export function parseToken(token: string | null): ParsedToken {
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

export function useAuth() { return useContext(AuthContext); }
