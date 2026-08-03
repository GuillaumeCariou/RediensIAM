import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import MfaChallenge from './MfaChallenge';

/**
 * The second factor, and the last thing standing between a stolen password and a session.
 *
 * The load-bearing detail is `safeOptions` in the WebAuthn path: the options are rebuilt field by
 * field from the server's response rather than spread from it, and `userVerification` is pinned to
 * `'required'`. Spreading would let a hostile or mis-configured backend downgrade the assertion —
 * drop user verification, or smuggle in fields this flow never meant to honour.
 *
 * The other is that a successful verification only ever navigates through `safeNavigate`, so a
 * `redirect_to` the server chose cannot send the browser off-origin.
 */

const api = vi.hoisted(() => ({
  verifyTotp: vi.fn(), verifyBackupCode: vi.fn(), verifySmsOtp: vi.fn(),
  sendSmsOtp: vi.fn(), getWebAuthnOptions: vi.fn(), verifyWebAuthn: vi.fn(),
}));
vi.mock('../api', () => api);

const nav = vi.hoisted(() => ({ safeNavigate: vi.fn(() => true) }));
vi.mock('../safeNavigate', () => nav);

const OK = { redirect_to: 'https://app.test/callback' };

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  nav.safeNavigate.mockReturnValue(true);
  api.verifyTotp.mockResolvedValue(OK);
  api.verifySmsOtp.mockResolvedValue(OK);
  api.verifyBackupCode.mockResolvedValue(OK);
  api.sendSmsOtp.mockResolvedValue({});
});

afterEach(() => vi.unstubAllGlobals());

function show(mfaType?: string, phoneHint?: string) {
  if (mfaType) sessionStorage.setItem('mfa_type', mfaType);
  if (phoneHint) sessionStorage.setItem('mfa_phone_hint', phoneHint);
  const user = userEvent.setup();
  render(<MfaChallenge />);
  return user;
}

const cells = () => screen.getAllByLabelText(/^Digit \d of 6$/);
const typeOtp = async (user: ReturnType<typeof userEvent.setup>, code: string) => {
  for (const [i, d] of [...code].entries()) await user.type(cells()[i], d);
};

describe('which method it opens on', () => {
  it('uses the one the login step recorded', () => {
    show('sms', '+33•••••00');
    expect(screen.getByText('Code sent to +33•••••00. Expires in 5 minutes.')).toBeInTheDocument();
  });

  it('falls back to the authenticator app when nothing was recorded', () => {
    show();
    expect(screen.getByText('Enter 6-digit code')).toBeInTheDocument();
    expect(screen.queryByText(/Expires in 5 minutes/)).not.toBeInTheDocument();
  });

  it('offers every method, naming the phone it would text', () => {
    show('totp', '+33•••••00');

    expect(screen.getByText('Authenticator app')).toBeInTheDocument();
    expect(screen.getByText('Security key')).toBeInTheDocument();
    expect(screen.getByText('SMS to +33•••••00')).toBeInTheDocument();
    expect(screen.getByText('Backup code')).toBeInTheDocument();
  });

  it('describes SMS generically when no phone hint was recorded', () => {
    show('totp');
    expect(screen.getByText('SMS to your registered phone')).toBeInTheDocument();
  });
});

