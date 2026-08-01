import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import Login from './Login';

const api = vi.hoisted(() => ({ getLoginChallenge: vi.fn(), submitLogin: vi.fn() }));
vi.mock('../api', () => api);

/**
 * Only `../api` is mocked above. safeNavigate is deliberately NOT mocked: refusing a hostile
 * redirect_to is part of what the login form has to do, and mocking it out would hide exactly
 * that. Adding a `vi.mock('../safeNavigate')` here would leave the redirect tests below passing
 * against nothing.
 */
const origin = globalThis.location.origin;
let visited: string[];

function Where() {
  const { pathname, search } = useLocation();
  return <span data-testid="where">{pathname + search}</span>;
}

const CHALLENGE = 'chal_123';

function show(theme: Record<string, unknown> = {}) {
  api.getLoginChallenge.mockResolvedValue(theme);
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[`/?login_challenge=${CHALLENGE}`]}>
      <Where />
      <Login />
    </MemoryRouter>,
  );
  return user;
}

const identifier = () => screen.getByLabelText(/Email/);
const submit = () => screen.getByRole('button', { name: /Continue/ });

async function signIn(user: Awaited<ReturnType<typeof show>>, who = 'ada@example.test', pw = 'hunter2') {
  await user.type(identifier(), who);
  await user.type(screen.getByLabelText('Password'), pw);
  await user.click(submit());
}

beforeEach(() => {
  vi.clearAllMocks();
  visited = [];
  localStorage.clear();
  sessionStorage.clear();
  vi.stubGlobal('location', {
    origin,
    get href() { return `${origin}/`; },
    set href(v: string) { visited.push(v); },
  });
});

afterEach(() => vi.unstubAllGlobals());

describe('the form', () => {
  it('labels both fields so they can be reached without a mouse', async () => {
    show({ project_name: 'Acme Portal' });
    expect(await screen.findByLabelText('Email or username')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
  });

  it('names the project being signed in to', async () => {
    show({ project_name: 'Acme Portal' });
    expect(await screen.findByText('Acme Portal')).toBeInTheDocument();
    expect(screen.getByRole('heading')).toHaveTextContent('Welcome back.');
  });

  it('asks an admin for an email specifically', async () => {
    show({ is_admin_login: true });
    await waitFor(() => expect(screen.getByRole('heading')).toHaveTextContent('Admin sign in.'));
    expect(screen.getByLabelText('Email')).toHaveAttribute('type', 'email');
  });

  it('hides the password until asked', async () => {
    const user = show();
    const password = screen.getByLabelText('Password');
    expect(password).toHaveAttribute('type', 'password');

    await user.click(screen.getByRole('button', { name: 'Toggle password visibility' }));
    expect(password).toHaveAttribute('type', 'text');
  });

  it('says so when the login link is not valid', async () => {
    api.getLoginChallenge.mockRejectedValue(new Error('410'));
    render(
      <MemoryRouter initialEntries={[`/?login_challenge=${CHALLENGE}`]}><Login /></MemoryRouter>,
    );
    expect(await screen.findByText('Invalid login link')).toBeInTheDocument();
  });
});

describe('signing in successfully', () => {
  it('follows the redirect the server hands back', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ redirect_to: `${origin}/oauth2/auth?consent=1` });

    await signIn(user);

    await waitFor(() => expect(visited).toEqual([`${origin}/oauth2/auth?consent=1`]));
    expect(screen.queryByText(/Invalid email or password/)).not.toBeInTheDocument();
  });

  it('sends an email as email and a bare name as username', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ redirect_to: '/next' });

    await signIn(user, 'ada@example.test');
    expect(api.submitLogin).toHaveBeenLastCalledWith(
      expect.objectContaining({ email: 'ada@example.test', login_challenge: CHALLENGE }));

    await user.clear(identifier());
    await signIn(user, 'ada');
    expect(api.submitLogin).toHaveBeenLastCalledWith(expect.objectContaining({ username: 'ada' }));
  });

  it('sends an admin identifier as an email', async () => {
    const user = show({ is_admin_login: true });
    await waitFor(() => expect(screen.getByRole('heading')).toHaveTextContent('Admin sign in.'));
    api.submitLogin.mockResolvedValue({ redirect_to: '/next' });

    await signIn(user, 'root@example.test');

    expect(api.submitLogin).toHaveBeenLastCalledWith(expect.objectContaining({ email: 'root@example.test' }));
  });

  it('will not submit an admin identifier that is not an email address', async () => {
    // The field is type=email, so the browser blocks the submit before any request is made.
    const user = show({ is_admin_login: true });
    await waitFor(() => expect(screen.getByRole('heading')).toHaveTextContent('Admin sign in.'));

    await signIn(user, 'root');

    expect(api.submitLogin).not.toHaveBeenCalled();
  });

  it('will not submit an empty form', async () => {
    const user = show();
    await user.click(submit());
    expect(api.submitLogin).not.toHaveBeenCalled();
  });

  it('hands off to the MFA challenge, remembering which factor to ask for', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ requires_mfa: true, mfa_type: 'sms', phone_hint: '•••• 4321' });

    await signIn(user);

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent(`/mfa?login_challenge=${CHALLENGE}`));
    expect(sessionStorage.getItem('mfa_type')).toBe('sms');
    expect(sessionStorage.getItem('mfa_phone_hint')).toBe('•••• 4321');
    expect(visited).toEqual([]);
  });

  it('defaults the factor to TOTP when the server does not name one', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ requires_mfa: true });

    await signIn(user);

    await waitFor(() => expect(sessionStorage.getItem('mfa_type')).toBe('totp'));
  });

  it('hands off to MFA enrolment when the project requires it', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ requires_mfa_setup: true, user_id: 'usr_7' });

    await signIn(user);

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/mfa-setup'));
    expect(sessionStorage.getItem('mfa_setup_challenge')).toBe(CHALLENGE);
    expect(sessionStorage.getItem('mfa_setup_user')).toBe('usr_7');
  });
});

