import { createContext, useContext } from 'react';

interface Scope {
  orgName: string;
  setOrgName: (n: string) => void;
  projectName: string;
  setProjectName: (n: string) => void;
}

// The provider lives in ScopeProvider.tsx: a file that exports a component may export nothing
// else, or Fast Refresh silently stops working for it (react-refresh/only-export-components).
export const ScopeCtx = createContext<Scope>({
  orgName: '', setOrgName: () => {},
  projectName: '', setProjectName: () => {},
});

export const useScope = () => useContext(ScopeCtx);
