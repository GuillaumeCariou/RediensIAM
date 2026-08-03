import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import SystemUsers from './Users';
import { fmtDate } from '@/lib/utils';

/**
 * A search across every tenant, so it starts empty on purpose: there is no "all users" listing to
 * page through, and the table only appears once something has been asked for. Every write here
 * goes through the system-scoped routes, which take a null list id.
 */

const api = vi.hoisted(() => ({
  searchUsers: vi.fn(), adminGetUser: vi.fn(), adminUpdateUser: vi.fn(),
  unlockUser: vi.fn(), getUserSessions: vi.fn(), revokeAllUserSessions: vi.fn(),
}));
vi.mock('@/api', () => api);

const HOUR = 3600_000;
const future = () => new Date(Date.now() + HOUR).toISOString();
const past = () => new Date(Date.now() - HOUR).toISOString();

const ADA = {
  id: 'u1', email: 'ada@acme.test', username: 'ada', discriminator: '0001',
  display_name: 'Ada Lovelace', active: true, last_login_at: '2026-03-04T05:06:07Z',
  org_name: 'Acme', user_list_name: 'Staff', org_id: 'o1',
};

beforeEach(() => {
  vi.clearAllMocks();
  api.searchUsers.mockResolvedValue({ users: [ADA] });
  api.adminGetUser.mockResolvedValue({ ...ADA, phone: '+33600000000', email_verified: true });
  api.getUserSessions.mockResolvedValue({ sessions: [{ client_id: 'portal', client_name: 'Portal' }] });
});

function show() {
  const user = userEvent.setup();
  render(<SystemUsers />);
  return user;
}

const box = () => screen.getByPlaceholderText('Search by email, username…');
/** Runs a search for `q` and waits for the results. */
async function search(user: Awaited<ReturnType<typeof show>>, q = 'ada') {
  await user.fill(box(), q);
  await user.click(screen.getByRole('button', { name: 'Search' }));
  return screen.findByText('Ada Lovelace');
}
const openMenu = (user: Awaited<ReturnType<typeof show>>) =>
  user.click([...screen.getByRole('row', { name: /ada/ }).querySelectorAll('button')].at(-1)!);

describe('before anything is searched for', () => {
  it('shows no table at all — an empty one would read as "no users exist"', () => {
    show();

    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText('No users found')).not.toBeInTheDocument();
  });

  it('will not search for nothing', async () => {
    const user = show();

    await user.fill(box(), '   ');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(api.searchUsers).not.toHaveBeenCalled();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});

describe('searching', () => {
  it('searches from the button', async () => {
    const user = show();

    await search(user);

    expect(api.searchUsers).toHaveBeenCalledWith('ada');
  });

  it('searches on Enter, which is what anyone typing will press', async () => {
    const user = show();

    await user.fill(box(), 'ada');
    await user.keyboard('{Enter}');

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
  });

  it('shows the tenant and list each match belongs to', async () => {
    const user = show();

    await search(user);

    expect(screen.getByText('Acme')).toBeInTheDocument();
    expect(screen.getByText('Staff')).toBeInTheDocument();
    expect(screen.getByText('ada#0001')).toBeInTheDocument();
    expect(screen.getByText(fmtDate(ADA.last_login_at))).toBeInTheDocument();
  });

  it('falls back to the username where there is no display name', async () => {
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, display_name: null }] });
    const user = show();
    await user.fill(box(), 'ada');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('ada', { selector: 'div' })).toBeInTheDocument();
  });

  it.each([
    ['a disabled account', { active: false }, 'Disabled'],
    ['a locked one', { locked_until: future() }, 'Locked'],
  ])('marks %s', async (_n, patch, label) => {
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, ...patch }] });
    const user = show();
    await user.fill(box(), 'ada');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText(label)).toBeInTheDocument();
  });

  it('treats a lock that has run out as no lock', async () => {
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, locked_until: past() }] });
    const user = show();
    await user.fill(box(), 'ada');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await screen.findByText('Ada Lovelace');
    expect(screen.queryByText('Locked')).not.toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.searchUsers.mockResolvedValue([ADA]);
    const user = show();

    await search(user);

    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
  });

  it('says nothing matched', async () => {
    api.searchUsers.mockResolvedValue({ users: [] });
    const user = show();
    await user.fill(box(), 'zzz');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('No users found')).toBeInTheDocument();
  });

  it('says nothing matched rather than keeping the last results when it fails', async () => {
    const user = show();
    await search(user);
    api.searchUsers.mockRejectedValue(new Error('500'));

    await user.fill(box(), 'grace');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('No users found')).toBeInTheDocument();
    expect(screen.queryByText('Ada Lovelace')).not.toBeInTheDocument();
  });

  it('shows placeholder rows and blocks a second search while one is running', async () => {
    api.searchUsers.mockReturnValue(new Promise(() => {}));
    const user = show();
    await user.fill(box(), 'ada');

    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
    expect(document.querySelectorAll('tbody tr')).toHaveLength(3);
  });
});

