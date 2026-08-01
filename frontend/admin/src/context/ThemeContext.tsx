import { createContext, useContext } from 'react';

export const STORAGE_KEY = 'iam-theme-dark';

interface Theme {
  dark: boolean;
  toggleDark: () => void;
}

// The provider lives in ThemeProvider.tsx — see the note in ScopeContext.tsx.
export const ThemeCtx = createContext<Theme>({ dark: false, toggleDark: () => {} });

export const useTheme = () => useContext(ThemeCtx);
