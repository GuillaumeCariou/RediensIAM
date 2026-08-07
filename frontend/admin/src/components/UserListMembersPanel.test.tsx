import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import UserListMembersPanel from './UserListMembersPanel';
import { ApiError } from '@/auth';

/**
 * The panel is the console's user-management surface, reused in three scopes. Two things about
 * it are load-bearing and neither is visible from the markup:
 *
 *  - which pair of routes it talks to. The system list is served by /admin/userlists/... and an
 *    organisation's by /org/userlists/...; sending one to the other is a 403 for an org admin and
 *    a cross-tenant read for a super admin. `isSystemCtx` is what picks, and the per-user actions
 *    pass `null` rather than the list id to say "system scope".
 *  - the roles column and the role picker exist only when the panel is mounted for a project.
 */

const api = vi.hoisted(() => ({
  listSystemUserListMembers: vi.fn(), listUserListMembers: vi.fn(),
  addUserToList: vi.fn(), orgAddUserToList: vi.fn(),
  removeSystemUserFromList: vi.fn(), removeUserFromList: vi.fn(),
  adminGetUser: vi.fn(), adminUpdateUser: vi.fn(),
  orgGetUser: vi.fn(), orgUpdateListUser: vi.fn(),
  listProjectUsers: vi.fn(), listRoles: vi.fn(), assignRole: vi.fn(), removeRole: vi.fn(),
  resendInvite: vi.fn(), unlockUser: vi.fn(), getUserSessions: vi.fn(), revokeAllUserSessions: vi.fn(),
}));
vi.mock('@/api', () => api);

const HOUR = 3600_000;
const future = () => new Date(Date.now() + HOUR).toISOString();
const past = () => new Date(Date.now() - HOUR).toISOString();

const ADA = {
  id: 'u1', email: 'ada@acme.test', username: 'ada', discriminator: '0001',
  display_name: 'Ada', active: true, last_login_at: '2026-03-04T05:06:07Z',
};
const MEMBERS = [ADA];

beforeEach(() => {
  vi.clearAllMocks();
  api.listUserListMembers.mockResolvedValue({ users: MEMBERS });
  api.listSystemUserListMembers.mockResolvedValue({ users: MEMBERS });
  api.listProjectUsers.mockResolvedValue({ users: [{ id: 'u1', roles: [{ id: 'r1', name: 'admin' }] }] });
  api.listRoles.mockResolvedValue({ roles: [{ id: 'r1', name: 'admin' }, { id: 'r2', name: 'viewer' }] });
  api.adminGetUser.mockResolvedValue({ ...ADA, phone: '+33600000000', email_verified: true });
  api.orgGetUser.mockResolvedValue({ ...ADA, phone: '+33600000000', email_verified: true });
  api.getUserSessions.mockResolvedValue({ sessions: [{ client_id: 'portal', client_name: 'Portal' }] });
});

function show(props: Partial<React.ComponentProps<typeof UserListMembersPanel>> = {}) {
  const onChanged = vi.fn();
  const user = userEvent.setup();
  render(<UserListMembersPanel listId="l1" onChanged={onChanged} {...props} />);
  return { user, onChanged };
}

describe('which routes the panel talks to', () => {
  it('reads an organisation list through the org routes', async () => {
    show();

    await screen.findByText('ada#0001');
    expect(api.listUserListMembers).toHaveBeenCalledWith('l1');
    expect(api.listSystemUserListMembers).not.toHaveBeenCalled();
  });

  it('reads the system list through the admin routes', async () => {
    show({ isSystemCtx: true });

    await screen.findByText('ada#0001');
    expect(api.listSystemUserListMembers).toHaveBeenCalledWith('l1');
    expect(api.listUserListMembers).not.toHaveBeenCalled();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listUserListMembers.mockResolvedValue(MEMBERS);
    show();

    expect(await screen.findByText('ada#0001')).toBeInTheDocument();
  });

  it('shows placeholder rows while loading', () => {
    api.listUserListMembers.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(3);
  });

  it('survives a list that cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listUserListMembers.mockRejectedValue(new Error('500'));
    show();

    await vi.waitFor(() => expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(0));
    vi.restoreAllMocks();
  });
});

