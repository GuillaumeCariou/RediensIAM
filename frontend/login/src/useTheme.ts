import { useEffect, useState } from 'react';

export type Theme = 'light' | 'dark' | 'system';

function apply(t: Theme) {
  if (t === 'system') delete document.documentElement.dataset['theme'];
  else document.documentElement.dataset['theme'] = t;
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(
    () => (localStorage.getItem('theme') as Theme) ?? 'system'
  );

  useEffect(() => { apply(theme); }, [theme]);

  const changeTheme = (t: Theme) => {
    localStorage.setItem('theme', t);
    setTheme(t);
    apply(t);
  };

  return { theme, setTheme: changeTheme };
}
