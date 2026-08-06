import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import Impersonation from './Impersonation';

/**
 * The page exists to satisfy one sentence of `IMPERSONATION.md` §5 — *an impersonation nobody can
 * list is an impersonation nobody can stop* — so the two things asserted hardest are that a live
 * session is visible and that ending one actually calls the revoke route.
 *
 * It supervises and never creates: opening mints a credential and is refused to a browser session
 * on purpose, which is why no test here looks for a "new session" control.
 */

const api = vi.hoisted(() => ({ listImpersonations: vi.fn(), revokeImpersonation: vi.fn() }));
vi.mock('@/api', () => api);

const session = (over: Record<string, unknown> = {}) => ({
  session_id: '7f3', act_sub: 'usr_operator', act_level: 'super_admin',
  org_id: 'acme', project_id: 'p1', mode: 'read', reason: 'ticket #4812',
  created_at: '2026-08-06T10:00:00Z', expires_at: '2026-08-06T10:15:00Z', last_used_at: null,
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  api.listImpersonations.mockResolvedValue([session()]);
  api.revokeImpersonation.mockResolvedValue(undefined);
});

function show() {
  const user = userEvent.setup();
  render(<Impersonation />);
  return user;
}

describe('what is running', () => {
  it('names the operator, the tenant and the stated reason', async () => {
    show();

    expect(await screen.findByText('usr_operator')).toBeInTheDocument();
    expect(screen.getByText('acme')).toBeInTheDocument();
    expect(screen.getByText('ticket #4812')).toBeInTheDocument();
  });

  /** `write` is the stronger capability, and the one that has to be visible without reading. */
  it('marks a write session apart from a read one', async () => {
    api.listImpersonations.mockResolvedValue([session({ mode: 'write', session_id: 'w1' })]);
    show();

    expect(await screen.findByText('write')).toBeInTheDocument();
  });

  it('says plainly when nothing is running', async () => {
    api.listImpersonations.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No live sessions')).toBeInTheDocument();
  });

  it('treats a null body as nothing running', async () => {
    api.listImpersonations.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No live sessions')).toBeInTheDocument();
  });

  it('names the failure when the list cannot be read', async () => {
    api.listImpersonations.mockRejectedValue(new Error('403'));
    show();

    expect(await screen.findByText(/Could not read the live sessions/)).toBeInTheDocument();
  });
});

describe('ending one', () => {
  it('asks first, and says the token stops immediately', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'End' }));

    expect(await screen.findByText(/stops working immediately/)).toBeInTheDocument();
    expect(api.revokeImpersonation).not.toHaveBeenCalled();
  });

  it('revokes on confirmation and reloads', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'End' }));
    await user.click(screen.getByRole('button', { name: 'End session' }));

    await waitFor(() => expect(api.revokeImpersonation).toHaveBeenCalledWith('7f3'));
    expect(api.listImpersonations).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'End' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(api.revokeImpersonation).not.toHaveBeenCalled();
  });

  /** A session that expired between the listing and the click is not an error worth a stack trace. */
  it('explains a revoke that fails', async () => {
    api.revokeImpersonation.mockRejectedValue(new Error('404'));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'End' }));
    await user.click(screen.getByRole('button', { name: 'End session' }));

    expect(await screen.findByText(/may have ended on its own/)).toBeInTheDocument();
  });
});

describe('what the page never offers', () => {
  /**
   * Opening is service-account-only at the server. A control that always answers 403 reads as a
   * feature the operator is using wrong.
   */
  it('has no way to open a session', async () => {
    show();
    await screen.findByText('usr_operator');

    expect(screen.queryByRole('button', { name: /new|open|start/i })).not.toBeInTheDocument();
  });
});
