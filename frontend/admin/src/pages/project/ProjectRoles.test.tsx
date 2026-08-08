import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectRoles from './ProjectRoles';
import { ApiError } from '@/auth';

/**
 * Rank orders privilege the wrong way round from intuition — lower is stronger — and it is what
 * decides which roles a project manager may hand out. So the ordering is asserted everywhere it
 * shows: the table, and the roles listed as granted on sign-up.
 */

const api = vi.hoisted(() => ({
  listRoles: vi.fn(), createRole: vi.fn(), updateRole: vi.fn(), deleteRole: vi.fn(),
  adminListRoles: vi.fn(), adminCreateRole: vi.fn(), adminDeleteRole: vi.fn(),
  updateProject: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ROLES = [
  { id: 'r2', name: 'viewer', description: null, rank: 100, is_default: true, holders: 176 },
  { id: 'r1', name: 'admin', description: 'Everything', rank: 1, is_default: false, holders: 7 },
];

const listing = (roles: unknown[] = ROLES) => {
  api.listRoles.mockResolvedValue({ roles });
  api.adminListRoles.mockResolvedValue({ roles });
};

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  listing();
});

/** Le même écran ouvert depuis /system/organisations/:oid/projects/:pid/roles. */
const showSystem = () =>
  show('/system/organisations/o9/projects/p9/roles',
    '/system/organisations/:oid/projects/:pid/roles');

function show(path = '/project/roles', pattern = '/project/roles') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ProjectRoles />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const tick = (name: string) => screen.getByRole('checkbox', { name: `Grant ${name} on sign-up` });
const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });
const submit = () => document.querySelector<HTMLButtonElement>('button[form="create-role-form"]')!;

