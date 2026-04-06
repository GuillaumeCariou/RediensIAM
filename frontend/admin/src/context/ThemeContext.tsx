import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

export type Theme = 'light' | 'dark' | 'system';

interface ThemeCtx { theme: Theme; setTheme: (t: Theme) => void; }

const Ctx = createContext<ThemeCtx>({ theme: 'system', setTheme: () => {} });

function applyTheme(t: Theme) {
  if (t === 'system') delete document.documentElement.dataset['theme'];
  else document.documentElement.dataset['theme'] = t;
}

export function ThemeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [theme, setTheme] = useState<Theme>(
    () => (localStorage.getItem('theme') as Theme) ?? 'system'
  );

  useEffect(() => { applyTheme(theme); }, [theme]);

  const changeTheme = (t: Theme) => {
    localStorage.setItem('theme', t);
    setTheme(t);
    applyTheme(t);
  };

  const ctx = useMemo(() => ({ theme, setTheme: changeTheme }), [theme]); // eslint-disable-line react-hooks/exhaustive-deps
  return <Ctx.Provider value={ctx}>{children}</Ctx.Provider>;
}

export const useTheme = () => useContext(Ctx);
