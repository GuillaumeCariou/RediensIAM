import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import Projects from './Projects';
import { ApiError } from '@/auth';
import { fmtDateShort } from '@/lib/utils';

const api = vi.hoisted(() => ({
  listProjects: vi.fn(), createProject: vi.fn(), deleteProject: vi.fn(),
  listUserLists: vi.fn(), assignUserList: vi.fn(), unassignUserList: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const PROJECTS = [
  {
    id: 'p1', name: 'Customer Portal', slug: 'portal', active: true,
    assigned_user_list_id: 'l1', assigned_user_list_name: 'Staff',
    require_role_to_login: true, created_at: '2026-01-02T00:00:00Z',
  },
  {
    id: 'p2', name: 'Internal Tools', slug: 'tools', active: false,
    assigned_user_list_id: null, assigned_user_list_name: null,
    require_role_to_login: false, created_at: '2026-01-02T00:00:00Z',
  },
];
const LISTS = [{ id: 'l1', name: 'Staff' }, { id: 'l2', name: 'Contractors' }];

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.listProjects.mockResolvedValue({ projects: PROJECTS });
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
});

function Here() {
  return <output data-testid="here">{useLocation().pathname + useLocation().search}</output>;
}

function show(path = '/org/projects', pattern = '/org/projects') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<Projects />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));
const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });
const openMenu = (user: Awaited<ReturnType<typeof show>>, name: string) =>
  user.click([...rowFor(name).querySelectorAll('button')].at(-1)!);
const create = () => document.querySelector<HTMLButtonElement>('button[form="create-project-form"]')!;

describe('the table', () => {
  it('shows each project with its slug, status, list and login policy', async () => {
    show();

    await screen.findByText('Customer Portal');
    expect(rowFor('Customer Portal')).toHaveTextContent('portal');
    expect(rowFor('Customer Portal')).toHaveTextContent('Active');
    expect(rowFor('Customer Portal')).toHaveTextContent('Staff');
    expect(rowFor('Customer Portal')).toHaveTextContent('Required');
    expect(rowFor('Customer Portal')).toHaveTextContent(fmtDateShort('2026-01-02T00:00:00Z'));
  });

  it('says a project with no user list has none, which means nobody can sign in', async () => {
    show();

    await screen.findByText('Internal Tools');
    expect(rowFor('Internal Tools')).toHaveTextContent('None');
    expect(rowFor('Internal Tools')).toHaveTextContent('Inactive');
    expect(rowFor('Internal Tools')).toHaveTextContent('Optional');
  });

  it('opens a project from its row', async () => {
    const user = show();

    await user.click(await screen.findByText('Customer Portal'));

    await arrivedAt('/project?project_id=p1');
  });

  it('opens it through the system route for a super admin browsing a tenant', async () => {
    const user = show('/system/organisations/o9/projects', '/system/organisations/:id/projects');

    await user.click(await screen.findByText('Customer Portal'));

    await arrivedAt('/system/organisations/o9/projects/p1');
  });

  it('shows placeholder rows while loading', () => {
    api.listProjects.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(4);
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listProjects.mockResolvedValue(PROJECTS);
    api.listUserLists.mockResolvedValue(LISTS);
    show();

    expect(await screen.findByText('Customer Portal')).toBeInTheDocument();
  });

  it('says there are none, and asks for nothing, without an organisation in scope', async () => {
    auth.orgId = '';
    show();

    expect(await screen.findByText('Select an organisation first.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /New Project/ })).not.toBeInTheDocument();
    expect(api.listProjects).not.toHaveBeenCalled();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listProjects.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('creating a project', () => {
  const openForm = async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await user.click(screen.getByRole('button', { name: /New Project/ }));
    await user.fill(screen.getByLabelText('Name'), 'Reporting');
    await user.fill(screen.getByLabelText('Slug'), 'reporting');
    return user;
  };

  it('splits the URI boxes into lists, dropping blank lines and stray spaces', async () => {
    const user = await openForm();
    await user.fill(screen.getByLabelText('Redirect URIs (one per line)'),
      ' https://a.test/cb \n\n https://b.test/cb ');
    await user.fill(screen.getByLabelText('Post-logout redirect URIs (one per line)'), 'https://a.test/\n');

    await user.click(create());

    await vi.waitFor(() => expect(api.createProject).toHaveBeenCalledWith({
      org_id: 'o1', name: 'Reporting', slug: 'reporting', require_role_to_login: false,
      redirect_uris: ['https://a.test/cb', 'https://b.test/cb'],
      post_logout_redirect_uris: ['https://a.test/'],
    }));
    expect(api.listProjects).toHaveBeenCalledTimes(2);
  });

  it('sends empty lists rather than a list holding one empty string', async () => {
    // Hydra refuses a client whose redirect_uris contains "", so this is not cosmetic.
    const user = await openForm();

    await user.click(create());

    await vi.waitFor(() => expect(api.createProject).toHaveBeenCalledWith(
      expect.objectContaining({ redirect_uris: [], post_logout_redirect_uris: [] })));
  });

  it('carries the login policy across', async () => {
    const user = await openForm();

    await user.click(screen.getByRole('checkbox'));
    await user.click(create());

    await vi.waitFor(() => expect(api.createProject).toHaveBeenCalledWith(
      expect.objectContaining({ require_role_to_login: true })));
  });

  it('turns a typed slug into a legal one as it is typed', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await user.click(screen.getByRole('button', { name: /New Project/ }));

    await user.fill(screen.getByLabelText('Slug'), 'My Dashboard');

    expect(screen.getByLabelText('Slug')).toHaveValue('my-dashboard');
  });

  it.each([
    ['the detail the server gave', { error: 'slug_taken', detail: 'That slug is already in use.' },
      'That slug is already in use.'],
    ['the error code when there is no detail', { error: 'slug_taken' }, 'slug_taken'],
    ['a generic message when the body says nothing', null, 'Failed to create project.'],
  ])('reports %s', async (_n, body, expected) => {
    const user = await openForm();
    api.createProject.mockRejectedValue(new ApiError(400, body));

    await user.click(create());

    expect(await screen.findByText(expected)).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Reporting');
  });

  it('reports a failure that is not an API error at all', async () => {
    const user = await openForm();
    api.createProject.mockRejectedValue(new TypeError('Failed to fetch'));

    await user.click(create());

    expect(await screen.findByText('Failed to create project.')).toBeInTheDocument();
  });

  it('clears the error when the form is closed and reopened', async () => {
    const user = await openForm();
    api.createProject.mockRejectedValue(new ApiError(400, { error: 'slug_taken' }));
    await user.click(create());
    await screen.findByText('slug_taken');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: /New Project/ }));

    expect(screen.queryByText('slug_taken')).not.toBeInTheDocument();
  });
});

describe('the row menu', () => {
  it('opens the project', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.click(screen.getByRole('button', { name: 'Open Project' }));

    await arrivedAt('/project?project_id=p1');
  });

  it('offers to unassign only where there is a list to unassign', async () => {
    const user = show();
    await screen.findByText('Internal Tools');

    await openMenu(user, 'Internal Tools');

    expect(screen.getByRole('button', { name: 'Assign User List' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Unassign User List' })).not.toBeInTheDocument();
  });

  it('closes on Escape', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('button', { name: 'Open Project' })).not.toBeInTheDocument();
  });

  it('closes when the operator clicks away', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'Open Project' })).not.toBeInTheDocument();
  });

  it('does not open the project when its menu is used', async () => {
    const user = show();
    await screen.findByText('Customer Portal');

    await openMenu(user, 'Customer Portal');

    expect(screen.getByTestId('here').textContent).toBe('/org/projects');
  });
});

