import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import App from './App';

/**
 * The login app's router. Its catch-all sends anything unrecognised to /login rather than showing
 * a blank page, and `/auth/oauth2/error` is where the backend lands a social login it could not
 * complete — that page has to offer the way back into the same challenge, not start a new one.
 */

/** Pages have their own tests; here each one only has to be identifiable. */
const stub = vi.hoisted(() => (name: string) => ({ default: () => <h2>{name}</h2> }));

vi.mock('./pages/Login', () => stub('Login'));
vi.mock('./pages/Logout', () => stub('Logout'));
vi.mock('./pages/MfaChallenge', () => stub('MfaChallenge'));
vi.mock('./pages/MfaSetup', () => stub('MfaSetup'));
vi.mock('./pages/PasswordReset', () => stub('PasswordReset'));
vi.mock('./pages/Preview', () => stub('Preview'));
vi.mock('./pages/Register', () => stub('Register'));
vi.mock('./pages/SetPassword', () => stub('SetPassword'));
vi.mock('./index.css', () => ({}));

const ORIGINAL_URL = globalThis.location.href;
const at = (path: string) => globalThis.history.replaceState({}, '', path);

beforeEach(() => at('/login'));
afterEach(() => globalThis.history.replaceState({}, '', ORIGINAL_URL));

const page = () => screen.queryByRole('heading', { level: 2 })?.textContent ?? null;

describe('the routes', () => {
  it.each([
    ['/login', 'Login'],
    ['/logout', 'Logout'],
    ['/mfa', 'MfaChallenge'],
    ['/mfa-setup', 'MfaSetup'],
    ['/password-reset', 'PasswordReset'],
    ['/preview', 'Preview'],
    ['/register', 'Register'],
    ['/set-password', 'SetPassword'],
  ])('serves %s', (path, expected) => {
    at(path);
    render(<App />);
    expect(page()).toBe(expected);
  });

  it('keeps the query string the flow depends on', () => {
    at('/login?login_challenge=c1');
    render(<App />);

    expect(page()).toBe('Login');
    expect(globalThis.location.search).toBe('?login_challenge=c1');
  });

  it('sends anything it does not recognise to the sign-in page', () => {
    // A blank page here is indistinguishable from a broken deployment.
    at('/nowhere');
    render(<App />);

    expect(page()).toBe('Login');
  });
});

describe('a social login that could not be completed', () => {
  it('says so, and offers the way back into the same challenge', () => {
    at('/auth/oauth2/error?login_challenge=c%201');
    render(<App />);

    expect(screen.getByRole('heading', { name: 'Sign-in failed.' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to sign in' }))
      // Re-encoded, so a challenge with a space survives the round trip.
      .toHaveAttribute('href', '/login?login_challenge=c%201');
  });

  it('offers no link at all when there is no challenge to return to', () => {
    // A link to /login with no challenge starts an unrelated flow and fails differently.
    at('/auth/oauth2/error');
    render(<App />);

    expect(screen.getByRole('heading', { name: 'Sign-in failed.' })).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});
