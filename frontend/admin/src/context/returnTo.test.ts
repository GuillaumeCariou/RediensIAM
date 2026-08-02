import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RETURN_TO_KEY, consumeReturnTo } from './returnTo';

/**
 * This value decides where the operator lands after signing in, and it comes out of storage any
 * script on this origin can write. Two things therefore matter as much as restoring the page:
 * it must never leave this origin, and it must be spent on first read.
 */

const store = new Map<string, string>();

beforeEach(() => {
  store.clear();
  vi.stubGlobal('sessionStorage', {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  });
});

const stored = (v: string) => store.set(RETURN_TO_KEY, v);

describe('consumeReturnTo', () => {
  it('returns null when nothing was stored', () => {
    expect(consumeReturnTo('/console', '/system')).toBeNull();
  });

  it('gives back the stored path with the basename stripped', () => {
    // The stored value is a browser path; the router works below the basename.
    stored('/console/org/projects');
    expect(consumeReturnTo('/console', '/system')).toBe('/org/projects');
  });

  it('leaves a path that does not carry the basename alone', () => {
    stored('/org/projects');
    expect(consumeReturnTo('/console', '/system')).toBe('/org/projects');
  });

  it('adds the leading slash the basename strip can eat', () => {
    stored('/consoleorg/projects');
    expect(consumeReturnTo('/console', '/system')).toBe('/org/projects');
  });

  it('spends the value, so a second sign-in does not resurrect it', () => {
    stored('/console/org/projects');

    expect(consumeReturnTo('/console', '/system')).toBe('/org/projects');
    expect(consumeReturnTo('/console', '/system')).toBeNull();
    expect(store.has(RETURN_TO_KEY)).toBe(false);
  });

  it('clears the value even when it refuses to use it', () => {
    stored('//evil.example/steal');

    expect(consumeReturnTo('/console', '/system')).toBeNull();
    expect(store.has(RETURN_TO_KEY)).toBe(false);
  });

  it('refuses a protocol-relative destination', () => {
    // `//host` is how an open redirect is spelled in something that looks like a path.
    stored('//evil.example/steal');
    expect(consumeReturnTo('/console', '/system')).toBeNull();
  });

  it('refuses one written with the basename in front of it', () => {
    stored('/console//evil.example/steal');
    expect(consumeReturnTo('/console', '/system')).toBeNull();
  });

  it('refuses to send the catch-all back to the page it is already on', () => {
    // The catch-all is what consumes this. Restoring the current path hands it a URL it cannot
    // route and the address bar keeps it — the exact state the stored destination exists to avoid.
    stored('/console/org/projects');
    expect(consumeReturnTo('/console', '/console/org/projects')).toBeNull();
  });

  it('ignores a trailing slash when deciding that', () => {
    stored('/console/org/projects/');
    expect(consumeReturnTo('/console', '/console/org/projects')).toBeNull();
  });
});
