import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import DeploymentSettings from './DeploymentSettings';
import { ApiError } from '@/auth';

/**
 * The page over `GET`/`PATCH /admin/instance`.
 *
 * Three properties matter more than the form itself. A PATCH must carry only what the operator
 * touched — a body naming one setting must not reset the nineteen it does not name. The page must
 * show the difference between what is **stored** and what is **in force**, because a configuration
 * source added after the instance provider silently wins, and a page reporting one number would
 * make that indistinguishable from a lost write. And what this endpoint will never write has to be
 * on the screen with its reason, because a hidden setting is one an operator hunts for in a
 * manifest — the relay password included, which has no column in the instance row at all.
 */

// `vi.mock` replaces the module, so the key-rotation panel this page embeds needs its two calls
// here as well — a missing export breaks the whole file, not just the panel.
const api = vi.hoisted(() => ({
  getInstanceConfig: vi.fn(), updateInstanceConfig: vi.fn(),
  getKeyRotationStatus: vi.fn(), reEncryptKeys: vi.fn(),
  getMe: vi.fn(),
}));
vi.mock('@/api', () => api);

const SETTINGS = {
  max_login_attempts: 5, lockout_minutes: 15, otp_ttl_seconds: 300,
  max_sms_per_window: 3, sms_window_minutes: 10, audit_retention_days: 365,
  invite_expiry_hours: 72, pat_cache_ttl_minutes: 5,
  smtp_host: 'mail.example', smtp_port: 587, smtp_start_tls: true,
  smtp_username: 'bot', smtp_from_address: 'noreply@example', smtp_from_name: 'RediensIAM',
};

const config = (over: Record<string, unknown> = {}) => ({
  config_version: 3,
  settings: { ...SETTINGS },
  stored: { ...SETTINGS },
  environment_only: { argon_memory_cost: 65536, hydra_admin_url: 'http://hydra:4445', trusted_proxies: '' },
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  api.getInstanceConfig.mockResolvedValue(config());
  api.updateInstanceConfig.mockResolvedValue({ changed: ['lockout_minutes'], config_version: 4 });
  api.getKeyRotationStatus.mockResolvedValue({
    active_key_id: 1, configured_key_ids: [1], columns: [], total_pending: 0,
  });
  api.getMe.mockResolvedValue({
    email: 'ops@rediens.io', username: 'ops', discriminator: '0001', totp_enabled: false,
  });
});

function show() {
  const user = userEvent.setup();
  render(<MemoryRouter><DeploymentSettings /></MemoryRouter>);
  return user;
}

