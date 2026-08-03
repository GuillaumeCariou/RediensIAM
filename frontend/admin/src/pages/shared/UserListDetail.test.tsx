import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import UserListDetail from './UserListDetail';

/**
 * Registered under three route shapes, two of which name the list `:id` and one `:listId`. Both
 * have to be read: dropping either breaks one route with no compile error, and the page then
 * renders a members panel for the empty string.
 */

const api = vi.hoisted(() => ({
  getUserList: vi.fn(), getSystemUserList: vi.fn(),
  cleanupUserList: vi.fn(), exportUserList: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const panel = vi.hoisted(() => ({ props: {} as Record<string, unknown> }));
vi.mock('@/components/UserListMembersPanel', () => ({
  default: (props: Record<string, unknown>) => {
    panel.props = props;
    return <p>members of {String(props['listId'])}</p>;
  },
}));

const LIST = {
  id: 'l1', name: 'Staff', org_id: 'o1', org_name: 'Acme',
  immovable: false, user_count: 40, created_at: '2026-01-02T00:00:00Z',
};

const BLOB = new Blob(['id,email\n']);
let click: ReturnType<typeof vi.spyOn>;
let revokeObjectURL: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.clearAllMocks();
  panel.props = {};
  api.getUserList.mockResolvedValue(LIST);
  api.getSystemUserList.mockResolvedValue({ ...LIST, immovable: true });
  api.exportUserList.mockResolvedValue(BLOB);
  api.cleanupUserList.mockResolvedValue({
    orphaned_roles_found: 3, inactive_users_found: 2,
    orphaned_roles_removed: 0, inactive_users_removed: 0, dry_run: true,
  });
  click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
  revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});

const ROUTES = {
  org: ['/org/userlists/l1', '/org/userlists/:id'],
  system: ['/system/userlists/l1', '/system/userlists/:id'],
  // A tenant's list opened from the system context: the param is named :listId here.
  tenant: ['/system/organisations/o1/userlists/l1', '/system/organisations/:id/userlists/:listId'],
} as const;

function show(route: keyof typeof ROUTES = 'org') {
  const [path, pattern] = ROUTES[route];
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<UserListDetail />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const title = () => document.querySelector('.iam-page-title')?.textContent ?? null;

describe('which list, and which route it is read through', () => {
  it('reads an organisation\'s own list through the org route', async () => {
    show('org');

    expect(await screen.findByText('members of l1')).toBeInTheDocument();
    expect(api.getUserList).toHaveBeenCalledWith('l1');
    expect(api.getSystemUserList).not.toHaveBeenCalled();
    expect(panel.props['isSystemCtx']).toBe(false);
  });

  it('reads it through the admin route in the system context', async () => {
    show('system');

    expect(await screen.findByText('members of l1')).toBeInTheDocument();
    expect(api.getSystemUserList).toHaveBeenCalledWith('l1');
    expect(api.getUserList).not.toHaveBeenCalled();
    expect(panel.props['isSystemCtx']).toBe(true);
  });

  it('resolves the list from :listId as well as from :id', async () => {
    show('tenant');

    expect(await screen.findByText('members of l1')).toBeInTheDocument();
    expect(api.getSystemUserList).toHaveBeenCalledWith('l1');
  });
});

describe('the header', () => {
  it('names the list and the organisation it belongs to', async () => {
    show();

    await vi.waitFor(() => expect(title()).toBe('Staff'));
    expect(screen.getByText('Organisation: Acme')).toBeInTheDocument();
  });

  it('says so while loading, rather than showing a bare "User List"', () => {
    api.getUserList.mockReturnValue(new Promise(() => {}));
    show();

    expect(title()).toBe('Loading…');
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
  });

  it('falls back to a generic title when the list cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getUserList.mockRejectedValue(new Error('404'));
    show();

    await vi.waitFor(() => expect(title()).toBe('User List'));
    vi.restoreAllMocks();
  });

  it('omits the organisation line for a list that belongs to none', async () => {
    api.getUserList.mockResolvedValue({ ...LIST, org_name: null });
    show();

    await vi.waitFor(() => expect(title()).toBe('Staff'));
    expect(screen.queryByText(/^Organisation:/)).not.toBeInTheDocument();
  });

  it.each([
    ['org', 'Movable'],
    ['system', 'Immovable'],
  ] as const)('marks whether the list can be moved (%s)', async (route, label) => {
    show(route);

    expect(await screen.findByText(label)).toBeInTheDocument();
  });
});

describe('exporting', () => {
  it('downloads the CSV under a name carrying the list, and releases the object URL', async () => {
    const user = show();
    await screen.findByText('members of l1');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(api.exportUserList).toHaveBeenCalledWith('l1');
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:fake');
  });

  it('re-enables the button after a failed export', async () => {
    api.exportUserList.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('members of l1');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Export CSV' })).toBeEnabled());
    expect(click).not.toHaveBeenCalled();
  });
});

describe('cleanup', () => {
  const openCleanup = async () => {
    const user = show();
    await screen.findByText('members of l1');
    await user.click(screen.getByRole('button', { name: /Cleanup/ }));
    return user;
  };

  it('previews by default, and does not offer to delete until asked', async () => {
    // The default has to be the harmless one: this deletes user accounts.
    const user = await openCleanup();

    expect(screen.getByLabelText(/Dry run/)).toBeChecked();
    expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();

    await user.click(screen.getByLabelText(/Dry run/));
    expect(screen.getByRole('button', { name: 'Run Cleanup' })).toBeInTheDocument();
  });

  it('leaves inactive users alone unless that box is ticked', async () => {
    const user = await openCleanup();

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await vi.waitFor(() => expect(api.cleanupUserList).toHaveBeenCalledWith('l1', {
      remove_orphaned_roles: true, remove_inactive_users: false,
      inactive_threshold_days: 90, dry_run: true,
    }));
  });

  it('sends the threshold when purging inactive users', async () => {
    const user = await openCleanup();

    await user.click(screen.getByRole('checkbox', { name: /Remove users inactive/ }));
    await user.fill(screen.getByRole('spinbutton'), '30');
    await user.click(screen.getByLabelText(/Dry run/));
    await user.click(screen.getByRole('button', { name: 'Run Cleanup' }));

    await vi.waitFor(() => expect(api.cleanupUserList).toHaveBeenCalledWith('l1', {
      remove_orphaned_roles: true, remove_inactive_users: true,
      inactive_threshold_days: 30, dry_run: false,
    }));
  });

  it('refuses a threshold below one, which would purge everyone', async () => {
    const user = await openCleanup();

    await user.fill(screen.getByRole('spinbutton'), '-5');

    expect(screen.getByRole('spinbutton')).toHaveValue(1);
  });

  it('falls back to the default rather than NaN when the box is emptied', async () => {
    const user = await openCleanup();

    await user.fill(screen.getByRole('spinbutton'), '');

    expect(screen.getByRole('spinbutton')).toHaveValue(90);
  });

  it('reports a preview as a preview, with nothing removed', async () => {
    const user = await openCleanup();

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    expect(await screen.findByText('Preview (dry run):')).toBeInTheDocument();
    expect(screen.getByText(/Orphaned role assignments:/)).toHaveTextContent('3');
    expect(screen.queryByText(/removed/)).not.toBeInTheDocument();
  });

  it('reports what a real run removed', async () => {
    api.cleanupUserList.mockResolvedValue({
      orphaned_roles_found: 3, inactive_users_found: 2,
      orphaned_roles_removed: 3, inactive_users_removed: 2, dry_run: false,
    });
    const user = await openCleanup();
    await user.click(screen.getByRole('checkbox', { name: /Remove users inactive/ }));
    await user.click(screen.getByLabelText(/Dry run/));

    await user.click(screen.getByRole('button', { name: 'Run Cleanup' }));

    expect(await screen.findByText(/\(3 removed\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(2 removed\)/)).toBeInTheDocument();
    expect(screen.queryByText('Preview (dry run):')).not.toBeInTheDocument();
  });

  it('says nothing about inactive users when they were not part of the run', async () => {
    const user = await openCleanup();

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await screen.findByText('Preview (dry run):');
    expect(screen.queryByText(/^Inactive users:/)).not.toBeInTheDocument();
  });

  it('re-enables the button after a run that failed', async () => {
    api.cleanupUserList.mockRejectedValue(new Error('500'));
    const user = await openCleanup();

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Preview' })).toBeEnabled());
  });

  it('forgets the last result when reopened, so an old count cannot be read as new', async () => {
    const user = await openCleanup();
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText('Preview (dry run):');

    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: /Cleanup/ }));

    expect(screen.queryByText('Preview (dry run):')).not.toBeInTheDocument();
  });
});


describe('going back, and dismissing the cleanup dialog', () => {
  it('goes back to wherever the operator came from, not to a guessed URL', async () => {
    // `navigate(-1)` pops the router's own history, so the assertion is on the entry it lands on
    // rather than on window.history — a hard-coded destination would ignore where they came from.
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/org/userlists', '/org/userlists/l1']} initialIndex={1}>
        <Routes>
          <Route path="/org/userlists" element={<p>the list of lists</p>} />
          <Route path="/org/userlists/:id" element={<UserListDetail />} />
        </Routes>
      </MemoryRouter>,
    );
    await screen.findByText('members of l1');

    await user.click(screen.getByRole('button', { name: /^Back$/ }));

    expect(await screen.findByText('the list of lists')).toBeInTheDocument();
  });

  it('closes the cleanup dialog on Escape and forgets its last result', async () => {
    const user = show();
    await screen.findByText('members of l1');
    await user.click(screen.getByRole('button', { name: /Cleanup/ }));
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText('Preview (dry run):');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText(/Dry run/)).toBeNull());
    await user.click(screen.getByRole('button', { name: /Cleanup/ }));

    expect(screen.queryByText('Preview (dry run):')).not.toBeInTheDocument();
  });
});
