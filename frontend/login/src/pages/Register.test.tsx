import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import Register from './Register';

/**
 * Self-registration, reachable by anyone the project allows it for. What matters here is that a
 * password the world already has is refused with a reason the visitor can act on, that the two
 * password fields are compared before anything is sent, and that the destination the server picks
 * only ever goes through `safeNavigate`.
 */

const api = vi.hoisted(() => ({ registerUser: vi.fn(), verifyRegistrationOtp: vi.fn() }));
vi.mock('../api', () => api);

const nav = vi.hoisted(() => ({ safeNavigate: vi.fn(() => true) }));
vi.mock('../safeNavigate', () => nav);

const OK = { redirect_to: 'https://app.test/callback' };

beforeEach(() => {
  vi.clearAllMocks();
  nav.safeNavigate.mockReturnValue(true);
  api.registerUser.mockResolvedValue(OK);
  api.verifyRegistrationOtp.mockResolvedValue(OK);
});

function show(challenge = 'c1') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[`/register?login_challenge=${challenge}`]}>
      <Register />
    </MemoryRouter>,
  );
  return user;
}

const cells = () => screen.getAllByLabelText(/^Digit \d of 6$/);

async function fillForm(user: ReturnType<typeof userEvent.setup>, over: Partial<{
  email: string; username: string; password: string; confirm: string;
}> = {}) {
  const v = { email: 'ada@acme.test', username: '', password: 'hunter2hunter2', confirm: 'hunter2hunter2', ...over };
  await user.type(screen.getByLabelText('Email'), v.email);
  if (v.username) await user.type(screen.getByLabelText(/Username/), v.username);
  await user.type(screen.getByLabelText('Password'), v.password);
  await user.type(screen.getByLabelText('Confirm password'), v.confirm);
  await user.click(screen.getByRole('button', { name: /Continue/ }));
}

describe('the form', () => {
  it('registers against the challenge the link carried', async () => {
    const user = show('abc');

    await fillForm(user);

    await vi.waitFor(() => expect(api.registerUser).toHaveBeenCalledWith({
      login_challenge: 'abc', email: 'ada@acme.test', password: 'hunter2hunter2', username: undefined,
    }));
    expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback');
  });

  it('sends a username only when one was given', async () => {
    const user = show();

    await fillForm(user, { username: 'ada' });

    await vi.waitFor(() => expect(api.registerUser).toHaveBeenCalledWith(
      expect.objectContaining({ username: 'ada' })));
  });

  it('compares the two password fields before sending anything', async () => {
    const user = show();

    await fillForm(user, { confirm: 'something-else' });

    expect(await screen.findByText('Passwords do not match.')).toBeInTheDocument();
    expect(api.registerUser).not.toHaveBeenCalled();
  });

  it('demands a password long enough to be one', () => {
    show();
    expect(screen.getByLabelText('Password')).toHaveAttribute('minlength', '8');
    expect(screen.getByLabelText('Email')).toBeRequired();
  });

  it.each([
    ['weak', 'abc'],
    ['fair', 'abcd'],
    // Length dominates the score, so a long lower-case password already reads as excellent —
    // the meter is a nudge, not the policy, which the project's own rules enforce server-side.
    ['strong', 'abcdef'],
    ['excellent', 'abcdefgh'],
  ])('rates a password as %s while it is typed', async (label, password) => {
    const user = show();

    await user.type(screen.getByLabelText('Password'), password);

    expect(screen.getByText(label)).toBeInTheDocument();
  });

  it('says nothing about strength before anything is typed', () => {
    show();
    expect(screen.queryByText('weak')).not.toBeInTheDocument();
  });
});

