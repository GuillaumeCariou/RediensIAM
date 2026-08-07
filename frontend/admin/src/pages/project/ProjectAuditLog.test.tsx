import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import ProjectAuditLog from './ProjectAuditLog';

/**
 * Le journal d'un projet. `/project/audit-log` est la seule route de journal qu'un project_admin
 * peut lire, et elle n'a pas d'export : la page ne doit donc offrir aucun bouton d'export, sous
 * peine de promettre un 404. Le reste est ce que ce chantier corrige partout — un refus se lit à
 * l'écran, pas dans les devtools.
 */

const api = vi.hoisted(() => ({ getProjectAuditLog: vi.fn() }));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: 'p1' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const entry = (i: number) => ({
  id: `e${i}`, action: 'role.assigned', actor_id: '11111111-2222-3333-4444-555555555555',
  target_type: 'user', target_id: '99999999-8888-7777-6666-555555555555',
  ip_address: '10.0.0.1', created_at: '2026-02-01T10:00:00Z',
});
const page = (n: number) => Array.from({ length: n }, (_, i) => entry(i));

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.getProjectAuditLog.mockResolvedValue([entry(1)]);
});

function show(path = '/project/audit-log', pattern = '/project/audit-log') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ProjectAuditLog />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

describe('the project audit log', () => {
  it('reads its own project, scoped by id', async () => {
    show();

    await vi.waitFor(() => expect(api.getProjectAuditLog)
      .toHaveBeenCalledWith('p1', { limit: 50, offset: 0 }));
    expect(await screen.findByText('role.assigned')).toBeInTheDocument();
  });

  it('says the log is empty rather than showing a bare table', async () => {
    api.getProjectAuditLog.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No audit events found')).toBeInTheDocument();
  });

  it('offers no export, because the project scope has no export route', async () => {
    show();

    await screen.findByText('role.assigned');
    expect(screen.queryByRole('button', { name: /Export/ })).not.toBeInTheDocument();
  });

  it('shows a refused read instead of an empty page', async () => {
    api.getProjectAuditLog.mockRejectedValue(new Error('403'));
    show();

    expect(await screen.findByText(/Could not read this project/)).toBeInTheDocument();
  });

  it('pages forward only while a full page came back', async () => {
    api.getProjectAuditLog.mockResolvedValue(page(50));
    const user = show();
    await screen.findAllByText('role.assigned');

    await user.click(screen.getByRole('button', { name: /Next/ }));

    await vi.waitFor(() => expect(api.getProjectAuditLog)
      .toHaveBeenLastCalledWith('p1', { limit: 50, offset: 50 }));
  });

  it('reads a project named in the URL when a super admin browses into one', async () => {
    show('/system/organisations/o9/projects/p9/audit-log',
      '/system/organisations/:oid/projects/:pid/audit-log');

    await vi.waitFor(() => expect(api.getProjectAuditLog)
      .toHaveBeenCalledWith('p9', { limit: 50, offset: 0 }));
  });

  it('asks for nothing when no project is in scope', () => {
    auth.projectId = '';
    show();

    expect(api.getProjectAuditLog).not.toHaveBeenCalled();
  });
});
