import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import OrgEmail from './OrgEmail';
import { ApiError } from '@/auth';

/**
 * `/org/smtp/test` deliberately stopped echoing the relay's own message: telling
 * "host unreachable" from "connection refused" turns an authenticated admin endpoint into a
 * port scanner for the internal network. Every failure now has to be explained from its code
 * alone, and nothing the server sends back may reach the screen verbatim.
 */

vi.mock('@/context/AuthContext', () => ({ useAuth: () => ({ isSuperAdmin: false }) }));

const api = vi.hoisted(() => ({
  getOrgSmtp: vi.fn(),
  upsertOrgSmtp: vi.fn(),
  deleteOrgSmtp: vi.fn(),
  testOrgSmtp: vi.fn(),
  adminGetOrgSmtp: vi.fn(),
  adminUpsertOrgSmtp: vi.fn(),
  adminDeleteOrgSmtp: vi.fn(),
  adminTestOrgSmtp: vi.fn(),
}));
vi.mock('@/api', () => api);

const CONFIGURED = {
  configured: true, host: 'smtp.acme.test', port: 587, start_tls: true,
  username: 'noreply@acme.test', from_address: 'noreply@acme.test', from_name: 'Acme',
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getOrgSmtp.mockResolvedValue({ configured: false });
});

const show = async () => {
  const user = userEvent.setup();
  render(<MemoryRouter><OrgEmail /></MemoryRouter>);
  return user;
};

/** Opens the edit form on an already-configured relay and fills nothing — the values load. */
const showEditor = async () => {
  api.getOrgSmtp.mockResolvedValue(CONFIGURED);
  const user = await show();
  await user.click(await screen.findByRole('button', { name: 'Edit' }));
  return user;
};

const save = async (user: Awaited<ReturnType<typeof show>>) =>
  user.click(screen.getByRole('button', { name: 'Save' }));

describe('when no relay is configured', () => {
  it('says the global relay is in use and offers to configure one', async () => {
    await show();
    expect(await screen.findByText('Using global SMTP')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Configure custom SMTP' })).toBeInTheDocument();
  });

  it('KNOWN GAP: a failed load is indistinguishable from having no relay', async () => {
    // `Failed to load SMTP configuration.` is set on the error state, but that state is only
    // rendered inside the edit form — which is closed at this point. So a 500 shows the same
    // "Using global SMTP" card as a genuinely unconfigured org. Documented, not asserted as
    // desirable: see `SECURITY-AUDIT-LOG.md` step 28.
    api.getOrgSmtp.mockRejectedValue(new ApiError(500, null));
    await show();

    expect(await screen.findByText('Using global SMTP')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load SMTP configuration.')).not.toBeInTheDocument();
  });
});

describe('saving a relay the server refuses', () => {
  const cases: ReadonlyArray<readonly [string, string]> = [
    ['smtp_host_required', 'Host is required.'],
    ['smtp_host_too_long', 'Host is too long (255 characters maximum).'],
    ['smtp_port_not_allowed', 'Port must be one of 25, 465, 587, 1025 or 2525.'],
    ['smtp_tls_required', 'TLS is required. Enable StartTLS, or use port 465 for implicit TLS.'],
    ['smtp_host_not_allowed', 'That host resolves to a private or reserved address and cannot be used.'],
  ];

  for (const [code, message] of cases) {
    it(`explains ${code}`, async () => {
      const user = await showEditor();
      api.upsertOrgSmtp.mockRejectedValue(new ApiError(400, { error: code }));

      await save(user);

      expect(await screen.findByText(message)).toBeInTheDocument();
    });
  }

  it('keeps the form open with the values still in it', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockRejectedValue(new ApiError(400, { error: 'smtp_port_not_allowed' }));

    await save(user);

    await screen.findByText('Port must be one of 25, 465, 587, 1025 or 2525.');
    expect(screen.getByLabelText('Host')).toHaveValue('smtp.acme.test');
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });

  it('falls back to a generic message for a code it does not know', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockRejectedValue(new ApiError(400, { error: 'smtp_something_new' }));

    await save(user);

    expect(await screen.findByText('Failed to save SMTP configuration.')).toBeInTheDocument();
  });

  it('falls back when the failure is not an API error at all', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockRejectedValue(new TypeError('Failed to fetch'));

    await save(user);

    expect(await screen.findByText('Failed to save SMTP configuration.')).toBeInTheDocument();
  });

  it('never prints a message the server supplied', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockRejectedValue(new ApiError(400, {
      error: 'smtp_host_not_allowed',
      detail: 'connect ECONNREFUSED 10.0.0.7:25',
      message: 'relay said: 550 nope',
    }));

    await save(user);

    await screen.findByText('That host resolves to a private or reserved address and cannot be used.');
    expect(document.body.textContent).not.toContain('10.0.0.7');
    expect(document.body.textContent).not.toContain('ECONNREFUSED');
    expect(document.body.textContent).not.toContain('550');
  });
});

describe('saving a relay the server accepts', () => {
  it('closes the form and shows the stored relay', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockResolvedValue({});
    api.getOrgSmtp.mockResolvedValue({ ...CONFIGURED, host: 'smtp.new.test', port: 465 });

    await save(user);

    expect(await screen.findByText('smtp.new.test:465', { exact: false })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });

  it('sends the port as a number and leaves an untouched password out', async () => {
    const user = await showEditor();
    api.upsertOrgSmtp.mockResolvedValue({});

    await save(user);

    expect(api.upsertOrgSmtp).toHaveBeenCalledWith(expect.objectContaining({ port: 587, password: undefined }));
  });
});

describe('testing the relay', () => {
  it('reports a failure from its code, without the relay\'s own words', async () => {
    api.getOrgSmtp.mockResolvedValue(CONFIGURED);
    const user = await show();
    api.testOrgSmtp.mockRejectedValue(new ApiError(400, {
      error: 'smtp_test_failed',
      detail: 'Connection refused (host 10.1.2.3 port 25)',
    }));

    await user.click(await screen.findByRole('button', { name: 'Test' }));

    expect(await screen.findByText('Could not send through this relay. Check the host, port, and credentials.')).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('10.1.2.3');
    expect(document.body.textContent).not.toContain('Connection refused');
  });

  it('confirms where the test message went', async () => {
    api.getOrgSmtp.mockResolvedValue(CONFIGURED);
    const user = await show();
    api.testOrgSmtp.mockResolvedValue({ to: 'admin@acme.test' });

    await user.click(await screen.findByRole('button', { name: 'Test' }));

    expect(await screen.findByText('Test email sent to admin@acme.test')).toBeInTheDocument();
  });
});

describe('removing the relay', () => {
  it('asks first, and does nothing if the answer is no', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    const user = await showEditor();

    await user.click(screen.getByRole('button', { name: 'Reset to global' }));

    expect(api.deleteOrgSmtp).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('reverts to the global relay once confirmed', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    const user = await showEditor();
    api.deleteOrgSmtp.mockResolvedValue(undefined);
    api.getOrgSmtp.mockResolvedValue({ configured: false });

    await user.click(screen.getByRole('button', { name: 'Reset to global' }));

    expect(await screen.findByText('Using global SMTP')).toBeInTheDocument();
    vi.unstubAllGlobals();
  });
});
