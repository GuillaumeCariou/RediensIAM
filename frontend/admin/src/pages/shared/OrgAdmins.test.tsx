import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import OrgAdmins from './OrgAdmins';
import { fmtDate } from '@/lib/utils';

/**
 * Every read and write on this page comes in a pair — the /admin route a super admin uses on
 * someone else's organisation, and the token-scoped /org route an org admin uses on their own.
 * `isSystemCtx` picks, and picking wrong is either a 403 or a cross-tenant grant of admin rights.
 */

const api = vi.hoisted(() => ({
  listOrgAdmins: vi.fn(), assignOrgAdmin: vi.fn(), removeOrgAdmin: vi.fn(),
  listOrgListManagers: vi.fn(), assignOrgListManager: vi.fn(), removeOrgListManager: vi.fn(),
  adminGetUser: vi.fn(), adminUpdateUser: vi.fn(), orgGetUser: vi.fn(), orgUpdateUser: vi.fn(),
  listProjects: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ADMINS = [
  {
    id: 'g1', user_id: 'u1', user_email: 'ada@acme.test', user_name: 'Ada',
    role: 'org_admin', scope_id: null, scope_name: null,
    granted_at: '2026-01-02T00:00:00Z', active: true,
  },
  {
    id: 'g2', user_id: 'u2', user_email: 'grace@acme.test', user_name: 'Grace',
    role: 'project_admin', scope_id: '0123456789abcdef', scope_name: null,
    granted_at: '2026-01-02T00:00:00Z', active: false,
  },
  {
    id: 'g3', user_id: 'u3', user_email: 'alan@acme.test', user_name: 'Alan',
    role: 'project_admin', scope_id: 'p1', scope_name: 'Portal',
    granted_at: '2026-01-02T00:00:00Z',
  },
];
const PROJECTS = [{ id: 'p1', name: 'Portal' }, { id: 'p2', name: 'Tools' }];
const USER = { email: 'ada@acme.test', username: 'ada', display_name: 'Ada', phone: '+33600000000', active: true, email_verified: true };

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.listOrgAdmins.mockResolvedValue({ admins: ADMINS });
  api.listOrgListManagers.mockResolvedValue({ admins: ADMINS });
  api.listProjects.mockResolvedValue({ projects: PROJECTS });
  api.adminGetUser.mockResolvedValue(USER);
  api.orgGetUser.mockResolvedValue(USER);
});

const SYSTEM = ['/system/organisations/o9/admins', '/system/organisations/:id/admins'] as const;

function show(path = '/org/admins', pattern = '/org/admins') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<OrgAdmins />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });
const editButton = (name: string) => rowFor(name).querySelectorAll('button')[0];
const removeButton = (name: string) => rowFor(name).querySelectorAll('button')[1];

describe('which routes the page uses', () => {
  it('reads the token-scoped list for an org admin', async () => {
    show();

    await screen.findByText('Ada');
    expect(api.listOrgListManagers).toHaveBeenCalledOnce();
    expect(api.listOrgAdmins).not.toHaveBeenCalled();
  });

  it('reads the named organisation for a super admin', async () => {
    show(...SYSTEM);

    await screen.findByText('Ada');
    expect(api.listOrgAdmins).toHaveBeenCalledWith('o9');
    expect(api.listOrgListManagers).not.toHaveBeenCalled();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listOrgListManagers.mockResolvedValue(ADMINS);
    api.listProjects.mockResolvedValue(PROJECTS);
    show();

    expect(await screen.findByText('Ada')).toBeInTheDocument();
  });

  it('asks for nothing without an organisation in scope', async () => {
    auth.orgId = '';
    show();

    await vi.waitFor(() => expect(document.querySelectorAll('.iam-skeleton').length).toBeGreaterThan(0));
    expect(api.listOrgListManagers).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /Assign Role/ })).not.toBeInTheDocument();
  });
});

