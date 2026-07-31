import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { isSafeRedirect, safeNavigate } from './safeNavigate';

/**
 * `redirect_to` comes back from the API and ends up in `location.href`. If an attacker can steer
 * it, they get an open redirect off the login page — a phishing primitive, and the reason the
 * react-router open-redirect advisory could not be reached here.
 *
 * VITE_HYDRA_PUBLIC_ORIGIN is read once at module load, so these tests exercise the build where
 * it is unset: same-origin only. That is the stricter of the two configurations.
 */

const origin = globalThis.location.origin; // http://localhost:3000 under jsdom

describe('what is accepted', () => {
  it('accepts a relative path', () => {
    expect(isSafeRedirect('/dashboard')).toBe(true);
    expect(isSafeRedirect('/oauth2/auth?client_id=x&state=y')).toBe(true);
  });

  it('accepts an absolute URL on this origin', () => {
    expect(isSafeRedirect(`${origin}/oauth2/auth`)).toBe(true);
  });
});

describe('what is refused', () => {
  it('refuses another origin', () => {
    expect(isSafeRedirect('https://evil.test/steal')).toBe(false);
    expect(isSafeRedirect('http://evil.test')).toBe(false);
  });

  it('refuses a protocol-relative URL, which is just another origin in disguise', () => {
    expect(isSafeRedirect('//evil.test')).toBe(false);
    expect(isSafeRedirect('//evil.test/path')).toBe(false);
  });

  it('refuses backslashes, which browsers normalise into a protocol-relative URL', () => {
    // `/\evil.test` is normalised to `//evil.test`. Without the explicit backslash check the
    // "starts with /" reasoning lets it straight through.
    for (const target of [
      String.raw`/\evil.test`,
      String.raw`\\evil.test`,
      String.raw`/\/evil.test`,
      String.raw`https://evil.test\@${origin}`,
      String.raw`\/\/evil.test`,
      String.raw`/dashboard\..\..\evil.test`,
    ]) {
      expect(isSafeRedirect(target), target).toBe(false);
    }
  });

  it('refuses schemes that are not http(s)', () => {
    for (const target of [
      'javascript:alert(1)',
      'JavaScript:alert(1)',
      'data:text/html,<script>alert(1)</script>',
      'vbscript:msgbox(1)',
      'file:///etc/passwd',
      'blob:http://localhost:3000/abc',
    ]) {
      expect(isSafeRedirect(target), target).toBe(false);
    }
  });

  it('refuses a credentials-in-userinfo URL that only looks same-origin', () => {
    expect(isSafeRedirect(`https://${origin.replace('http://', '')}@evil.test/`)).toBe(false);
    expect(isSafeRedirect('https://localhost:3000.evil.test/')).toBe(false);
  });

  it('refuses a same-host URL on another port or scheme', () => {
    expect(isSafeRedirect('http://localhost:9999/x')).toBe(false);
    expect(isSafeRedirect('https://localhost:3000/x')).toBe(false);
  });

  it('refuses nothing at all', () => {
    expect(isSafeRedirect(null)).toBe(false);
    expect(isSafeRedirect(undefined)).toBe(false);
    expect(isSafeRedirect('')).toBe(false);
  });

  it('refuses a string the URL parser cannot make sense of', () => {
    expect(isSafeRedirect('http://')).toBe(false);
    expect(isSafeRedirect('http://[')).toBe(false);
  });
});

describe('safeNavigate', () => {
  let assigned: string[];

  beforeEach(() => {
    assigned = [];
    vi.spyOn(console, 'error').mockImplementation(() => {});
    // jsdom's location.href is unforgeable and assigning to it only logs "Not implemented:
    // navigation". Swap the whole object for one that records what the code tried to visit.
    vi.stubGlobal('location', {
      origin,
      get href() { return `${origin}/`; },
      set href(v: string) { assigned.push(v); },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('navigates and reports success for a target it trusts', () => {
    expect(safeNavigate('/after-login')).toBe(true);
    expect(assigned).toEqual(['/after-login']);
  });

  it('does not navigate, and says so, for a target it does not trust', () => {
    // The caller has to be able to tell — it shows "could not complete sign-in" rather than
    // leaving the user on a page that looks like it is still working.
    expect(safeNavigate('https://evil.test/steal')).toBe(false);
    expect(assigned).toEqual([]);
  });

  it('does not navigate on a backslash smuggle', () => {
    expect(safeNavigate(String.raw`/\evil.test`)).toBe(false);
    expect(assigned).toEqual([]);
  });

  it('does not navigate when there is no target', () => {
    expect(safeNavigate(null)).toBe(false);
    expect(safeNavigate(undefined)).toBe(false);
    expect(assigned).toEqual([]);
  });
});
