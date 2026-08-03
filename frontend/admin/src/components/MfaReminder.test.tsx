import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import MfaReminder from './MfaReminder';

/**
 * The banner stands between exactly one account — the first administrator, who has to sign in
 * without a factor to configure the providers that make one deliverable — and a password on its
 * own. Whether it appears is therefore the whole component.
 */

const api = vi.hoisted(() => ({ getMfaStatus: vi.fn(), listWebAuthnCredentials: vi.fn() }));
vi.mock('@/api', () => api);

const NO_FACTOR = { totp_enabled: false, phone_verified: false };

beforeEach(() => {
  vi.clearAllMocks();
  api.getMfaStatus.mockResolvedValue(NO_FACTOR);
  api.listWebAuthnCredentials.mockResolvedValue([]);
});

const show = () => render(<MemoryRouter><MfaReminder /></MemoryRouter>);
const banner = () => screen.queryByText(/no second factor/);

/** Lets the two requests in the effect settle before asserting an absence. */
const settled = () => vi.waitFor(() => expect(api.getMfaStatus).toHaveBeenCalled()).then(() => Promise.resolve());

describe('an account with no factor', () => {
  it('is told, on every page, with a way to fix it', async () => {
    show();

    expect(await screen.findByText(/no second factor/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Set up MFA' })).toHaveAttribute('href', '/account');
  });

  it('is given no way to dismiss it', async () => {
    // A reminder you can silence is one you silence on day one and never see again.
    show();
    await screen.findByText(/no second factor/);

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe.each([
  ['an authenticator app', { totp_enabled: true, phone_verified: false }, []],
  ['a verified phone', { totp_enabled: false, phone_verified: true }, []],
  ['a passkey', NO_FACTOR, [{ id: 'c1' }]],
])('an account with %s', (_n, mfa, creds) => {
  it('sees nothing', async () => {
    api.getMfaStatus.mockResolvedValue(mfa);
    api.listWebAuthnCredentials.mockResolvedValue(creds);
    show();

    await settled();
    expect(banner()).toBeNull();
  });
});

describe('when the status cannot be read', () => {
  it('says nothing rather than crying wolf at an account that may well be enrolled', async () => {
    api.getMfaStatus.mockRejectedValue(new Error('500'));
    show();

    await settled();
    expect(banner()).toBeNull();
  });

  it('still warns when only the passkey list is unreadable but the rest says no factor', async () => {
    // Passkeys are the optional half; losing them must not hide a genuinely unprotected account.
    api.listWebAuthnCredentials.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText(/no second factor/)).toBeInTheDocument();
  });

  it('shows nothing at all before the answer arrives', () => {
    const { container } = show();
    expect(container).toBeEmptyDOMElement();
  });
});
