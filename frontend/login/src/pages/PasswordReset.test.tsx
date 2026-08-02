import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import PasswordReset from './PasswordReset';

/**
 * Reachable by anyone with the link and no session at all, which is why every step is deliberately
 * vague about whether the address exists: "No account found or verification not configured" is the
 * same answer for a typo and for a real address, so the page cannot be used to enumerate users.
 */

const api = vi.hoisted(() => ({
  requestPasswordReset: vi.fn(), verifyPasswordResetOtp: vi.fn(), confirmPasswordReset: vi.fn(),
}));
vi.mock('../api', () => api);

beforeEach(() => {
  vi.clearAllMocks();
  api.requestPasswordReset.mockResolvedValue({ session_id: 's1' });
  api.verifyPasswordResetOtp.mockResolvedValue({ reset_token: 't1' });
  api.confirmPasswordReset.mockResolvedValue({});
});

function show(projectId = 'p1') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[`/password-reset?project_id=${projectId}`]}>
      <PasswordReset />
    </MemoryRouter>,
  );
  return user;
}

const cells = () => screen.getAllByLabelText(/^Digit \d of 6$/);

const askForCode = async (user: ReturnType<typeof userEvent.setup>, email = 'ada@acme.test') => {
  await user.type(screen.getByLabelText('Email'), email);
  await user.click(screen.getByRole('button', { name: /Send code/ }));
};
const enterCode = async (user: ReturnType<typeof userEvent.setup>, code = '123456') => {
  for (const [i, d] of [...code].entries()) await user.type(cells()[i], d);
  await user.click(screen.getByRole('button', { name: /Verify/ }));
};
const setNewPassword = async (user: ReturnType<typeof userEvent.setup>, pw = 'hunter2hunter2', confirm = pw) => {
  await user.type(screen.getByLabelText('New password'), pw);
  await user.type(screen.getByLabelText('Confirm new password'), confirm);
  await user.click(screen.getByRole('button', { name: /Reset password|Set password|Continue/ }));
};