describe('the rows', () => {
  it('shows the handle, the address and the last sign-in', async () => {
    show();

    expect(await screen.findByText('ada#0001')).toBeInTheDocument();
    expect(screen.getByText('ada@acme.test')).toBeInTheDocument();
  });

  it.each([
    ['an invited account that has not accepted', { invite_pending: true }, 'Invite pending'],
    ['an account locked out by failed sign-ins', { locked_until: future() }, 'Locked'],
    ['a deactivated account', { active: false }, 'Inactive'],
    ['a working account', {}, 'Active'],
  ])('marks %s', async (_n, patch, label) => {
    api.listUserListMembers.mockResolvedValue({ users: [{ ...ADA, ...patch }] });
    show();

    expect(await screen.findByText(label)).toBeInTheDocument();
  });

  it('treats an expired lock as no lock', async () => {
    // locked_until is a moment, not a flag: a lock that has run out must not keep the account
    // looking locked, and must not offer an unlock that does nothing.
    api.listUserListMembers.mockResolvedValue({ users: [{ ...ADA, locked_until: past() }] });
    show();

    expect(await screen.findByText('Active')).toBeInTheDocument();
  });

  it('has no roles column outside a project', async () => {
    show();

    await screen.findByText('ada#0001');
    expect(screen.queryByRole('columnheader', { name: 'Roles' })).not.toBeInTheDocument();
    expect(api.listRoles).not.toHaveBeenCalled();
  });

  it('shows each member\'s roles inside one', async () => {
    show({ projectId: 'p1' });

    expect(await screen.findByRole('columnheader', { name: 'Roles' })).toBeInTheDocument();
    expect(screen.getByText('admin')).toBeInTheDocument();
  });
});

