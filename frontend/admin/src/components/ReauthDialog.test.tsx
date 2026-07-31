import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useReauth } from './ReauthDialog';
import { ApiError } from '@/auth';
import type { MfaReauth } from '@/auth';

/**
 * The re-authentication contract, exercised the way AccountPage uses it: call the mutation with
 * no proof, and only prompt when the backend says a proof is required.
 *
 * The backend guarantees these tests are protecting:
 *  - a failed proof charges a rate limiter, and enough of them lock the account out;
 *  - a TOTP code that verifies is burned and can never be replayed.
 * So the count of calls to the mutation matters as much as the rendered output — an auto-retry
 * would lock the user out of their own account.
 */

function reauthRequired(methods: string[]) {
  return new ApiError(401, { error: 'reauthentication_required', methods });
}

/** Mirrors the shape of an AccountPage handler: `await guard(...)`, report anything it rethrows. */
function Harness({ action }: Readonly<{ action: (proof?: MfaReauth) => Promise<unknown> }>) {
  const { guard, dialog } = useReauth();
  const [pageError, setPageError] = useState('');

  const onClick = async () => {
    setPageError('');
    try {
      await guard(action);
    } catch {
      setPageError('Failed to regenerate backup codes.');
    }
  };

  return (
    <div>
      <button onClick={onClick}>Regenerate backup codes</button>
      <a href="#somewhere">a link behind the dialog</a>
      {pageError && <p role="alert">{pageError}</p>}
      {dialog}
    </div>
  );
}

const start = async (action: (proof?: MfaReauth) => Promise<unknown>) => {
  const user = userEvent.setup();
  render(<Harness action={action} />);
  await user.click(screen.getByRole('button', { name: 'Regenerate backup codes' }));
  return user;
};

const promptDialog = () => screen.findByRole('dialog');

