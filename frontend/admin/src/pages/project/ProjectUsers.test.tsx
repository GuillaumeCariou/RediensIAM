import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectUsers from './ProjectUsers';
import { ApiError } from '@/auth';

/**
 * A project with no user list assigned cannot be signed into at all, so this page is where that
 * gets fixed — and it has to write through the right pair of routes. The org-scoped ones answer
 * about the caller's own organisation; a super admin editing someone else's project needs the
 * admin ones.
 */

const api = vi.hoisted(() => ({
  getProjectInfo: vi.fn(), listUserLists: vi.fn(),
  assignUserList: vi.fn(), unassignUserList: vi.fn(),
  adminAssignUserList: vi.fn(), adminUnassignUserList: vi.fn(),
  // La portée projet : ce qu'un project_admin peut appeler, et lui seul dans cette page.
  listProjectUsers: vi.fn(), listRoles: vi.fn(), assignRole: vi.fn(), removeRole: vi.fn(),
  getProjectUser: vi.fn(), revokeProjectUserSessions: vi.fn(), cleanupProject: vi.fn(),
  createProjectUser: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({
  orgId: 'o1', projectId: 'p1', isOrgAdmin: false, isSuperAdmin: false,
}));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const panel = vi.hoisted(() => ({ props: {} as Record<string, unknown> }));
vi.mock('@/components/UserListMembersPanel', () => ({
  default: (props: Record<string, unknown>) => {
    panel.props = props;
    return <p>members of {String(props['listId'])}</p>;
  },
}));

const ASSIGNED = {
  assigned_user_list_id: 'l1', assigned_user_list_name: 'Staff', default_role_id: 'r1',
};
const UNASSIGNED = {
  assigned_user_list_id: null, assigned_user_list_name: null, default_role_id: null,
};
const LISTS = [
  { id: 'l1', name: 'Staff' },
  { id: 'l2', name: 'Contractors' },
  { id: 'sys', name: 'System', immovable: true },
];

const MEMBERS = [
  {
    id: 'u1', username: 'ada', discriminator: '4242', email: 'ada@acme.test',
    display_name: 'Ada', active: true, last_login_at: '2026-02-01T10:00:00Z',
    roles: [{ id: 'r1', name: 'admin' }],
  },
];
const ROLES = [{ id: 'r1', name: 'admin' }, { id: 'r2', name: 'viewer' }];
const DETAIL = {
  id: 'u1', username: 'ada', discriminator: '4242', email: 'ada@acme.test', active: true,
  roles: [{ role_id: 'r1', name: 'admin', rank: 1 }],
};

beforeEach(() => {
  vi.clearAllMocks();
  panel.props = {};
  Object.assign(auth, { orgId: 'o1', projectId: 'p1', isOrgAdmin: false, isSuperAdmin: false });
  api.getProjectInfo.mockResolvedValue(ASSIGNED);
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
  api.listProjectUsers.mockResolvedValue(MEMBERS);
  api.listRoles.mockResolvedValue(ROLES);
  api.getProjectUser.mockResolvedValue(DETAIL);
  api.revokeProjectUserSessions.mockResolvedValue({ message: 'sessions_revoked' });
  api.cleanupProject.mockImplementation((_p: string, dryRun: boolean) =>
    Promise.resolve({ orphaned_roles_removed: 2, dry_run: dryRun }));
});

function show(path = '/project/users', pattern = '/project/users') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ProjectUsers />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const SYSTEM = ['/system/organisations/o9/projects/p9/users',
  '/system/organisations/:oid/projects/:pid/users'] as const;
const picker = () => screen.getByRole('combobox');

describe('a project manager, who cannot reassign the list', () => {
  it('is shown which list the project uses, and no way to change it', async () => {
    show();

    expect(await screen.findByText('Staff')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(api.listUserLists).not.toHaveBeenCalled();
  });

  it('is told plainly when there is none', async () => {
    api.getProjectInfo.mockResolvedValue(UNASSIGNED);
    show();

    expect(await screen.findByText('No user list assigned')).toBeInTheDocument();
  });

  /**
   * Le panneau de l'org admin lit `/org/userlists/{id}/users`, gardé en OrgAdmin : ouvert à un
   * project_admin il ne rendrait que des 403. Il voit le sien, servi par `/project/users`.
   */
  it('gets the project-scoped members panel, not the org admin\'s', async () => {
    show();

    expect(await screen.findByText('ada#4242')).toBeInTheDocument();
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
    expect(api.listProjectUsers).toHaveBeenCalledWith('p1');
  });

  // `POST /project/users` : la console ne savait créer un compte que par
  // `/admin/userlists/{id}/users`, hors de portée d'un project_admin.
  it('creates a member in the project\'s own list', async () => {
    api.createProjectUser.mockResolvedValue({ id: 'u9' });
    const user = show();
    await screen.findByText('ada#4242');

    await user.click(screen.getByRole('button', { name: 'Add member' }));
    await user.fill(screen.getByLabelText('Email'), 'grace@acme.test');
    await user.fill(screen.getByLabelText('Password'), 'Sup3r-Passw0rd!');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createProjectUser).toHaveBeenCalledWith('p1', {
      email: 'grace@acme.test', password: 'Sup3r-Passw0rd!', username: undefined,
    }));
  });

  // Le plancher appliqué est le maximum entre le réglage du projet et le minimum absolu : le
  // client ne peut pas le recalculer, il recopie `min_length`.
  it('repeats the length the server actually requires', async () => {
    const { ApiError } = await import('@/auth');
    api.createProjectUser.mockRejectedValue(new ApiError(400, { error: 'password_too_short', min_length: 14 }));
    const user = show();
    await screen.findByText('ada#4242');

    await user.click(screen.getByRole('button', { name: 'Add member' }));
    await user.fill(screen.getByLabelText('Email'), 'grace@acme.test');
    await user.fill(screen.getByLabelText('Password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/at least 14 characters/)).toBeInTheDocument();
  });

  it('names the duplicate rather than failing blankly', async () => {
    const { ApiError } = await import('@/auth');
    api.createProjectUser.mockRejectedValue(new ApiError(409, { error: 'email_already_exists' }));
    const user = show();
    await screen.findByText('ada#4242');

    await user.click(screen.getByRole('button', { name: 'Add member' }));
    await user.fill(screen.getByLabelText('Email'), 'ada@acme.test');
    await user.fill(screen.getByLabelText('Password'), 'Sup3r-Passw0rd!');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/already in this project/)).toBeInTheDocument();
  });
});

describe('an org admin', () => {
  beforeEach(() => { auth.isOrgAdmin = true; });

  it('can pick from the organisation\'s lists', async () => {
    show();

    await vi.waitFor(() => expect(picker()).toHaveValue('l1'));
    const options = [...picker().querySelectorAll('option')].map(o => o.textContent);
    // The system list is immovable and must never be offered to a project.
    expect(options).toEqual(['— No user list assigned —', 'Staff', 'Contractors']);
  });

  it('is warned that nobody can sign in while no list is assigned', async () => {
    api.getProjectInfo.mockResolvedValue(UNASSIGNED);
    show();

    expect(await screen.findByText('No user list assigned — users cannot log in to this project.'))
      .toBeInTheDocument();
  });

  it('assigns one through the org-scoped route and re-reads the project', async () => {
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('l1'));

    await user.selectOptions(picker(), 'l2');

    await vi.waitFor(() => expect(api.assignUserList).toHaveBeenCalledWith('p1', 'l2'));
    expect(api.adminAssignUserList).not.toHaveBeenCalled();
    expect(api.getProjectInfo).toHaveBeenCalledTimes(2);
  });

  it('unassigns through the org-scoped route', async () => {
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('l1'));

    await user.selectOptions(picker(), '__none__');

    await vi.waitFor(() => expect(api.unassignUserList).toHaveBeenCalledWith('p1'));
  });

  it('opens the members panel on the assigned list, in the project scope', async () => {
    show();

    expect(await screen.findByText('members of l1')).toBeInTheDocument();
    expect(panel.props['projectId']).toBe('p1');
    expect(panel.props['defaultRoleId']).toBe('r1');
    expect(panel.props['title']).toBe('Staff — Members');
  });

  it('shows no members panel when there is no list to show members of', async () => {
    api.getProjectInfo.mockResolvedValue(UNASSIGNED);
    show();

    await screen.findByText(/users cannot log in/);
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
  });

  it('names the list from the organisation\'s own list when the project did not', async () => {
    // The project payload has carried a null name with a real id; falling back keeps the heading
    // from reading "User List — Members".
    api.getProjectInfo.mockResolvedValue({ ...ASSIGNED, assigned_user_list_name: null });
    show();

    expect(await screen.findByText('members of l1')).toBeInTheDocument();
    expect(panel.props['title']).toBe('Staff — Members');
  });
});

