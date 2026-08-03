import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import SetPassword from './SetPassword';

/**
 * Accepting an invite. The token in the URL is a single-use credential, so the first thing the
 * page does is scrub it out of the address bar and browser history: left there it leaks through
 * Referer headers to anything the page loads, and through the history of a shared machine.
 *
 * The project's theme is applied from the server's own response, with the custom CSS sanitised
 * and every property it touched removed again on unmount.
 */

const api = vi.hoisted(() => ({ completeInvite: vi.fn(), getThemeByProject: vi.fn() }));
vi.mock('../api', () => api);

const css = vi.hoisted(() => ({ safeCssValue: vi.fn(), sanitizeCss: vi.fn() }));
vi.mock('../lib/sanitizeCss', () => css);

const ORIGINAL_URL = globalThis.location.href;

beforeEach(() => {
  vi.clearAllMocks();
  api.completeInvite.mockResolvedValue({});
  api.getThemeByProject.mockResolvedValue({ theme: {} });
  css.safeCssValue.mockImplementation((v?: string) => v);
  css.sanitizeCss.mockImplementation((v: string) => v);
});

afterEach(() => {
  globalThis.history.replaceState({}, '', ORIGINAL_URL);
  document.documentElement.removeAttribute('style');
  for (const n of document.querySelectorAll('style[data-iam-theme]')) n.remove();
});

function show(query = '?token=t1') {
  const user = userEvent.setup();
  const r = render(
    <MemoryRouter initialEntries={[`/set-password${query}`]}>
      <SetPassword />
    </MemoryRouter>,
  );
  return { user, ...r };
}

const fill = async (user: ReturnType<typeof userEvent.setup>, pw = 'hunter2hunter2', confirm = pw) => {
  await user.type(screen.getByLabelText('New password'), pw);
  await user.type(screen.getByLabelText('Confirm password'), confirm);
  await user.click(screen.getByRole('button', { name: /Accept invite/ }));
};

describe('the invite token', () => {
  it('is scrubbed from the address bar before anything else happens', async () => {
    globalThis.history.replaceState({}, '', '/set-password?token=secret-token&project_id=p1');
    show('?token=secret-token&project_id=p1');

    await vi.waitFor(() => expect(globalThis.location.search).not.toContain('secret-token'));
    expect(globalThis.location.search).toContain('project_id=p1');
  });

  it('is still usable after the scrub — it was read before the URL changed', async () => {
    const { user } = show();

    await fill(user);

    await vi.waitFor(() => expect(api.completeInvite).toHaveBeenCalledWith('t1', 'hunter2hunter2'));
  });

  it('says the link is unusable when there is no token at all', () => {
    show('');

    expect(screen.getByText('Invalid link.')).toBeInTheDocument();
    expect(screen.queryByLabelText('New password')).not.toBeInTheDocument();
  });
});

