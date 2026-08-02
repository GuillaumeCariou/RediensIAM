import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectRoles from './ProjectRoles';
import { fmtDate } from '@/lib/utils';

/**
 * Rank orders privilege the wrong way round from intuition — lower is stronger — and it is what
 * decides which roles a project manager may hand out. So the ordering is asserted everywhere it
 * shows: the table, and the default-role picker.
 */

const api = vi.hoisted(() => ({
  listRoles: vi.fn(), createRole: vi.fn(), deleteRole: vi.fn(),
  getProjectInfo: vi.fn(), updateProject: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ROLES = [
  { id: 'r2', name: 'viewer', description: null, rank: 100, created_at: '2026-01-02T00:00:00Z' },
  { id: 'r1', name: 'admin', description: 'Everything', rank: 1, created_at: '2026-01-02T00:00:00Z' },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.listRoles.mockResolvedValue({ roles: ROLES });
  api.getProjectInfo.mockResolvedValue({ default_role_id: 'r2' });
});

function show(path = '/project/roles', pattern = '/project/roles') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ProjectRoles />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const picker = () => screen.getByRole('combobox');
const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });
const submit = () => document.querySelector<HTMLButtonElement>('button[form="create-role-form"]')!;

describe('the table', () => {
  it('lists the roles strongest first, whatever order they arrived in', async () => {
    show();

    await screen.findByText('admin');
    const names = screen.getAllByRole('row').slice(1).map(r => r.querySelector('.iam-mono')!.textContent);
    expect(names).toEqual(['admin', 'viewer']);
  });

  it('shows the description, the rank and when it was made', async () => {
    show();

    await screen.findByText('admin');
    expect(rowFor('admin')).toHaveTextContent('Everything');
    expect(rowFor('admin')).toHaveTextContent('1');
    expect(rowFor('admin')).toHaveTextContent(fmtDate('2026-01-02T00:00:00Z'));
  });

  it('prints an em dash for a role with no description', async () => {
    show();

    await screen.findByText('viewer');
    expect(rowFor('viewer')).toHaveTextContent('—');
  });

  it('marks which role new users are given', async () => {
    show();

    await screen.findByText('viewer');
    expect(rowFor('viewer')).toHaveTextContent('Default');
    expect(rowFor('admin')).not.toHaveTextContent('Default');
  });

  it('explains which way rank runs, but only when there are roles to rank', async () => {
    show();

    expect(await screen.findByText(/lower number = higher privilege/)).toBeInTheDocument();
  });

  it('says there are none, without the ranking note', async () => {
    api.listRoles.mockResolvedValue({ roles: [] });
    show();

    expect(await screen.findByText('No roles defined yet')).toBeInTheDocument();
    expect(screen.queryByText(/lower number = higher privilege/)).not.toBeInTheDocument();
  });

  it('shows placeholder rows while loading', () => {
    api.listRoles.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(4);
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
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

describe('the default role', () => {
  it('opens on the project\'s current one, with the options in rank order', async () => {
    show();

    await vi.waitFor(() => expect(picker()).toHaveValue('r2'));
    expect([...picker().querySelectorAll('option')].map(o => o.textContent))
      .toEqual(['No default role', 'admin (rank 1)', 'viewer (rank 100)']);
  });

  it('opens on "none" for a project that has none', async () => {
    api.getProjectInfo.mockResolvedValue({ default_role_id: null });
    show();

    await vi.waitFor(() => expect(picker()).toHaveValue('__none__'));
  });

  it('sets one', async () => {
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('r2'));

    await user.selectOptions(picker(), 'r1');

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', { default_role_id: 'r1' }));
    expect(picker()).toHaveValue('r1');
  });

  it('clears it with an explicit flag, not by sending null', async () => {
    // A null default_role_id is indistinguishable from "field omitted" on a PATCH.
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('r2'));

    await user.selectOptions(picker(), '__none__');

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', { clear_default_role: true }));
    expect(picker()).toHaveValue('__none__');
  });

  it('says so, and leaves the picker where it was, when the save fails', async () => {
    api.updateProject.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('r2'));

    await user.selectOptions(picker(), 'r1');

    expect(await screen.findByText('Failed to save default role.')).toBeInTheDocument();
    expect(picker()).toHaveValue('r2');
  });

  it('re-enables the picker after a failure', async () => {
    api.updateProject.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(picker()).toHaveValue('r2'));

    await user.selectOptions(picker(), 'r1');

    await vi.waitFor(() => expect(picker()).toBeEnabled());
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