describe('a super admin browsing another organisation\'s project', () => {
  beforeEach(() => { auth.isOrgAdmin = true; auth.isSuperAdmin = true; });

  it('reads the lists of the organisation in the URL, not their own', async () => {
    show(...SYSTEM);

    await vi.waitFor(() => expect(api.listUserLists).toHaveBeenCalledWith('o9'));
  });

  it('writes through the admin routes', async () => {
    const user = show(...SYSTEM);
    await vi.waitFor(() => expect(picker()).toHaveValue('l1'));

    await user.selectOptions(picker(), 'l2');

    await vi.waitFor(() => expect(api.adminAssignUserList).toHaveBeenCalledWith('p9', 'l2'));
    expect(api.assignUserList).not.toHaveBeenCalled();
  });

  it('unassigns through the admin route too', async () => {
    const user = show(...SYSTEM);
    await vi.waitFor(() => expect(picker()).toHaveValue('l1'));

    await user.selectOptions(picker(), '__none__');

    await vi.waitFor(() => expect(api.adminUnassignUserList).toHaveBeenCalledWith('p9'));
  });

  it('tells the members panel it is in the system scope', async () => {
    show(...SYSTEM);

    await screen.findByText('members of l1');
    expect(panel.props['isSystemCtx']).toBe(true);
  });
});

