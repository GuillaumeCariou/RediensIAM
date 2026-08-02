/// <reference types="node" />
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * Markup contracts that type-checking and the render tests both miss.
 *
 * Every defect below shipped as well-typed, lint-clean code that simply did not do what it looked
 * like it did: a submit button rendered outside its form does nothing at all, a Cancel button with
 * no handler defaults to `type="submit"` and is inert, and a controlled `<select>` whose value is
 * absent from its options displays a value the component does not hold. React reports none of it,
 * and a unit test only catches the dialog it was written for — so these are checked across the
 * whole tree at once.
 */

const SRC = dirname(fileURLToPath(import.meta.url));
const ROOT = join(SRC, '..');

function tsxFiles(dir: string): string[] {
  return readdirSync(dir).flatMap(entry => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) return tsxFiles(path);
    return /\.tsx$/.test(entry) && !/\.test\.tsx$/.test(entry) ? [path] : [];
  });
}

const FILES = tsxFiles(SRC).map(f => [f.slice(SRC.length + 1), readFileSync(f, 'utf8')] as const);

/** The `<IamDialog …>` … `</IamDialog>` blocks in a file, opening tag included. */
function dialogs(text: string): string[] {
  const out: string[] = [];
  for (let at = text.indexOf('<IamDialog'); at >= 0; at = text.indexOf('<IamDialog', at + 1)) {
    const end = text.indexOf('</IamDialog>', at);
    out.push(text.slice(at, end < 0 ? text.length : end));
  }
  return out;
}

describe('dialog submit buttons have a form to submit', () => {
  it('every type="submit" in a dialog footer names its form', () => {
    // IamDialog renders `footer` as a sibling of the body, so a submit button there has no form
    // owner unless it carries `form="…"`. Without it the button is inert: no request, no error.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      for (const dialog of dialogs(text)) {
        const footerAt = dialog.indexOf('footer=');
        if (footerAt < 0) continue;
        const footer = dialog.slice(footerAt);
        for (const m of footer.matchAll(/<button[^>]*type="submit"[^>]*>/g)) {
          if (!/\bform="/.test(m[0])) offenders.push(`${file}: ${m[0].replace(/\s+/g, ' ').slice(0, 90)}`);
        }
      }
    }
    expect(offenders, `submit buttons with no form owner:\n${offenders.join('\n')}`).toEqual([]);
  });

  it('every form= reference points at a form id that exists in the same file', () => {
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      const ids = new Set([...text.matchAll(/<form[^>]*\bid="([^"]+)"/g)].map(m => m[1]));
      for (const m of text.matchAll(/\bform="([^"]+)"/g)) {
        if (!ids.has(m[1])) offenders.push(`${file}: form="${m[1]}" has no matching <form id>`);
      }
    }
    expect(offenders, offenders.join('\n')).toEqual([]);
  });
});

describe('dialog buttons that are not submits say so', () => {
  it('every non-submit button inside a dialog has an onClick or an explicit type', () => {
    // A <button> with no type is type="submit". In a footer with no form owner that is a button
    // that does nothing — which is how ten Cancel buttons ended up as decoration next to a live
    // Delete.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      for (const dialog of dialogs(text)) {
        for (const m of dialog.matchAll(/<button(?![^>]*\btype=)[^>]*>/g)) {
          if (!/\bonClick=/.test(m[0])) offenders.push(`${file}: ${m[0].replace(/\s+/g, ' ').slice(0, 90)}`);
        }
      }
    }
    expect(offenders, `buttons that do nothing:\n${offenders.join('\n')}`).toEqual([]);
  });
});

