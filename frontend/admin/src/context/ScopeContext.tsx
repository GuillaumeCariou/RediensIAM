import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';

interface ScopeCtx {
  orgName: string;
  setOrgName: (n: string) => void;
  projectName: string;
  setProjectName: (n: string) => void;
}

const Ctx = createContext<ScopeCtx>({
  orgName: '', setOrgName: () => {},
  projectName: '', setProjectName: () => {},
});

export function ScopeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [orgName, setOrgName] = useState('');
  const [projectName, setProjectName] = useState('');
  const ctx = useMemo(() => ({ orgName, setOrgName, projectName, setProjectName }), [orgName, projectName]);
  return <Ctx.Provider value={ctx}>{children}</Ctx.Provider>;
}

export const useScope = () => useContext(Ctx);