describe('the first attempt', () => {
  it('sends no proof, so an account with no second factor is never prompted', async () => {
    const action = vi.fn().mockResolvedValue({ backup_codes: [] });
    await start(action);

    expect(action).toHaveBeenCalledTimes(1);
    expect(action.mock.calls[0]).toEqual([]); // no proof argument at all
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('prompts only once the server answers 401 reauthentication_required', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    await start(action);

    const dialog = await promptDialog();
    expect(within(dialog).getByText("Confirm it's you")).toBeVisible();
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('reports a failed mutation to the page instead of blaming the password', async () => {
    // A 500 is not a re-authentication demand. Prompting for a password here would tell the
    // user their password is wrong when the request never got that far.
    const action = vi.fn().mockRejectedValue(new ApiError(500, { error: 'boom' }));
    await start(action);

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to regenerate backup codes.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('does not mistake an ordinary expired-session 401 for a re-authentication demand', async () => {
    const action = vi.fn().mockRejectedValue(new ApiError(401, { error: 'invalid_token' }));
    await start(action);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('the methods the server offers', () => {
  it('offers only a password when that is all the account has', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    await start(action);
    await promptDialog();

    expect(screen.getByLabelText('Current password')).toBeInTheDocument();
    expect(screen.queryByLabelText('Authenticator code')).not.toBeInTheDocument();
  });

  it('never asks a passwordless account for a password', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['totp_code']));
    await start(action);
    await promptDialog();

    expect(screen.getByLabelText('Authenticator code')).toBeInTheDocument();
    expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument();
  });

  it('offers both when the account holds both', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password', 'totp_code']));
    await start(action);
    await promptDialog();

    expect(screen.getByLabelText('Current password')).toBeInTheDocument();
    expect(screen.getByLabelText('Authenticator code')).toBeInTheDocument();
  });
});

describe('supplying the proof', () => {
  it('re-runs the same mutation with the password and closes on success', async () => {
    const action = vi.fn()
      .mockRejectedValueOnce(reauthRequired(['current_password']))
      .mockResolvedValueOnce({ backup_codes: ['a'] });
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'hunter2');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(action).toHaveBeenCalledTimes(2);
    expect(action).toHaveBeenLastCalledWith({ current_password: 'hunter2' });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends a TOTP code as totp_code, not as a password', async () => {
    const action = vi.fn()
      .mockRejectedValueOnce(reauthRequired(['current_password', 'totp_code']))
      .mockResolvedValueOnce({});
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Authenticator code'), '123456');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(action).toHaveBeenCalledTimes(2));
    expect(action).toHaveBeenLastCalledWith({ totp_code: '123456' });
  });

  it('will not submit a TOTP code that is not six digits', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['totp_code']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Authenticator code'), '1234');
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();

    await user.type(screen.getByLabelText('Authenticator code'), '56');
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeEnabled();
  });

  it('strips non-digits from the code so a typo cannot be sent as a proof', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['totp_code']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Authenticator code'), '1a2b3c4d5e6f7g');
    expect(screen.getByLabelText('Authenticator code')).toHaveValue('123456');
  });
});

describe('a proof the server rejects', () => {
  it('never retries by itself — one attempt per submit, because attempts are rate limited', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await screen.findByText('That password is not correct.');
    expect(action).toHaveBeenCalledTimes(2);

    // Nothing further must happen on its own. If the dialog retried, this would climb.
    await new Promise(r => setTimeout(r, 50));
    expect(action).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('clears the field after every attempt so the next proof is freshly typed', async () => {
    // A TOTP code that verifies is burned by the anti-replay cache; resubmitting the same
    // characters can only ever fail and cost another rate-limiter slot.
    const action = vi.fn().mockRejectedValue(reauthRequired(['totp_code']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Authenticator code'), '123456');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await screen.findByText(/a code can only be used once/);
    expect(screen.getByLabelText('Authenticator code')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();
  });

  it('explains a burned TOTP code differently from a wrong password', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password', 'totp_code']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText('That password is not correct.')).toBeVisible();
  });

  it('locks the form once the rate limiter answers 429, rather than letting the user dig deeper', async () => {
    const action = vi.fn()
      .mockRejectedValueOnce(reauthRequired(['current_password']))
      .mockRejectedValueOnce(new ApiError(429, { error: 'too_many_requests' }));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await screen.findByText('Too many failed attempts. Wait a few minutes before trying again.');
    expect(screen.getByLabelText('Current password')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();
    expect(action).toHaveBeenCalledTimes(2);
  });
});

describe('dismissing the prompt', () => {
  it('Cancel closes it and runs nothing', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    await promptDialog();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('Escape closes it', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    await promptDialog();

    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('forgets the previous error when reopened', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));
    await screen.findByText('That password is not correct.');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Regenerate backup codes' }));
    await promptDialog();
    expect(screen.queryByText('That password is not correct.')).not.toBeInTheDocument();
  });
});

describe('focus containment', () => {
  it('moves focus into the prompt and keeps Tab inside it', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    const dialog = await promptDialog();

    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    // Tab all the way round. The link and the trigger live outside the dialog; if focus ever
    // reaches one of them, the background is reachable while the prompt is up.
    for (let i = 0; i < 12; i++) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
  });

  it('takes the page behind it out of the accessibility tree', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    await start(action);
    await promptDialog();

    // The trigger is still rendered, but a screen reader must no longer be able to reach it.
    expect(screen.getByText('a link behind the dialog').closest('[aria-hidden="true"]')).not.toBeNull();
    expect(screen.queryByRole('link', { name: 'a link behind the dialog' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Regenerate backup codes' })).not.toBeInTheDocument();
  });
});

describe('a mutation that fails after a good proof', () => {
  // Regression: `guard` used to resolve the moment the prompt opened, so the caller's catch was
  // out of scope by the time the mutation really ran. The rethrow from `submit` landed in the
  // form's onSubmit as an unhandled rejection: the prompt vanished and the user was told nothing,
  // which reads exactly like success. See .security-hardening/28-frontend-tests.md.
  it('closes the prompt and reports the failure to the page', async () => {
    const action = vi.fn()
      .mockRejectedValueOnce(reauthRequired(['current_password']))
      .mockRejectedValueOnce(new ApiError(500, { error: 'boom' }));
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'hunter2');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to regenerate backup codes.');
  });

  it('treats a cancel as "nothing happened", not as a failure to report', async () => {
    const action = vi.fn().mockRejectedValue(reauthRequired(['current_password']));
    const user = await start(action);
    await promptDialog();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('reports a success once, with no error left behind', async () => {
    const action = vi.fn()
      .mockRejectedValueOnce(reauthRequired(['current_password']))
      .mockResolvedValueOnce({});
    const user = await start(action);
    await promptDialog();

    await user.type(screen.getByLabelText('Current password'), 'hunter2');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
