import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import KeyRotationPanel from './KeyRotationPanel';
import { ApiError } from '@/auth';

/**
 * The panel over `GET /admin/key-rotation` and `POST /admin/key-rotation/reencrypt`.
 *
 * The property that matters is that **pending above zero is never presented as done**: an operator
 * who reads "up to date" and then drops the retired key from `Security:EncryptionKeys` has lost
 * every value still under it. So the count is shown per column, the sweep answers with the status
 * it actually reached rather than the one it hoped for, and a partial run says so.
 */

const api = vi.hoisted(() => ({ getKeyRotationStatus: vi.fn(), reEncryptKeys: vi.fn() }));
vi.mock('@/api', () => api);

const PENDING = {
  active_key_id: 2,
  configured_key_ids: [2, 1],
  columns: [
    { column: 'User.TotpSecret', pending: 12 },
    { column: 'Webhook.SecretEnc', pending: 0 },
    { column: 'OrgSmtpConfig.PasswordEnc', pending: 3 },
    { column: 'Project.LoginTheme', pending: 0 },
  ],
  total_pending: 15,
};

const SETTLED = {
  active_key_id: 2,
  configured_key_ids: [2],
  columns: PENDING.columns.map(c => ({ ...c, pending: 0 })),
  total_pending: 0,
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getKeyRotationStatus.mockResolvedValue(PENDING);
  api.reEncryptKeys.mockResolvedValue(SETTLED);
});

function show() {
  const user = userEvent.setup();
  render(<KeyRotationPanel />);
  return user;
}

const confirmButton = () => screen.getByRole('button', { name: 'Re-encrypt' });

describe('what it shows', () => {
  it('names the active key and the ones still configured beside it', async () => {
    show();

    expect(await screen.findByText('active key k2')).toBeInTheDocument();
    expect(screen.getByText(/also configured: k1/)).toBeInTheDocument();
  });

  it('breaks the pending count down by column', async () => {
    show();

    expect(await screen.findByText('User.TotpSecret')).toBeInTheDocument();
    expect(screen.getByText('12 pending')).toBeInTheDocument();
    expect(screen.getByText('3 pending')).toBeInTheDocument();
    expect(screen.getByText('15 value(s) pending')).toBeInTheDocument();
  });

  /** Nothing left is the one state where a retired key may be dropped — the panel says that. */
  it('says nothing is pending, and that the other keys can go', async () => {
    api.getKeyRotationStatus.mockResolvedValue(SETTLED);
    show();

    expect(await screen.findByText('nothing pending')).toBeInTheDocument();
    expect(screen.getByText(/may be removed from the deployment/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Re-encrypt now' })).toBeDisabled();
  });

  it('shows what the API refused rather than leaving the panel blank', async () => {
    api.getKeyRotationStatus.mockRejectedValue(new ApiError(403, { error: 'forbidden' }));
    show();

    expect(await screen.findByText('forbidden')).toBeInTheDocument();
  });
});

describe('running the sweep', () => {
  /** It rewrites every stored secret in one request — it does not start on a single click. */
  it('does not call the API until the confirmation is accepted', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));

    expect(screen.getByText(/rewrites 15 stored secret/)).toBeInTheDocument();
    expect(api.reEncryptKeys).not.toHaveBeenCalled();
  });

  it('says what will happen: the columns, the cost, and that re-running is safe', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));

    expect(screen.getByText(/TOTP secrets, webhook secrets, SMTP passwords and login themes/)).toBeInTheDocument();
    expect(screen.getByText(/safe to re-run/)).toBeInTheDocument();
  });

  it('starts nothing when the confirmation is dismissed', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.reEncryptKeys).not.toHaveBeenCalled();
  });

  it('sweeps and reports the state it reached, without re-reading', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));
    await user.click(confirmButton());

    expect(await screen.findByText(/Sweep complete/)).toBeInTheDocument();
    expect(screen.getByText('nothing pending')).toBeInTheDocument();
    expect(api.getKeyRotationStatus).toHaveBeenCalledTimes(1);
  });

  /**
   * The failure this exists for: a sweep that stopped short must not read as a finished one, or the
   * retired key gets dropped over the rows still under it.
   */
  it('says how much is left when the sweep did not finish', async () => {
    api.reEncryptKeys.mockResolvedValue({ ...PENDING, total_pending: 4 });
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));
    await user.click(confirmButton());

    expect(await screen.findByText(/4 value\(s\) still pending/)).toBeInTheDocument();
  });

  it('shows the refusal and keeps the dialog open', async () => {
    api.reEncryptKeys.mockRejectedValue(new ApiError(500, { detail: 'key k1 is no longer configured' }));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));
    await user.click(confirmButton());

    expect(await screen.findByText(/key k1 is no longer configured/)).toBeInTheDocument();
    expect(confirmButton()).toBeInTheDocument();
  });

  it('reports progress and refuses a second start while one is running', async () => {
    let release: (v: unknown) => void = () => {};
    api.reEncryptKeys.mockReturnValue(new Promise(r => { release = r; }));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Re-encrypt now' }));
    await user.click(confirmButton());

    await waitFor(() => expect(screen.getByRole('button', { name: 'Re-encrypting…' })).toBeDisabled());
    release(SETTLED);
    expect(await screen.findByText(/Sweep complete/)).toBeInTheDocument();
  });
});
