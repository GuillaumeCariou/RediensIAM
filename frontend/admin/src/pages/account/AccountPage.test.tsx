import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import AccountPage from './AccountPage';
import { ApiError } from '@/auth';

/**
 * The one page every signed-in operator can reach, and the only place a second factor is enrolled
 * or removed. Two rules run through all of it:
 *
 *  - every mutation that adds, replaces or destroys a factor goes through the re-authentication
 *    guard. A bearer token alone must not be enough to overwrite the victim's second factor, nor
 *    to enrol the attacker's alongside it.
 *  - a secret shown once — the TOTP seed, the backup codes — must come from the server's response
 *    and never be reconstructed or re-displayed afterwards.
 */

const api = vi.hoisted(() => ({
  getMe: vi.fn(), updateMe: vi.fn(), changePassword: vi.fn(),
  getMfaStatus: vi.fn(), setupTotp: vi.fn(), confirmTotp: vi.fn(), regenerateBackupCodes: vi.fn(),
  getSessions: vi.fn(), revokeSession: vi.fn(), revokeAllSessions: vi.fn(),
  setupPhone: vi.fn(), verifyPhone: vi.fn(), removePhone: vi.fn(),
  beginWebAuthnRegistration: vi.fn(), completeWebAuthnRegistration: vi.fn(),
  listWebAuthnCredentials: vi.fn(), deleteWebAuthnCredential: vi.fn(),
  getSocialAccounts: vi.fn(), unlinkSocialAccount: vi.fn(),
}));
vi.mock('@/api', () => api);

const ME = {
  id: '0123456789abcdef', username: 'ada', discriminator: '0001', email: 'ada@acme.test',
  display_name: 'Ada Lovelace', email_verified: true, totp_enabled: false,
  last_login_at: '2026-03-04T05:06:07Z', roles: ['org_admin'],
  org_id: 'o1', project_id: 'p1', new_device_alerts_enabled: true,
};

const MFA_OFF = { totp_enabled: false, backup_codes_remaining: 0, phone_verified: false };
const MFA_ON = { totp_enabled: true, backup_codes_remaining: 3, phone_verified: true };

beforeEach(() => {
  vi.clearAllMocks();
  api.getMe.mockResolvedValue(ME);
  api.updateMe.mockResolvedValue({});
  api.getMfaStatus.mockResolvedValue(MFA_OFF);
  api.getSessions.mockResolvedValue([]);
  api.getSocialAccounts.mockResolvedValue([]);
  api.listWebAuthnCredentials.mockResolvedValue([]);
});

function show() {
  const user = userEvent.setup();
  render(<MemoryRouter><AccountPage /></MemoryRouter>);
  return user;
}

const loaded = () => screen.findByRole('tab', { name: /Profile/ });
const openTab = async (user: Awaited<ReturnType<typeof show>>, name: RegExp) => {
  await loaded();
  await user.click(screen.getByRole('tab', { name }));
};

describe('the page', () => {
  it('names the operator and shortens their id', async () => {
    show();

    await loaded();
    expect(screen.getByText('ada#0001 · ada@acme.test')).toBeInTheDocument();
    expect(screen.getByText('01234567…')).toBeInTheDocument();
  });

  it('shows placeholders, and no tabs, while loading', () => {
    api.getMe.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(2);
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });

  it('says so when the account cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getMe.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('Failed to load account.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('the profile tab', () => {
  it('shows the identity the operator cannot change here', async () => {
    show();
    await loaded();

    // The handle is split across a <span> so the discriminator can be dimmed.
    expect(screen.getByText('ada').parentElement).toHaveTextContent('ada#0001');
    expect(screen.getByText('org_admin')).toBeInTheDocument();
    expect(screen.getByLabelText('Display name')).toHaveValue('Ada Lovelace');
  });

  it('saves a new display name and re-reads the account', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Display name'), 'Ada L');
    await user.click(screen.getByRole('button', { name: /Save/ }));

    await vi.waitFor(() => expect(api.updateMe).toHaveBeenCalledWith({ display_name: 'Ada L' }));
    expect(api.getMe).toHaveBeenCalledTimes(2);
  });

  it('sends no display name rather than an empty one', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Display name'), '');
    await user.click(screen.getByRole('button', { name: /Save/ }));

    await vi.waitFor(() => expect(api.updateMe).toHaveBeenCalledWith({ display_name: undefined }));
  });

  it('turns the new-device alert off on its own, without a save', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('checkbox'));

    await vi.waitFor(() => expect(api.updateMe)
      .toHaveBeenCalledWith({ new_device_alerts_enabled: false }));
  });
});