describe('when sign-in fails', () => {
  it('gives the same message for a bad password as for an unknown account', async () => {
    // Distinguishing them tells an attacker which addresses exist.
    const user = show();
    api.submitLogin.mockResolvedValue({ error: 'invalid_credentials' });
    await signIn(user);
    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument();

    api.submitLogin.mockResolvedValue({ error: 'user_not_found' });
    await user.click(submit());
    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument();
  });

  it('explains a missing role rather than blaming the password', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ error: 'no_role' });

    await signIn(user);

    expect(await screen.findByText('You do not have permission to access this application.')).toBeInTheDocument();
  });

  it('says when the account is locked and until when', async () => {
    const user = show();
    const until = new Date(Date.now() + 900_000);
    api.submitLogin.mockResolvedValue({ error: 'account_locked', locked_until: until.toISOString() });

    await signIn(user);

    expect(await screen.findByText(`Account locked until ${until.toLocaleTimeString()}`)).toBeInTheDocument();
  });

  it('reports a network failure without leaking the exception', async () => {
    const user = show();
    api.submitLogin.mockRejectedValue(new TypeError('Failed to fetch'));

    await signIn(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('Failed to fetch');
  });

  it('clears the previous error when the next attempt starts', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ error: 'no_role' });
    await signIn(user);
    await screen.findByText('You do not have permission to access this application.');

    api.submitLogin.mockResolvedValue({ redirect_to: '/next' });
    await user.click(submit());

    await waitFor(() =>
      expect(screen.queryByText('You do not have permission to access this application.')).not.toBeInTheDocument());
  });

  it('re-enables the button so a failed attempt can be retried', async () => {
    const user = show();
    api.submitLogin.mockResolvedValue({ error: 'invalid_credentials' });

    await signIn(user);

    await screen.findByText('Invalid email or password.');
    expect(submit()).toBeEnabled();
  });
});

describe('a redirect_to the server should not have sent', () => {
  const hostile = [
    'https://evil.test/steal',
    '//evil.test',
    String.raw`/\evil.test`,
    'javascript:alert(document.cookie)',
  ];

  for (const target of hostile) {
    it(`refuses ${target} and tells the user instead of following it`, async () => {
      const user = show();
      api.submitLogin.mockResolvedValue({ redirect_to: target });

      await signIn(user);

      expect(await screen.findByText('Sign-in could not complete. Please try again.')).toBeInTheDocument();
      expect(visited).toEqual([]);
    });
  }
});

describe('tenant theming', () => {
  it('applies a colour the tenant is allowed to set', async () => {
    show({ theme: { primary_color: '#3b82f6', font_family: 'Inter, sans-serif' } });

    await waitFor(() => expect(document.documentElement.style.getPropertyValue('--primary')).toBe('#3b82f6'));
    expect(document.documentElement.style.getPropertyValue('--font-sans')).toBe('Inter, sans-serif');
  });

  it('refuses a colour that tries to break out of its declaration', async () => {
    show({ theme: { primary_color: 'red; background: url(https://evil.test/x)', background_color: '#fff' } });

    await waitFor(() => expect(document.documentElement.style.getPropertyValue('--background')).toBe('#fff'));
    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
  });

  it('sanitises custom CSS before putting it on the page', async () => {
    show({
      theme: {
        custom_css: '.login-card { color: red } input[type=password] { background: url(https://evil.test/k) }',
      },
    });

    const style = await waitFor(() => {
      const node = document.head.querySelector<HTMLStyleElement>('style[data-iam-theme="login"]');
      expect(node).not.toBeNull();
      return node!;
    });
    expect(style.textContent).toContain('color: red');
    expect(style.textContent).not.toContain('evil.test');
    expect(style.textContent?.toLowerCase()).not.toContain('password');
  });

  it('takes its style node away again when the page unmounts', async () => {
    api.getLoginChallenge.mockResolvedValue({ theme: { custom_css: '.a { color: red }', primary_color: '#123456' } });
    const { unmount } = render(
      <MemoryRouter initialEntries={[`/?login_challenge=${CHALLENGE}`]}><Login /></MemoryRouter>,
    );

    await waitFor(() => expect(document.head.querySelector('style[data-iam-theme="login"]')).not.toBeNull());
    unmount();

    expect(document.head.querySelector('style[data-iam-theme="login"]')).toBeNull();
    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
  });
});

describe('social providers', () => {
  const provider = (over: Record<string, unknown> = {}) => ({
    id: 'idp_1', type: 'google', label: 'Google', client_id: 'c1', enabled: true, ...over,
  });

  it('offers the enabled ones only', async () => {
    show({ theme: { providers: [provider(), provider({ id: 'idp_2', label: 'GitHub', type: 'github', enabled: false })] } });

    expect(await screen.findByRole('button', { name: /Continue with Google/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Continue with GitHub/ })).not.toBeInTheDocument();
  });

  it('starts the provider flow with the challenge and provider id escaped', async () => {
    const user = show({ theme: { providers: [provider({ id: 'idp/1 &x' })] } });

    await user.click(await screen.findByRole('button', { name: /Continue with Google/ }));

    expect(visited).toEqual([`/auth/oauth2/start?login_challenge=${CHALLENGE}&provider_id=idp%2F1%20%26x`]);
  });
});
