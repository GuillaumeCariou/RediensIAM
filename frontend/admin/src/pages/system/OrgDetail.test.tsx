import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import OrgDetail from './OrgDetail';
import { ApiError } from '@/auth';
import { fmtDateShort } from '@/lib/utils';

/**
 * The super admin's view of one tenant: its admin user list, its service accounts, its user lists
 * and its projects, all read through the /admin routes for that organisation rather than the
 * caller's own. Suspending and deleting are behind `isSuperAdmin` — an org admin who reaches this
 * page can rename and manage users, but not switch the tenant off.
 */

const api = vi.hoisted(() => ({
  getOrg: vi.fn(), suspendOrg: vi.fn(), unsuspendOrg: vi.fn(), updateOrg: vi.fn(), deleteOrg: vi.fn(),
  listSystemUserListMembers: vi.fn(), listOrgAdmins: vi.fn(), assignOrgAdmin: vi.fn(),
  addUserToList: vi.fn(), removeSystemUserFromList: vi.fn(),
  listServiceAccounts: vi.fn(), listUserLists: vi.fn(), adminCreateUserList: vi.fn(),
  listProjects: vi.fn(), adminCreateProject: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '', isSuperAdmin: true }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ORG = {
  id: 'o1', name: 'Acme', slug: 'acme', active: true, suspended_at: null,
  created_at: '2026-01-02T00:00:00Z', org_list_id: 'ol1',
};
const MEMBERS = [
  { id: 'u1', username: 'ada', discriminator: '0001', email: 'ada@acme.test', active: true },
  { id: 'u2', username: 'grace', discriminator: '0002', email: 'grace@acme.test', active: true },
];
const ROLES = [
  { id: 'g1', user_id: 'u1', user_name: 'Ada', user_email: 'ada@acme.test', role: 'org_admin', scope_id: null, scope_name: null, granted_at: '2026-01-02T00:00:00Z' },
  { id: 'g2', user_id: 'u1', user_name: 'Ada', user_email: 'ada@acme.test', role: 'project_admin', scope_id: 'p1', scope_name: 'Portal', granted_at: '2026-01-02T00:00:00Z' },
];
const SAS = [
  { id: 's1', name: 'ci-deploy', description: 'CI', active: true, last_used_at: '2026-03-04T05:06:07Z', org_id: 'o1' },
  { id: 's2', name: 'other-tenant', description: null, active: false, last_used_at: null, org_id: 'o2' },
];
const LISTS = [
  { id: 'l1', name: 'Staff', immovable: false },
  { id: 'ol1', name: 'Org admins', immovable: true },
];
const PROJECTS = [
  { id: 'p1', name: 'Portal', slug: 'portal', active: true, assigned_user_list_id: 'l1' },
  { id: 'p2', name: 'Tools', slug: 'tools', active: false, assigned_user_list_id: null },
  { id: 'p3', name: 'Legacy', slug: 'legacy', active: true, assigned_user_list_id: '0123456789abcdef' },
];

beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(auth, { orgId: 'o1', projectId: '', isSuperAdmin: true });
  api.getOrg.mockResolvedValue(ORG);
  api.listSystemUserListMembers.mockResolvedValue(MEMBERS);
  api.listOrgAdmins.mockResolvedValue(ROLES);
  api.listServiceAccounts.mockResolvedValue(SAS);
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
  api.listProjects.mockResolvedValue({ projects: PROJECTS });
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show() {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/system/organisations/o1']}>
      <Routes><Route path="/system/organisations/:id" element={<OrgDetail />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const loaded = () => screen.findByRole('heading', { name: 'Acme' });
const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));
const section = (heading: string | RegExp) =>
  within(screen.getByRole('heading', { name: heading }).closest('.rounded-xl, .space-y-3')!);
const rowFor = (heading: string | RegExp, label: string) =>
  section(heading).getByRole('row', { name: new RegExp(label) });

describe('the organisation', () => {
  it('names it, with its slug and creation date', async () => {
    show();

    await loaded();
    expect(screen.getByText(`/acme · Created ${fmtDateShort(ORG.created_at)}`)).toBeInTheDocument();
    // The header chip, plus the service account's and the two active projects' own.
    expect(screen.getAllByText('Active')).toHaveLength(4);
  });

  it('marks a suspended tenant', async () => {
    api.getOrg.mockResolvedValue({ ...ORG, suspended_at: '2026-04-01T00:00:00Z' });
    show();

    expect(await screen.findByText('Suspended')).toBeInTheDocument();
  });

  it('reads everything against this organisation, including its own admin list', async () => {
    show();

    await loaded();
    expect(api.listSystemUserListMembers).toHaveBeenCalledWith('ol1');
    expect(api.listOrgAdmins).toHaveBeenCalledWith('o1');
    expect(api.listUserLists).toHaveBeenCalledWith('o1');
    expect(api.listProjects).toHaveBeenCalledWith('o1');
  });

  it('shows placeholders, and no actions, while loading', () => {
    api.getOrg.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: /Rename/ })).not.toBeInTheDocument();
  });

  it('finishes loading when the organisation cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getOrg.mockRejectedValue(new Error('404'));
    show();

    expect(await screen.findByText('No users in org list.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('goes back to the list of organisations', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Back to Organisations/ }));

    await arrivedAt('/system/organisations');
  });
});

