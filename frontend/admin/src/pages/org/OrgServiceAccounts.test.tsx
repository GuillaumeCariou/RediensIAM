import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import OrgServiceAccounts from './OrgServiceAccounts';
import SystemServiceAccounts from '@/pages/system/SystemServiceAccounts';
import { fmtDate, fmtDateShort } from '@/lib/utils';

/**
 * `/service-accounts` returns every account the caller can see, across scopes. Both pages read it
 * and then filter — the org page to `org_id === orgId`, the system page to `is_system` — so the
 * filter is the tenant boundary as the operator experiences it. Dropping it would list another
 * organisation's automation identities, with a delete button beside each.
 */

const api = vi.hoisted(() => ({
  listServiceAccounts: vi.fn(), createServiceAccount: vi.fn(),
  deleteServiceAccount: vi.fn(), listUserLists: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const sa = (over: Record<string, unknown> = {}) => ({
  id: 's1', name: 'ci-deploy', description: 'CI pipeline', active: true,
  last_used_at: '2026-03-04T05:06:07Z', created_at: '2026-01-02T00:00:00Z',
  org_id: 'o1', is_system: false, ...over,
});

const ALL = [
  sa(),
  sa({ id: 's2', name: 'other-tenant', org_id: 'o2', description: null }),
  sa({ id: 's3', name: 'platform-bot', org_id: null, is_system: true, active: false }),
];

const LISTS = [{ id: 'l1', name: 'Staff', org_id: 'o1', immovable: false }];
const SYSTEM_LISTS = [
  { id: 'l1', name: 'Staff', org_id: 'o1', immovable: false },
  { id: 'sys', name: 'System', org_id: null, immovable: true },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.listServiceAccounts.mockResolvedValue(ALL);
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function showOrg(path = '/org/service-accounts', pattern = '/org/service-accounts') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      {/* Outside the Routes: navigating to a detail page leaves `pattern` unmatched, and the
          address readout has to survive that. */}
      <Routes><Route path={pattern} element={<OrgServiceAccounts />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

function showSystem() {
  api.listUserLists.mockResolvedValue(SYSTEM_LISTS);
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/system/service-accounts']}>
      <Routes><Route path="*" element={<SystemServiceAccounts />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));

describe('the org page', () => {
  it('lists this organisation\'s accounts and nobody else\'s', async () => {
    showOrg();

    expect(await screen.findByText('ci-deploy')).toBeInTheDocument();
    expect(screen.queryByText('other-tenant')).not.toBeInTheDocument();
    expect(screen.queryByText('platform-bot')).not.toBeInTheDocument();
  });

  it('shows the description, status and timestamps', async () => {
    showOrg();

    expect(await screen.findByText('CI pipeline')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText(fmtDate('2026-03-04T05:06:07Z'))).toBeInTheDocument();
  });

  it('opens an account from its row', async () => {
    const user = showOrg();

    await user.click(await screen.findByText('ci-deploy'));

    await arrivedAt('/org/service-accounts/s1');
  });

  it('opens it through the system route for a super admin browsing a tenant', async () => {
    auth.orgId = 'unused';
    const user = showOrg('/system/organisations/o1/service-accounts',
      '/system/organisations/:id/service-accounts');

    await user.click(await screen.findByText('ci-deploy'));

    await arrivedAt('/system/organisations/o1/service-accounts/s1');
  });

  it('creates one against the chosen user list', async () => {
    const user = showOrg();
    await screen.findByText('ci-deploy');

    await user.click(screen.getByRole('button', { name: /New Service Account/ }));
    await user.fill(screen.getByLabelText('Name'), 'reporting-bot');
    await user.fill(screen.getByLabelText('Description (optional)'), 'nightly reports');
    await user.selectOptions(screen.getByLabelText('User List'), 'l1');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith({
      name: 'reporting-bot', description: 'nightly reports', user_list_id: 'l1',
    }));
    expect(api.listServiceAccounts).toHaveBeenCalledTimes(2);
  });

  it('sends no description rather than an empty one', async () => {
    const user = showOrg();
    await screen.findByText('ci-deploy');

    await user.click(screen.getByRole('button', { name: /New Service Account/ }));
    await user.fill(screen.getByLabelText('Name'), 'reporting-bot');
    await user.selectOptions(screen.getByLabelText('User List'), 'l1');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ description: undefined })));
  });

  it('requires a name and a list', async () => {
    const user = showOrg();
    await screen.findByText('ci-deploy');

    await user.click(screen.getByRole('button', { name: /New Service Account/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
    expect(screen.getByLabelText('User List')).toBeRequired();
  });

  it('offers nothing to create with when no organisation is in scope', async () => {
    auth.orgId = '';
    showOrg();

    await screen.findByText('No service accounts');
    expect(screen.queryByRole('button', { name: /New Service Account/ })).not.toBeInTheDocument();
    expect(api.listServiceAccounts).not.toHaveBeenCalled();
  });
});

describe('the system page', () => {
  it('lists the system accounts and nobody\'s tenant ones', async () => {
    showSystem();

    expect(await screen.findByText('platform-bot')).toBeInTheDocument();
    expect(screen.queryByText('ci-deploy')).not.toBeInTheDocument();
    expect(screen.queryByText('other-tenant')).not.toBeInTheDocument();
  });

  it('marks an inactive account as such', async () => {
    showSystem();
    expect(await screen.findByText('Inactive')).toBeInTheDocument();
  });

  it('shows the dates in the short form this page uses', async () => {
    showSystem();
    expect(await screen.findByText(fmtDateShort('2026-01-02T00:00:00Z'))).toBeInTheDocument();
  });

  it('opens an account from its row', async () => {
    const user = showSystem();

    await user.click(await screen.findByText('platform-bot'));

    await arrivedAt('/system/service-accounts/s3');
  });

  it('creates against the system list, which it finds rather than being told', async () => {
    const user = showSystem();
    await screen.findByText('platform-bot');

    await user.click(screen.getByRole('button', { name: /New Service Account/ }));
    await user.fill(screen.getByLabelText('Name'), 'audit-bot');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith({
      name: 'audit-bot', description: undefined, user_list_id: 'sys',
    }));
  });

  it('refuses to offer creation until the system list has been identified', async () => {
    // Creating without one would attach the account to no list at all.
    api.listUserLists.mockResolvedValue([{ id: 'l1', name: 'Staff', org_id: 'o1', immovable: false }]);
    const user = userEvent.setup();
    render(<MemoryRouter><SystemServiceAccounts /></MemoryRouter>);
    await screen.findByText('platform-bot');

    expect(screen.getByRole('button', { name: /New Service Account/ })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: /New Service Account/ })).catch(() => {});
    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });
});

