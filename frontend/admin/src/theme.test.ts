/// <reference types="node" />
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * The console shipped with two palettes: the `iam-*` custom properties, which had a light and a
 * dark value, and a second set of HSL tokens behind Tailwind's colour utilities, which had only a
 * light value. So `text-muted-foreground` and `bg-card` stayed light on a dark screen — "I'm in
 * dark theme but there is white theme stuff". Tailwind now reads the same variables as everything
 * else, and these tests are what keeps it that way: a variable that exists in one theme and not
 * the other is the bug, and a class name with no rule behind it is the silent version of it.
 */

const SRC = dirname(fileURLToPath(import.meta.url));
const CSS = readFileSync(join(SRC, 'index.css'), 'utf8');
const TAILWIND = readFileSync(join(SRC, '..', 'tailwind.config.js'), 'utf8');

/**
 * Every declaration under a selector. There is more than one `:root` block — one for the tokens
 * that do not vary by theme (fonts, radii, shadows) and one for the light palette — so a helper
 * that stopped at the first would report the whole light palette as missing.
 */
function declared(selector: string): Set<string> {
  const out = new Set<string>();
  const needle = `\n${selector} {`;
  for (let at = CSS.indexOf(needle); at >= 0; at = CSS.indexOf(needle, at + 1)) {
    const open = CSS.indexOf('{', at);
    const body = CSS.slice(open + 1, CSS.indexOf('\n}', open));
    for (const m of body.matchAll(/^\s*(--[\w-]+)\s*:/gm)) out.add(m[1]);
  }
  if (out.size === 0) throw new Error(`no declarations under ${selector} in index.css`);
  return out;
}

const LIGHT = declared(':root');
const DARK = declared(':root[data-theme="dark"]');

/** Tokens that are intentionally theme-independent (radii, fonts, shadows, row height). */
function isShared(v: string): boolean {
  return /^--(font|iam-radius|row-h|shadow)/.test(v);
}

function tsxFiles(dir: string): string[] {
  return readdirSync(dir).flatMap(entry => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) return tsxFiles(path);
    return /\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) ? [path] : [];
  });
}

const SOURCES = tsxFiles(SRC).map(f => readFileSync(f, 'utf8')).join('\n');