describe('the table', () => {
  it('lists the roles strongest first, whatever order they arrived in', async () => {
    show();

    await screen.findByText('admin');
    const names = screen.getAllByRole('row').slice(1).map(r => r.querySelector('.iam-mono')!.textContent);
    expect(names).toEqual(['admin', 'viewer']);
  });

  it('shows the description, the rank and how many hold it', async () => {
    show();

    await screen.findByText('admin');
    expect(rowFor('admin')).toHaveTextContent('Everything');
    expect(rowFor('admin')).toHaveTextContent('1');
    expect(rowFor('admin')).toHaveTextContent('7');
  });

  // Le nom nu est ambigu entre locataires : c'est `{projectId}/{nom}` que le serveur de ressources
  // compare, et la seule forme dans laquelle le rôle atteint un jeton.
  it('shows the qualified name the token will carry', async () => {
    show();

    expect(await screen.findByText('p1/admin')).toBeInTheDocument();
  });

  it('reads the holder count of a role nobody holds as zero', async () => {
    listing([{ id: 'r3', name: 'editor', description: null, rank: 50 }]);
    show();

    await screen.findByText('editor');
    expect(rowFor('editor')).toHaveTextContent('0');
  });

  it('prints an em dash for a role with no description', async () => {
    show();

    // `viewer` is in the table and again in the sign-up footer; `admin` marks the load too.
    await screen.findByText('admin');
    expect(rowFor('viewer')).toHaveTextContent('—');
  });

  it('ticks the roles new users are given, and only those', async () => {
    show();

    // `viewer` is in the table and again in the sign-up footer; `admin` marks the load too.
    await screen.findByText('admin');
    expect(tick('viewer')).toBeChecked();
    expect(tick('admin')).not.toBeChecked();
  });

  it('explains which way rank runs, but only when there are roles to rank', async () => {
    show();

    expect(await screen.findByText(/lower number = higher privilege/)).toBeInTheDocument();
  });

  it('says there are none, without the ranking note or the sign-up footer', async () => {
    listing([]);
    show();

    expect(await screen.findByText('No roles defined yet')).toBeInTheDocument();
    expect(screen.queryByText(/lower number = higher privilege/)).not.toBeInTheDocument();
    expect(screen.queryByText('Granted on sign-up:')).not.toBeInTheDocument();
  });

  it('shows placeholder rows while loading', () => {
    api.listRoles.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(4);
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listRoles.mockResolvedValue(ROLES);
    show();

    expect(await screen.findByText('admin')).toBeInTheDocument();
  });

  it('asks for nothing when no project is in scope', async () => {
    auth.projectId = '';
    show();

    expect(await screen.findByText('No roles defined yet')).toBeInTheDocument();
    expect(api.listRoles).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /New Role/ })).not.toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listRoles.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No roles defined yet')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

/**
 * Le défaut est un ensemble, pas un rôle. Chaque écriture énonce l'ensemble entier : un PATCH qui
 * n'enverrait que le rôle coché ne pourrait jamais en décocher un.
 */
describe('the roles granted on sign-up', () => {
  const footer = () => screen.getByText('Granted on sign-up:').parentElement!;

  it('lists the ticked ones, strongest first', async () => {
    listing([{ ...ROLES[0] }, { ...ROLES[1], is_default: true }]);
    show();

    await screen.findByText('Granted on sign-up:');
    expect([...footer().querySelectorAll('.iam-chip')].map(c => c.textContent))
      .toEqual(['admin', 'viewer']);
  });

  it('says a project with none grants nothing, and offers nothing to clear', async () => {
    listing(ROLES.map(r => ({ ...r, is_default: false })));
    show();

    expect(await screen.findByText(/a new account starts with no role/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear all defaults' })).not.toBeInTheDocument();
  });

  it('adds one to the set rather than replacing it', async () => {
    const user = show();
    await screen.findByText('admin');

    await user.click(tick('admin'));

    await vi.waitFor(() => expect(api.updateProject)
      .toHaveBeenCalledWith('p1', { default_role_ids: ['r2', 'r1'] }));
    expect(tick('admin')).toBeChecked();
    expect(tick('viewer')).toBeChecked();
  });

  it('unticking sends the set without it', async () => {
    const user = show();
    // `viewer` is in the table and again in the sign-up footer; `admin` marks the load too.
    await screen.findByText('admin');

    await user.click(tick('viewer'));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', { default_role_ids: [] }));
    expect(tick('viewer')).not.toBeChecked();
  });

  it('clears them all at once', async () => {
    listing(ROLES.map(r => ({ ...r, is_default: true })));
    const user = show();
    await screen.findByText('Granted on sign-up:');

    await user.click(screen.getByRole('button', { name: 'Clear all defaults' }));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', { default_role_ids: [] }));
    expect(tick('admin')).not.toBeChecked();
    expect(tick('viewer')).not.toBeChecked();
  });

  it('says so, and puts the tick back, when the save is refused', async () => {
    api.updateProject.mockRejectedValue(new ApiError(400, { error: 'invalid_default_role' }));
    const user = show();
    await screen.findByText('admin');

    await user.click(tick('admin'));

    expect(await screen.findByText(/no longer belongs to this project/)).toBeInTheDocument();
    expect(tick('admin')).not.toBeChecked();
    expect(tick('viewer')).toBeChecked();
  });

  it('falls back to its own words for a failure that carries none', async () => {
    api.updateProject.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('admin');

    await user.click(tick('admin'));

    expect(await screen.findByText('Failed to save the default roles.')).toBeInTheDocument();
  });

  it('re-enables the ticks after a failure', async () => {
    api.updateProject.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('admin');

    await user.click(tick('admin'));

    await vi.waitFor(() => expect(tick('admin')).toBeEnabled());
  });
});

describe('creating a role', () => {
  const openForm = async () => {
    const user = show();
    await screen.findByText('admin');
    await user.click(screen.getByRole('button', { name: /New Role/ }));
    return user;
  };

  it('creates it with the rank as a number, and reloads', async () => {
    const user = await openForm();

    await user.fill(screen.getByLabelText('Name'), 'editor');
    await user.fill(screen.getByLabelText('Description (optional)'), 'Can edit');
    await user.fill(screen.getByLabelText('Rank'), '50');
    await user.click(submit());

    await vi.waitFor(() => expect(api.createRole)
      .toHaveBeenCalledWith('p1', { name: 'editor', description: 'Can edit', rank: 50 }));
    expect(api.listRoles).toHaveBeenCalledTimes(2);
  });

  it('defaults the rank to the weakest of the suggested three', async () => {
    const user = await openForm();

    await user.fill(screen.getByLabelText('Name'), 'editor');
    await user.click(submit());

    await vi.waitFor(() => expect(api.createRole)
      .toHaveBeenCalledWith('p1', expect.objectContaining({ rank: 100, description: undefined })));
  });

  it('normalises the name the way the backend stores it', async () => {
    const user = await openForm();

    await user.fill(screen.getByLabelText('Name'), 'Content Editor');

    expect(screen.getByLabelText('Name')).toHaveValue('content_editor');
  });

  it('requires a name and refuses a rank below one', async () => {
    const user = await openForm();

    expect(screen.getByLabelText('Name')).toBeRequired();
    expect(screen.getByLabelText('Rank')).toHaveAttribute('min', '1');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.createRole).not.toHaveBeenCalled();
  });
});

describe('deleting a role', () => {
  it('warns that its holders lose it, and asks first', async () => {
    const user = show();
    await screen.findByText('admin');

    await user.click(rowFor('admin').querySelector('button')!);

    expect(await screen.findByText('Delete role "admin"?')).toBeInTheDocument();
    expect(screen.getByText(/will lose it/)).toBeInTheDocument();
    expect(api.deleteRole).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and reloads', async () => {
    const user = show();
    await screen.findByText('admin');
    await user.click(rowFor('admin').querySelector('button')!);

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    await vi.waitFor(() => expect(api.deleteRole).toHaveBeenCalledWith('p1', 'r1'));
    expect(api.listRoles).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('admin');
    await user.click(rowFor('admin').querySelector('button')!);
    await screen.findByText('Delete role "admin"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.deleteRole).not.toHaveBeenCalled();
  });
});

describe('dismissing a dialog with Escape', () => {
  // A modal <dialog> closes itself on Escape and fires `close`; the page has to notice and clear
  // the state behind it, or the dialog is off the screen and still open as far as it knows.
  it('closes the create form, and reopens it empty', async () => {
    const user = show();
    await screen.findByText('admin');
    await user.click(screen.getByRole('button', { name: /New Role/ }));
    await user.fill(screen.getByLabelText('Name'), 'editor');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());

    await user.click(screen.getByRole('button', { name: /New Role/ }));
    expect(screen.getByLabelText('Name')).toHaveValue('editor');
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = show();
    await screen.findByText('admin');
    await user.click(rowFor('admin').querySelector('button')!);
    await screen.findByText('Delete role "admin"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete role "admin"?')).toBeNull());
    expect(api.deleteRole).not.toHaveBeenCalled();
  });
});