describe('the table', () => {
  it('shows each grant with its holder, role and date', async () => {
    show();

    await screen.findByText('Ada');
    expect(rowFor('Ada')).toHaveTextContent('ada@acme.test');
    expect(rowFor('Ada')).toHaveTextContent('org_admin');
    expect(rowFor('Ada')).toHaveTextContent(fmtDate('2026-01-02T00:00:00Z'));
  });

  it.each([
    ['a grant over the whole organisation', 'Ada', 'Entire org'],
    ['one named after its project', 'Alan', 'Portal'],
    // The id is all the server sent; eight characters still identify which project it was.
    ['one whose project the server did not name', 'Grace', '01234567…'],
  ])('shows the scope of %s', async (_n, who, scope) => {
    show();

    await screen.findByText(who);
    expect(rowFor(who)).toHaveTextContent(scope);
  });

  it.each([
    ['Ada', 'Active'],
    ['Grace', 'Disabled'],
  ])('shows whether %s\'s account is usable', async (who, status) => {
    show();

    await screen.findByText(who);
    expect(rowFor(who)).toHaveTextContent(status);
  });

  it('prints an em dash where the server said nothing about the account', async () => {
    // Absent is not the same as disabled — claiming the latter would read as a revoked account.
    show();

    await screen.findByText('Alan');
    expect(rowFor('Alan')).toHaveTextContent('—');
    expect(rowFor('Alan')).not.toHaveTextContent('Disabled');
  });

  it('shows placeholder rows while loading', () => {
    api.listOrgListManagers.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(18);
    expect(screen.queryByText('No admins assigned yet.')).not.toBeInTheDocument();
  });

  it('says there are none when there are none', async () => {
    api.listOrgListManagers.mockResolvedValue({ admins: [] });
    show();

    expect(await screen.findByText('No admins assigned yet.')).toBeInTheDocument();
  });
});

describe('granting a role', () => {
  const openAssign = async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));
    await user.fill(screen.getByLabelText('User ID'), 'u9');
    return user;
  };

  it('offers no project picker for an organisation-wide role', async () => {
    await openAssign();
    expect(screen.queryByLabelText('Project scope')).not.toBeInTheDocument();
  });

  it('asks which project a project admin gets', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');

    const options = [...screen.getByLabelText('Project scope').querySelectorAll('option')]
      .map(o => o.textContent);
    expect(options).toEqual(['Select a project…', 'Portal', 'Tools']);
  });

  it('forgets a chosen project when the role goes back to organisation-wide', async () => {
    // Otherwise an org_admin grant carries a stale project scope the server would honour.
    const user = await openAssign();
    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    await user.selectOptions(screen.getByLabelText('Project scope'), 'p1');

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgListManager)
      .toHaveBeenCalledWith({ user_id: 'u9', role: 'org_admin', scope_id: undefined }));
  });

  it('grants through the token-scoped route for an org admin', async () => {
    const user = await openAssign();

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgListManager).toHaveBeenCalled());
    expect(api.assignOrgAdmin).not.toHaveBeenCalled();
    expect(api.listOrgListManagers).toHaveBeenCalledTimes(2);
  });

  it('grants against the named organisation for a super admin', async () => {
    const user = show(...SYSTEM);
    await screen.findByText('Ada');
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));
    await user.fill(screen.getByLabelText('User ID'), 'u9');
    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    await user.selectOptions(screen.getByLabelText('Project scope'), 'p2');

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgAdmin).toHaveBeenCalledWith('o9', 'u9', 'project_admin', 'p2'));
    expect(api.assignOrgListManager).not.toHaveBeenCalled();
  });

  it('requires a user', async () => {
    const user = show();
    await screen.findByText('Ada');

    await user.click(screen.getByRole('button', { name: /Assign Role/ }));

    expect(screen.getByLabelText('User ID')).toBeRequired();
  });

  it('closes without granting anything', async () => {
    const user = await openAssign();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.assignOrgListManager).not.toHaveBeenCalled();
  });
});