describe('the six-digit code', () => {
  it('verifies through the authenticator route and leaves for the redirect', async () => {
    const user = show('totp');

    await typeOtp(user, '123456');

    await vi.waitFor(() => expect(api.verifyTotp).toHaveBeenCalledWith('123456'));
    expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback');
  });

  it('verifies through the SMS route in SMS mode', async () => {
    const user = show('sms');

    await typeOtp(user, '123456');

    await vi.waitFor(() => expect(api.verifySmsOtp).toHaveBeenCalledWith('123456'));
    expect(api.verifyTotp).not.toHaveBeenCalled();
  });

  it('forgets what it knew about the factor once the challenge is passed', async () => {
    // Left behind, the hint would open the next challenge on a method this session no longer has.
    const user = show('sms', '+33•••••00');

    await typeOtp(user, '123456');

    await vi.waitFor(() => expect(sessionStorage.getItem('mfa_type')).toBeNull());
    expect(sessionStorage.getItem('mfa_phone_hint')).toBeNull();
  });

  it('advances the cursor as digits are typed, and back on delete', async () => {
    const user = show('totp');

    await user.type(cells()[0], '1');
    expect(cells()[1]).toHaveFocus();

    await user.type(cells()[1], '{Backspace}');
    await user.type(cells()[1], '{Backspace}');
    expect(cells()[0]).toHaveFocus();
  });

  it('accepts nothing but a digit per cell', async () => {
    const user = show('totp');

    await user.type(cells()[0], 'a');

    expect(cells()[0]).toHaveValue('');
  });

  it('spreads a pasted code across the cells and submits it', async () => {
    const user = show('totp');

    await user.click(cells()[0]);
    await user.paste('123456');

    await vi.waitFor(() => expect(api.verifyTotp).toHaveBeenCalledWith('123456'));
  });

  it('strips the punctuation out of a pasted code', async () => {
    const user = show('totp');

    await user.click(cells()[0]);
    await user.paste('123 456');

    await vi.waitFor(() => expect(api.verifyTotp).toHaveBeenCalledWith('123456'));
  });

  it('fills what it can of a short paste and waits for the rest', async () => {
    const user = show('totp');

    await user.click(cells()[0]);
    await user.paste('123');

    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '', '', '']);
    expect(api.verifyTotp).not.toHaveBeenCalled();
    expect(cells()[3]).toHaveFocus();
  });

  it('ignores a paste with no digits in it at all', async () => {
    const user = show('totp');

    await user.click(cells()[0]);
    await user.paste('hello');

    expect(cells()[0]).toHaveValue('');
  });

  it('clears the cells and says so when the code is refused', async () => {
    api.verifyTotp.mockResolvedValue({ error: 'invalid_code' });
    const user = show('totp');

    await typeOtp(user, '000000');

    expect(await screen.findByText('Invalid or expired code. Try again.')).toBeInTheDocument();
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['', '', '', '', '', '']);
    expect(cells()[0]).toHaveFocus();
  });

  it('says something generic when the request fails outright', async () => {
    api.verifyTotp.mockRejectedValue(new Error('500'));
    const user = show('totp');

    await typeOtp(user, '123456');

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('says so when the redirect the server chose is refused', async () => {
    // safeNavigate refuses anything off-origin; the sign-in must not silently appear to hang.
    nav.safeNavigate.mockReturnValue(false);
    const user = show('totp');

    await typeOtp(user, '123456');

    expect(await screen.findByText('Sign-in could not complete. Please try again.')).toBeInTheDocument();
  });

  it('does nothing when the server answers with neither an error nor a destination', async () => {
    api.verifyTotp.mockResolvedValue({});
    const user = show('totp');

    await typeOtp(user, '123456');

    await vi.waitFor(() => expect(api.verifyTotp).toHaveBeenCalled());
    expect(nav.safeNavigate).not.toHaveBeenCalled();
  });
});

describe('SMS', () => {
  it('resends the code and says it did', async () => {
    const user = show('sms', '+33•••••00');

    await user.click(screen.getByRole('button', { name: 'Resend code' }));

    await vi.waitFor(() => expect(api.sendSmsOtp).toHaveBeenCalled());
    expect(await screen.findByText('Code resent!')).toBeInTheDocument();
  });
});

