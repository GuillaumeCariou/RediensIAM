import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import SessionsDialog, { type OAuthSession } from './SessionsDialog';
import { fmtDate } from '@/lib/utils';

const SESSIONS: OAuthSession[] = [
  { client_id: 'portal', client_name: 'Customer Portal', granted_at: '2026-03-04T05:06:07Z' },
  { client_id: 'cli-tool' },
];

function show(props: Partial<React.ComponentProps<typeof SessionsDialog>> = {}) {
  const h = { onClose: vi.fn(), onRevokeAll: vi.fn() };
  render(
    <SessionsDialog
      userEmail="ada@acme.test" sessions={SESSIONS} loading={false} revokeAllLoading={false}
      {...h} {...props}
    />,
  );
  return { ...h, user: userEvent.setup() };
}

describe('the list', () => {
  it('names the account it is about', () => {
    show();
    expect(screen.getByText(/ada@acme.test/)).toBeInTheDocument();
  });

  it('shows each application and when access was granted', () => {
    show();

    expect(screen.getByText('Customer Portal')).toBeInTheDocument();
    expect(screen.getByText(fmtDate('2026-03-04T05:06:07Z'))).toBeInTheDocument();
  });

  it('falls back to the client id for an application with no registered name', () => {
    show();
    expect(screen.getByText('cli-tool')).toBeInTheDocument();
  });

  it('prints an em dash rather than "Invalid Date" when the grant has no timestamp', () => {
    show();
    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('shows placeholders while loading, and no empty-state claim', () => {
    const { container } = render(
      <SessionsDialog userEmail="ada@acme.test" sessions={[]} loading revokeAllLoading={false}
        onClose={vi.fn()} onRevokeAll={vi.fn()} />,
    );

    expect(container.querySelectorAll('.iam-skeleton')).toHaveLength(3);
    expect(screen.queryByText('No active sessions.')).not.toBeInTheDocument();
  });

  it('says the account has no sessions when it has none', () => {
    show({ sessions: [] });
    expect(screen.getByText('No active sessions.')).toBeInTheDocument();
  });
});

describe('revoking', () => {
  it('asks the caller to revoke everything', async () => {
    const { user, onRevokeAll } = show();

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    expect(onRevokeAll).toHaveBeenCalledOnce();
  });

  it('is refused when there is nothing to revoke', () => {
    show({ sessions: [] });
    expect(screen.getByRole('button', { name: 'Revoke all sessions' })).toBeDisabled();
  });

  it('says it is working and refuses a second click meanwhile', () => {
    show({ revokeAllLoading: true });
    expect(screen.getByRole('button', { name: 'Revoking…' })).toBeDisabled();
  });
});

describe('closing', () => {
  it('closes on the button', async () => {
    const { user, onClose } = show();

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('is not shown at all when no account is selected', () => {
    show({ userEmail: null });
    expect(screen.queryByText('No active sessions.')).not.toBeInTheDocument();
  });
});

describe('dismissing it with Escape', () => {
  it('tells the page, so the state behind it is cleared too', async () => {
    const { user, onClose } = show();

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(onClose).toHaveBeenCalled());
  });
});