describe('adding a user', () => {
  const fillAndSubmit = async (user: ReturnType<typeof show>['user']) => {
    await user.click(screen.getByRole('button', { name: /Add User/ }));
    await user.fill(screen.getByLabelText('Email'), 'grace@acme.test');
    await user.fill(screen.getByLabelText('Username'), 'grace');
    await user.fill(screen.getByLabelText('Password'), 'hunter2hunter2');
    await user.click(screen.getByLabelText('Email verified'));
    // The dialog's submit, not the header button that opened it — both read "Add User".
    await user.click(document.querySelector<HTMLButtonElement>('button[form="userlistmemberspanel-form"]')!);
  };

  const BODY = { email: 'grace@acme.test', username: 'grace', password: 'hunter2hunter2', email_verified: true };

  it('creates the account and reloads the list', async () => {
    const { user, onChanged } = show();
    await screen.findByText('ada#0001');

    await fillAndSubmit(user);

    await vi.waitFor(() => expect(api.orgAddUserToList).toHaveBeenCalledWith('l1', BODY));
    expect(api.listUserListMembers).toHaveBeenCalledTimes(2);
    expect(onChanged).toHaveBeenCalledOnce();
  });

  // La portée était figée sur /admin, réservé au super admin : un org_admin recevait 403 en
  // ajoutant un membre de sa propre liste, et sans catch la boîte restait ouverte, inchangée.
  it.each([
    ['an organisation list', false, 'orgAddUserToList', 'addUserToList'],
    ['the system list', true, 'addUserToList', 'orgAddUserToList'],
  ] as const)('creates through the right route for %s', async (_n, isSystemCtx, used, unused) => {
    const { user } = show({ isSystemCtx });
    await screen.findByText('ada#0001');

    await fillAndSubmit(user);

    await vi.waitFor(() => expect(api[used]).toHaveBeenCalledWith('l1', BODY));
    expect(api[unused]).not.toHaveBeenCalled();
  });

  it('shows what the server refused, and keeps the form open', async () => {
    api.orgAddUserToList.mockRejectedValue(new ApiError(409, { error: 'email_already_exists' }));
    const { user, onChanged } = show();
    await screen.findByText('ada#0001');

    await fillAndSubmit(user);

    expect(await screen.findByText('Someone in this list already uses that address.')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('falls back to a plain sentence when the refusal has no known code', async () => {
    api.orgAddUserToList.mockRejectedValue(new Error('500'));
    const { user } = show();
    await screen.findByText('ada#0001');

    await fillAndSubmit(user);

    expect(await screen.findByText('Failed to add this user.')).toBeInTheDocument();
  });

  it('demands a password long enough to be one', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');

    await user.click(screen.getByRole('button', { name: /Add User/ }));

    expect(screen.getByLabelText('Password')).toHaveAttribute('minlength', '8');
    expect(screen.getByLabelText('Email')).toBeRequired();
  });

  it('closes without creating anything on cancel', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');

    await user.click(screen.getByRole('button', { name: /Add User/ }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.orgAddUserToList).not.toHaveBeenCalled();
  });
});

describe('editing a user', () => {
  it('opens from the row itself and loads the account\'s current details', async () => {
    const { user } = show();

    await user.click(await screen.findByText('ada#0001'));

    expect(await screen.findByLabelText('Phone')).toHaveValue('+33600000000');
    expect(screen.getByLabelText('Email verified')).toBeChecked();
  });

  it('saves the changes and reloads', async () => {
    const { user, onChanged } = show();
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.fill(screen.getByLabelText('Display name'), 'Ada L');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.orgUpdateListUser).toHaveBeenCalledWith('l1', 'u1',
      expect.objectContaining({ display_name: 'Ada L' })));
    expect(onChanged).toHaveBeenCalledOnce();
  });

  // Même défaut que l'ajout : la lecture et l'écriture partaient sur /admin quelle que soit la
  // portée, donc 403 pour un org_admin sur un membre de sa propre liste.
  it.each([
    ['an organisation list', false, 'orgGetUser', 'adminGetUser'],
    ['the system list', true, 'adminGetUser', 'orgGetUser'],
  ] as const)('reads the account through the right route for %s', async (_n, isSystemCtx, used, unused) => {
    const { user } = show({ isSystemCtx });

    await user.click(await screen.findByText('ada#0001'));

    await vi.waitFor(() => expect(api[used]).toHaveBeenCalledWith('u1'));
    expect(api[unused]).not.toHaveBeenCalled();
  });

  it('saves the system list through the admin route, taking no list id', async () => {
    const { user } = show({ isSystemCtx: true });
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.adminUpdateUser).toHaveBeenCalledWith('u1', expect.any(Object)));
    expect(api.orgUpdateListUser).not.toHaveBeenCalled();
  });

  it('shows what the server refused rather than a generic sentence', async () => {
    api.orgUpdateListUser.mockRejectedValue(new ApiError(409, { error: 'email_already_exists' }));
    const { user } = show();
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Someone in this list already uses that address.')).toBeInTheDocument();
  });

  it('leaves the password out of the request when the field was untouched', async () => {
    // Sending an empty string would be a request to set the password to nothing.
    const { user } = show();
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.orgUpdateListUser).toHaveBeenCalledWith('l1', 'u1',
      expect.objectContaining({ new_password: undefined })));
  });

  it('says so, and keeps the dialog open, when the details cannot be read', async () => {
    api.orgGetUser.mockRejectedValue(new Error('500'));
    const { user } = show();

    await user.click(await screen.findByText('ada#0001'));

    expect(await screen.findByText('Failed to load user details.')).toBeInTheDocument();
  });

  it('says so, and keeps the dialog open, when the save is refused', async () => {
    api.orgUpdateListUser.mockRejectedValue(new Error('409'));
    const { user } = show();
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Failed to save changes.')).toBeInTheDocument();
    expect(screen.getByLabelText('Phone')).toBeInTheDocument();
  });
});

describe('the project roles inside the edit dialog', () => {
  const openEdit = async () => {
    const handle = show({ projectId: 'p1', defaultRoleId: 'r1' });
    await handle.user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');
    return handle;
  };

  it('lists the roles the member holds, marking the project default', async () => {
    await openEdit();

    expect(screen.getByText('Project Roles')).toBeInTheDocument();
    expect(screen.getByText('default')).toBeInTheDocument();
  });

  it('offers only the roles they do not already hold', async () => {
    await openEdit();

    const options = [...screen.getByLabelText('Project role to add').querySelectorAll('option')]
      .map(o => o.textContent);
    expect(options).toEqual(['Select a role…', 'viewer']);
  });

  it('assigns one and reloads the roles, not the whole list', async () => {
    const { user } = await openEdit();

    await user.selectOptions(screen.getByLabelText('Project role to add'), 'r2');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await vi.waitFor(() => expect(api.assignRole).toHaveBeenCalledWith('p1', 'u1', 'r2'));
    expect(api.listRoles).toHaveBeenCalledTimes(2);
  });

  it('refuses to assign before a role has been chosen', async () => {
    await openEdit();
    expect(screen.getByRole('button', { name: 'Add' })).toBeDisabled();
  });

  it('removes one without waiting for a reload', async () => {
    const { user } = await openEdit();

    // The unlabelled button inside the role chip, not the row's menu trigger.
    await user.click(document.querySelector<HTMLButtonElement>('.iam-chip button')!);

    await vi.waitFor(() => expect(api.removeRole).toHaveBeenCalledWith('p1', 'u1', 'r1'));
    expect(await screen.findByText('No roles assigned')).toBeInTheDocument();
  });

  it('hides the picker once every role is held', async () => {
    api.listRoles.mockResolvedValue({ roles: [{ id: 'r1', name: 'admin' }] });
    await openEdit();

    expect(screen.queryByLabelText('Project role to add')).not.toBeInTheDocument();
  });
});