describe('backup codes', () => {
  it('takes a sixteen-character code and verifies it', async () => {
    const user = show('totp');
    await user.click(screen.getByText('Backup code'));

    await user.type(screen.getByRole('textbox'), 'abcd1234efgh5678');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    await vi.waitFor(() => expect(api.verifyBackupCode).toHaveBeenCalledWith('ABCD1234EFGH5678'));
  });

  it('normalises what is typed and refuses to submit a partial code', async () => {
    const user = show('totp');
    await user.click(screen.getByText('Backup code'));

    await user.type(screen.getByRole('textbox'), 'abcd-1234');

    expect(screen.getByRole('textbox')).toHaveValue('ABCD1234');
    expect(screen.getByRole('button', { name: 'Verify' })).toBeDisabled();
  });

  it('says so, in its own words, when the code is refused', async () => {
    api.verifyBackupCode.mockResolvedValue({ error: 'invalid' });
    const user = show('totp');
    await user.click(screen.getByText('Backup code'));
    await user.type(screen.getByRole('textbox'), 'abcd1234efgh5678');

    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText('Invalid backup code. Check the code and try again.')).toBeInTheDocument();
  });
});

describe('the passkey', () => {
  const OPTIONS = {
    challenge: 'AAAA', timeout: 30000, rpId: 'acme.test',
    allowCredentials: [{ id: 'BBBB', type: 'public-key', transports: ['usb'] }],
  };
  const ASSERTION = {
    id: 'cred-1', rawId: new Uint8Array([1, 2, 3]).buffer, type: 'public-key',
    response: {
      authenticatorData: new Uint8Array([4]).buffer,
      clientDataJSON: new Uint8Array([5]).buffer,
      signature: new Uint8Array([6]).buffer,
      userHandle: new Uint8Array([7]).buffer,
    },
  };
  /** What the browser throws when the operator dismisses the prompt or it times out. */
  const cancelled = () => Object.assign(new Error('cancelled'), { name: 'NotAllowedError' });
  const withCredentials = (get: ReturnType<typeof vi.fn>) =>
    vi.stubGlobal('navigator', new Proxy(globalThis.navigator, {
      get: (t, k) => k === 'credentials' ? { get } : Reflect.get(t, k),
    }));

  it('prompts as soon as the method is chosen, and only once', async () => {
    const get = vi.fn(async () => ASSERTION);
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    api.verifyWebAuthn.mockResolvedValue(OK);
    show('webauthn');

    await vi.waitFor(() => expect(get).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback'));
  });

  it('never lets the server relax user verification', async () => {
    const get = vi.fn(async () => ASSERTION);
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue({ ...OPTIONS, userVerification: 'discouraged', extensions: { evil: true } });
    api.verifyWebAuthn.mockResolvedValue(OK);
    show('webauthn');

    await vi.waitFor(() => expect(get).toHaveBeenCalled());
    const { publicKey } = get.mock.calls[0][0] as { publicKey: Record<string, unknown> };
    expect(publicKey['userVerification']).toBe('required');
    // Nothing the server added travels through: the options are an allowlist, not a spread.
    expect(publicKey).not.toHaveProperty('extensions');
    expect(Object.keys(publicKey).sort())
      .toEqual(['allowCredentials', 'challenge', 'rpId', 'timeout', 'userVerification']);
  });

  it('defaults the timeout and drops a non-string rpId rather than passing them on', async () => {
    const get = vi.fn(async () => ASSERTION);
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue({ challenge: 'AAAA', timeout: 'soon', rpId: 42 });
    api.verifyWebAuthn.mockResolvedValue(OK);
    show('webauthn');

    await vi.waitFor(() => expect(get).toHaveBeenCalled());
    const { publicKey } = get.mock.calls[0][0] as { publicKey: Record<string, unknown> };
    expect(publicKey['timeout']).toBe(60000);
    expect(publicKey['rpId']).toBeUndefined();
    expect(publicKey['allowCredentials']).toBeUndefined();
  });

  it('sends the assertion base64url-encoded', async () => {
    const get = vi.fn(async () => ASSERTION);
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    api.verifyWebAuthn.mockResolvedValue(OK);
    show('webauthn');

    await vi.waitFor(() => expect(api.verifyWebAuthn).toHaveBeenCalled());
    expect(api.verifyWebAuthn.mock.calls[0][0]).toMatchObject({
      id: 'cred-1', type: 'public-key', rawId: 'AQID',
      response: { authenticatorData: 'BA', clientDataJSON: 'BQ', signature: 'Bg', userHandle: 'Bw' },
    });
  });

  it('sends a null user handle rather than inventing one', async () => {
    const get = vi.fn(async () => ({ ...ASSERTION, response: { ...ASSERTION.response, userHandle: null } }));
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    api.verifyWebAuthn.mockResolvedValue(OK);
    show('webauthn');

    await vi.waitFor(() => expect(api.verifyWebAuthn).toHaveBeenCalled());
    expect((api.verifyWebAuthn.mock.calls[0][0] as { response: { userHandle: unknown } })
      .response.userHandle).toBeNull();
  });

  it.each([
    ['the options cannot be fetched', () => api.getWebAuthnOptions.mockResolvedValue({ error: 'no' }),
      'Failed to get passkey options.'],
    ['the verification is refused', () => {
      api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
      api.verifyWebAuthn.mockResolvedValue({ error: 'bad_signature' });
    }, 'Passkey verification failed. Try again.'],
  ])('says so when %s', async (_n, arrange, message) => {
    withCredentials(vi.fn(async () => ASSERTION));
    arrange();
    show('webauthn');

    expect(await screen.findByText(message)).toBeInTheDocument();
  });

  it('tells the operator plainly when they dismiss the browser prompt', async () => {
    withCredentials(vi.fn(async () => { throw cancelled(); }));
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    show('webauthn');

    expect(await screen.findByText('Passkey prompt was cancelled or timed out.')).toBeInTheDocument();
  });

  it('suggests another method when it fails for any other reason', async () => {
    const get = vi.fn(async () => { throw new Error('boom'); });
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    show('webauthn');

    expect(await screen.findByText('Something went wrong. Try a different method.')).toBeInTheDocument();
  });

  it('says so when the browser returns no credential at all', async () => {
    const get = vi.fn(async () => null);
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    show('webauthn');

    expect(await screen.findByText('No credential returned.')).toBeInTheDocument();
  });

  it('can be retried by hand once the first attempt has finished', async () => {
    const get = vi.fn(async () => { throw cancelled(); });
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    const user = show('webauthn');
    await screen.findByText('Passkey prompt was cancelled or timed out.');

    await user.click(screen.getByRole('button', { name: 'Use passkey' }));

    await vi.waitFor(() => expect(get).toHaveBeenCalledTimes(2));
  });

  it('prompts again after leaving the method and coming back', async () => {
    const get = vi.fn(async () => { throw cancelled(); });
    withCredentials(get);
    api.getWebAuthnOptions.mockResolvedValue(OPTIONS);
    const user = show('webauthn');
    await vi.waitFor(() => expect(get).toHaveBeenCalledOnce());

    await user.click(screen.getByText('Authenticator app'));
    await user.click(screen.getByText('Security key'));

    await vi.waitFor(() => expect(get).toHaveBeenCalledTimes(2));
  });
});

describe('switching between methods', () => {
  it('clears whatever was half-typed, and the last complaint', async () => {
    api.verifyTotp.mockResolvedValue({ error: 'invalid_code' });
    const user = show('totp');
    await typeOtp(user, '000000');
    await screen.findByText('Invalid or expired code. Try again.');
    await user.type(cells()[0], '9');

    await user.click(screen.getByText('Backup code'));
    await user.click(screen.getByText('Authenticator app'));

    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['', '', '', '', '', '']);
    expect(screen.queryByText('Invalid or expired code. Try again.')).not.toBeInTheDocument();
  });
});

describe('going back', () => {
  it('returns to whatever came before, rather than to a guessed URL', async () => {
    const back = vi.fn();
    vi.spyOn(globalThis.history, 'back').mockImplementation(back);
    const user = show('totp');

    await user.click(screen.getByRole('button', { name: 'Back' }));

    expect(back).toHaveBeenCalledOnce();
    vi.restoreAllMocks();
  });
});
