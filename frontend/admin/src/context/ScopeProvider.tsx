import { useMemo, useState, type ReactNode } from 'react';
import { ScopeCtx } from './ScopeContext';

export function ScopeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [orgName, setOrgName] = useState('');
  const [projectName, setProjectName] = useState('');
  const ctx = useMemo(() => ({ orgName, setOrgName, projectName, setProjectName }), [orgName, projectName]);
  return <ScopeCtx.Provider value={ctx}>{children}</ScopeCtx.Provider>;
}
