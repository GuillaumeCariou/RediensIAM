import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectUsers from './ProjectUsers';

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

beforeEach(() => {
  vi.clearAllMocks();
  panel.props = {};
  Object.assign(auth, { orgId: 'o1', projectId: 'p1', isOrgAdmin: false, isSuperAdmin: false });
  api.getProjectInfo.mockResolvedValue(ASSIGNED);
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
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

  it('does not see the members panel, which is an org admin\'s tool', async () => {
    show();

    await screen.findByText('Staff');
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
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
  });
});