describe('asking for a code', () => {
  it('asks against the project the link named', async () => {
    const user = show('proj-9');

    await askForCode(user);

    await vi.waitFor(() => expect(api.requestPasswordReset).toHaveBeenCalledWith('proj-9', 'ada@acme.test'));
    expect(await screen.findByText('Enter the 6-digit code from your email.')).toBeInTheDocument();
  });

  it('says the same thing whether the address exists or not', async () => {
    // Anything more specific turns this page into a user-enumeration oracle.
    api.requestPasswordReset.mockResolvedValue({});
    const user = show();

    await askForCode(user, 'nobody@acme.test');

    expect(await screen.findByText('No account found or verification not configured.')).toBeInTheDocument();
  });

  it('says so when the project has reset turned off', async () => {
    api.requestPasswordReset.mockResolvedValue({ error: 'reset_disabled' });
    const user = show();

    await askForCode(user);

    expect(await screen.findByText('Password reset is not available for this project.')).toBeInTheDocument();
  });

  it('says something generic when the request fails outright', async () => {
    api.requestPasswordReset.mockRejectedValue(new Error('500'));
    const user = show();

    await askForCode(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('requires an address, and offers a way back to signing in', () => {
    show();

    expect(screen.getByLabelText('Email')).toBeRequired();
    expect(screen.getByRole('link', { name: /Back to sign in/ })).toHaveAttribute('href', '/login');
  });
});

describe('entering the code', () => {
  const toOtp = async () => {
    const user = show();
    await askForCode(user);
    await screen.findByText('Enter the 6-digit code from your email.');
    return user;
  };

  it('names the address it went to', async () => {
    await toOtp();
    expect(screen.getByText('ada@acme.test')).toBeInTheDocument();
  });

  it('verifies against the session the request opened', async () => {
    const user = await toOtp();

    await enterCode(user);

    await vi.waitFor(() => expect(api.verifyPasswordResetOtp).toHaveBeenCalledWith('s1', '123456'));
    expect(await screen.findByText('Create your new password.')).toBeInTheDocument();
  });

  it('will not submit a partial code', async () => {
    const user = await toOtp();

    await user.type(cells()[0], '1');

    expect(screen.getByRole('button', { name: /Verify/ })).toBeDisabled();
  });

  it('advances and retreats the cursor, and takes only digits', async () => {
    const user = await toOtp();

    await user.type(cells()[0], 'a');
    expect(cells()[0]).toHaveValue('');

    await user.type(cells()[0], '1');
    expect(cells()[1]).toHaveFocus();

    await user.type(cells()[1], '{Backspace}');
    await user.type(cells()[1], '{Backspace}');
    expect(cells()[0]).toHaveFocus();
  });

  it('spreads a pasted code, and ignores one with no digits', async () => {
    const user = await toOtp();

    await user.click(cells()[0]);
    await user.paste('123456');
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);

    await user.paste('hello');
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);
  });

  it('leaves the cursor on the next empty cell after a short paste', async () => {
    const user = await toOtp();

    await user.click(cells()[0]);
    await user.paste('12');

    expect(cells()[2]).toHaveFocus();
  });

  it('clears the cells and says so when the code is wrong', async () => {
    api.verifyPasswordResetOtp.mockResolvedValue({ error: 'invalid_code' });
    const user = await toOtp();

    await enterCode(user, '000000');

    expect(await screen.findByText('Invalid or expired code.')).toBeInTheDocument();
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['', '', '', '', '', '']);
  });

  it('says something generic when the verification fails outright', async () => {
    api.verifyPasswordResetOtp.mockRejectedValue(new Error('500'));
    const user = await toOtp();

    await enterCode(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('goes back to the address step', async () => {
    const user = await toOtp();

    await user.click(screen.getByRole('button', { name: /Back/ }));

    expect(screen.getByLabelText('Email')).toBeInTheDocument();
  });
});

describe('setting the new password', () => {
  const toPassword = async () => {
    const user = show();
    await askForCode(user);
    await screen.findByText('Enter the 6-digit code from your email.');
    await enterCode(user);
    await screen.findByText('Create your new password.');
    return user;
  };

  it('confirms it against the token the code earned', async () => {
    const user = await toPassword();

    await setNewPassword(user);

    await vi.waitFor(() => expect(api.confirmPasswordReset).toHaveBeenCalledWith('t1', 'hunter2hunter2'));
    expect(await screen.findByText('Password updated successfully. You can now sign in.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/login');
  });

  it('compares the two fields before sending anything', async () => {
    const user = await toPassword();

    await setNewPassword(user, 'hunter2hunter2', 'something-else');

    expect(await screen.findByText('Passwords do not match.')).toBeInTheDocument();
    expect(api.confirmPasswordReset).not.toHaveBeenCalled();
  });

  it('demands one long enough to be a password', async () => {
    await toPassword();
    expect(screen.getByLabelText('New password')).toHaveAttribute('minlength', '8');
  });

  it('names the breach count, so the reason is actionable', async () => {
    api.confirmPasswordReset.mockResolvedValue({ error: 'password_breached', count: 12345 });
    const user = await toPassword();

    await setNewPassword(user);

    expect(await screen.findByText(/appeared in 12,345 data breaches/)).toBeInTheDocument();
  });

  it('says "multiple" when it did not count them', async () => {
    api.confirmPasswordReset.mockResolvedValue({ error: 'password_breached' });
    const user = await toPassword();

    await setNewPassword(user);

    expect(await screen.findByText(/appeared in multiple data breaches/)).toBeInTheDocument();
  });

  it('tells the visitor to start over when the token has expired', async () => {
    api.confirmPasswordReset.mockResolvedValue({ error: 'token_expired' });
    const user = await toPassword();

    await setNewPassword(user);

    expect(await screen.findByText('Reset link expired. Please start over.')).toBeInTheDocument();
  });

  it('says something generic when the request fails outright', async () => {
    api.confirmPasswordReset.mockRejectedValue(new Error('500'));
    const user = await toPassword();

    await setNewPassword(user);

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });
});
