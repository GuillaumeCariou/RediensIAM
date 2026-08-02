import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import SystemProjectDetail from './SystemProjectDetail';
import { fmtDateShort } from '@/lib/utils';

const api = vi.hoisted(() => ({
  adminGetProject: vi.fn(), adminGetProjectStats: vi.fn(),
  updateProject: vi.fn(), adminDeleteProject: vi.fn(),
}));
vi.mock('@/api', () => api);

const PROJECT = {
  id: 'p1', name: 'Customer Portal', slug: 'portal', active: true,
  hydra_client_id: 'client-abc', assigned_user_list_id: 'l1', created_at: '2026-01-02T00:00:00Z',
};
const STATS = { total_users: 10, active_users: 8, users_by_role: [] };

beforeEach(() => {
  vi.clearAllMocks();
  api.adminGetProject.mockResolvedValue(PROJECT);
  api.adminGetProjectStats.mockResolvedValue(STATS);
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show(path = '/system/organisations/o1/projects/p1') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/system/organisations/:oid/projects/:pid" element={<SystemProjectDetail />} />
      </Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));

describe('the project', () => {
  it('names it with its client id and creation date', async () => {
    show();

    expect(await screen.findByRole('heading', { name: 'Customer Portal' })).toBeInTheDocument();
    // The line is broken up by a <span> around the client id, so it is read off the element.
    expect(screen.getByText(/^\/portal/).textContent)
      .toBe(`/portal · client-abc · Created ${fmtDateShort(PROJECT.created_at)}`);
    expect(screen.getByText('Active')).toBeInTheDocument();
  });

  it('marks an inactive one', async () => {
    api.adminGetProject.mockResolvedValue({ ...PROJECT, active: false });
    show();

    expect(await screen.findByText('Inactive')).toBeInTheDocument();
  });

  it('shows its statistics', async () => {
    show();
    expect(await screen.findByText('10')).toBeInTheDocument();
  });

  it('shows the project even when the statistics endpoint fails', async () => {
    api.adminGetProjectStats.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByRole('heading', { name: 'Customer Portal' })).toBeInTheDocument();
  });

  it('shows placeholders, and no actions to press, while loading', () => {
    api.adminGetProject.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: /Rename/ })).not.toBeInTheDocument();
  });

  it('finishes loading when the project cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.adminGetProject.mockRejectedValue(new Error('404'));
    show();

    await vi.waitFor(() => expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(0));
    expect(screen.queryByRole('button', { name: /Rename/ })).not.toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('going back', () => {
  it('returns to the organisation this project belongs to', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });

    await user.click(screen.getByRole('button', { name: /Back to Organisation/ }));

    await arrivedAt('/system/organisations/o1');
  });
});

describe('renaming', () => {
  it('opens on the current name and saves the new one, then re-reads', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });

    await user.click(screen.getByRole('button', { name: /Rename/ }));
    expect(screen.getByLabelText('Name')).toHaveValue('Customer Portal');
    await user.fill(screen.getByLabelText('Name'), 'Client Portal');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', { name: 'Client Portal' }));
    expect(api.adminGetProject).toHaveBeenCalledTimes(2);
  });

  it('refuses a blank name', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });

    await user.click(screen.getByRole('button', { name: /Rename/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });
    await user.click(screen.getByRole('button', { name: /Rename/ }));

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.updateProject).not.toHaveBeenCalled();
  });
});

describe('deleting', () => {
  it('names it and warns what else goes, before doing anything', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });

    await user.click(screen.getByRole('button', { name: /Delete/ }));

    expect(await screen.findByText('Delete project "Customer Portal"?')).toBeInTheDocument();
    expect(screen.getByText(/OAuth2 client for this project will also be deleted/)).toBeInTheDocument();
    expect(api.adminDeleteProject).not.toHaveBeenCalled();
  });

  it('deletes and leaves for the organisation, there being no project left to show', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });
    await user.click(screen.getByRole('button', { name: /Delete/ }));

    await user.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);

    await vi.waitFor(() => expect(api.adminDeleteProject).toHaveBeenCalledWith('p1'));
    await arrivedAt('/system/organisations/o1');
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });
    await user.click(screen.getByRole('button', { name: /Delete/ }));
    await screen.findByText('Delete project "Customer Portal"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.adminDeleteProject).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  it('closes the rename form without saving', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });
    await user.click(screen.getByRole('button', { name: /Rename/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
    expect(api.updateProject).not.toHaveBeenCalled();
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'Customer Portal' });
    await user.click(screen.getByRole('button', { name: /Delete/ }));
    await screen.findByText('Delete project "Customer Portal"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete project "Customer Portal"?')).toBeNull());
    expect(api.adminDeleteProject).not.toHaveBeenCalled();
  });
});
