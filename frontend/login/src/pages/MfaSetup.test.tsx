import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import MfaSetup from './MfaSetup';

/**
 * Enrolment forced mid-login, for a project that requires a second factor. It authenticates off
 * the pending-MFA session cookie rather than a bearer, so the page is only reachable while that
 * session exists — anything else is bounced to /login rather than shown a half-built form.
 *
 * The backup codes are shown exactly once, which is why the step that shows them will not move on
 * until the visitor says they have them.
 */

const api = vi.hoisted(() => ({ setupTotp: vi.fn(), confirmTotp: vi.fn() }));
vi.mock('../api', () => api);

const nav = vi.hoisted(() => ({ safeNavigate: vi.fn(() => true) }));
vi.mock('../safeNavigate', () => nav);

const navigate = vi.hoisted(() => vi.fn());
vi.mock('react-router', async orig => ({
  ...(await orig<typeof import('react-router')>()),
  useNavigate: () => navigate,
}));

const SETUP = { otpauth_url: 'otpauth://totp/RediensIAM:ada', secret: 'JBSWY3DPEHPK3PXP' };

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  sessionStorage.setItem('mfa_setup_challenge', 'c1');
  nav.safeNavigate.mockReturnValue(true);
  api.setupTotp.mockResolvedValue(SETUP);
  api.confirmTotp.mockResolvedValue({ redirect_to: 'https://app.test/callback' });
});

function show() {
  const user = userEvent.setup();
  render(<MemoryRouter><MfaSetup /></MemoryRouter>);
  return user;
}

const cells = () => screen.getAllByLabelText(/^Digit \d of 6$/);
const toVerify = async (user: ReturnType<typeof userEvent.setup>) => {
  await screen.findByText('Set up two-factor.');
  await user.click(screen.getByRole('button', { name: /Continue|Next/ }));
};
const typeCode = async (user: ReturnType<typeof userEvent.setup>, code: string) => {
  for (const [i, d] of [...code].entries()) await user.type(cells()[i], d);
};

describe('getting in', () => {
  it('starts the enrolment and shows the seed', async () => {
    show();

    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument();
    expect(api.setupTotp).toHaveBeenCalledOnce();
  });

  it('bounces to the login page without a pending session', async () => {
    sessionStorage.clear();
    show();

    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith('/login'));
    expect(api.setupTotp).not.toHaveBeenCalled();
  });

  it.each([
    ['the server refuses', () => api.setupTotp.mockResolvedValue({ error: 'no_session' })],
    ['it answers without a seed', () => api.setupTotp.mockResolvedValue({ otpauth_url: 'x' })],
    ['the request fails outright', () => api.setupTotp.mockRejectedValue(new Error('500'))],
  ])('bounces to the login page when %s', async (_n, arrange) => {
    arrange();
    show();

    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith('/login'));
  });

  it('shows nothing about the factor until the seed has arrived', () => {
    api.setupTotp.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.getByText('Setting up two-factor authentication…')).toBeInTheDocument();
  });
});

describe('the seed', () => {
  it('can be copied, and says it was', async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', new Proxy(globalThis.navigator, {
      get: (t, k) => k === 'clipboard' ? { writeText } : Reflect.get(t, k),
    }));
    const user = show();
    await screen.findByText('JBSWY3DPEHPK3PXP');

    await user.click(screen.getByRole('button', { name: /Copy/ }));

    expect(writeText).toHaveBeenCalledWith('JBSWY3DPEHPK3PXP');
    // The seed's copy button confirms with a tick rather than a word.
    expect(await screen.findByRole('button', { name: '✓' })).toBeInTheDocument();
    vi.unstubAllGlobals();
  });
});

