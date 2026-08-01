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