/**
 * `/project/roles?project_id=` répond au super admin, si bien que les trois routes système
 * existaient sans appelant — et une porte sans appelant est ce qui a laissé les deux copies de la
 * création de rôle diverger jusqu'à ce que l'une journalise l'audit sous une organisation nulle.
 * Chaque appel part donc vers la portée d'où la page a été ouverte, et rien d'autre.
 */
describe('the scope each call goes to', () => {
  it('reads the project route when opened from the project scope', async () => {
    show();

    await screen.findByText('admin');
    expect(api.listRoles).toHaveBeenCalledWith('p1');
    expect(api.adminListRoles).not.toHaveBeenCalled();
  });

  it('reads the system route, on the project in the path, when opened from the system scope', async () => {
    showSystem();

    await screen.findByText('admin');
    expect(api.adminListRoles).toHaveBeenCalledWith('p9');
    expect(api.listRoles).not.toHaveBeenCalled();
  });

  it('creates through the system route', async () => {
    const user = showSystem();
    await screen.findByText('admin');
    await user.click(screen.getByRole('button', { name: /New Role/ }));

    await user.fill(screen.getByLabelText('Name'), 'editor');
    await user.click(submit());

    await vi.waitFor(() => expect(api.adminCreateRole)
      .toHaveBeenCalledWith('p9', { name: 'editor', description: undefined, rank: 100 }));
    expect(api.createRole).not.toHaveBeenCalled();
    expect(api.adminListRoles).toHaveBeenCalledTimes(2);
  });

  it('deletes through the system route', async () => {
    const user = showSystem();
    await screen.findByText('admin');
    await user.click(rowFor('admin').querySelector('button')!);

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    await vi.waitFor(() => expect(api.adminDeleteRole).toHaveBeenCalledWith('p9', 'r1'));
    expect(api.deleteRole).not.toHaveBeenCalled();
    expect(api.adminListRoles).toHaveBeenCalledTimes(2);
  });
});