describe('controlled selects can represent their own state', () => {
  it('every select bound to state that starts empty offers an empty option', () => {
    // Radix's Select supplied a placeholder implicitly; the native element does not. A controlled
    // select holding '' with no matching <option> paints the first option as chosen while state
    // stays empty — so the form submits nothing, or worse, grants a role at a scope the operator
    // never picked.
    //
    // Scoped to a plain `value={x}` or `value={obj.field}` whose initial value in the same file is
    // the empty string. A value computed by an expression (a ternary, an `includes()` guard) is
    // pinned into its option set by construction and is not this bug.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      const emptyState = new Set([
        ...[...text.matchAll(/(?:const|let)\s+\[(\w+),/g)]
          .filter(m => new RegExp(`\\[${m[1]},[^\\]]*\\]\\s*=\\s*useState(?:<[^>]*>)?\\(''\\)`).test(text))
          .map(m => m[1]),
        ...[...text.matchAll(/(\w+)\s*:\s*''/g)].map(m => m[1]),
      ]);

      for (let at = text.indexOf('<select'); at >= 0; at = text.indexOf('<select', at + 1)) {
        const end = text.indexOf('</select>', at);
        const block = text.slice(at, end < 0 ? text.length : end);
        const open = block.slice(0, block.indexOf('>') + 1);
        const bound = /\bvalue=\{\s*(\w+(?:\.(\w+))?)\s*\}/.exec(open);
        if (!bound) continue;
        const name = bound[2] ?? bound[1];
        if (!emptyState.has(name)) continue;
        if (/<option[^>]*value=""/.test(block)) continue;
        offenders.push(`${file}: value={${bound[1]}} can be '' with no matching <option>`);
      }
    }
    expect(offenders, `selects that can display a value they do not hold:\n${offenders.join('\n')}`).toEqual([]);
  });
});

describe('the image can build what the console imports', () => {
  it('the admin build stage contains every path the Vite alias resolves to', () => {
    // deploy.sh builds the SPAs on the host before `docker build`, so a stage that cannot resolve
    // an alias still leaves a dist behind on the host and the deploy reports success — while
    // pushing a stale image. This is the check that would have caught that.
    const dockerfile = readFileSync(join(ROOT, '..', '..', 'Dockerfile'), 'utf8');
    const vite = readFileSync(join(ROOT, 'vite.config.ts'), 'utf8');

    const aliased = [...vite.matchAll(/'\.\.\/\.\.\/([\w./-]+)'/g)].map(m => m[1]);
    expect(aliased.length, 'expected at least one repo-relative alias in vite.config.ts')
      .toBeGreaterThan(0);

    const stage = dockerfile.slice(dockerfile.indexOf('AS admin-build'), dockerfile.indexOf('AS backend-build'));
    const missing = aliased.filter(path => !stage.includes(path.split('/').slice(0, 3).join('/')));
    expect(missing, `not copied into the admin build stage: ${missing.join(', ')}`).toEqual([]);
  });
});

describe('nothing on screen is invented', () => {
  it('the activity chart is fed by its caller, not by a generator', () => {
    // ActivityChart drew bars from Math.sin(i / 4) and (i * 17 + 3) % 20 under a heading reading
    // "Login activity · last 24h", with a Success/Failed legend and a real recent_logins count
    // beside it. Identical on every deployment and every reload. Metrics.logins_by_hour was
    // declared in the interface and never read.
    const chart = FILES.find(([file]) => file.endsWith('components/iam/ActivityChart.tsx'));
    expect(chart, 'ActivityChart.tsx not found').toBeDefined();
    const [, text] = chart!;

    expect(text).not.toMatch(/Math\.(sin|cos|random)/);
    expect(text, 'the component must take its data as a prop').toMatch(/data\s*[?:]/);
  });
});

describe('controls announce what they are', () => {
  it('every toggle built from a bare button carries switch semantics', () => {
    // ~15 settings toggles were <button> with no type, no role, no aria-pressed and no accessible
    // name: a screen reader heard "button" fifteen times, and "Require MFA" was indistinguishable
    // from "Reject breached passwords".
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      const at = text.indexOf('function Toggle');
      if (at < 0) continue;
      // To the first closing brace in column 0. Parsing the parameter list is not worth it — the
      // signature contains `)` inside an arrow type, which a naive `[^)]*` splits in the wrong
      // place and then runs the "body" on into unrelated markup further down the file.
      const end = text.indexOf('\n}', at);
      const body = text
        .slice(at, end < 0 ? text.length : end)
        .replaceAll(/\/\/[^\n]*/g, '')          // a comment mentioning <button> is not a <button>
        .replaceAll(/\/\*[\s\S]*?\*\//g, '');
      if (!/<button/.test(body)) continue;      // already an <input type="checkbox">
      if (/role="switch"/.test(body) && /aria-checked=/.test(body)) continue;
      offenders.push(`${file}: Toggle renders a <button> with no switch semantics`);
    }
    expect(offenders, offenders.join('\n')).toEqual([]);
  });

  it('every row-action menu closes on Escape', () => {
    // Four legacy dropdowns closed through a <div role="none" onClick onKeyDown> backdrop. A
    // non-focusable div never receives keydown, so Escape did nothing and the handler was dead
    // code. IamMenu listens on globalThis and gets this right.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      if (file.endsWith('components/iam/IamMenu.tsx')) continue;
      if (/role="none"[^>]*onKeyDown=/.test(text.replace(/\s+/g, ' '))) {
        offenders.push(`${file}: hand-rolled menu backdrop — use IamMenu`);
      }
    }
    expect(offenders, offenders.join('\n')).toEqual([]);
  });
});

