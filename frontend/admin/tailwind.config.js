/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  // `media` followed the operating system while the toggle sets data-theme, so a `dark:` utility
  // and the palette could disagree on the same screen.
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Geist Variable', 'Geist', 'ui-sans-serif', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['Geist Mono Variable', 'Geist Mono', 'ui-monospace', 'SF Mono', 'Menlo', 'monospace'],
      },
      // One palette. These used to be a second set of HSL tokens defined only for the light
      // theme, so every `text-muted-foreground` / `bg-card` / `border-border` stayed light while
      // the iam-* surfaces around it went dark. They now point at the same variables the rest of
      // the design system uses, which are defined for both themes in index.css.
      colors: {
        border: 'var(--border)',
        input: 'var(--border-strong)',
        ring: 'var(--ia-accent)',
        background: 'var(--bg)',
        foreground: 'var(--fg)',
        primary: {
          DEFAULT: 'var(--ia-accent)',
          foreground: 'var(--accent-fg)',
        },
        secondary: {
          DEFAULT: 'var(--surface-2)',
          foreground: 'var(--fg)',
        },
        destructive: {
          DEFAULT: 'var(--danger)',
          foreground: 'var(--danger-fg)',
        },
        muted: {
          DEFAULT: 'var(--surface-2)',
          foreground: 'var(--fg-muted)',
        },
        accent: {
          DEFAULT: 'var(--accent-soft)',
          foreground: 'var(--ia-accent)',
        },
        popover: {
          DEFAULT: 'var(--surface)',
          foreground: 'var(--fg)',
        },
        card: {
          DEFAULT: 'var(--surface)',
          foreground: 'var(--fg)',
        },
        sidebar: {
          DEFAULT: 'var(--iam-sidebar)',
          foreground: 'var(--iam-sidebar-fg)',
          border: 'var(--iam-sidebar-border)',
          accent: 'var(--iam-sidebar-accent)',
          'accent-foreground': 'var(--iam-sidebar-fg)',
        },
      },
      borderRadius: {
        lg: 'var(--iam-radius-lg)',
        md: 'var(--iam-radius)',
        sm: 'var(--iam-radius-sm)',
      },
    },
  },
  plugins: [],
};
