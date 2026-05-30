// Defence against open-redirect via attacker-controlled `redirect_to` in API responses.
// Only allow same-origin URLs or paths starting with the Hydra public origin (when known
// via VITE_HYDRA_PUBLIC_ORIGIN). Anything else throws — callers must handle the error and
// show the user a generic "could not complete sign-in" message rather than silently navigating.
const HYDRA = (import.meta.env.VITE_HYDRA_PUBLIC_ORIGIN ?? '').trim();

export function isSafeRedirect(target: string | null | undefined): boolean {
  if (!target) return false;
  // Relative paths are always same-origin.
  if (target.startsWith('/') && !target.startsWith('//')) return true;
  try {
    const u = new URL(target, globalThis.location.origin);
    if (u.origin === globalThis.location.origin) return true;
    if (HYDRA && u.origin === HYDRA) return true;
    return false;
  } catch {
    return false;
  }
}

export function safeNavigate(target: string | null | undefined): boolean {
  if (!isSafeRedirect(target)) {
    // eslint-disable-next-line no-console
    console.error('Refusing to navigate to untrusted redirect_to:', target);
    return false;
  }
  globalThis.location.href = target as string;
  return true;
}