describe('changing the password', () => {
  const security = async () => {
    const user = show();
    await openTab(user, /Security/);
    return user;
  };
  const fill = async (user: Awaited<ReturnType<typeof show>>, current: string, next: string, confirm: string) => {
    await user.fill(screen.getByLabelText('Current password'), current);
    await user.fill(screen.getByLabelText('New password'), next);
    await user.fill(screen.getByLabelText('Confirm new password'), confirm);
    await user.click(screen.getByRole('button', { name: 'Change Password' }));
  };

  it('changes it', async () => {
    api.changePassword.mockResolvedValue({});
    const user = await security();

    await fill(user, 'old-password', 'new-password', 'new-password');

    await vi.waitFor(() => expect(api.changePassword)
      .toHaveBeenCalledWith({ current_password: 'old-password', new_password: 'new-password' }));
  });

  it('refuses a mismatched confirmation before sending anything', async () => {
    const user = await security();

    await fill(user, 'old-password', 'new-password', 'different');

    expect(await screen.findByText('New passwords do not match.')).toBeInTheDocument();
    expect(api.changePassword).not.toHaveBeenCalled();
  });

  it('refuses one shorter than the minimum before sending anything', async () => {
    const user = await security();

    await fill(user, 'old-password', 'short', 'short');

    expect(await screen.findByText('Password must be at least 8 characters.')).toBeInTheDocument();
    expect(api.changePassword).not.toHaveBeenCalled();
  });

  it('reports a wrong current password as exactly that', async () => {
    api.changePassword.mockResolvedValue({ error: 'invalid_current_password' });
    const user = await security();

    await fill(user, 'wrong', 'new-password', 'new-password');

    expect(await screen.findByText('Current password is incorrect.')).toBeInTheDocument();
  });

  it('reports any other failure generically', async () => {
    api.changePassword.mockRejectedValue(new Error('500'));
    const user = await security();

    await fill(user, 'old-password', 'new-password', 'new-password');

    expect(await screen.findByText('Failed to change password. Please try again.')).toBeInTheDocument();
  });

  it('can reveal each password field, which start hidden', async () => {
    const user = await security();
    expect(screen.getByLabelText('Current password')).toHaveAttribute('type', 'password');

    await user.click(screen.getByLabelText('Current password').parentElement!.querySelector('button')!);

    expect(screen.getByLabelText('Current password')).toHaveAttribute('type', 'text');
  });
});

describe('the linked social accounts', () => {
  const security = async () => {
    const user = show();
    await openTab(user, /Security/);
    return user;
  };

  it('says there are none', async () => {
    await security();
    expect(await screen.findByText('No linked accounts.')).toBeInTheDocument();
  });

  it('lists the ones that are linked', async () => {
    api.getSocialAccounts.mockResolvedValue([{ id: 'a1', provider: 'google', provider_user_id: 'g1' }]);
    await security();

    expect(await screen.findByText('Google')).toBeInTheDocument();
  });

  it('unlinks one', async () => {
    api.getSocialAccounts.mockResolvedValue([{ id: 'a1', provider: 'google', provider_user_id: 'g1' }]);
    api.unlinkSocialAccount.mockResolvedValue({});
    const user = await security();
    await screen.findByText('Google');

    await user.click(screen.getByRole('button', { name: /Unlink/ }));

    await vi.waitFor(() => expect(api.unlinkSocialAccount).toHaveBeenCalledWith('a1'));
  });

  it('refuses to unlink the only way left to sign in', async () => {
    // Unlinking it would lock the account out with no password to fall back to.
    api.getSocialAccounts.mockResolvedValue([{ id: 'a1', provider: 'google', provider_user_id: 'g1' }]);
    api.unlinkSocialAccount.mockResolvedValue({ error: 'cannot_remove_last_auth_method' });
    const user = await security();
    await screen.findByText('Google');

    await user.click(screen.getByRole('button', { name: /Unlink/ }));

    expect(await screen.findByText('Cannot unlink — this is your only login method. Set a password first.'))
      .toBeInTheDocument();
    // Still listed: nothing was removed.
    expect(screen.getByText('Google')).toBeInTheDocument();
  });
});