describe('what an org admin may do here', () => {
  beforeEach(() => { auth.isSuperAdmin = false; });

  it('can rename, but cannot suspend or delete the tenant', async () => {
    show();
    await loaded();

    expect(screen.getByRole('button', { name: /Rename/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Suspend/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Delete/ })).not.toBeInTheDocument();
  });
});

describe('suspending', () => {
  it('suspends a running tenant and re-reads', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Suspend/ }));

    await vi.waitFor(() => expect(api.suspendOrg).toHaveBeenCalledWith('o1'));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('unsuspends a suspended one', async () => {
    api.getOrg.mockResolvedValue({ ...ORG, suspended_at: '2026-04-01T00:00:00Z' });
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Unsuspend/ }));

    await vi.waitFor(() => expect(api.unsuspendOrg).toHaveBeenCalledWith('o1'));
  });
});

describe('renaming', () => {
  it('opens on the current name and saves the new one', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Rename/ }));
    expect(screen.getByLabelText('Name')).toHaveValue('Acme');
    await user.fill(screen.getByLabelText('Name'), 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateOrg).toHaveBeenCalledWith('o1', { name: 'Acme Corp' }));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('refuses a blank name, and does nothing on cancel', async () => {
    const user = show();
    await loaded();
    await user.click(screen.getByRole('button', { name: /Rename/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.updateOrg).not.toHaveBeenCalled();
  });
});

describe('deleting the tenant', () => {
  it('warns what goes with it, and asks first', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Delete/ }));

    expect(await screen.findByText('Delete organisation "Acme"?')).toBeInTheDocument();
    expect(screen.getByText(/All user lists, projects, and service accounts/)).toBeInTheDocument();
    expect(api.deleteOrg).not.toHaveBeenCalled();
  });

  it('deletes and leaves for the list', async () => {
    const user = show();
    await loaded();
    await user.click(screen.getByRole('button', { name: /Delete/ }));

    await user.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);

    await vi.waitFor(() => expect(api.deleteOrg).toHaveBeenCalledWith('o1'));
    await arrivedAt('/system/organisations');
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await loaded();
    await user.click(screen.getByRole('button', { name: /Delete/ }));
    await screen.findByText('Delete organisation "Acme"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.deleteOrg).not.toHaveBeenCalled();
  });
});

describe('the organisation\'s admin user list', () => {
  it('shows each member with the roles they hold', async () => {
    show();
    await loaded();

    expect(rowFor('Org User List', 'ada')).toHaveTextContent('ada#0001');
    expect(rowFor('Org User List', 'ada')).toHaveTextContent('Org Admin');
    // A project role is shown by the project it is over, not by its raw name.
    expect(rowFor('Org User List', 'ada')).toHaveTextContent('PM: Portal');
  });

  it('says plainly when a member holds none', async () => {
    show();
    await loaded();

    expect(rowFor('Org User List', 'grace')).toHaveTextContent('No role');
  });

  it('falls back to an ellipsis when the server did not name the project', async () => {
    api.listOrgAdmins.mockResolvedValue([{ ...ROLES[1], scope_name: null }]);
    show();
    await loaded();

    expect(rowFor('Org User List', 'ada')).toHaveTextContent('PM: …');
  });

  it('says the list is empty when it is', async () => {
    api.listSystemUserListMembers.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No users in org list.')).toBeInTheDocument();
  });

  it('treats a null body as empty', async () => {
    api.listSystemUserListMembers.mockResolvedValue(null);
    api.listOrgAdmins.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No users in org list.')).toBeInTheDocument();
  });
});