describe('removing a user', () => {
  it('asks first', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');
    await user.click(screen.getAllByRole('button').at(-1)!);

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    expect(await screen.findByText(/Remove ada@acme.test/)).toBeInTheDocument();
    expect(api.removeUserFromList).not.toHaveBeenCalled();
  });

  it.each([
    ['an organisation list', false, 'removeUserFromList'],
    ['the system list', true, 'removeSystemUserFromList'],
  ] as const)('deletes through the right route for %s', async (_n, isSystemCtx, fn) => {
    const { user, onChanged } = show({ isSystemCtx });
    await screen.findByText('ada#0001');
    await user.click(screen.getAllByRole('button').at(-1)!);
    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    await user.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);

    await vi.waitFor(() => expect(api[fn]).toHaveBeenCalledWith('l1', 'u1'));
    expect(screen.queryByText('ada#0001')).not.toBeInTheDocument();
    expect(onChanged).toHaveBeenCalledOnce();
  });
});

describe('the per-user actions', () => {
  const openMenu = async (patch: Record<string, unknown> = {}, props = {}) => {
    api.listUserListMembers.mockResolvedValue({ users: [{ ...ADA, ...patch }] });
    api.listSystemUserListMembers.mockResolvedValue({ users: [{ ...ADA, ...patch }] });
    const handle = show(props);
    await screen.findByText('ada#0001');
    await handle.user.click(screen.getAllByRole('button').at(-1)!);
    return handle;
  };

  it('offers a resend only to an account that has not accepted its invitation', async () => {
    await openMenu();
    expect(screen.queryByRole('button', { name: 'Resend invite' })).not.toBeInTheDocument();
  });

  it('resends the invitation', async () => {
    api.resendInvite.mockResolvedValue({});
    const { user } = await openMenu({ invite_pending: true });

    await user.click(screen.getByRole('button', { name: 'Resend invite' }));

    expect(await screen.findByText('Invite resent to ada@acme.test.')).toBeInTheDocument();
  });

  it('says so when the invitation had already been accepted', async () => {
    // The endpoint answers 200 with an error field, so this is not a rejection to catch.
    api.resendInvite.mockResolvedValue({ error: 'user_already_active' });
    const { user } = await openMenu({ invite_pending: true });

    await user.click(screen.getByRole('button', { name: 'Resend invite' }));

    expect(await screen.findByText('This user has already accepted their invitation.')).toBeInTheDocument();
  });

  it('says so when the resend fails outright', async () => {
    api.resendInvite.mockRejectedValue(new Error('500'));
    const { user } = await openMenu({ invite_pending: true });

    await user.click(screen.getByRole('button', { name: 'Resend invite' }));

    expect(await screen.findByText('Failed to resend invite.')).toBeInTheDocument();
  });

  it('offers an unlock only to a locked account', async () => {
    await openMenu();
    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
  });

  it.each([
    ['an organisation list', {}, 'l1'],
    // null, not the list id: the system-scoped unlock route takes the user id alone.
    ['the system list', { isSystemCtx: true }, null],
  ] as const)('unlocks through the right route for %s', async (_n, props, expected) => {
    const { user } = await openMenu({ locked_until: future() }, props);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    await vi.waitFor(() => expect(api.unlockUser).toHaveBeenCalledWith(expected, 'u1'));
    expect(await screen.findByText('Account unlocked.')).toBeInTheDocument();
  });

  it('says so when the unlock fails', async () => {
    api.unlockUser.mockRejectedValue(new Error('500'));
    const { user } = await openMenu({ locked_until: future() });

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    expect(await screen.findByText('Failed to unlock account.')).toBeInTheDocument();
  });

  it('takes the message away again so it cannot be read as still true', async () => {
    api.unlockUser.mockResolvedValue({});
    const { user } = await openMenu({ locked_until: future() });
    await user.click(screen.getByRole('button', { name: 'Unlock account' }));
    await screen.findByText('Account unlocked.');

    await vi.waitFor(() => expect(screen.queryByText('Account unlocked.')).toBeNull(), { timeout: 6000 });
  }, 10_000);
});