describe('enrolling an authenticator app', () => {
  const mfa = async () => {
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);
    return user;
  };

  it('shows the seed the server generated, once', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'JBSWY3DPEHPK3PXP' });
    const user = await mfa();

    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));

    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument();
  });

  it('confirms the code and reloads the status', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'S' });
    api.confirmTotp.mockResolvedValue({ backup_codes: ['aaa', 'bbb'] });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));

    await user.fill(await screen.findByPlaceholderText('000000'), '123456');
    await user.click(screen.getByRole('button', { name: /Confirm|Verify|Enable/ }));

    await vi.waitFor(() => expect(api.confirmTotp)
      .toHaveBeenCalledWith({ code: '123456' }, undefined));
  });

  it('accepts only six digits, and nothing that is not one', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'S' });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));

    // Typed rather than pasted: the field filters each keystroke.
    await user.click(await screen.findByPlaceholderText('000000'));
    await user.keyboard('abc12345678');

    expect(screen.getByPlaceholderText('000000')).toHaveValue('123456');
  });

  it('says the code was wrong', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'S' });
    api.confirmTotp.mockResolvedValue({ error: 'invalid_code' });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));
    await user.fill(await screen.findByPlaceholderText('000000'), '000000');

    await user.click(screen.getByRole('button', { name: /Confirm|Verify|Enable/ }));

    expect(await screen.findByText('Invalid code. Please try again.')).toBeInTheDocument();
  });

  it('asks for a proof when the backend demands re-authentication, then retries', async () => {
    // A bearer token alone must not be enough to enrol a factor beside the victim's.
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'S' });
    api.confirmTotp
      .mockRejectedValueOnce(new ApiError(401, { error: 'reauthentication_required', methods: ['current_password'] }))
      .mockResolvedValue({ backup_codes: ['aaa'] });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));
    await user.fill(await screen.findByPlaceholderText('000000'), '123456');

    await user.click(screen.getByRole('button', { name: /Confirm|Verify|Enable/ }));

    const prompt = await screen.findByLabelText(/password/i, { selector: 'input[type="password"]' });
    await user.fill(prompt, 'my-password');
    await user.click(screen.getAllByRole('button', { name: /Confirm|Continue|Verify/ }).at(-1)!);

    await vi.waitFor(() => expect(api.confirmTotp)
      .toHaveBeenLastCalledWith({ code: '123456' }, { current_password: 'my-password' }));
  });
});

describe('the backup codes', () => {
  const mfa = async () => {
    api.getMfaStatus.mockResolvedValue(MFA_ON);
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);
    return user;
  };

  it('says how many are left', async () => {
    await mfa();
    expect(await screen.findByText('3 codes remaining.')).toBeInTheDocument();
  });

  it('uses the singular for the last one', async () => {
    api.getMfaStatus.mockResolvedValue({ ...MFA_ON, backup_codes_remaining: 1 });
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);

    expect(await screen.findByText('1 code remaining.')).toBeInTheDocument();
  });

  it('regenerates them, showing the new set once', async () => {
    api.regenerateBackupCodes.mockResolvedValue({ backup_codes: ['code-1', 'code-2'] });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Regenerate/ }));

    await user.click(screen.getAllByRole('button', { name: 'Regenerate' }).at(-1)!);

    await vi.waitFor(() => expect(api.regenerateBackupCodes).toHaveBeenCalled());
    expect(await screen.findByText('code-1')).toBeInTheDocument();
  });

  it('says so when the regeneration fails', async () => {
    api.regenerateBackupCodes.mockRejectedValue(new Error('500'));
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Regenerate/ }));

    await user.click(screen.getAllByRole('button', { name: 'Regenerate' }).at(-1)!);

    expect(await screen.findByText('Failed to regenerate backup codes.')).toBeInTheDocument();
  });
});

