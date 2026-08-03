import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { STORAGE_KEY, ThemeCtx } from './ThemeContext';

export function ThemeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [dark, setDark] = useState<boolean>(() => {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved !== null) return saved === '1';
    return globalThis.matchMedia('(prefers-color-scheme: dark)').matches;
  });

  // index.css keys the dark palette off data-theme="dark"; its absence is the light palette.
  useEffect(() => {
    if (dark) document.documentElement.dataset['theme'] = 'dark';
    else delete document.documentElement.dataset['theme'];
  }, [dark]);

  const ctx = useMemo(() => ({
    dark,
    toggleDark: () => setDark(d => {
      localStorage.setItem(STORAGE_KEY, d ? '0' : '1');
      return !d;
    }),
  }), [dark]);

  return <ThemeCtx.Provider value={ctx}>{children}</ThemeCtx.Provider>;
}
