/**
 * Where the operator was going when the sign-in interrupted them.
 *
 * Its own module rather than a second export from AuthProvider: a file that exports both a
 * component and a helper loses fast refresh, and the rule that says so is right — the helper would
 * be re-created on every render of a provider that wraps the whole application.
 */
export const RETURN_TO_KEY = 'rediensiam.returnTo';

/**
 * The path stored before the sign-in redirect, once, or null.
 *
 * Read by the router's catch-all rather than applied with history.replaceState: the authorization
 * server returns to /console/callback, which matches no route, so the catch-all navigates to the
 * scope home — and it would overwrite any URL written behind its back. Consumed on read, so a
 * second sign-in in the same tab does not resurrect an old destination.
 */
export function consumeReturnTo(basename: string, currentPath: string): string | null {
  const stored = sessionStorage.getItem(RETURN_TO_KEY);
  sessionStorage.removeItem(RETURN_TO_KEY);
  if (!stored) return null;

  // Router paths are relative to the basename; the stored value is a browser path.
  const path = stored.startsWith(basename) ? stored.slice(basename.length) : stored;
  const normalised = path.startsWith('/') ? path : `/${path}`;

  // Never leaves this origin. A stored value naming another one would be an open redirect handed
  // to whoever can write this tab's storage, and `//host` is how that is spelled.
  if (normalised.startsWith('//')) return null;

  // And never back to where we already are. The catch-all is what consumes this, so restoring the
  // current path sends an unroutable URL straight back to the catch-all — the address bar keeps a
  // path the application cannot render, which is exactly what the destination was meant to prevent.
  return currentPath.replace(/\/$/, '').endsWith(normalised.replace(/\/$/, '')) ? null : normalised;
}