describe('adding a user to the admin list', () => {
  const openAdd = async () => {
    const user = show();
    await loaded();
    await user.click(screen.getByRole('button', { name: /Add User/ }));
    return user;
  };

  it('creates the account in the organisation\'s own admin list', async () => {
    const user = await openAdd();

    await user.fill(screen.getByLabelText('Email'), 'alan@acme.test');
    await user.fill(screen.getByLabelText('Username'), 'alan');
    await user.fill(screen.getByLabelText('Password'), 'hunter2hunter2');
    // The dialog's submit, not the header button that opened it.
    await user.click(document.querySelector<HTMLButtonElement>('button[form="orgdetail-form-4"]')!);

    await vi.waitFor(() => expect(api.addUserToList).toHaveBeenCalledWith('ol1', {
      email: 'alan@acme.test', username: 'alan', password: 'hunter2hunter2',
    }));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('says so, and keeps the form, when the address is taken', async () => {
    api.addUserToList.mockRejectedValue(new Error('409'));
    const user = await openAdd();
    await user.fill(screen.getByLabelText('Email'), 'ada@acme.test');
    await user.fill(screen.getByLabelText('Username'), 'ada2');
    await user.fill(screen.getByLabelText('Password'), 'hunter2hunter2');

    await user.click(document.querySelector<HTMLButtonElement>('button[form="orgdetail-form-4"]')!);

    expect(await screen.findByText('Failed to add user.')).toBeInTheDocument();
    expect(screen.getByLabelText('Username')).toHaveValue('ada2');
  });

  it('requires all three fields, and does nothing on cancel', async () => {
    const user = await openAdd();

    expect(screen.getByLabelText('Email')).toBeRequired();
    expect(screen.getByLabelText('Username')).toBeRequired();
    expect(screen.getByLabelText('Password')).toBeRequired();
    // The operator's own saved password must not be offered for someone else's new account.
    expect(screen.getByLabelText('Password')).toHaveAttribute('autocomplete', 'new-password');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.addUserToList).not.toHaveBeenCalled();
  });
});

describe('granting a role to a member', () => {
  const openAssign = async () => {
    const user = show();
    await loaded();
    await user.click([...rowFor('Org User List', 'grace').querySelectorAll('button')].at(-1)!);
    await user.click(screen.getByRole('button', { name: /Assign role/ }));
    return user;
  };

  it('names who it is for', async () => {
    await openAssign();
    // The row shows the handle too; the dialog's description is the one that names the action.
    expect(screen.getByText(/Assign an admin role to grace#0002/)).toBeInTheDocument();
  });

  it('grants an organisation-wide role with no scope', async () => {
    const user = await openAssign();

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgAdmin)
      .toHaveBeenCalledWith('o1', 'u2', 'org_admin', undefined));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('asks which project a project role is over', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    const options = [...screen.getByLabelText('Project (scope)').querySelectorAll('option')]
      .map(o => o.textContent);
    expect(options).toEqual(['Select a project…', 'Portal', 'Tools', 'Legacy']);

    await user.selectOptions(screen.getByLabelText('Project (scope)'), 'p2');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgAdmin)
      .toHaveBeenCalledWith('o1', 'u2', 'project_admin', 'p2'));
  });

  it('forgets a chosen project when the role goes back to organisation-wide', async () => {
    const user = await openAssign();
    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    await user.selectOptions(screen.getByLabelText('Project (scope)'), 'p2');

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignOrgAdmin)
      .toHaveBeenCalledWith('o1', 'u2', 'org_admin', undefined));
  });

  it('does nothing on cancel', async () => {
    const user = await openAssign();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.assignOrgAdmin).not.toHaveBeenCalled();
  });
});

describe('removing a member', () => {
  const openRemove = async () => {
    const user = show();
    await loaded();
    await user.click([...rowFor('Org User List', 'grace').querySelectorAll('button')].at(-1)!);
    await user.click(screen.getByRole('button', { name: /Remove from org/ }));
    return user;
  };

  it('says the account is deleted, not merely unlinked', async () => {
    await openRemove();

    expect(await screen.findByText('Remove grace#0002?')).toBeInTheDocument();
    expect(screen.getByText(/permanently deletes their account/)).toBeInTheDocument();
    expect(api.removeSystemUserFromList).not.toHaveBeenCalled();
  });

  it('removes them from the organisation\'s own list', async () => {
    const user = await openRemove();

    await user.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);

    await vi.waitFor(() => expect(api.removeSystemUserFromList).toHaveBeenCalledWith('ol1', 'u2'));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = await openRemove();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.removeSystemUserFromList).not.toHaveBeenCalled();
  });
});