describe('the phone factor', () => {
  const mfa = async (status = MFA_OFF) => {
    api.getMfaStatus.mockResolvedValue(status);
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);
    return user;
  };

  it('sends a code to the number given', async () => {
    api.setupPhone.mockResolvedValue({});
    const user = await mfa();

    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));

    await vi.waitFor(() => expect(api.setupPhone).toHaveBeenCalledWith('+33600000000'));
    expect(await screen.findByText('Enter the 6-digit code sent to +33600000000.')).toBeInTheDocument();
  });

  it('says so when the code could not be sent', async () => {
    api.setupPhone.mockRejectedValue(new Error('500'));
    const user = await mfa();

    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));

    expect(await screen.findByText('Failed to send code.')).toBeInTheDocument();
  });

  it('verifies the code and reports success', async () => {
    api.setupPhone.mockResolvedValue({});
    api.verifyPhone.mockResolvedValue({});
    const user = await mfa();
    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));

    await user.fill(await screen.findByPlaceholderText('000000'), '123456');
    await user.click(screen.getByRole('button', { name: /Verify/ }));

    await vi.waitFor(() => expect(api.verifyPhone).toHaveBeenCalledWith('123456', undefined));
    expect(await screen.findByText('Phone number verified successfully.')).toBeInTheDocument();
  });

  it('says the code was wrong', async () => {
    api.setupPhone.mockResolvedValue({});
    api.verifyPhone.mockResolvedValue({ error: 'invalid_code' });
    const user = await mfa();
    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));
    await user.fill(await screen.findByPlaceholderText('000000'), '000000');

    await user.click(screen.getByRole('button', { name: /Verify/ }));

    expect(await screen.findByText('Invalid code. Try again.')).toBeInTheDocument();
  });

  it('can be abandoned before the code is entered', async () => {
    api.setupPhone.mockResolvedValue({});
    const user = await mfa();
    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));
    await screen.findByPlaceholderText('000000');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByLabelText('Phone number')).toBeInTheDocument();
  });

  it('removes a verified number', async () => {
    api.removePhone.mockResolvedValue({});
    const user = await mfa(MFA_ON);

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    await vi.waitFor(() => expect(api.removePhone).toHaveBeenCalled());
  });

  it('says so when the removal fails', async () => {
    api.removePhone.mockRejectedValue(new Error('500'));
    const user = await mfa(MFA_ON);

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    expect(await screen.findByText('Failed to remove the phone number.')).toBeInTheDocument();
  });
});

