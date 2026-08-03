import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import Logout from './Logout';

const api = vi.hoisted(() => ({ getLogoutChallenge: vi.fn(), acceptLogout: vi.fn() }));
vi.mock('../api', () => api);

/**
 * Hydra's `urls.logout` is a browser redirect target: it ends the SSO session by sending the user
 * to a page, which confirms the challenge and posts the acceptance back. It pointed at
 * `/auth/logout` — a controller that answers JSON — so the browser landed on a raw
 * `{"logout_challenge":"…"}` body, nothing ever accepted the request, and the session survived a
 * sign-out that looked like it had happened.
 *
 * safeNavigate is deliberately not mocked. The URL Hydra hands back after acceptance is the
 * client's own post-logout target, and refusing a hostile one is part of what this page does.
 */
const origin = globalThis.location.origin;

function show(search = '?logout_challenge=lc_123') {
  render(
    <MemoryRouter initialEntries={[`/logout${search}`]}>
      <Logout />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { ...globalThis.location, origin, href: origin, assign: vi.fn() },
  });
});

describe('the logout page completes the challenge', () => {
  it('accepts the challenge and follows the URL Hydra returns', async () => {
    api.getLogoutChallenge.mockResolvedValue({ logout_challenge: 'lc_123' });
    api.acceptLogout.mockResolvedValue({ redirect_to: `${origin}/admin/` });

    show();

    await waitFor(() => expect(api.acceptLogout).toHaveBeenCalledWith('lc_123'));
    await waitFor(() => expect(globalThis.location.href).toBe(`${origin}/admin/`));
  });

  it('refuses a redirect that leaves this origin', async () => {
    api.getLogoutChallenge.mockResolvedValue({ logout_challenge: 'lc_123' });
    api.acceptLogout.mockResolvedValue({ redirect_to: 'https://evil.test/steal' });

    show();

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(globalThis.location.href).toBe(origin);
  });

  it('says so when there is no challenge to complete', async () => {
    show('');

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(api.acceptLogout).not.toHaveBeenCalled();
  });

  it('does not claim the session ended when the server refuses the challenge', async () => {
    api.getLogoutChallenge.mockRejectedValue(new Error('400'));

    show();

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(api.acceptLogout).not.toHaveBeenCalled();
  });
});