describe('loading and failure', () => {
  it('shows a placeholder before the project has answered', () => {
    api.getProjectInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.queryByText('Staff')).not.toBeInTheDocument();
  });

  it('re-reads once the roles arrive, not only on the first render', async () => {
    // isOrgAdmin and orgId are not both known on the first render. With [projectId] alone the
    // effect never re-ran, and an org admin opening the page directly got an empty dropdown.
    api.listUserLists.mockResolvedValue({ user_lists: LISTS });
    const { rerender } = render(
      <MemoryRouter initialEntries={['/project/users']}><ProjectUsers /></MemoryRouter>,
    );
    expect(api.listUserLists).not.toHaveBeenCalled();

    auth.isOrgAdmin = true;
    rerender(<MemoryRouter initialEntries={['/project/users']}><ProjectUsers /></MemoryRouter>);

    await vi.waitFor(() => expect(api.listUserLists).toHaveBeenCalledWith('o1'));
  });

  it('still finishes when the project cannot be read', async () => {
    auth.isOrgAdmin = true;
    api.getProjectInfo.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText(/users cannot log in/)).toBeInTheDocument();
  });

  it('still finishes when the lists cannot be read', async () => {
    auth.isOrgAdmin = true;
    api.listUserLists.mockRejectedValue(new Error('500'));
    show();

    // The picker renders, with nothing to choose from — and no false warning, because the project
    // does have a list assigned even though this page could not name it.
    await vi.waitFor(() => expect(picker()).toBeInTheDocument());
    expect([...picker().querySelectorAll('option')]).toHaveLength(1);
    expect(screen.queryByText(/users cannot log in/)).not.toBeInTheDocument();
  });

  it('does nothing at all when no project is in scope', () => {
    auth.projectId = '';
    show();

    expect(api.getProjectInfo).not.toHaveBeenCalled();
    expect(api.listProjectUsers).not.toHaveBeenCalled();
  });
});

/**
 * Ce qu'un project_admin peut faire de ses membres sans jamais toucher à `/org` : les lire, ouvrir
 * l'un d'eux, lui donner ou lui retirer un rôle, couper ses sessions. Toutes ces routes sont
 * gardées en ProjectAdmin — c'est la console qui ne les appelait nulle part.
 */
