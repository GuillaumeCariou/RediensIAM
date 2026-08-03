import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import SystemProjects from './SystemProjects';
import { fmtDateShort } from '@/lib/utils';

const api = vi.hoisted(() => ({ adminListAllProjects: vi.fn() }));
vi.mock('@/api', () => api);

const PROJECTS = [
  { id: 'p1', name: 'Customer Portal', slug: 'portal', active: true, org_id: 'o1', org_name: 'Acme', hydra_client_id: 'c1', created_at: '2026-03-04T05:06:07Z' },
  { id: 'p2', name: 'Internal Tools', slug: 'tools', active: false, org_id: 'o2', org_name: 'Globex', hydra_client_id: null, created_at: '2026-01-02T00:00:00Z' },
];

beforeEach(() => {
  vi.clearAllMocks();
  api.adminListAllProjects.mockResolvedValue({ projects: PROJECTS });
});

const show = () => {
  const user = userEvent.setup();
  render(<MemoryRouter><SystemProjects /></MemoryRouter>);
  return user;
};

describe('the table', () => {
  it('lists every project with its organisation, status and creation date', async () => {
    show();

    expect(await screen.findByText('Customer Portal')).toBeInTheDocument();
    expect(screen.getByText('/portal')).toBeInTheDocument();
    expect(screen.getByText('Acme')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Inactive')).toBeInTheDocument();
    expect(screen.getByText(fmtDateShort('2026-03-04T05:06:07Z'))).toBeInTheDocument();
  });

  it('links each project to its own page and to its organisation', async () => {
    show();
    await screen.findByText('Customer Portal');

    expect(screen.getByRole('link', { name: /Customer Portal/ }))
      .toHaveAttribute('href', '/system/organisations/o1/projects/p1');
    expect(screen.getByRole('link', { name: 'Acme' })).toHaveAttribute('href', '/system/organisations/o1');
  });

  it('accepts a bare array as well as an envelope', async () => {
    // The endpoint has answered both shapes; unwrapping only one left the table permanently empty.
    api.adminListAllProjects.mockResolvedValue(PROJECTS);
    show();

    expect(await screen.findByText('Customer Portal')).toBeInTheDocument();
  });

  it('shows placeholder rows while loading, not the empty state', async () => {
    api.adminListAllProjects.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByText('No projects yet')).not.toBeInTheDocument();
    expect(document.querySelectorAll('tbody tr')).toHaveLength(6);
  });

  it('says the platform has no projects when it has none', async () => {
    api.adminListAllProjects.mockResolvedValue({ projects: [] });
    show();

    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
  });

  it('survives an endpoint that fails, showing the empty state rather than nothing', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.adminListAllProjects.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('the search box', () => {
  it.each([
    ['a project name', 'portal ', 'Customer Portal'],
    ['an organisation name', 'globex', 'Internal Tools'],
    ['a slug', 'tools', 'Internal Tools'],
  ])('matches on %s, ignoring case', async (_n, query, expected) => {
    const user = show();
    await screen.findByText('Customer Portal');

    await user.fill(screen.getByPlaceholderText('Search by name, org, or slug…'), query.trim());

    expect(screen.getByText(expected)).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(2);   // the header, plus the one match
  });

  it('says the search matched nothing, which is not the same as having no projects', async () => {
    const user = show();
    await screen.findByText('Customer Portal');

    await user.fill(screen.getByPlaceholderText('Search by name, org, or slug…'), 'zzz');

    expect(screen.getByText('No projects match your search')).toBeInTheDocument();
  });
});