describe.each([
  ['the org page', () => showOrg(), 'ci-deploy'],
  ['the system page', showSystem, 'platform-bot'],
] as const)('%s', (name, show, target) => {
  it('shows placeholder rows while loading', () => {
    api.listServiceAccounts.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(3);
    expect(screen.queryByText(/^No .*service accounts/)).not.toBeInTheDocument();
  });

  it('says there are none when there are none', async () => {
    api.listServiceAccounts.mockResolvedValue([]);
    show();

    expect(await screen.findByText(/No .*service accounts/)).toBeInTheDocument();
  });

  it('treats a null body as none rather than crashing', async () => {
    api.listServiceAccounts.mockResolvedValue(null);
    show();

    expect(await screen.findByText(/No .*service accounts/)).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listServiceAccounts.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText(/No .*service accounts/)).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('warns that the tokens go with the account, and asks before deleting', async () => {
    const user = show();
    await screen.findByText(target);

    await user.click(screen.getByRole('row', { name: new RegExp(target) }).querySelector('button')!);

    expect(await screen.findByText(/PATs/)).toBeInTheDocument();
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and reloads', async () => {
    const user = show();
    await screen.findByText(target);
    await user.click(screen.getByRole('row', { name: new RegExp(target) }).querySelector('button')!);

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    const id = name === 'the org page' ? 's1' : 's3';
    await vi.waitFor(() => expect(api.deleteServiceAccount).toHaveBeenCalledWith(id));
    expect(api.listServiceAccounts).toHaveBeenCalledTimes(2);
  });

  it('does not open the account when the delete button in its row is used', async () => {
    // The row is itself a click target; without stopPropagation the delete opens the account too.
    const user = show();
    await screen.findByText(target);

    await user.click(screen.getByRole('row', { name: new RegExp(target) }).querySelector('button')!);

    expect(screen.getByTestId('here').textContent).not.toMatch(/service-accounts\/s/);
  });

  it('closes the create dialog without creating anything', async () => {
    const user = show();
    await screen.findByText(target);

    await user.click(screen.getByRole('button', { name: /New Service Account/ }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  it.each([
    ['the org page', () => showOrg(), 'ci-deploy'],
    ['the system page', showSystem, 'platform-bot'],
  ] as const)('closes the create form on %s', async (_n, show, target) => {
    const user = show();
    await screen.findByText(target);
    await user.click(screen.getByRole('button', { name: /New Service Account/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });

  it.each([
    ['the org page', () => showOrg(), 'ci-deploy'],
    ['the system page', showSystem, 'platform-bot'],
  ] as const)('closes the delete confirmation on %s', async (_n, show, target) => {
    const user = show();
    await screen.findByText(target);
    await user.click(screen.getByRole('row', { name: new RegExp(target) }).querySelector('button')!);
    await screen.findByText(/PATs/);

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText(/PATs/)).toBeNull());
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('cancels the delete confirmation on the system page', async () => {
    const user = showSystem();
    await screen.findByText('platform-bot');
    await user.click(screen.getByRole('row', { name: /platform-bot/ }).querySelector('button')!);
    await screen.findByText(/PATs/);

    await user.click(screen.getAllByRole('button', { name: 'Cancel' }).at(-1)!);

    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('cancels the delete confirmation on the org page', async () => {
    const user = showOrg();
    await screen.findByText('ci-deploy');
    await user.click(screen.getByRole('row', { name: /ci-deploy/ }).querySelector('button')!);
    await screen.findByText(/PATs/);

    await user.click(screen.getAllByRole('button', { name: 'Cancel' }).at(-1)!);

    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('records a description on the system page', async () => {
    const user = showSystem();
    await screen.findByText('platform-bot');
    await user.click(screen.getByRole('button', { name: /New Service Account/ }));

    await user.fill(screen.getByLabelText('Name'), 'audit-bot');
    await user.fill(screen.getByLabelText('Description'), 'nightly audit');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ description: 'nightly audit' })));
  });
});