describe('the branding preview can actually render', () => {
  it('the preview iframe is allowed to run scripts', () => {
    // sandbox="allow-same-origin" with no allow-scripts blocks all JavaScript, so the React
    // preview could never paint — the panel was blank regardless of the CSP work done to let the
    // console frame it at all.
    const auth = FILES.find(([file]) => file.endsWith('pages/project/Authentication.tsx'));
    expect(auth, 'Authentication.tsx not found').toBeDefined();
    const [, text] = auth!;

    const iframe = /<iframe[^>]*>/.exec(text.replace(/\s+/g, ' '));
    expect(iframe, 'no iframe found').not.toBeNull();
    if (/sandbox=/.test(iframe![0])) {
      expect(iframe![0], 'a sandbox without allow-scripts renders nothing').toMatch(/allow-scripts/);
    }
  });
});

describe('numeric inputs cannot submit a value the user cleared', () => {
  it('never converts an empty number field straight to 0', () => {
    // Clearing a type="number" yields '', and Number('') is 0. The cleanup dialog sent
    // inactive_threshold_days: 0 — which matches every user in the list — from a field the user
    // had simply emptied.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      for (const m of text.matchAll(/onChange=\{[^}]*Number\(e\.target\.value\)[^}]*\}/g)) {
        // An explicit empty-string branch is the same guard spelled differently.
        if (/\|\||Math\.max|isNaN|Number\.isFinite|===\s*''\s*\?/.test(m[0])) continue;
        offenders.push(`${file}: ${m[0].slice(0, 80)}`);
      }
    }
    expect(offenders, `unguarded Number(e.target.value):\n${offenders.join('\n')}`).toEqual([]);
  });
});

describe('a failed load is not mistaken for a real configuration', () => {
  it('the editors that PATCH their whole form track a load error', () => {
    // Authentication and ProjectSettings render hardcoded defaults when the initial GET fails, and
    // Save writes every field back — so one transient 500 plus one Save replaced a tenant's theme,
    // providers, verification flags, allowed domains, IP allowlist and scopes with the defaults.
    for (const name of ['pages/project/Authentication.tsx', 'pages/project/ProjectSettings.tsx']) {
      const entry = FILES.find(([file]) => file.endsWith(name));
      expect(entry, `${name} not found`).toBeDefined();
      const [, text] = entry!;
      expect(text, `${name} must distinguish a failed load from a loaded config`)
        .toMatch(/loadError|loadFailed/);
    }
  });
});


describe('a row you can click is a row you can reach', () => {
  it('no table row carries a click handler and nothing else', () => {
    // Eight tables navigate on a whole-row onClick. With no tabindex and no key handler the row is
    // mouse-only: a keyboard user tabs straight past every organisation, project, user list and
    // service account, and on the pages whose row action is not repeated in a menu there is no
    // second way in at all.
    const offenders: string[] = [];
    for (const [file, text] of FILES) {
      for (const m of text.matchAll(/<tr\b[^>]*onClick[^>]*>/g)) {
        if (/onKeyDown|rowActivation/.test(m[0])) continue;
        offenders.push(`${file}: ${m[0].slice(0, 90)}`);
      }
    }
    expect(offenders, `rows reachable by mouse only:\n${offenders.join('\n')}`).toEqual([]);
  });
});
