import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import NavTree from './NavTree';

/**
 * The tree is the console's answer to "where am I". Everything asserted here is about that: which
 * node is lit, what a node opens, and what the tree refuses to fetch until you ask.
 *
 * Not asserted here: how a URL is spelled. That belongs to `scope.ts`, which has its own tests —
 * this component is not allowed to know, and a test that pinned URLs here would make it know.
 */

const api = vi.hoisted(() => ({ listOrgs: vi.fn(), listProjects: vi.fn() }));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({
  isSuperAdmin: true, isOrgAdmin: false, isProjectManager: false,
}));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ORGS = [
  { id: 'o1', name: 'Yandee Infrastructure', active: true },
  { id: 'o2', name: 'Acme Corp', active: false },
];
const PROJECTS = [{ id: 'p1', name: 'Client Portal' }];

beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(auth, { isSuperAdmin: true, isOrgAdmin: false, isProjectManager: false });
  api.listOrgs.mockResolvedValue(ORGS);
  api.listProjects.mockResolvedValue(PROJECTS);
});

function show(path = '/system') {
  const user = userEvent.setup();
  render(<MemoryRouter initialEntries={[path]}><NavTree /></MemoryRouter>);
  return user;
}

const item = (name: string | RegExp) => screen.queryByRole('link', { name });

describe('what a super admin sees', () => {
  it('roots the tree at the deployment and lists the tenants', async () => {
    show();

    expect(await screen.findByRole('link', { name: 'Deployment' })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: /Yandee Infrastructure/ })).toBeInTheDocument();
    expect(screen.getByText('Tenants · 2')).toBeInTheDocument();
  });

  /**
   * A deployment with fifty tenants must not make fifty requests to draw a sidebar. Projects load
   * when their organisation opens, and not before.
   */
  it('does not fetch a tenant\'s projects until it is opened', async () => {
    const user = show();
    await screen.findByRole('link', { name: /Acme Corp/ });

    expect(api.listProjects).not.toHaveBeenCalledWith('o2');

    await user.click(screen.getByRole('button', { name: 'Expand Acme Corp' }));

    await waitFor(() => expect(api.listProjects).toHaveBeenCalledWith('o2'));
    expect(await screen.findByRole('link', { name: 'Client Portal' })).toBeInTheDocument();
  });

  it('opens a project onto its own destinations', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Expand Acme Corp' }));
    await user.click(await screen.findByRole('button', { name: 'Expand Client Portal' }));

    expect(await screen.findByRole('link', { name: 'Roles' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Authentication' })).toBeInTheDocument();
  });
});

describe('where you are', () => {
  it('opens the organisation the URL names, without being asked', async () => {
    show('/system/organisations/o1/userlists');

    expect(await screen.findByRole('link', { name: 'User lists' })).toBeInTheDocument();
  });

  it('lights the destination the URL is on', async () => {
    show('/system/organisations/o1/webhooks');

    await waitFor(() => expect(item('Webhooks')!.className).toContain('active'));
  });

  /**
   * The organisation's own page and its Overview destination share a URL. Being on it must light
   * the organisation node, not leave the tree pointing at nothing.
   */
  it('lights the tenant node when on the tenant itself', async () => {
    show('/system/organisations/o1');

    await waitFor(() => expect(item(/Yandee Infrastructure/)!.className).toContain('active'));
  });
});

describe('the filter', () => {
  it('narrows the tree to what matches', async () => {
    const user = show();
    await screen.findByRole('link', { name: /Acme Corp/ });

    await user.fill(screen.getByRole('textbox', { name: 'Filter the tree' }), 'acme');

    expect(item(/Acme Corp/)).toBeInTheDocument();
    expect(item(/Yandee Infrastructure/)).not.toBeInTheDocument();
  });

  it('keeps a tenant whose destinations match, so a page is reachable by its own name', async () => {
    const user = show();
    await screen.findByRole('link', { name: /Acme Corp/ });

    await user.fill(screen.getByRole('textbox', { name: 'Filter the tree' }), 'webhook');

    expect(item(/Acme Corp/)).toBeInTheDocument();
  });
});

describe('collapsing', () => {
  /** Opened by the URL on arrival, closable by hand — and it stays closed. */
  it('closes a node the operator collapses', async () => {
    const user = show('/system/organisations/o1/webhooks');
    expect(await screen.findByRole('link', { name: 'Webhooks' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Collapse Yandee Infrastructure' }));

    expect(item('Webhooks')).not.toBeInTheDocument();
    expect(item(/Yandee Infrastructure/)).toBeInTheDocument();
  });
});

describe('what a tenant administrator sees', () => {
  beforeEach(() => {
    Object.assign(auth, { isSuperAdmin: false, isOrgAdmin: true, isProjectManager: false });
  });

  /**
   * One organisation and no way to reach another: a "Tenants" list of one is a navigation control
   * that never navigates, and a Deployment node they cannot open is a locked door in a menu.
   */
  it('is rooted at their own organisation, with no deployment node and no tenant list', async () => {
    show('/org');

    expect(await screen.findByRole('link', { name: 'Organisation' })).toBeInTheDocument();
    expect(item('Deployment')).not.toBeInTheDocument();
    expect(screen.queryByText(/^Tenants/)).not.toBeInTheDocument();
  });

  it('asks the server for nothing it cannot browse', async () => {
    show('/org');
    await screen.findByRole('link', { name: 'Organisation' });

    expect(api.listOrgs).not.toHaveBeenCalled();
  });

  it('offers its destinations', async () => {
    show('/org');

    expect(await screen.findByRole('link', { name: 'Webhooks' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Service accounts' })).toBeInTheDocument();
  });

  /**
   * `superOnly` marks deployment entries that belong to a super-admin alone. Nobody else may be
   * offered them — an entry that leads to a 403 is worse than an absent one.
   */
  it('is never offered a super-admin-only entry', async () => {
    show('/org');
    await screen.findByRole('link', { name: 'Organisation' });

    expect(item('Health')).not.toBeInTheDocument();
    expect(item('Metrics')).not.toBeInTheDocument();
  });
});

describe('what a project administrator sees', () => {
  beforeEach(() => {
    Object.assign(auth, { isSuperAdmin: false, isOrgAdmin: false, isProjectManager: true });
  });

  it('is rooted at their project', async () => {
    show('/project');

    expect(await screen.findByRole('link', { name: 'Project' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Roles' })).toBeInTheDocument();
    expect(item('Organisation')).not.toBeInTheDocument();
  });
});
