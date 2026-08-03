import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import OrgDashboard from './OrgDashboard';
import { fmtDateShort } from '@/lib/utils';

const api = vi.hoisted(() => ({ getOrgInfo: vi.fn(), listProjects: vi.fn(), listUserLists: vi.fn() }));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const ORG = {
  id: 'o1', name: 'Acme', slug: 'acme', active: true, suspended_at: null,
  created_at: '2026-03-04T05:06:07Z', metadata: {},
};
const PROJECTS = [
  { id: 'p1', name: 'Portal', slug: 'portal', active: true },
  { id: 'p2', name: 'Tools', slug: 'tools', active: false },
];
const LISTS = [
  { id: 'l1', name: 'Staff', immovable: true, user_count: 40 },
  { id: 'l2', name: 'Contractors', immovable: false, user_count: 2 },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.getOrgInfo.mockResolvedValue(ORG);
  api.listProjects.mockResolvedValue({ projects: PROJECTS });
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
});

function show(path = '/org', pattern = '/org') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<OrgDashboard />} /></Routes>
    </MemoryRouter>,
  );
}

/** The page title is a styled div, not a heading — see PageHeader. */
const title = () => document.querySelector('.iam-page-title')?.textContent ?? null;
const titled = (t: string) => vi.waitFor(() => expect(title()).toBe(t));
/** The value on the counter card labelled `label`. */
const stat = (label: string) =>
  [...document.querySelectorAll('.iam-stat')]
    .find(c => c.querySelector('.iam-stat-label')?.textContent === label)
    ?.querySelector('.iam-stat-value')?.textContent ?? null;

describe('the summary', () => {
  it('names the organisation and counts what it holds', async () => {
    show();

    await titled('Acme');
    expect(screen.getByText('/acme')).toBeInTheDocument();
    // The header chip, plus the one on the active project's row.
    expect(screen.getAllByText('Active')).toHaveLength(2);
    expect(stat('Projects')).toBe('2');
    expect(stat('User Lists')).toBe('2');
    expect(stat('Total Users')).toBe('42');
    expect(stat('Member since')).toBe(fmtDateShort(ORG.created_at));
  });

  it('shows an em dash rather than zero counts while loading', () => {
    api.getOrgInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(title()).toBe('Loading…');
    expect(screen.getAllByText('—')).toHaveLength(4);
  });

  it.each([
    ['suspended', { suspended_at: '2026-04-01T00:00:00Z' }, 'Suspended'],
    ['inactive', { active: false }, 'Inactive'],
  ])('marks a %s organisation as such', async (_n, patch, label) => {
    // A suspended organisation must not read as merely inactive: the first is a billing action
    // an operator has taken, the second is a flag.
    api.getOrgInfo.mockResolvedValue({ ...ORG, ...patch });
    api.listProjects.mockResolvedValue({ projects: [] });
    show();

    expect(await screen.findByText(label)).toBeInTheDocument();
  });

  it('counts a list whose user_count the server omitted as zero, not NaN', async () => {
    api.getOrgInfo.mockResolvedValue(ORG);
    api.listUserLists.mockResolvedValue({ user_lists: [{ id: 'l1', name: 'Staff', immovable: true }] });
    show();

    await titled('Acme');
    expect(stat('Total Users')).toBe('0');
  });
});

describe('the tables', () => {
  it('lists the projects with a link into each', async () => {
    show();

    expect(await screen.findByText('Portal')).toBeInTheDocument();
    expect(screen.getByText('tools')).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Open →' })[0])
      .toHaveAttribute('href', '/project?project_id=p1');
  });

  it('sends a super admin through the system routes instead', async () => {
    show('/system/organisations/o9', '/system/organisations/:id');

    await screen.findByText('Portal');
    expect(screen.getAllByRole('link', { name: 'Open →' })[0])
      .toHaveAttribute('href', '/system/organisations/o9/projects/p1');
    expect(screen.getAllByRole('link', { name: 'Manage →' })[0])
      .toHaveAttribute('href', '/system/organisations/o9/projects');
  });

  it('lists the user lists with their sizes', async () => {
    show();

    expect(await screen.findByText('Staff')).toBeInTheDocument();
    expect(screen.getByText('40')).toBeInTheDocument();
  });

  it('leaves a table out entirely when the organisation has none of that thing', async () => {
    api.listProjects.mockResolvedValue({ projects: [] });
    api.listUserLists.mockResolvedValue({ user_lists: [] });
    show();

    await titled('Acme');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listProjects.mockResolvedValue(PROJECTS);
    api.listUserLists.mockResolvedValue(LISTS);
    show();

    expect(await screen.findByText('Portal')).toBeInTheDocument();
    expect(screen.getByText('Staff')).toBeInTheDocument();
  });
});

describe('when the page is reached with no organisation in scope', () => {
  it('says so and points at the list, rather than requesting /org/info for nobody', () => {
    auth.orgId = '';
    show();

    expect(screen.getByText(/No organisation selected/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Organisations' }))
      .toHaveAttribute('href', '/system/organisations');
    expect(api.getOrgInfo).not.toHaveBeenCalled();
  });
});

describe('when the organisation cannot be read', () => {
  it('finishes loading rather than sitting on the spinner forever', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getOrgInfo.mockRejectedValue(new Error('500'));
    show();

    await titled('Organisation');
    vi.restoreAllMocks();
  });
});