describe('a refusal from either scope', () => {
  const fillAndSubmit = async (user: ReturnType<typeof show>) => {
    await user.click(screen.getByRole('button', { name: /New Role/ }));
    await user.fill(screen.getByLabelText('Name'), 'super_admin');
    await user.click(submit());
  };

  it('names the duplicate in the project scope', async () => {
    api.createRole.mockRejectedValue(new ApiError(409, { error: 'role_name_exists' }));
    const user = show();
    await screen.findByText('admin');

    await fillAndSubmit(user);

    expect(await screen.findByText('This project already has a role with that name.')).toBeInTheDocument();
  });

  it('names the duplicate in the system scope too', async () => {
    // La route système rendait 500 sur doublon avant d'être unifiée sur CreateRoleAsync ; elle
    // rend le même 409 que la portée projet, donc la même phrase.
    api.adminCreateRole.mockRejectedValue(new ApiError(409, { error: 'role_name_exists' }));
    const user = showSystem();
    await screen.findByText('admin');

    await fillAndSubmit(user);

    expect(await screen.findByText('This project already has a role with that name.')).toBeInTheDocument();
  });

  it('explains a reserved name, and leaves the form open to fix it', async () => {
    api.adminCreateRole.mockRejectedValue(new ApiError(400, { error: 'role_name_reserved' }));
    const user = showSystem();
    await screen.findByText('admin');

    await fillAndSubmit(user);

    expect(await screen.findByText(/reserved for management roles/)).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('super_admin');
    expect(submit()).toBeEnabled();
  });

  it('falls back to the server\'s own words for a code it does not know', async () => {
    api.adminCreateRole.mockRejectedValue(new ApiError(400, { detail: 'Rank must be positive.' }));
    const user = showSystem();
    await screen.findByText('admin');

    await fillAndSubmit(user);

    expect(await screen.findByText('Rank must be positive.')).toBeInTheDocument();
  });

  it('shows a failed delete in the confirmation, which stays open', async () => {
    api.adminDeleteRole.mockRejectedValue(new ApiError(403, null));
    const user = showSystem();
    await screen.findByText('admin');
    await user.click(rowFor('admin').querySelector('button')!);

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    expect(await screen.findByText('Failed to delete the role.')).toBeInTheDocument();
    expect(screen.getByText('Delete role "admin"?')).toBeInTheDocument();
  });
});

/**
 * L'édition n'a qu'une route, `PATCH /project/roles/{id}?project_id=`, et pas d'équivalent
 * système : `?project_id=` est honoré dès le niveau OrgAdmin, donc le super-admin passe par la
 * même. Le nom n'y figure pas — c'est la relation Keto écrite pour chaque porteur du rôle.
 */
describe('editing a role', () => {
  const openEdit = (user: ReturnType<typeof show>, name: string) =>
    user.click(screen.getByRole('button', { name: `Edit role ${name}` }));

  it('opens on the role as it stands', async () => {
    const user = show();
    await screen.findByText('admin');

    await openEdit(user, 'admin');

    expect(await screen.findByLabelText('Description')).toHaveValue('Everything');
    expect(screen.getByLabelText('Rank')).toHaveValue(1);
  });

  it('leaves the description empty for a role that has none', async () => {
    const user = show();
    // `viewer` is in the table and again in the sign-up footer; `admin` marks the load too.
    await screen.findByText('admin');

    await openEdit(user, 'viewer');

    expect(await screen.findByLabelText('Description')).toHaveValue('');
  });

  it('sends the new description and rank, then reloads', async () => {
    const user = show();
    await screen.findByText('admin');
    await openEdit(user, 'admin');
    await screen.findByLabelText('Description');

    await user.clear(screen.getByLabelText('Description'));
    await user.type(screen.getByLabelText('Description'), 'Reads only');
    await user.clear(screen.getByLabelText('Rank'));
    await user.type(screen.getByLabelText('Rank'), '20');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateRole).toHaveBeenCalledWith('p1', 'r1', { description: 'Reads only', rank: 20 }));
    await vi.waitFor(() => expect(api.listRoles).toHaveBeenCalledTimes(2));
    expect(screen.queryByText('Edit role "admin"')).not.toBeInTheDocument();
  });

  it('targets the project in the URL when opened from the system tree', async () => {
    const user = showSystem();
    await screen.findByText('admin');
    await openEdit(user, 'admin');
    await screen.findByLabelText('Rank');

    await user.clear(screen.getByLabelText('Rank'));
    await user.type(screen.getByLabelText('Rank'), '5');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateRole).toHaveBeenCalledWith('p9', 'r1', { description: 'Everything', rank: 5 }));
    // Le rechargement reste celui de la portée : la liste système, pas /project/roles.
    await vi.waitFor(() => expect(api.adminListRoles).toHaveBeenCalledTimes(2));
    expect(api.listRoles).not.toHaveBeenCalled();
  });

  it('shows a refused edit and keeps the form open on it', async () => {
    api.updateRole.mockRejectedValue(new ApiError(400, { detail: 'Rank must be positive.' }));
    const user = show();
    await screen.findByText('admin');
    await openEdit(user, 'admin');
    await screen.findByLabelText('Rank');

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Rank must be positive.')).toBeInTheDocument();
    expect(screen.getByText('Edit role "admin"')).toBeInTheDocument();
    expect(api.listRoles).toHaveBeenCalledTimes(1);
  });
});