describe('assigning a user list', () => {
  it('opens on the list the project already has', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    expect(screen.getByRole('combobox')).toHaveValue('l1');
  });

  it('opens on "no list" for a project that has none — never on the first entry', async () => {
    // '' matches no option, which paints the first list as chosen while the state says otherwise.
    const user = show();
    await screen.findByText('Internal Tools');
    await openMenu(user, 'Internal Tools');

    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    expect(screen.getByRole('combobox')).toHaveValue('__none__');
  });

  it('saves the chosen list and reloads', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    await user.selectOptions(screen.getByRole('combobox'), 'l2');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.assignUserList).toHaveBeenCalledWith('p1', 'l2'));
    expect(api.listProjects).toHaveBeenCalledTimes(2);
  });

  it('unassigns when "no list" is chosen', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    await user.selectOptions(screen.getByRole('combobox'), '__none__');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.unassignUserList).toHaveBeenCalledWith('p1'));
  });

  it('opens straight onto "no list" from the unassign entry', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.click(screen.getByRole('button', { name: 'Unassign User List' }));

    expect(screen.getByRole('combobox')).toHaveValue('__none__');
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.assignUserList).not.toHaveBeenCalled();
    expect(api.unassignUserList).not.toHaveBeenCalled();
  });
});

describe('deleting a project', () => {
  it('warns that the OAuth client goes with it, and asks first', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText('Delete Customer Portal?')).toBeInTheDocument();
    expect(screen.getByText(/Hydra OAuth2 client/)).toBeInTheDocument();
    expect(api.deleteProject).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and reloads', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await user.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);

    await vi.waitFor(() => expect(api.deleteProject).toHaveBeenCalledWith('p1'));
    expect(api.listProjects).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText('Delete Customer Portal?');

    await user.click(screen.getAllByRole('button', { name: 'Cancel' }).at(-1)!);

    expect(api.deleteProject).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  it('closes the create form and clears the error it was showing', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await user.click(screen.getByRole('button', { name: /New Project/ }));
    await user.fill(screen.getByLabelText('Name'), 'Reporting');
    await user.fill(screen.getByLabelText('Slug'), 'reporting');
    api.createProject.mockRejectedValue(new ApiError(400, { error: 'slug_taken' }));
    await user.click(create());
    await screen.findByText('slug_taken');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText('Slug')).toBeNull());
    await user.click(screen.getByRole('button', { name: /New Project/ }));

    expect(screen.queryByText('slug_taken')).not.toBeInTheDocument();
  });

  it('closes the assign dialog without assigning', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Assign User List' }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByRole('combobox')).toBeNull());
    expect(api.assignUserList).not.toHaveBeenCalled();
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = show();
    await screen.findByText('Customer Portal');
    await openMenu(user, 'Customer Portal');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText('Delete Customer Portal?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete Customer Portal?')).toBeNull());
    expect(api.deleteProject).not.toHaveBeenCalled();
  });
});