describe('the service accounts', () => {
  it('shows this tenant\'s and nobody else\'s', async () => {
    show();
    await loaded();

    const sas = section('Service Accounts');
    expect(sas.getByText('ci-deploy')).toBeInTheDocument();
    expect(screen.queryByText('other-tenant')).not.toBeInTheDocument();
    expect(sas.getByText('Active')).toBeInTheDocument();
    expect(sas.getByText(fmtDateShort('2026-03-04T05:06:07Z'))).toBeInTheDocument();
  });

  it('prints an em dash for an account with no description or no use', async () => {
    api.listServiceAccounts.mockResolvedValue([{ ...SAS[0], description: null, last_used_at: null }]);
    show();
    await loaded();

    expect(section('Service Accounts').getAllByText('—')).toHaveLength(2);
  });

  it('says there are none', async () => {
    api.listServiceAccounts.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No service accounts.')).toBeInTheDocument();
  });

  it('treats a null body as none', async () => {
    api.listServiceAccounts.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No service accounts.')).toBeInTheDocument();
  });
});

describe('the user lists', () => {
  it('shows the movable ones only — the admin list is managed above', async () => {
    show();
    await loaded();

    const lists = section(/User Lists/);
    expect(lists.getByText('Staff')).toBeInTheDocument();
    expect(lists.queryByText('Org admins')).not.toBeInTheDocument();
  });

  it('opens one', async () => {
    const user = show();
    await loaded();

    await user.click(section(/User Lists/).getByText('Staff'));

    await arrivedAt('/system/organisations/o1/userlists/l1');
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listUserLists.mockResolvedValue(LISTS);
    show();
    await loaded();

    expect(section(/User Lists/).getByText('Staff')).toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.listUserLists.mockResolvedValue({ user_lists: [] });
    show();

    expect(await screen.findByText('No user lists.')).toBeInTheDocument();
  });

  it('creates one in this organisation', async () => {
    const user = show();
    await loaded();

    await user.click(section(/User Lists/).getByRole('button', { name: /New/ }));
    await user.fill(screen.getByLabelText('Name'), 'Contractors');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.adminCreateUserList)
      .toHaveBeenCalledWith({ name: 'Contractors', org_id: 'o1' }));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('requires a name, and does nothing on cancel', async () => {
    const user = show();
    await loaded();
    await user.click(section(/User Lists/).getByRole('button', { name: /New/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.adminCreateUserList).not.toHaveBeenCalled();
  });
});

describe('the projects', () => {
  it('names the list each project authenticates against', async () => {
    show();
    await loaded();

    expect(rowFor(/Projects/, 'Portal')).toHaveTextContent('Staff');
    // Unassigned means nobody can sign in — it must not read as a blank cell.
    expect(rowFor(/Projects/, 'Tools')).toHaveTextContent('Unassigned');
  });

  it('falls back to a shortened id for a list it cannot name', async () => {
    // The list may be immovable, and those are filtered out of the lookup above.
    show();
    await loaded();

    expect(rowFor(/Projects/, 'Legacy')).toHaveTextContent('01234567…');
  });

  it('marks an inactive project as a draft', async () => {
    show();
    await loaded();

    expect(rowFor(/Projects/, 'Tools')).toHaveTextContent('Draft');
    expect(rowFor(/Projects/, 'Portal')).toHaveTextContent('Active');
  });

  it('opens one', async () => {
    const user = show();
    await loaded();

    await user.click(section(/Projects/).getByText('Portal'));

    await arrivedAt('/system/organisations/o1/projects/p1');
  });

  it('says there are none', async () => {
    api.listProjects.mockResolvedValue({ projects: [] });
    show();

    expect(await screen.findByText('No projects.')).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listProjects.mockResolvedValue(PROJECTS);
    show();
    await loaded();

    expect(section(/Projects/).getByText('Portal')).toBeInTheDocument();
  });
});

describe('creating a project in this organisation', () => {
  const openCreate = async () => {
    const user = show();
    await loaded();
    await user.click(section(/Projects/).getByRole('button', { name: /New/ }));
    await user.fill(screen.getByLabelText('Name'), 'Reporting');
    await user.fill(screen.getByLabelText('Slug'), 'reporting');
    return user;
  };

  it('creates it against the named organisation, not the caller\'s own', async () => {
    const user = await openCreate();

    await user.fill(screen.getByLabelText('Redirect URI'), 'https://r.test/cb');
    await user.fill(screen.getByLabelText('Post-logout redirect URI'), 'https://r.test/');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.adminCreateProject).toHaveBeenCalledWith('o1', {
      name: 'Reporting', slug: 'reporting',
      redirect_uris: ['https://r.test/cb'], post_logout_redirect_uris: ['https://r.test/'],
    }));
    expect(api.getOrg).toHaveBeenCalledTimes(2);
  });

  it('sends empty lists rather than lists holding an empty string', async () => {
    // Hydra refuses a client whose redirect_uris contains "".
    const user = await openCreate();

    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.adminCreateProject).toHaveBeenCalledWith('o1',
      expect.objectContaining({ redirect_uris: [], post_logout_redirect_uris: [] })));
  });

  it('turns a typed slug into a legal one as it is typed', async () => {
    const user = show();
    await loaded();
    await user.click(section(/Projects/).getByRole('button', { name: /New/ }));

    await user.fill(screen.getByLabelText('Slug'), 'My App');

    expect(screen.getByLabelText('Slug')).toHaveValue('my-app');
    expect(screen.getByLabelText('Slug')).toHaveAttribute('pattern', '[a-z0-9]+(-[a-z0-9]+)*');
  });

  it.each([
    ['the detail the server gave', { error: 'slug_taken', detail: 'That slug is in use.' }, 'That slug is in use.'],
    ['the code when there is no detail', { error: 'slug_taken' }, 'slug_taken'],
    ['a generic message when the body says nothing', null, 'Failed to create project.'],
  ])('reports %s', async (_n, body, expected) => {
    const user = await openCreate();
    api.adminCreateProject.mockRejectedValue(new ApiError(400, body));

    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(expected)).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Reporting');
  });

  it('reports a failure that is not an API error at all', async () => {
    const user = await openCreate();
    api.adminCreateProject.mockRejectedValue(new TypeError('Failed to fetch'));

    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Failed to create project.')).toBeInTheDocument();
  });

  it('does nothing on cancel', async () => {
    const user = await openCreate();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.adminCreateProject).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  const cases: ReadonlyArray<readonly [string, (u: Awaited<ReturnType<typeof show>>) => Promise<unknown>, string]> = [
    ['the rename form', u => u.click(screen.getByRole('button', { name: /Rename/ })), 'Rename Organisation'],
    ['the add-user form', u => u.click(screen.getByRole('button', { name: /Add User/ })), 'Add User to Org List'],
    ['the new-list form', u => u.click(section(/User Lists/).getByRole('button', { name: /New/ })), 'New User List'],
    ['the new-project form', u => u.click(section(/Projects/).getByRole('button', { name: /New/ })), 'New Project'],
    ['the delete confirmation', u => u.click(screen.getByRole('button', { name: /Delete/ })), 'Delete organisation "Acme"?'],
  ];

  it.each(cases)('closes %s', async (_n, open, heading) => {
    const user = show();
    await loaded();
    await open(user);
    await screen.findByText(heading);

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText(heading)).toBeNull());
  });

  it('closes the assign-role form without granting anything', async () => {
    const user = show();
    await loaded();
    await user.click([...rowFor('Org User List', 'grace').querySelectorAll('button')].at(-1)!);
    await user.click(screen.getByRole('button', { name: /Assign role/ }));
    await screen.findByText(/Assign an admin role to grace/);

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText(/Assign an admin role to grace/)).toBeNull());
    expect(api.assignOrgAdmin).not.toHaveBeenCalled();
  });

  it('closes the remove-user confirmation without removing anyone', async () => {
    const user = show();
    await loaded();
    await user.click([...rowFor('Org User List', 'grace').querySelectorAll('button')].at(-1)!);
    await user.click(screen.getByRole('button', { name: /Remove from org/ }));
    await screen.findByText('Remove grace#0002?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Remove grace#0002?')).toBeNull());
    expect(api.removeSystemUserFromList).not.toHaveBeenCalled();
  });
});
