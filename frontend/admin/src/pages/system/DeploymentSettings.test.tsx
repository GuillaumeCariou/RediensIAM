import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import DeploymentSettings from './DeploymentSettings';

/**
 * The page over `GET`/`PATCH /admin/instance`.
 *
 * Two properties matter more than the form itself. A PATCH must carry only what the operator
 * touched — a body naming one setting must not reset the nineteen it does not name — and the page
 * must show the difference between what is **stored** and what is **in force**, because a
 * configuration source added after the instance provider silently wins. An operator who saves a
 * value and sees it unchanged is looking at that, and a page that reported one number would make
 * it indistinguishable from a lost write.
 */

const api = vi.hoisted(() => ({ getInstanceConfig: vi.fn(), updateInstanceConfig: vi.fn() }));
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
});

function show() {
  const user = userEvent.setup();
  render(<DeploymentSettings />);
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
   * The settings the server refuses to write are shown rather than hidden — with the reason. A
   * hidden setting is one an operator hunts for in a manifest.
   */
  it('lists what only the deployment decides', async () => {
    show();

    expect(await screen.findByText('argon_memory_cost')).toBeInTheDocument();
    expect(screen.getByText('hydra_admin_url')).toBeInTheDocument();
    expect(screen.getByText(/memory limit/)).toBeInTheDocument();
  });

  it('says so when the read fails', async () => {
    api.getInstanceConfig.mockRejectedValue(new Error('403'));
    show();

    expect(await screen.findByText(/Could not read the deployment settings/)).toBeInTheDocument();
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
});