describe('the project-scoped members panel', () => {
  const dialog = () => screen.getByRole('dialog');
  const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });

  it('lists the members with the roles they hold here', async () => {
    show();

    expect(await screen.findByText('ada@acme.test')).toBeInTheDocument();
    expect(rowFor('ada')).toHaveTextContent('admin');
    expect(api.listRoles).toHaveBeenCalledWith('p1');
  });

  it('says so plainly when nobody is in the list', async () => {
    api.listProjectUsers.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No members yet')).toBeInTheDocument();
  });

  it('shows the refusal instead of an empty table', async () => {
    api.listProjectUsers.mockRejectedValue(new ApiError(403, { error: 'forbidden' }));
    show();

    expect(await screen.findByText('forbidden')).toBeInTheDocument();
  });

  it('reads one member through the project route', async () => {
    const user = show();
    await screen.findByText('ada@acme.test');

    await user.click(rowFor('ada'));

    await vi.waitFor(() => expect(api.getProjectUser).toHaveBeenCalledWith('p1', 'u1'));
    expect(dialog()).toHaveTextContent('admin');
  });

  it('assigns a role from the detail and re-reads the member', async () => {
    const user = show();
    await screen.findByText('ada@acme.test');
    await user.click(rowFor('ada'));
    await vi.waitFor(() => expect(api.getProjectUser).toHaveBeenCalled());

    await user.selectOptions(screen.getByRole('combobox', { name: 'Project role to add' }), 'r2');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await vi.waitFor(() => expect(api.assignRole).toHaveBeenCalledWith('p1', 'u1', 'r2'));
    expect(api.getProjectUser).toHaveBeenCalledTimes(2);
  });

  it('says why a role could not be assigned', async () => {
    api.assignRole.mockRejectedValue(new ApiError(400, { error: 'User is not in this project\'s assigned UserList' }));
    const user = show();
    await screen.findByText('ada@acme.test');
    await user.click(rowFor('ada'));
    await vi.waitFor(() => expect(api.getProjectUser).toHaveBeenCalled());

    await user.selectOptions(screen.getByRole('combobox', { name: 'Project role to add' }), 'r2');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText(/not in this project/)).toBeInTheDocument();
  });

  it('revokes the sessions only after a confirmation that names the member', async () => {
    const user = show();
    await screen.findByText('ada@acme.test');
    await user.click(rowFor('ada'));
    await vi.waitFor(() => expect(api.getProjectUser).toHaveBeenCalled());

    await user.click(screen.getByRole('button', { name: 'Revoke sessions' }));

    expect(dialog()).toHaveTextContent('Revoke every session of ada#4242?');
    expect(api.revokeProjectUserSessions).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Revoke sessions' }));

    await vi.waitFor(() => expect(api.revokeProjectUserSessions).toHaveBeenCalledWith('p1', 'u1'));
    expect(await screen.findByText(/Every session of ada#4242 was revoked/)).toBeInTheDocument();
  });

  it('shows the refusal when the revocation fails, and keeps the dialog open', async () => {
    api.revokeProjectUserSessions.mockRejectedValue(new ApiError(500, null));
    const user = show();
    await screen.findByText('ada@acme.test');
    await user.click(rowFor('ada'));
    await vi.waitFor(() => expect(api.getProjectUser).toHaveBeenCalled());
    await user.click(screen.getByRole('button', { name: 'Revoke sessions' }));

    await user.click(screen.getByRole('button', { name: 'Revoke sessions' }));

    expect(await screen.findByText(/Failed to revoke the sessions/)).toBeInTheDocument();
  });
});

/** Destructif : il se propose en `dry_run`, montre le compte, et n'exécute qu'au second clic. */
describe('the cleanup', () => {
  const openCleanup = async (user: ReturnType<typeof show>) => {
    await user.click(screen.getByRole('button', { name: 'Cleanup' }));
  };

  it('previews first, and deletes nothing while it does', async () => {
    const user = show();
    await openCleanup(user);

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await vi.waitFor(() => expect(api.cleanupProject).toHaveBeenCalledWith('p1', true));
    expect(await screen.findByText(/would be removed/)).toBeInTheDocument();
  });

  it('offers nothing destructive before a preview has run', async () => {
    const user = show();
    await openCleanup(user);

    expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
  });

  it('names the count on the button that actually deletes', async () => {
    const user = show();
    await openCleanup(user);
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText(/would be removed/);

    await user.click(screen.getByRole('button', { name: 'Remove 2 role assignments' }));

    await vi.waitFor(() => expect(api.cleanupProject).toHaveBeenLastCalledWith('p1', false));
    expect(await screen.findByText(/were removed/)).toBeInTheDocument();
  });

  it('offers no deletion when the preview finds nothing', async () => {
    api.cleanupProject.mockResolvedValue({ orphaned_roles_removed: 0, dry_run: true });
    const user = show();
    await openCleanup(user);

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await screen.findByText(/would be removed/);
    expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
  });

  it('shows the refusal rather than closing on a failure', async () => {
    api.cleanupProject.mockRejectedValue(new ApiError(400, null));
    const user = show();
    await openCleanup(user);

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    expect(await screen.findByText(/could not run/)).toBeInTheDocument();
  });
});