describe('the two themes define the same variables', () => {
  it('every colour Tailwind exposes is defined in both themes', () => {
    const used = [...TAILWIND.matchAll(/var\((--[\w-]+)\)/g)].map(m => m[1]);
    expect(used.length).toBeGreaterThan(10);

    // A Tailwind utility resolving to a variable the dark theme never sets is exactly the
    // reported bug: the surface goes dark, the text on it does not.
    const missingInDark = [...new Set(used)].filter(v => !DARK.has(v) && !isShared(v));
    expect(missingInDark, `not defined for the dark theme: ${missingInDark.join(', ')}`).toEqual([]);

    const missingInLight = [...new Set(used)].filter(v => !LIGHT.has(v));
    expect(missingInLight, `not defined for the light theme: ${missingInLight.join(', ')}`).toEqual([]);
  });

  it('every variable the dark theme overrides exists in the light theme too', () => {
    const orphans = [...DARK].filter(v => !LIGHT.has(v));
    expect(orphans, `only ever set in dark: ${orphans.join(', ')}`).toEqual([]);
  });

  it('every var() used in a component is defined', () => {
    const used = new Set([...SOURCES.matchAll(/var\((--[\w-]+)/g)].map(m => m[1]));
    const undefinedVars = [...used].filter(v => !LIGHT.has(v) && !DARK.has(v));
    expect(undefinedVars, `used in .tsx but never declared: ${undefinedVars.join(', ')}`).toEqual([]);
  });
});

describe('the browser is told which theme it is drawing', () => {
  it('declares color-scheme in both themes', () => {
    // Without this the UA draws every widget it owns in light chrome regardless of the palette:
    // <select> popups, date pickers, bare checkboxes, autofill highlights and scrollbars. It is
    // the single largest source of "white theme stuff" on a dark screen, and no amount of custom
    // properties fixes it — the browser is not reading them for those surfaces.
    expect(LIGHT.has('color-scheme') || /:root\s*\{[^}]*color-scheme:\s*light/.test(CSS)).toBe(true);
    expect(/:root\[data-theme="dark"\]\s*\{[^}]*color-scheme:\s*dark/.test(CSS)).toBe(true);
  });
});

describe('the iam-* class names components use exist', () => {
  it('has a rule for every iam-* class referenced in a component', () => {
    const defined = new Set([...CSS.matchAll(/\.(iam-[\w-]+)/g)].map(m => m[1]));
    const used = new Set(
      [...SOURCES.matchAll(/className=(?:"([^"]*)"|\{`([^`]*)`\})/g)]
        // `${…}` is a value this test cannot resolve; drop the interpolation, keep the literal
        // fragments around it so a misspelled static class is still caught.
        .flatMap(m => (m[1] ?? m[2]).replaceAll(/\$\{[^}]*\}/g, ' ').split(/\s+/))
        .map(c => c.replaceAll(/[^\w-]/g, ''))
        .filter(c => c.startsWith('iam-')),
    );
    const unstyled = [...used].filter(c => !defined.has(c));
    expect(unstyled, `no rule in index.css: ${unstyled.join(', ')}`).toEqual([]);
  });

  it('nothing uses a design-system class with its iam- prefix dropped', () => {
    // `className="mono"` in two places, where the rule is `.iam-mono`. The check above only looks
    // at names that already start with iam-, so a dropped prefix reads as an ordinary utility and
    // silently does nothing — the role label under the sidebar lost its monospace and nobody saw.
    const defined = [...new Set([...CSS.matchAll(/\.iam-([\w-]+)/g)].map(m => m[1]))];
    const offenders = defined.filter(bare => new RegExp(`className="(?:[^"]*\\s)?${bare}(?:\\s[^"]*)?"`).test(SOURCES));
    expect(offenders, `used without the iam- prefix: ${offenders.join(', ')}`).toEqual([]);
  });
});

describe('nothing pins a colour that cannot follow the theme', () => {
  it('has no hardcoded white or black in component chrome', () => {
    // The tenant login-theme editor renders colour literals as DATA — those live in
    // pages/project/Authentication.tsx and are the tenant's own palette, not this app's.
    const chrome = tsxFiles(SRC)
      .filter(f => !f.endsWith('pages/project/Authentication.tsx'))
      .map(f => [f, readFileSync(f, 'utf8')] as const);

    const offenders = chrome.flatMap(([file, text]) =>
      [...text.matchAll(/(?:background|color|borderColor|fill|stroke)\s*:\s*'(#[0-9a-fA-F]{3,8}|white|black)'/g)]
        .map(m => `${file.slice(SRC.length + 1)}: ${m[0]}`),
    );
    expect(offenders, offenders.join(' | ')).toEqual([]);
  });
});

/* ─────────────────────────────────────────────────────────────────────────────
   Contrast
   ───────────────────────────────────────────────────────────────────────────── */

/** oklch(L C H) → linear-sRGB. Björn Ottosson's matrices; no gamma step, luminance wants linear. */
function oklchToLinearRgb(l: number, c: number, hDeg: number): [number, number, number] {
  const h = (hDeg * Math.PI) / 180;
  const a = c * Math.cos(h);
  const b = c * Math.sin(h);
  const L = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const M = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const S = (l - 0.0894841775 * a - 1.2914855480 * b) ** 3;
  return [
    4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
    -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
    -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S,
  ];
}

function luminance(oklch: string): number {
  const m = /oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)/.exec(oklch);
  if (!m) throw new Error(`not an oklch() literal: ${oklch}`);
  const [r, g, b] = oklchToLinearRgb(+m[1], +m[2], +m[3]).map(v => Math.min(1, Math.max(0, v)));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(fg: string, bg: string): number {
  const [a, b] = [luminance(fg), luminance(bg)].sort((x, y) => y - x);
  return (a + 0.05) / (b + 0.05);
}

/** The value of one custom property inside one theme block. */
function value(selector: string, name: string): string {
  const needle = `\n${selector} {`;
  for (let at = CSS.indexOf(needle); at >= 0; at = CSS.indexOf(needle, at + 1)) {
    const open = CSS.indexOf('{', at);
    const body = CSS.slice(open + 1, CSS.indexOf('\n}', open));
    const m = new RegExp(`^\\s*${name}\\s*:\\s*([^;]+);`, 'm').exec(body);
    if (m) return m[1].trim();
  }
  throw new Error(`${name} is not declared under ${selector}`);
}

/**
 * Every pair of tokens that ends up as text on a surface. "Or too dark, or nothing" is what an
 * unchecked palette reads like: each of these was legible in the theme it was picked in and
 * assumed to be legible in the other.
 *
 * 4.5:1 is WCAG AA for body text. The console has no text large enough to claim the 3:1 exception
 * — the largest is a 20px heading, and AA calls that large only at 18.66px **bold**.
 */
const TEXT_ON_SURFACE: readonly (readonly [string, string])[] = [
  ['--fg', '--bg'], ['--fg', '--bg-sunken'], ['--fg', '--surface'], ['--fg', '--surface-2'],
  ['--fg-muted', '--bg'], ['--fg-muted', '--surface'], ['--fg-muted', '--surface-2'],
  ['--fg-subtle', '--bg'], ['--fg-subtle', '--surface'], ['--fg-subtle', '--surface-2'],
  ['--iam-sidebar-fg', '--iam-sidebar'], ['--iam-sidebar-fg', '--iam-sidebar-accent'],
  ['--iam-sidebar-muted', '--iam-sidebar'], ['--iam-sidebar-muted', '--iam-sidebar-accent'],
  ['--accent-fg', '--ia-accent'],
  ['--danger-fg', '--danger'],
  // Chips: 11px text, the smallest in the console, on their own tinted surface.
  ['--success', '--success-soft'], ['--warn', '--warn-soft'],
  ['--danger', '--danger-soft'], ['--ia-accent', '--accent-soft'],
  ['--info', '--info-soft'],
  // Links, trend arrows and the scope-kind badges print an accent or a status colour straight
  // onto the page — the badges over a 15% tint of themselves, which barely moves the backdrop.
  ['--ia-accent', '--bg'], ['--ia-accent', '--surface'], ['--ia-accent', '--iam-sidebar'],
  ['--danger', '--bg'], ['--danger', '--surface'],
  ['--success', '--bg'], ['--success', '--surface'],
  // The relation tuple prints a namespace and a relation on the sunken surface, and the toast
  // inverts the page outright.
  ['--ia-accent', '--surface-2'], ['--success', '--surface-2'],
  ['--bg', '--fg'],
  // …and the neutral chip, which is --fg-muted on --surface-2, already above.
];

describe('text is legible on the surface it sits on', () => {
  for (const [selector, theme] of [[':root', 'light'], [':root[data-theme="dark"]', 'dark']] as const) {
    it(`meets WCAG AA in the ${theme} theme`, () => {
      const failures = TEXT_ON_SURFACE
        .map(([fg, bg]) => [fg, bg, contrast(value(selector, fg), value(selector, bg))] as const)
        .filter(([, , ratio]) => ratio < 4.5)
        .map(([fg, bg, ratio]) => `${fg} on ${bg}: ${ratio.toFixed(2)}:1`);
      expect(failures, `below 4.5:1 in ${theme}:\n${failures.join('\n')}`).toEqual([]);
    });
  }
});

describe('the sidebar belongs to the theme around it', () => {
  it('is a light surface in light and a dark one in dark', () => {
    // It was a fixed dark navy in both: the light theme drew a dark rail down the side of a light
    // application, which is a palette from a different design, not a light theme.
    const lightSidebar = luminance(value(':root', '--iam-sidebar'));
    const lightPage    = luminance(value(':root', '--bg'));
    const darkSidebar  = luminance(value(':root[data-theme="dark"]', '--iam-sidebar'));
    const darkPage     = luminance(value(':root[data-theme="dark"]', '--bg'));

    expect(contrast(value(':root', '--iam-sidebar'), value(':root', '--bg')),
      'the light sidebar must sit near the light page, not invert it').toBeLessThan(2);
    expect(contrast(value(':root[data-theme="dark"]', '--iam-sidebar'), value(':root[data-theme="dark"]', '--bg')),
      'the dark sidebar must sit near the dark page').toBeLessThan(2);
    expect(lightSidebar).toBeGreaterThan(darkPage);
    expect(darkSidebar).toBeLessThan(lightPage);
  });
});
