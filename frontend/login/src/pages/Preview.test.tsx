import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import Preview from './Preview';

/**
 * The live preview the admin console renders in an iframe. Everything it draws comes out of one
 * base64 query parameter, which is attacker-supplyable by definition — so a malformed `cfg` has to
 * render the defaults rather than throw, and every CSS value out of it goes through the sanitiser
 * before it reaches the document.
 */

const css = vi.hoisted(() => ({ safeCssValue: vi.fn() }));
vi.mock('../lib/sanitizeCss', () => css);

beforeEach(() => {
  vi.clearAllMocks();
  css.safeCssValue.mockImplementation((v?: unknown) => (typeof v === 'string' ? v : ''));
});

afterEach(() => {
  document.documentElement.removeAttribute('style');
  delete document.documentElement.dataset['theme'];
});

const encode = (cfg: unknown) => btoa(JSON.stringify(cfg));

function show(cfg?: unknown, raw?: string) {
  const q = raw ?? (cfg === undefined ? '' : `?cfg=${encode(cfg)}`);
  render(<MemoryRouter initialEntries={[`/preview${q}`]}><Preview /></MemoryRouter>);
}

describe('the configuration it is given', () => {
  it('defaults to the sign-in card when there is none', () => {
    show();
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('renders the defaults rather than throwing on a malformed one', () => {
    // The parameter is just a query string; a broken one must not take the iframe down.
    show(undefined, '?cfg=not-base64!!');
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('renders the defaults on base64 that is not JSON', () => {
    show(undefined, `?cfg=${btoa('not json')}`);
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });
});

describe('the three cards', () => {
  it('draws the sign-in card', () => {
    show({ mode: 'login' });

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.getByPlaceholderText('you@example.com or username#1234')).toBeDisabled();
  });

  it('draws the registration card', () => {
    show({ mode: 'register' });

    expect(screen.getByRole('heading', { name: 'Create account' })).toBeInTheDocument();
    expect(screen.getByPlaceholderText('alice (optional)')).toBeDisabled();
  });

  it.each([
    ['email', { email_verification_enabled: true }, 'Check your email'],
    ['SMS', { sms_verification_enabled: true }, 'Check your phone'],
    ['neither', {}, 'Check your contact'],
  ])('draws the verification card for %s', (_n, over, heading) => {
    show({ mode: 'verify', ...over });

    expect(screen.getByRole('heading', { name: heading })).toBeInTheDocument();
    expect(screen.getByPlaceholderText('123456')).toBeDisabled();
  });

  it('falls through to the verification card for a mode it does not know', () => {
    show({ mode: 'something-else' });
    expect(screen.getByRole('heading', { name: /^Check your/ })).toBeInTheDocument();
  });
});

describe('what the project has turned on', () => {
  it('offers the links only where the feature exists', () => {
    show({ mode: 'login', allow_self_registration: true, email_verification_enabled: true });

    expect(screen.getByText('Forgot password?')).toBeInTheDocument();
    expect(screen.getByText('Create account')).toBeInTheDocument();
  });

  it('offers neither when neither is on', () => {
    show({ mode: 'login' });

    expect(screen.queryByText('Forgot password?')).not.toBeInTheDocument();
    expect(screen.queryByText('Create account')).not.toBeInTheDocument();
  });

  it('hides the password form when the project has turned it off', () => {
    show({ mode: 'login', theme: { hydra_local_login: false } });

    expect(screen.queryByPlaceholderText('you@example.com or username#1234')).not.toBeInTheDocument();
  });

  it('shows only the social providers that are enabled', () => {
    show({
      mode: 'login',
      theme: {
        providers: [
          { id: 'google', type: 'google', label: 'Continue with Google', enabled: true },
          { id: 'github', type: 'github', label: 'Continue with GitHub', enabled: false },
        ],
      },
    });

    expect(screen.getByText('Continue with Google')).toBeInTheDocument();
    expect(screen.queryByText('Continue with GitHub')).not.toBeInTheDocument();
  });

  it('draws no provider block at all when none is enabled', () => {
    show({ mode: 'login', theme: { providers: [] } });
    expect(screen.queryByText(/^Continue with/)).not.toBeInTheDocument();
  });

  it('shows the password rules the project enforces, on the registration card', () => {
    show({
      mode: 'register', min_password_length: 12,
      password_require_uppercase: true, password_require_lowercase: true,
      password_require_digit: true, password_require_special: true,
    });

    expect(screen.getByText('At least 12 characters')).toBeInTheDocument();
    expect(screen.getByText('One uppercase letter (A–Z)')).toBeInTheDocument();
    expect(screen.getByText('One lowercase letter (a–z)')).toBeInTheDocument();
    expect(screen.getByText('One number (0–9)')).toBeInTheDocument();
    expect(screen.getByText('One special character (!@#$…)')).toBeInTheDocument();
  });

  it('shows no rules at all when the project sets none', () => {
    show({ mode: 'register' });
    expect(screen.queryByText(/^At least/)).not.toBeInTheDocument();
  });

  it('shows the logo when there is one', () => {
    show({ mode: 'login', theme: { logo_url: 'https://cdn.test/logo.png' } });
    expect(screen.getByAltText('Logo')).toHaveAttribute('src', 'https://cdn.test/logo.png');
  });
});

describe('the theme it applies', () => {
  it('writes the colours and the font through the sanitiser', () => {
    show({
      mode: 'login',
      theme: {
        primary_color: '#ff0000', background_color: '#eeeeee',
        surface_color: '#ffffff', text_color: '#111111', font_family: 'Inter',
      },
    });

    const el = document.documentElement;
    expect(el.style.getPropertyValue('--primary')).toBe('#ff0000');
    expect(el.style.getPropertyValue('--background')).toBe('#eeeeee');
    expect(el.style.getPropertyValue('--surface')).toBe('#ffffff');
    expect(el.style.getPropertyValue('--text')).toBe('#111111');
    expect(el.style.getPropertyValue('--font-family')).toBe('Inter');
    expect(css.safeCssValue).toHaveBeenCalledWith('#ff0000');
  });

  it('writes nothing the sanitiser refuses', () => {
    css.safeCssValue.mockReturnValue('');
    show({ mode: 'login', theme: { primary_color: 'url(https://evil.test/x)' } });

    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
  });

  it.each([
    ['a sane radius', 12, '12px'],
    ['zero', 0, '0px'],
  ])('applies %s', (_n, border_radius, expected) => {
    show({ mode: 'login', theme: { border_radius } });
    expect(document.documentElement.style.getPropertyValue('--radius')).toBe(expected);
  });

  it.each([
    ['a negative radius', -1],
    ['an absurd one', 999],
    ['one that is not a number', '12'],
  ])('refuses %s', (_n, border_radius) => {
    show({ mode: 'login', theme: { border_radius } });
    expect(document.documentElement.style.getPropertyValue('--radius')).toBe('');
  });

  it.each([
    [true, 'dark'],
    [false, 'light'],
  ])('follows the dark flag (%s)', (dark, expected) => {
    show({ mode: 'login', dark });
    expect(document.documentElement.dataset['theme']).toBe(expected);
  });
});
