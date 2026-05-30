import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

export const PRESETS = [
  { id: 'stripe',  label: 'Stripe',  dark: false, bg: 'oklch(0.985 0.002 260)', accent: 'oklch(0.445 0.195 270)', sidebar: 'oklch(0.155 0.040 270)' },
  { id: 'jamm',   label: 'Jamm',    dark: false, bg: 'oklch(0.990 0.003 80)',  accent: 'oklch(0.620 0.190 40)',  sidebar: 'oklch(0.175 0.040 40)'  },
  { id: 'aurora', label: 'Aurora',  dark: false, bg: 'oklch(0.985 0.006 165)', accent: 'oklch(0.520 0.180 165)', sidebar: 'oklch(0.155 0.040 165)' },
  { id: 'sand',   label: 'Sand',    dark: false, bg: 'oklch(0.986 0.008 80)',  accent: 'oklch(0.570 0.175 55)',  sidebar: 'oklch(0.175 0.032 60)'  },
  { id: 'forest', label: 'Forest',  dark: false, bg: 'oklch(0.988 0.008 150)', accent: 'oklch(0.460 0.190 152)', sidebar: 'oklch(0.920 0.030 148)' },
  { id: 'ocean',  label: 'Ocean',   dark: false, bg: 'oklch(0.988 0.006 220)', accent: 'oklch(0.490 0.200 225)', sidebar: 'oklch(0.908 0.032 220)' },
  { id: 'desert', label: 'Desert',  dark: false, bg: 'oklch(0.990 0.012 60)',  accent: 'oklch(0.545 0.200 35)',  sidebar: 'oklch(0.905 0.042 55)'  },
  { id: 'tundra', label: 'Tundra',  dark: false, bg: 'oklch(0.985 0.005 240)', accent: 'oklch(0.462 0.155 248)', sidebar: 'oklch(0.918 0.018 242)' },
  { id: 'siberia',label: 'Siberia', dark: false, bg: 'oklch(0.992 0.004 225)', accent: 'oklch(0.448 0.138 232)', sidebar: 'oklch(0.928 0.014 228)' },
  { id: 'void',   label: 'Void',    dark: true,  bg: 'oklch(0.085 0.010 260)', accent: 'oklch(0.760 0.240 240)', sidebar: 'oklch(0.060 0.008 260)' },
] as const;

const DARK_VARIANTS = new Set(['stripe', 'jamm', 'forest', 'ocean', 'desert', 'tundra', 'siberia']);
const DARK_ONLY     = new Set(['void']);
const LIGHT_ONLY    = new Set(['aurora', 'sand']);

export function resolveDataTheme(preset: string, dark: boolean): string {
  if (DARK_ONLY.has(preset)) return preset;
  if (LIGHT_ONLY.has(preset)) return preset;
  if (dark && DARK_VARIANTS.has(preset)) return `${preset}-dark`;
  return preset;
}

export function isDarkPreset(preset: string): boolean {
  return DARK_ONLY.has(preset);
}

export function isLightPreset(preset: string): boolean {
  return LIGHT_ONLY.has(preset);
}

interface ThemeCtx {
  preset: string;
  dark: boolean;
  setPreset: (preset: string) => void;
  toggleDark: () => void;
}

const Ctx = createContext<ThemeCtx>({
  preset: 'stripe', dark: false,
  setPreset: () => {}, toggleDark: () => {},
});

function applyTheme(preset: string, dark: boolean) {
  document.documentElement.dataset['theme'] = resolveDataTheme(preset, dark);
}

export function ThemeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [preset, setPreset] = useState<string>(
    () => localStorage.getItem('iam-theme-preset') ?? 'stripe'
  );
  const [dark, setDark] = useState<boolean>(() => {
    const saved = localStorage.getItem('iam-theme-dark');
    if (saved !== null) return saved === '1';
    if (DARK_ONLY.has(localStorage.getItem('iam-theme-preset') ?? 'stripe')) return true;
    return globalThis.matchMedia('(prefers-color-scheme: dark)').matches;
  });

  useEffect(() => { applyTheme(preset, dark); }, [preset, dark]);

  const changePreset = (p: string) => {
    let d = dark;
    if (DARK_ONLY.has(p)) d = true;
    if (LIGHT_ONLY.has(p)) d = false;
    localStorage.setItem('iam-theme-preset', p);
    localStorage.setItem('iam-theme-dark', d ? '1' : '0');
    setPreset(p);
    setDark(d);
  };

  const toggleDark = () => {
    if (DARK_ONLY.has(preset) || LIGHT_ONLY.has(preset)) return;
    const next = !dark;
    localStorage.setItem('iam-theme-dark', next ? '1' : '0');
    setDark(next);
  };

  const ctx = useMemo(() => ({ preset, dark, setPreset: changePreset, toggleDark }), [preset, dark]); // eslint-disable-line react-hooks/exhaustive-deps
  return <Ctx.Provider value={ctx}>{children}</Ctx.Provider>;
}

export const useTheme = () => useContext(Ctx);