describe('editing an admin\'s account', () => {
  it('reads it through the token-scoped route for an org admin', async () => {
    const user = show();
    await screen.findByText('Ada');

    await user.click(editButton('Ada'));

    expect(await screen.findByLabelText('Phone')).toHaveValue('+33600000000');
    expect(api.orgGetUser).toHaveBeenCalledWith('u1');
    expect(api.adminGetUser).not.toHaveBeenCalled();
  });

  it('reads it through the admin route for a super admin', async () => {
    const user = show(...SYSTEM);
    await screen.findByText('Ada');

    await user.click(editButton('Ada'));

    await screen.findByLabelText('Phone');
    expect(api.adminGetUser).toHaveBeenCalledWith('u1');
    expect(api.orgGetUser).not.toHaveBeenCalled();
  });

  it('saves through the matching route and reloads', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(editButton('Ada'));
    await screen.findByLabelText('Phone');

    await user.fill(screen.getByLabelText('Display name'), 'Ada L');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.orgUpdateUser).toHaveBeenCalledWith('u1',
      expect.objectContaining({ display_name: 'Ada L', new_password: undefined })));
    expect(api.adminUpdateUser).not.toHaveBeenCalled();
  });

  it('saves through the admin route for a super admin', async () => {
    const user = show(...SYSTEM);
    await screen.findByText('Ada');
    await user.click(editButton('Ada'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.adminUpdateUser).toHaveBeenCalled());
    expect(api.orgUpdateUser).not.toHaveBeenCalled();
  });

  it('says so when the account cannot be read', async () => {
    api.orgGetUser.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('Ada');

    await user.click(editButton('Ada'));

    expect(await screen.findByText('Failed to load user details.')).toBeInTheDocument();
  });

  it('says so, and stays open, when the save is refused', async () => {
    api.orgUpdateUser.mockRejectedValue(new Error('409'));
    const user = show();
    await screen.findByText('Ada');
    await user.click(editButton('Ada'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Failed to save changes.')).toBeInTheDocument();
    expect(screen.getByLabelText('Phone')).toBeInTheDocument();
  });

  it('closes without saving', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(editButton('Ada'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.orgUpdateUser).not.toHaveBeenCalled();
  });
});

describe('revoking a role', () => {
  it('names the grant, not just the person, and asks first', async () => {
    const user = show();
    await screen.findByText('Grace');

    await user.click(removeButton('Grace'));

    expect(await screen.findByText('Remove Grace — project_admin?')).toBeInTheDocument();
    expect(api.removeOrgListManager).not.toHaveBeenCalled();
  });

  it('revokes through the token-scoped route for an org admin', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(removeButton('Ada'));

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    await vi.waitFor(() => expect(api.removeOrgListManager).toHaveBeenCalledWith('g1'));
    expect(api.removeOrgAdmin).not.toHaveBeenCalled();
    expect(api.listOrgListManagers).toHaveBeenCalledTimes(2);
  });

  it('revokes against the named organisation for a super admin', async () => {
    const user = show(...SYSTEM);
    await screen.findByText('Ada');
    await user.click(removeButton('Ada'));

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    await vi.waitFor(() => expect(api.removeOrgAdmin).toHaveBeenCalledWith('o9', 'g1'));
    expect(api.removeOrgListManager).not.toHaveBeenCalled();
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(removeButton('Ada'));
    await screen.findByText('Remove Ada — org_admin?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.removeOrgListManager).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  it('closes the assign form without granting anything', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('User ID')).toBeNull());
    expect(api.assignOrgListManager).not.toHaveBeenCalled();
  });

  it('closes the revoke confirmation without revoking', async () => {
    const user = show();
    await screen.findByText('Ada');
    await user.click(removeButton('Ada'));
    await screen.findByText('Remove Ada — org_admin?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Remove Ada — org_admin?')).toBeNull());
    expect(api.removeOrgListManager).not.toHaveBeenCalled();
  });
});