describe('setting the password', () => {
  it('accepts the invite and says the account is ready', async () => {
    const { user } = show();

    await fill(user);

    expect(await screen.findByText('Password set!')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to sign in' })).toHaveAttribute('href', '/login');
  });

  it('compares the two fields before sending anything', async () => {
    const { user } = show();

    await fill(user, 'hunter2hunter2', 'something-else');

    expect(await screen.findByText('Passwords do not match.')).toBeInTheDocument();
    expect(api.completeInvite).not.toHaveBeenCalled();
  });

  it('demands one long enough to be a password, and never offers a saved one', () => {
    show();

    const pw = screen.getByLabelText('New password');
    expect(pw).toHaveAttribute('minlength', '8');
    expect(pw).toHaveAttribute('autocomplete', 'new-password');
    expect(screen.getByLabelText('Confirm password')).toHaveAttribute('autocomplete', 'new-password');
  });

  it.each([
    ['a breached password, counted', { error: 'password_breached', count: 12345 }, /appeared in 12,345 data breaches/],
    ['a breached password, uncounted', { error: 'password_breached' }, /appeared in multiple data breaches/],
    ['an expired link', { error: 'token_expired' },
      'This invite link has expired. Ask your administrator to resend the invite.'],
    ['a link that was never valid', { error: 'token_not_found' },
      'This invite link has expired. Ask your administrator to resend the invite.'],
    ['a password the policy refuses, with a reason', { error: 'password_policy', detail: 'Needs a digit.' },
      'Needs a digit.'],
    ['a password the policy refuses, without one', { error: 'password_policy' },
      'Password does not meet the requirements. Please try a stronger password.'],
    ['anything else', { error: 'boom' }, 'Something went wrong. Please try again.'],
  ])('reports %s', async (_n, body, expected) => {
    api.completeInvite.mockResolvedValue(body);
    const { user } = show();

    await fill(user);

    expect(await screen.findByText(expected)).toBeInTheDocument();
    expect(screen.queryByText('Password set!')).not.toBeInTheDocument();
  });

  it('says something generic when the request fails outright', async () => {
    api.completeInvite.mockRejectedValue(new Error('500'));
    const { user } = show();

    await fill(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('re-enables the button after a failure', async () => {
    api.completeInvite.mockRejectedValue(new Error('500'));
    const { user } = show();

    await fill(user);

    await vi.waitFor(() => expect(screen.getByRole('button', { name: /Accept invite/ })).toBeEnabled());
  });
});

describe("the project's own theme", () => {
  const withTheme = (theme: Record<string, string>) =>
    api.getThemeByProject.mockResolvedValue({ theme });

  it('is not fetched when the link names no project', async () => {
    show('?token=t1');
    await screen.findByText("You've been invited.");
    expect(api.getThemeByProject).not.toHaveBeenCalled();
  });

  it('applies the colours and the font the server sent', async () => {
    withTheme({ primary_color: '#ff0000', background_color: '#eeeeee', font_family: 'Inter' });
    show('?token=t1&project_id=p1');

    await vi.waitFor(() =>
      expect(document.documentElement.style.getPropertyValue('--primary')).toBe('#ff0000'));
    expect(document.documentElement.style.getPropertyValue('--bg')).toBe('#eeeeee');
    expect(document.documentElement.style.getPropertyValue('--font-sans')).toBe('Inter');
  });

  it('derives the smaller radius from the larger one, never below a usable floor', async () => {
    withTheme({ border_radius: '1' });
    show('?token=t1&project_id=p1');

    await vi.waitFor(() =>
      expect(document.documentElement.style.getPropertyValue('--radius')).toBe('1px'));
    expect(document.documentElement.style.getPropertyValue('--radius-sm')).toBe('4px');
  });

  it('drops a value the sanitiser refuses rather than writing it', async () => {
    // The theme is attacker-influenceable through the admin console; a CSS value carrying a url()
    // would fetch from wherever it named.
    css.safeCssValue.mockReturnValue('');
    withTheme({ primary_color: 'url(https://evil.test/x)' });
    show('?token=t1&project_id=p1');

    await vi.waitFor(() => expect(css.safeCssValue).toHaveBeenCalled());
    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
  });

  it('sanitises the custom stylesheet before it reaches the document', async () => {
    css.sanitizeCss.mockReturnValue('.card { color: red }');
    withTheme({ custom_css: '.card { color: red } @import "evil";' });
    show('?token=t1&project_id=p1');

    await vi.waitFor(() =>
      expect(document.querySelector('style[data-iam-theme]')?.textContent).toBe('.card { color: red }'));
    expect(css.sanitizeCss).toHaveBeenCalledWith('.card { color: red } @import "evil";');
  });

  it('takes everything it applied back down when the page goes away', async () => {
    withTheme({ primary_color: '#ff0000', custom_css: '.card { color: red }' });
    const { unmount } = show('?token=t1&project_id=p1');
    await vi.waitFor(() =>
      expect(document.documentElement.style.getPropertyValue('--primary')).toBe('#ff0000'));

    unmount();

    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
    expect(document.querySelector('style[data-iam-theme]')).toBeNull();
  });

  it('applies nothing that arrives after the page has gone', async () => {
    let resolve!: (v: unknown) => void;
    api.getThemeByProject.mockReturnValue(new Promise(r => { resolve = r; }));
    const { unmount } = show('?token=t1&project_id=p1');

    unmount();
    resolve({ theme: { primary_color: '#ff0000' } });

    await vi.waitFor(() => expect(api.getThemeByProject).toHaveBeenCalled());
    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('');
  });

  it('renders the form anyway when the theme cannot be read', async () => {
    api.getThemeByProject.mockRejectedValue(new Error('404'));
    show('?token=t1&project_id=p1');

    expect(await screen.findByLabelText('New password')).toBeInTheDocument();
  });

  it('survives a response with no theme in it at all', async () => {
    api.getThemeByProject.mockResolvedValue({});
    show('?token=t1&project_id=p1');

    expect(await screen.findByLabelText('New password')).toBeInTheDocument();
  });
});
