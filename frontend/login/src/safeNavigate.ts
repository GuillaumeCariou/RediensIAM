/**
 * The one extra origin a redirect may target, beyond this SPA's own. Empty when unset, and the
 * `HYDRA &&` guard below is what keeps an unset variable from turning into a match-anything rule.
 */
const HYDRA = (import.meta.env.VITE_HYDRA_PUBLIC_ORIGIN ?? '').trim();

/**
 * Open-redirect guard for the attacker-controllable `redirect_to` in API responses. Only a
 * same-origin URL, or one on the Hydra public origin (when known via VITE_HYDRA_PUBLIC_ORIGIN),
 * is safe. Everything else is refused — callers must show a generic "could not complete sign-in"
 * message rather than navigating anyway.
 *
 * The order of the checks below is the control, not a style choice:
 *
 * 1. The backslash rejection must come FIRST, before anything reasons about the string looking
 *    like a relative path. Browsers normalise `/\evil.com` to `//evil.com`, which is
 *    protocol-relative and lands off-origin; a leading-slash short-circuit would wave it through.
 * 2. The URL parser then canonicalises the target and resolves relative paths against this
 *    origin, so `..` segments, percent-encoding and case tricks cannot smuggle a different host
 *    past the comparison. Absolute URLs keep their own origin and are caught by it.
 * 3. Only http(s) survives the protocol test — this is what rejects `javascript:`, `data:` and
 *    `file:` targets that no origin comparison would catch.
 *
 * Compare full origins, never a hostname prefix or suffix: `evil-app.test` and
 * `app.test.evil.test` both pass a naive substring test.
 */
export function isSafeRedirect(target: string | null | undefined): boolean {
  if (!target) return false;
  if (target.includes('\\')) return false;
  let u: URL;
  try {
    u = new URL(target, globalThis.location.origin);
  } catch {
    return false;
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;
  if (u.origin === globalThis.location.origin) return true;
  if (HYDRA && u.origin === HYDRA) return true;
  return false;
}

/**
 * Navigates only if {@link isSafeRedirect} accepts the target; returns false without navigating
 * otherwise, so the caller can tell the user sign-in could not be completed.
 *
 * The `console.error` below is deliberate: a refused redirect is the one signal that an
 * open-redirect attempt reached the browser, and it has to be visible in the console of an
 * unauthenticated page where no error reporter is wired up. Do not silence it.
 */
export function safeNavigate(target: string | null | undefined): boolean {
  if (!isSafeRedirect(target)) {
    console.error('Refusing to navigate to untrusted redirect_to:', target);
    return false;
  }
  globalThis.location.href = target as string;
  return true;
}