describe('confirming the code', () => {
  it('confirms it and leaves for the destination when there are no codes to show', async () => {
    const user = show();
    await toVerify(user);

    await typeCode(user, '123456');
    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));

    await vi.waitFor(() => expect(api.confirmTotp).toHaveBeenCalledWith('123456'));
    expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback');
  });

  it('forgets the pending-enrolment session once it is done', async () => {
    sessionStorage.setItem('mfa_setup_user', 'ada');
    const user = show();
    await toVerify(user);

    await typeCode(user, '123456');
    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));

    await vi.waitFor(() => expect(sessionStorage.getItem('mfa_setup_challenge')).toBeNull());
    expect(sessionStorage.getItem('mfa_setup_user')).toBeNull();
  });

  it('will not submit a partial code', async () => {
    const user = show();
    await toVerify(user);

    await typeCode(user, '12');

    expect(screen.getByRole('button', { name: /Verify|Confirm|Enable/ })).toBeDisabled();
  });

  it('advances and retreats the cursor, and takes only digits', async () => {
    const user = show();
    await toVerify(user);

    await user.type(cells()[0], 'a');
    expect(cells()[0]).toHaveValue('');

    await user.type(cells()[0], '1');
    expect(cells()[1]).toHaveFocus();

    await user.type(cells()[1], '{Backspace}');
    await user.type(cells()[1], '{Backspace}');
    expect(cells()[0]).toHaveFocus();
  });

  it('spreads a pasted code, and ignores one with no digits', async () => {
    const user = show();
    await toVerify(user);

    await user.click(cells()[0]);
    await user.paste('123456');
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);

    await user.paste('hello');
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['1', '2', '3', '4', '5', '6']);
  });

  it('leaves the cursor on the next empty cell after a short paste', async () => {
    const user = show();
    await toVerify(user);

    await user.click(cells()[0]);
    await user.paste('12');

    expect(cells()[2]).toHaveFocus();
  });

  it('clears the cells and says so when the code is wrong', async () => {
    api.confirmTotp.mockResolvedValue({ error: 'invalid_code' });
    const user = show();
    await toVerify(user);
    await typeCode(user, '000000');

    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));

    expect(await screen.findByText('Incorrect code. Check your authenticator app and try again.'))
      .toBeInTheDocument();
    expect(cells().map(c => (c as HTMLInputElement).value)).toEqual(['', '', '', '', '', '']);
  });

  it('says something generic when the request fails outright', async () => {
    api.confirmTotp.mockRejectedValue(new Error('500'));
    const user = show();
    await toVerify(user);
    await typeCode(user, '123456');

    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('says so when the destination it chose is refused', async () => {
    nav.safeNavigate.mockReturnValue(false);
    const user = show();
    await toVerify(user);
    await typeCode(user, '123456');

    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));

    expect(await screen.findByText('Sign-in could not complete. Please try again.')).toBeInTheDocument();
  });

  it('goes back to the seed', async () => {
    const user = show();
    await toVerify(user);

    await user.click(screen.getByRole('button', { name: /Back/ }));

    expect(screen.getByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument();
  });
});

describe('the backup codes', () => {
  const toBackup = async () => {
    api.confirmTotp.mockResolvedValue({
      backup_codes: ['code-one', 'code-two'], redirect_to: 'https://app.test/callback',
    });
    const user = show();
    await toVerify(user);
    await typeCode(user, '123456');
    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));
    await screen.findByText('Save your backup codes.');
    return user;
  };

  it('shows them, and warns they will not be shown again', async () => {
    await toBackup();

    expect(screen.getByText('code-one')).toBeInTheDocument();
    expect(screen.getByText('code-two')).toBeInTheDocument();
    expect(screen.getByText('You will not see these again. Copy them before continuing.'))
      .toBeInTheDocument();
  });

  it('does not navigate away on its own', async () => {
    // They are shown once; leaving before the visitor has them is how they are lost.
    await toBackup();
    expect(nav.safeNavigate).not.toHaveBeenCalled();
  });

  it('copies them all at once', async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', new Proxy(globalThis.navigator, {
      get: (t, k) => k === 'clipboard' ? { writeText } : Reflect.get(t, k),
    }));
    const user = await toBackup();

    await user.click(screen.getByRole('button', { name: 'Copy all codes' }));

    expect(writeText).toHaveBeenCalledWith('code-one\ncode-two');
    expect(await screen.findByText('✓ Copied')).toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it('continues to the destination once the visitor says they have them', async () => {
    const user = await toBackup();

    await user.click(screen.getByRole('button', { name: /I've saved my codes/ }));

    expect(nav.safeNavigate).toHaveBeenCalledWith('https://app.test/callback');
  });

  it('falls back to the login page when there is no destination to go to', async () => {
    api.confirmTotp.mockResolvedValue({ backup_codes: ['code-one'] });
    const user = show();
    await toVerify(user);
    await typeCode(user, '123456');
    await user.click(screen.getByRole('button', { name: /Verify|Confirm|Enable/ }));
    await screen.findByText('Save your backup codes.');

    await user.click(screen.getByRole('button', { name: /I've saved my codes/ }));

    expect(navigate).toHaveBeenCalledWith('/login');
  });

  it('falls back to the login page when the destination is refused', async () => {
    nav.safeNavigate.mockReturnValue(false);
    const user = await toBackup();

    await user.click(screen.getByRole('button', { name: /I've saved my codes/ }));

    expect(navigate).toHaveBeenCalledWith('/login');
  });
});