describe('the passkeys', () => {
  const mfa = async () => {
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);
    return user;
  };

  it('lists the ones registered, naming an unnamed one', async () => {
    api.listWebAuthnCredentials.mockResolvedValue([
      { id: 'c1', device_name: 'MacBook', created_at: '2026-01-02T00:00:00Z' },
      { id: 'c2', device_name: null, created_at: '2026-01-02T00:00:00Z' },
    ]);
    await mfa();

    expect(await screen.findByText('MacBook')).toBeInTheDocument();
    expect(screen.getByText('Unnamed passkey')).toBeInTheDocument();
  });

  it('registers one from what the browser signs', async () => {
    api.beginWebAuthnRegistration.mockResolvedValue({
      challenge: 'AAAA', user: { id: 'AAAA' }, rp: { name: 'RediensIAM' },
      pubKeyCredParams: [], excludeCredentials: [],
    });
    api.completeWebAuthnRegistration.mockResolvedValue({});
    const create = vi.fn(async () => ({
      id: 'cred-1', rawId: new ArrayBuffer(4), type: 'public-key',
      response: {
        clientDataJSON: new ArrayBuffer(4), attestationObject: new ArrayBuffer(4),
      },
    }));
    vi.spyOn(navigator, 'credentials', 'get').mockReturnValue({ create } as unknown as CredentialsContainer);
    const user = await mfa();

    await user.fill(await screen.findByPlaceholderText('Passkey name (optional)'), 'YubiKey');
    await user.click(screen.getByRole('button', { name: /Add passkey|Register/ }));

    await vi.waitFor(() => expect(api.completeWebAuthnRegistration).toHaveBeenCalled());
    vi.restoreAllMocks();
  });

  it('says so plainly when the operator dismisses the browser prompt', async () => {
    api.beginWebAuthnRegistration.mockResolvedValue({ challenge: 'AAAA', user: { id: 'AAAA' } });
    const create = vi.fn(async () => { throw new DOMException('cancelled', 'NotAllowedError'); });
    vi.spyOn(navigator, 'credentials', 'get').mockReturnValue({ create } as unknown as CredentialsContainer);
    const user = await mfa();

    await user.click(await screen.findByRole('button', { name: /Add passkey|Register/ }));

    expect(await screen.findByText('Passkey prompt was cancelled.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('removes one', async () => {
    api.listWebAuthnCredentials.mockResolvedValue([{ id: 'c1', device_name: 'MacBook', created_at: '2026-01-02T00:00:00Z' }]);
    api.deleteWebAuthnCredential.mockResolvedValue({});
    const user = await mfa();
    await screen.findByText('MacBook');

    // The row's only button, which carries an icon and no text.
    await user.click(screen.getByText('MacBook').closest('div')!.parentElement!
      .parentElement!.querySelector<HTMLButtonElement>('button')!);

    await vi.waitFor(() => expect(api.deleteWebAuthnCredential).toHaveBeenCalledWith('c1', undefined));
  });

  it('says so when the removal fails', async () => {
    api.listWebAuthnCredentials.mockResolvedValue([{ id: 'c1', device_name: 'MacBook', created_at: '2026-01-02T00:00:00Z' }]);
    api.deleteWebAuthnCredential.mockRejectedValue(new Error('500'));
    const user = await mfa();
    await screen.findByText('MacBook');

    await user.click(screen.getByText('MacBook').closest('div')!.parentElement!
      .parentElement!.querySelector<HTMLButtonElement>('button')!);

    expect(await screen.findByText('Failed to remove the passkey.')).toBeInTheDocument();
  });
});

describe('the sessions tab', () => {
  const sessions = async (list: unknown[] = []) => {
    api.getSessions.mockResolvedValue(list);
    const user = show();
    await openTab(user, /Sessions/);
    return user;
  };

  it('says there are none', async () => {
    await sessions();
    expect(await screen.findByText('No active sessions.')).toBeInTheDocument();
  });

  it('lists each application, naming an unnamed one', async () => {
    await sessions([
      { client_id: 'portal', client_name: 'Customer Portal', granted_at: '2026-03-04T05:06:07Z' },
      { client_id: 'cli', client_name: null },
    ]);

    expect(await screen.findByText('Customer Portal')).toBeInTheDocument();
    expect(screen.getByText('cli')).toBeInTheDocument();
  });

  it('revokes one', async () => {
    api.revokeSession.mockResolvedValue({});
    const user = await sessions([{ client_id: 'portal', client_name: 'Customer Portal' }]);
    await screen.findByText('Customer Portal');

    await user.click(screen.getByRole('button', { name: /Revoke$/ }));

    await vi.waitFor(() => expect(api.revokeSession).toHaveBeenCalledWith('portal'));
  });

  it('asks before revoking everything, then does', async () => {
    api.revokeAllSessions.mockResolvedValue({});
    const user = await sessions([{ client_id: 'portal', client_name: 'Customer Portal' }]);
    await screen.findByText('Customer Portal');

    await user.click(screen.getByRole('button', { name: /Revoke all/i }));
    expect(api.revokeAllSessions).not.toHaveBeenCalled();

    // The dialog's confirm, not the button that opened it.
    await user.click(screen.getAllByRole('button', { name: 'Revoke All' }).at(-1)!);

    await vi.waitFor(() => expect(api.revokeAllSessions).toHaveBeenCalled());
  });

  it('survives a session list that cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getSessions.mockRejectedValue(new Error('500'));
    const user = show();
    await openTab(user, /Sessions/);

    expect(await screen.findByText('No active sessions.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('the remaining reveal, copy and dismiss paths', () => {
  const mfa = async (status = MFA_OFF) => {
    api.getMfaStatus.mockResolvedValue(status);
    const user = show();
    await openTab(user, /MFA|Two-factor|Security keys/i);
    return user;
  };

  it('reveals the new password too, not only the current one', async () => {
    const user = show();
    await openTab(user, /Security/);

    await user.click(screen.getByLabelText('New password').parentElement!.querySelector('button')!);

    expect(screen.getByLabelText('New password')).toHaveAttribute('type', 'text');
  });

  it('copies the TOTP seed', async () => {
    const writeText = vi.fn(async () => {});
    vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'JBSWY3DPEHPK3PXP' });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));
    await screen.findByText('JBSWY3DPEHPK3PXP');

    await user.click(screen.getAllByRole('button', { name: /Copy/ })[0]);

    expect(writeText).toHaveBeenCalledWith('JBSWY3DPEHPK3PXP');
    vi.restoreAllMocks();
  });

  it('abandons the TOTP setup, forgetting the seed', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'JBSWY3DPEHPK3PXP' });
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));
    await screen.findByText('JBSWY3DPEHPK3PXP');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByText('JBSWY3DPEHPK3PXP')).not.toBeInTheDocument();
  });

  it('reports a TOTP confirmation that failed outright', async () => {
    api.setupTotp.mockResolvedValue({ otpauth_url: 'otpauth://totp/x', secret: 'S' });
    api.confirmTotp.mockRejectedValue(new Error('500'));
    const user = await mfa();
    await user.click(await screen.findByRole('button', { name: /Set up TOTP/ }));
    await user.fill(await screen.findByPlaceholderText('000000'), '123456');

    await user.click(screen.getByRole('button', { name: /Confirm|Verify|Enable/ }));

    expect(await screen.findByText('Could not confirm the code. Please try again.')).toBeInTheDocument();
  });

  it('reports a phone verification that failed outright', async () => {
    api.setupPhone.mockResolvedValue({});
    api.verifyPhone.mockRejectedValue(new Error('500'));
    const user = await mfa();
    await user.fill(await screen.findByLabelText('Phone number'), '+33600000000');
    await user.click(screen.getByRole('button', { name: 'Send code' }));
    await user.fill(await screen.findByPlaceholderText('000000'), '123456');

    await user.click(screen.getByRole('button', { name: /Verify/ }));

    expect(await screen.findByText('Failed to verify code.')).toBeInTheDocument();
  });

  it('reports a passkey registration the browser refused for some other reason', async () => {
    api.beginWebAuthnRegistration.mockResolvedValue({ challenge: 'AAAA', user: { id: 'AAAA' } });
    const create = vi.fn(async () => { throw new DOMException('boom', 'SecurityError'); });
    vi.spyOn(navigator, 'credentials', 'get').mockReturnValue({ create } as unknown as CredentialsContainer);
    const user = await mfa();

    await user.click(await screen.findByRole('button', { name: /Add passkey|Register/ }));

    expect(await screen.findByText('Passkey registration failed.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('closes the regenerate confirmation, from Cancel and from Escape', async () => {
    const user = await mfa(MFA_ON);
    await user.click(await screen.findByRole('button', { name: /Regenerate/ }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.regenerateBackupCodes).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Regenerate/ }));
    await user.keyboard('{Escape}');

    // The page's own trigger stays; it is the dialog that has to go.
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(api.regenerateBackupCodes).not.toHaveBeenCalled();
  });

  it('closes the revoke-all confirmation, from Cancel and from Escape', async () => {
    api.getSessions.mockResolvedValue([{ client_id: 'portal', client_name: 'Customer Portal' }]);
    const user = show();
    await openTab(user, /Sessions/);
    await screen.findByText('Customer Portal');

    await user.click(screen.getByRole('button', { name: /Revoke all/i }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.revokeAllSessions).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Revoke all/i }));
    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(api.revokeAllSessions).not.toHaveBeenCalled();
  });
});
