import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { getLogoutChallenge, acceptLogout } from '../api';
import { safeNavigate } from '../safeNavigate';

/**
 * The page Hydra sends the browser to when a client ends the SSO session.
 *
 * `hydra.urls.logout` used to point at `/auth/logout`, which is a controller answering JSON: the
 * browser landed on a raw `{"logout_challenge":"…"}` body, nothing ever posted the acceptance, and
 * the session survived a sign-out that looked like it had happened. This page is the missing half —
 * it reads the challenge, accepts it, and follows the client's own post-logout target.
 *
 * There is no confirmation prompt. The user reached here by asking to sign out from an application
 * that already had one; a second one is a step that only ever gets clicked through. If a front-
 * channel logout ever needs one, it belongs behind Hydra's own `rp_initiated` settings, not here.
 */
export default function Logout() {
  const [params] = useSearchParams();
  const challenge = params.get('logout_challenge');
  const [failure, setFailure] = useState('');
  // A missing challenge is a fact about the URL, not something that happens later — deriving it
  // during render keeps the effect for the one thing that is actually asynchronous.
  const error = challenge
    ? failure
    : 'This sign-out link is incomplete. Close this tab and sign out from the application again.';

  useEffect(() => {
    if (!challenge) return;
    let cancelled = false;
    (async () => {
      try {
        // Confirming the challenge first is what makes a forged link a dead end rather than a
        // one-click sign-out for a session the sender does not own.
        await getLogoutChallenge(challenge);
        if (cancelled) return;
        const res = await acceptLogout(challenge);
        if (cancelled) return;
        // The URL comes from Hydra, built from the client's whitelist — but it arrives through the
        // browser, so it goes through the same guard as every other redirect this SPA follows.
        if (!safeNavigate(res.redirect_to)) {
          setFailure('You have been signed out, but the application asked us to return you somewhere we do not trust.');
        }
      } catch {
        if (!cancelled) setFailure('We could not complete the sign-out. Close this tab and try again from the application.');
      }
    })();
    return () => { cancelled = true; };
  }, [challenge]);

  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <div className="login-logo">
          <div className="brand-mark">R</div>
          <span>RediensIAM</span>
        </div>
        <h1 className="login-title">Signing you out.</h1>
        {error
          ? (
            <div role="alert" className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
              {error}
            </div>
          )
          : <p className="login-subtitle">Ending your session…</p>}
      </div>
    </div>
  );
}
