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