describe('editing a match', () => {
  it('opens from the row and loads the full account', async () => {
    const user = show();
    await search(user);

    await user.click(screen.getByText('Ada Lovelace'));

    expect(await screen.findByLabelText('Phone')).toHaveValue('+33600000000');
    expect(api.adminGetUser).toHaveBeenCalledWith('u1');
  });

  it('opens from the row menu too', async () => {
    const user = show();
    await search(user);
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Edit' }));

    expect(await screen.findByLabelText('Phone')).toBeInTheDocument();
  });

  it('saves, and reflects the change in the row without a second search', async () => {
    // There is no list to reload here — the results are a search, not a resource.
    const user = show();
    await search(user);
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.fill(screen.getByLabelText('Display name'), 'Ada L');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.adminUpdateUser).toHaveBeenCalledWith('u1',
      expect.objectContaining({ display_name: 'Ada L', new_password: undefined })));
    expect(await screen.findByText('Ada L')).toBeInTheDocument();
    expect(api.searchUsers).toHaveBeenCalledOnce();
  });

  it('falls back to the username in the row when the display name is cleared', async () => {
    const user = show();
    await search(user);
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.fill(screen.getByLabelText('Display name'), '');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('ada', { selector: 'div' })).toBeInTheDocument();
  });

  it('says so when the account cannot be read', async () => {
    api.adminGetUser.mockRejectedValue(new Error('500'));
    const user = show();
    await search(user);

    await user.click(screen.getByText('Ada Lovelace'));

    expect(await screen.findByText('Failed to load user details.')).toBeInTheDocument();
  });

  it('says so, and stays open, when the save is refused', async () => {
    api.adminUpdateUser.mockRejectedValue(new Error('409'));
    const user = show();
    await search(user);
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Failed to save changes.')).toBeInTheDocument();
    expect(screen.getByLabelText('Phone')).toBeInTheDocument();
  });
});

describe('the row menu', () => {
  it('offers an unlock only to a locked account', async () => {
    const user = show();
    await search(user);

    await openMenu(user);

    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
  });

  it('unlocks through the system route and clears the badge without re-searching', async () => {
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, locked_until: future() }] });
    const user = show();
    await search(user);
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    // null, not a list id: the system-scoped unlock takes the user alone.
    await vi.waitFor(() => expect(api.unlockUser).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('Account unlocked.')).toBeInTheDocument();
    expect(screen.queryByText('Locked')).not.toBeInTheDocument();
  });

  it('says so when the unlock fails, and leaves the badge alone', async () => {
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, locked_until: future() }] });
    api.unlockUser.mockRejectedValue(new Error('500'));
    const user = show();
    await search(user);
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    expect(await screen.findByText('Failed to unlock account.')).toBeInTheDocument();
    expect(screen.getByText('Locked')).toBeInTheDocument();
  });

  it('takes the message away again', async () => {
    // clearAllMocks keeps implementations, so the rejection set above has to be undone here.
    api.unlockUser.mockResolvedValue({});
    api.searchUsers.mockResolvedValue({ users: [{ ...ADA, locked_until: future() }] });
    const user = show();
    await search(user);
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Unlock account' }));
    await screen.findByText('Account unlocked.');

    await vi.waitFor(() => expect(screen.queryByText('Account unlocked.')).toBeNull(), { timeout: 6000 });
  }, 10_000);

  it('closes on Escape', async () => {
    const user = show();
    await search(user);
    await openMenu(user);

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
  });

  it('closes when the operator clicks away', async () => {
    const user = show();
    await search(user);
    await openMenu(user);

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
  });

  it('does not open the editor when the menu itself is used', async () => {
    const user = show();
    await search(user);

    await openMenu(user);

    expect(screen.queryByLabelText('Phone')).not.toBeInTheDocument();
  });
});

describe('a user\'s sessions', () => {
  const openSessions = async () => {
    const user = show();
    await search(user);
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View sessions' }));
    return user;
  };

  it('reads them through the system route', async () => {
    await openSessions();

    await vi.waitFor(() => expect(api.getUserSessions).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('Portal')).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.getUserSessions.mockResolvedValue([{ client_id: 'portal', client_name: 'Portal' }]);
    await openSessions();

    expect(await screen.findByText('Portal')).toBeInTheDocument();
  });

  it('shows none rather than stale sessions when the read fails', async () => {
    api.getUserSessions.mockRejectedValue(new Error('500'));
    await openSessions();

    expect(await screen.findByText('No active sessions.')).toBeInTheDocument();
  });

  it('revokes them all', async () => {
    const user = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    await vi.waitFor(() => expect(api.revokeAllUserSessions).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('All sessions revoked.')).toBeInTheDocument();
  });

  it('says so when the revoke fails', async () => {
    api.revokeAllUserSessions.mockRejectedValue(new Error('500'));
    const user = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    expect(await screen.findByText('Failed to revoke sessions.')).toBeInTheDocument();
  });

  it('forgets them on close', async () => {
    const user = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(screen.queryByText('Portal')).not.toBeInTheDocument();
  });
});


describe('dismissing the editor with Escape', () => {
  it('closes it without saving', async () => {
    const user = show();
    await search(user);
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Phone')).toBeNull());
    expect(api.adminUpdateUser).not.toHaveBeenCalled();
  });
});
