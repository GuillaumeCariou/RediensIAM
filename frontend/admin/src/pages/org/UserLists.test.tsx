import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import UserLists from './UserLists';

/**
 * One page at three routes. With an organisation in scope it is that tenant's lists and offers to
 * add one; at /system/userlists it is every list on the platform, read-only and searchable — a
 * "New list" button there would have no organisation to create in.
 */

const api = vi.hoisted(() => ({ listUserLists: vi.fn(), createUserList: vi.fn() }));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const LISTS = [
  { id: 'l1', name: 'Staff', org_id: 'o1', org_name: 'Acme', immovable: false, user_count: 40, created_at: '2026-01-02T00:00:00Z' },
  { id: 'l2', name: 'System', org_id: null, org_name: null, immovable: true, created_at: '2026-01-02T00:00:00Z' },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.listUserLists.mockResolvedValue({ user_lists: LISTS });
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show(path = '/org/userlists', pattern = '/org/userlists') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<UserLists />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

/** The global view: no organisation in scope at all. */
function showGlobal() {
  auth.orgId = '';
  return show('/system/userlists', '/system/userlists');
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));

describe('an organisation\'s own lists', () => {
  it('asks for that organisation and shows each list with its size', async () => {
    show();

    expect(await screen.findByText('Staff')).toBeInTheDocument();
    expect(api.listUserLists).toHaveBeenCalledWith('o1');
    expect(screen.getByText('40')).toBeInTheDocument();
  });

  it('prints an em dash where the server did not count the members', async () => {
    show();

    await screen.findByText('System');
    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('marks the immovable list, which cannot be assigned to a project', async () => {
    show();

    expect(await screen.findByText('Immovable')).toBeInTheDocument();
    expect(screen.getByText('Movable')).toBeInTheDocument();
  });

  it('opens a list from its row', async () => {
    const user = show();

    await user.click(await screen.findByText('Staff'));

    await arrivedAt('/org/userlists/l1');
  });

  it('opens it under the tenant for a super admin browsing one', async () => {
    const user = show('/system/organisations/o9/userlists', '/system/organisations/:id/userlists');

    await user.click(await screen.findByText('Staff'));

    await arrivedAt('/system/organisations/o9/userlists/l1');
  });

  it('offers no search — the tenant\'s list of lists is short by construction', async () => {
    show();

    await screen.findByText('Staff');
    expect(screen.queryByPlaceholderText(/Search/)).not.toBeInTheDocument();
  });

  it('creates a list in that organisation and reloads', async () => {
    const user = show();
    await screen.findByText('Staff');

    await user.click(screen.getByRole('button', { name: /New User List/ }));
    await user.fill(screen.getByLabelText('Name'), 'Contractors');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createUserList)
      .toHaveBeenCalledWith({ name: 'Contractors', org_id: 'o1' }));
    expect(api.listUserLists).toHaveBeenCalledTimes(2);
  });

  it('requires a name', async () => {
    const user = show();
    await screen.findByText('Staff');

    await user.click(screen.getByRole('button', { name: /New User List/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
  });

  it('closes without creating anything', async () => {
    const user = show();
    await screen.findByText('Staff');

    await user.click(screen.getByRole('button', { name: /New User List/ }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.createUserList).not.toHaveBeenCalled();
  });

  it('says the organisation has none yet', async () => {
    api.listUserLists.mockResolvedValue({ user_lists: [] });
    show();

    expect(await screen.findByText('No user lists yet')).toBeInTheDocument();
  });
});

describe('every list on the platform', () => {
  it('asks for all of them, and names the organisation each belongs to', async () => {
    showGlobal();

    expect(await screen.findByText('Acme')).toBeInTheDocument();
    // The empty string, not an id: `listUserLists` treats it as falsy and sends no org_id filter.
    expect(api.listUserLists).toHaveBeenCalledWith('');
  });

  it('names the ownerless list for what it is, not as a blank', async () => {
    showGlobal();
    expect(await screen.findByText('System (root)')).toBeInTheDocument();
  });

  it('opens a list through the system route', async () => {
    const user = showGlobal();

    await user.click(await screen.findByText('Staff'));

    await arrivedAt('/system/userlists/l1');
  });

  it('offers no way to create one, there being no organisation to create it in', async () => {
    showGlobal();

    await screen.findByText('Staff');
    expect(screen.queryByRole('button', { name: /New User List/ })).not.toBeInTheDocument();
  });

  it.each([
    ['a list name', 'staff', 'Staff'],
    ['an organisation name', 'ACME', 'Staff'],
  ])('searches by %s, ignoring case', async (_n, query, expected) => {
    const user = showGlobal();
    await screen.findByText('Staff');

    await user.fill(screen.getByPlaceholderText('Search by name or organisation…'), query);

    expect(screen.getAllByRole('row')).toHaveLength(2);
    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it('says the search matched nothing', async () => {
    const user = showGlobal();
    await screen.findByText('Staff');

    await user.fill(screen.getByPlaceholderText('Search by name or organisation…'), 'zzz');

    expect(screen.getByText('No user lists found')).toBeInTheDocument();
  });
});

describe('loading and failure', () => {
  it('shows placeholder rows', () => {
    api.listUserLists.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(4);
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listUserLists.mockResolvedValue(LISTS);
    show();

    expect(await screen.findByText('Staff')).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listUserLists.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No user lists yet')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});


describe('dismissing the create form with Escape', () => {
  // A modal <dialog> closes itself on Escape and fires `close`; the page has to clear the state
  // behind it, or the form is off the screen and still open as far as it knows.
  it('closes it without creating anything', async () => {
    const user = show();
    await screen.findByText('Staff');
    await user.click(screen.getByRole('button', { name: /New User List/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
    expect(api.createUserList).not.toHaveBeenCalled();
  });
});