describe('a user\'s sessions', () => {
  const openSessions = async (props = {}) => {
    const handle = show(props);
    await screen.findByText('ada#0001');
    await handle.user.click(screen.getAllByRole('button').at(-1)!);
    await handle.user.click(screen.getByRole('button', { name: 'View sessions' }));
    return handle;
  };

  it.each([
    ['an organisation list', {}, 'l1'],
    ['the system list', { isSystemCtx: true }, null],
  ] as const)('reads them through the right route for %s', async (_n, props, expected) => {
    await openSessions(props);

    await vi.waitFor(() => expect(api.getUserSessions).toHaveBeenCalledWith(expected, 'u1'));
    expect(await screen.findByText('Portal')).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.getUserSessions.mockResolvedValue([{ client_id: 'portal', client_name: 'Portal' }]);
    await openSessions();

    expect(await screen.findByText('Portal')).toBeInTheDocument();
  });

  it('shows an empty list rather than stale sessions when the read fails', async () => {
    api.getUserSessions.mockRejectedValue(new Error('500'));
    await openSessions();

    expect(await screen.findByText('No active sessions.')).toBeInTheDocument();
  });

  it('revokes them all', async () => {
    const { user } = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    await vi.waitFor(() => expect(api.revokeAllUserSessions).toHaveBeenCalledWith('l1', 'u1'));
    expect(await screen.findByText('All sessions revoked.')).toBeInTheDocument();
  });

  it('says so when the revoke fails', async () => {
    api.revokeAllUserSessions.mockRejectedValue(new Error('500'));
    const { user } = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    expect(await screen.findByText('Failed to revoke sessions.')).toBeInTheDocument();
  });

  it('forgets them on close, so the next user does not open onto someone else\'s', async () => {
    const { user } = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(screen.queryByText('Portal')).not.toBeInTheDocument();
  });
});

describe('the remaining ways in and out of a dialog', () => {
  it('opens the editor from the row menu as well as from the row', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');
    await user.click(screen.getAllByRole('button').at(-1)!);

    await user.click(screen.getByRole('button', { name: 'Edit' }));

    expect(await screen.findByLabelText('Phone')).toHaveValue('+33600000000');
  });

  it('closes the add form on Escape', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');
    await user.click(screen.getByRole('button', { name: /Add User/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Password')).toBeNull());
    expect(api.orgAddUserToList).not.toHaveBeenCalled();
  });

  it('closes the editor on Escape', async () => {
    const { user } = show();
    await user.click(await screen.findByText('ada#0001'));
    await screen.findByLabelText('Phone');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Phone')).toBeNull());
    expect(api.orgUpdateListUser).not.toHaveBeenCalled();
  });

  it('closes the remove confirmation on Escape, and on Cancel', async () => {
    const { user } = show();
    await screen.findByText('ada#0001');
    await user.click(screen.getAllByRole('button').at(-1)!);
    await user.click(await screen.findByRole('button', { name: 'Remove' }));
    await screen.findByText(/Remove ada@acme.test/);

    await user.click(screen.getAllByRole('button', { name: 'Cancel' }).at(-1)!);
    expect(api.removeUserFromList).not.toHaveBeenCalled();

    await user.click(screen.getAllByRole('button').at(-1)!);
    await user.click(await screen.findByRole('button', { name: 'Remove' }));
    await screen.findByText(/Remove ada@acme.test/);
    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText(/Remove ada@acme.test/)).toBeNull());
    expect(api.removeUserFromList).not.toHaveBeenCalled();
  });
});
