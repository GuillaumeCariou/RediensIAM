import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectDashboard from './ProjectDashboard';

const api = vi.hoisted(() => ({ getProjectInfo: vi.fn(), getProjectStats: vi.fn() }));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const PROJECT = {
  id: 'p1', name: 'Customer Portal', slug: 'portal', active: true,
  assigned_user_list_id: 'l1', assigned_user_list_name: 'Staff',
  require_role_to_login: false, hydra_client_id: 'client-abc',
};
const STATS = { total_users: 10, active_users: 8, users_by_role: [{ role_id: 'r1', role_name: 'admin', count: 6 }] };

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.getProjectInfo.mockResolvedValue(PROJECT);
  api.getProjectStats.mockResolvedValue(STATS);
});

function show(path = '/project', pattern = '/project') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ProjectDashboard />} /></Routes>
    </MemoryRouter>,
  );
}

/** The page title is a styled div, not a heading — see PageHeader. */
const title = () => document.querySelector('.iam-page-title')?.textContent ?? null;
const titled = (t: string) => vi.waitFor(() => expect(title()).toBe(t));

describe('the header', () => {
  it('names the project and its OAuth client', async () => {
    show();

    await titled('Customer Portal');
    expect(screen.getByText('/portal · client-abc')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
  });

  it('says so while it is still loading, rather than showing a bare "Project"', () => {
    api.getProjectInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(title()).toBe('Loading…');
  });

  it('marks an inactive project, and one that gates sign-in on a role', async () => {
    api.getProjectInfo.mockResolvedValue({ ...PROJECT, active: false, require_role_to_login: true });
    show();

    expect(await screen.findByText('Inactive')).toBeInTheDocument();
    expect(screen.getByText('Role Required')).toBeInTheDocument();
  });
});

describe('the statistics', () => {
  it('shows them once both requests have answered', async () => {
    show();

    expect(await screen.findByText('10')).toBeInTheDocument();
    expect(screen.getByText('80% active')).toBeInTheDocument();
  });

  it('links to the pages behind each counter, in the scope the page is in', async () => {
    show('/system/organisations/o1/projects/p9', '/system/organisations/:oid/projects/:pid');

    await titled('Customer Portal');
    const links = screen.getAllByRole('link', { name: 'Manage' }).map(a => a.getAttribute('href'));
    expect(links).toEqual([
      '/system/organisations/o1/projects/p9/users',
      '/system/organisations/o1/projects/p9/roles',
    ]);
  });

  it('still shows the project when the statistics endpoint fails', async () => {
    // The counters are secondary; losing them must not take the page down with them.
    api.getProjectStats.mockRejectedValue(new Error('500'));
    show();

    await titled('Customer Portal');
    expect(screen.getAllByText('—')).not.toHaveLength(0);
  });

  it('finishes loading even when the project itself cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getProjectInfo.mockRejectedValue(new Error('500'));
    show();

    await titled('Project');
    vi.restoreAllMocks();
  });
});

describe('when the page is reached with no project in scope', () => {
  it('says so instead of requesting /project/info for nobody', () => {
    auth.projectId = '';
    show();

    expect(screen.getByText(/No project selected/)).toBeInTheDocument();
    expect(api.getProjectInfo).not.toHaveBeenCalled();
  });
});