describe('what it shows', () => {
  it('fills the form from what is in force', async () => {
    show();

    expect(await screen.findByLabelText(/Lockout duration/)).toHaveValue(15);
    expect(screen.getByLabelText('SMTP host')).toHaveValue('mail.example');
  });

  it('names the config version, so two pods can be told apart', async () => {
    show();

    expect(await screen.findByText(/version 3/)).toBeInTheDocument();
  });

  /**
   * The relay's own credentials were the half of the mail card that existed nowhere: the username
   * and the sender's name were stored and unreachable, so changing either meant a manifest edit for
   * a value the row already held.
   */
  it('offers the relay identity, not just its address', async () => {
    show();

    expect(await screen.findByLabelText('Username')).toHaveValue('bot');
    expect(screen.getByLabelText('From address')).toHaveValue('noreply@example');
    expect(screen.getByLabelText('From name')).toHaveValue('RediensIAM');
    expect(screen.getByLabelText('STARTTLS')).toBeChecked();
  });

  /** And says why the one field the design asked for is absent, rather than showing a dead box. */
  it('says the relay password is not settable here, and where it comes from', async () => {
    show();

    expect(await screen.findByText(/instance row has no column for it/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  });

  /**
   * The settings the server refuses to write are shown rather than hidden — with the reason. A
   * hidden setting is one an operator hunts for in a manifest.
   */
  it('lists what only the deployment decides, with the reason beside each one', async () => {
    show();

    expect(await screen.findByText('argon_memory_cost')).toBeInTheDocument();
    expect(screen.getByText('hydra_admin_url')).toBeInTheDocument();
    expect(screen.getByText(/must not redirect the authorisation store/)).toBeInTheDocument();
    expect(screen.getAllByText(/memory limit/).length).toBeGreaterThan(0);
  });

  it('shows an em dash where the deployment set nothing, rather than an empty row', async () => {
    show();

    await screen.findByText('trusted_proxies');
    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('says so when the read fails', async () => {
    api.getInstanceConfig.mockRejectedValue(new Error('403'));
    show();

    expect(await screen.findByText(/Could not read the deployment settings/)).toBeInTheDocument();
  });

  it('says what the server said when the read is refused for a named reason', async () => {
    api.getInstanceConfig.mockRejectedValue(new ApiError(404, { error: 'instance_row_missing' }));
    show();

    expect(await screen.findByText('instance_row_missing')).toBeInTheDocument();
  });
});

describe('the operator’s own account', () => {
  it('says who is signed in and whether they carry a second factor', async () => {
    show();

    expect(await screen.findByText(/ops@rediens.io/)).toBeInTheDocument();
    expect(screen.getByText(/no second factor/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /second factor and sessions/ }))
      .toHaveAttribute('href', '/account');
  });

  it('says so when the enrolment is already done', async () => {
    api.getMe.mockResolvedValue({ email: 'ops@rediens.io', username: 'ops', discriminator: '1', totp_enabled: true });
    show();

    expect(await screen.findByText(/authenticator app enrolled/)).toBeInTheDocument();
  });

  it('says it could not read the account instead of showing a blank line', async () => {
    api.getMe.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('Could not read your own account.')).toBeInTheDocument();
  });

  /** It is the one card that does not depend on the instance row, so it survives that read failing. */
  it('is still there when the deployment settings cannot be read', async () => {
    api.getInstanceConfig.mockRejectedValue(new Error('403'));
    show();

    expect(await screen.findByText(/ops@rediens.io/)).toBeInTheDocument();
  });
});

describe('the stored / in-force difference', () => {
  /**
   * The case that would otherwise read as a lost write: the row holds 99, the environment pins 15,
   * and the operator has to be told which is which.
   */
  it('flags a setting the environment overrides, and names the stored value', async () => {
    api.getInstanceConfig.mockResolvedValue(config({
      settings: { ...SETTINGS, lockout_minutes: 15 },
      stored:   { ...SETTINGS, lockout_minutes: 99 },
    }));
    show();

    expect(await screen.findByText(/stored 99/)).toBeInTheDocument();
    expect(screen.getByText(/overridden by the environment/)).toBeInTheDocument();
  });

  it('flags an overridden relay address too, not only the numbers', async () => {
    api.getInstanceConfig.mockResolvedValue(config({
      settings: { ...SETTINGS, smtp_host: 'relay.env' },
      stored:   { ...SETTINGS, smtp_host: 'mail.row' },
    }));
    show();

    expect(await screen.findByText(/stored mail.row/)).toBeInTheDocument();
  });

  it('says nothing where the two agree', async () => {
    show();
    await screen.findByLabelText(/Lockout duration/);

    expect(screen.queryByText(/overridden by the environment/)).not.toBeInTheDocument();
  });
});

describe('saving', () => {
  it('sends only what was touched', async () => {
    const user = show();
    await user.fill(await screen.findByLabelText(/Lockout duration/), '42');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.updateInstanceConfig).toHaveBeenCalledWith({ lockout_minutes: 42 }));
  });

  it('sends several fields together when several were touched', async () => {
    const user = show();
    await user.fill(await screen.findByLabelText(/Invitation expiry/), '24');
    await user.fill(screen.getByLabelText('SMTP host'), 'relay.example');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.updateInstanceConfig).toHaveBeenCalledWith({
      invite_expiry_hours: 24, smtp_host: 'relay.example',
    }));
  });

  it('carries the relay identity fields the row holds', async () => {
    const user = show();
    await user.fill(await screen.findByLabelText('Username'), 'postmaster@mg.rediens.io');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.updateInstanceConfig).toHaveBeenCalledWith({
      smtp_username: 'postmaster@mg.rediens.io',
    }));
  });

  it('sends the TLS switch as a boolean, not as a string', async () => {
    const user = show();
    await user.click(await screen.findByLabelText('STARTTLS'));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.updateInstanceConfig).toHaveBeenCalledWith({ smtp_start_tls: false }));
  });

  /** Nothing touched is nothing to send — and the button says so rather than posting an empty body. */
  it('cannot be saved before anything changes', async () => {
    show();
    await screen.findByLabelText(/Lockout duration/);

    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  });

  /**
   * Out of range is clamped by the server, not refused, so the page re-reads instead of trusting
   * what it typed: showing 100000 where the row holds 1440 would be the page lying on the server's
   * behalf.
   */
  it('re-reads after saving rather than keeping the typed value', async () => {
    const user = show();
    await user.fill(await screen.findByLabelText(/Lockout duration/), '100000');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.getInstanceConfig).toHaveBeenCalledTimes(2));
    expect(await screen.findByLabelText(/Lockout duration/)).toHaveValue(15);
  });

  it('says how many settings were stored', async () => {
    const user = show();
    await user.fill(await screen.findByLabelText(/Lockout duration/), '42');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(/Saved 1 setting/)).toBeInTheDocument();
  });

  it('says nothing was changed when the save fails', async () => {
    api.updateInstanceConfig.mockRejectedValue(new Error('500'));
    const user = show();
    await user.fill(await screen.findByLabelText(/Lockout duration/), '42');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(/Nothing was changed/)).toBeInTheDocument();
  });

  it('repeats the server’s own refusal when there is one', async () => {
    api.updateInstanceConfig.mockRejectedValue(new ApiError(404, { error: 'instance_row_missing' }));
    const user = show();
    await user.fill(await screen.findByLabelText(/Lockout duration/), '42');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('instance_row_missing')).toBeInTheDocument();
  });
});