describe('when the server refuses', () => {
  it('names the breach count, so the reason is actionable', async () => {
    api.registerUser.mockResolvedValue({ error: 'password_breached', count: 12345 });
    const user = show();

    await fillForm(user);

    expect(await screen.findByText(/appeared in 12,345 data breaches/)).toBeInTheDocument();
  });

  it('says "multiple" when it did not count them', async () => {
    api.registerUser.mockResolvedValue({ error: 'password_breached' });
    const user = show();

    await fillForm(user);

    expect(await screen.findByText(/appeared in multiple data breaches/)).toBeInTheDocument();
  });

  it('shows the reason the server gave', async () => {
    api.registerUser.mockResolvedValue({ error: 'email_taken', error_description: 'That address is in use.' });
    const user = show();

    await fillForm(user);

    expect(await screen.findByText('That address is in use.')).toBeInTheDocument();
  });

  it('falls back to a generic message when it gave none', async () => {
    api.registerUser.mockResolvedValue({ error: 'email_taken' });
    const user = show();

    await fillForm(user);

    expect(await screen.findByText('Registration failed.')).toBeInTheDocument();
  });

  it('says something generic when the request fails outright', async () => {
    api.registerUser.mockRejectedValue(new Error('500'));
    const user = show();

    await fillForm(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('says so when the destination it chose is refused', async () => {
    nav.safeNavigate.mockReturnValue(false);
    const user = show();

    await fillForm(user);

    expect(await screen.findByText('Could not complete registration. Please try again.')).toBeInTheDocument();
  });

  it('re-enables the button after a failure', async () => {
    api.registerUser.mockRejectedValue(new Error('500'));
    const user = show();

    await fillForm(user);

    await vi.waitFor(() => expect(screen.getByRole('button', { name: /Continue/ })).toBeEnabled());
  });
});

describe('verifying the address', () => {
  const toOtp = async () => {
    api.registerUser.mockResolvedValue({ requires_verification: true, session_id: 's1' });
    const user = show();
    await fillForm(user);
    await screen.findByText('Check your inbox.');
    return user;
  };

  it('names the address it sent the code to', async () => {
    await toOtp();
    expect(screen.getByText('ada@acme.test')).toBeInTheDocument();
  });

  it('verifies the code against the session the registration opened', async () => {
    const user = await toOtp();

    for (const [i, d] of [...'123456'].entries()) await user.type(cells()[i], d);
    await user.click(screen.getByRole('button', { name: /Verify/ }));

    await vi.waitFor(() => expect(api.verifyRegistrationOtp).toHaveBeenCalledWith('s1', '123456'));
    expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback');
  });

  it('will not submit a partial code', async () => {
    const user = await toOtp();

    await user.type(cells()[0], '1');

    expect(screen.getByRole('button', { name: /Verify/ })).toBeDisabled();
  });

  it('advances and retreats the cursor as digits are typed and deleted', async () => {
    const user = await toOtp();

    await user.type(cells()[0], '1');
    expect(cells()[1]).toHaveFocus();

    await user.type(cells()[1], '{Backspace}');
    await user.type(cells()[1], '{Backspace}');
    expect(cells()[0]).toHaveFocus();
  });

  it('accepts nothing but a digit per cell', async () => {
    const user = await toOtp();

    await user.type(cells()[0], 'a');

    expect(cells()[0]).toHaveValue('');
  });

  it('spreads a pasted code across the cells', async () => {
    const user = await toOtp();

    await user.click(cells()[0]);
    await user.paste('123456');

    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);
  });

  it('fills what it can of a short paste, and ignores one with no digits', async () => {
    const user = await toOtp();

    await user.click(cells()[0]);
    await user.paste('12');
    expect(cells()[2]).toHaveFocus();

    await user.paste('hello');
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '', '', '', '']);
  });

  it('clears the cells and says so when the code is wrong', async () => {
    api.verifyRegistrationOtp.mockResolvedValue({ error: 'invalid_code' });
    const user = await toOtp();
    for (const [i, d] of [...'000000'].entries()) await user.type(cells()[i], d);

    await user.click(screen.getByRole('button', { name: /Verify/ }));

    expect(await screen.findByText('Invalid or expired code.')).toBeInTheDocument();
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['', '', '', '', '', '']);
  });

  it('says something generic when the verification fails outright', async () => {
    api.verifyRegistrationOtp.mockRejectedValue(new Error('500'));
    const user = await toOtp();
    for (const [i, d] of [...'123456'].entries()) await user.type(cells()[i], d);

    await user.click(screen.getByRole('button', { name: /Verify/ }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('says so when the destination it chose is refused', async () => {
    nav.safeNavigate.mockReturnValue(false);
    const user = await toOtp();
    for (const [i, d] of [...'123456'].entries()) await user.type(cells()[i], d);

    await user.click(screen.getByRole('button', { name: /Verify/ }));

    expect(await screen.findByText('Could not complete registration. Please try again.')).toBeInTheDocument();
  });

  it('goes back to the form with what was typed still in it', async () => {
    const user = await toOtp();

    await user.click(screen.getByRole('button', { name: /Back/ }));

    expect(screen.getByLabelText('Email')).toHaveValue('ada@acme.test');
  });
});

describe('the way back to signing in', () => {
  it('carries the challenge, so the link does not start a fresh one', () => {
    show('abc');
    expect(screen.getByRole('link', { name: 'Sign in' }))
      .toHaveAttribute('href', '/login?login_challenge=abc');
  });
});
