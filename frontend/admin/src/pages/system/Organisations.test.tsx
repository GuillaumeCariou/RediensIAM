import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import Organisations from './Organisations';
import { fmtDateShort } from '@/lib/utils';

const api = vi.hoisted(() => ({
  listOrgs: vi.fn(), createOrg: vi.fn(),
  suspendOrg: vi.fn(), unsuspendOrg: vi.fn(), deleteOrg: vi.fn(),
}));
vi.mock('@/api', () => api);

const org = (over: Record<string, unknown> = {}) => ({
  id: 'o1', name: 'Acme Corp', slug: 'acme-corp', active: true, suspended_at: null,
  created_at: '2026-01-02T00:00:00Z', metadata: {}, ...over,
});

const ORGS = [
  org(),
  org({ id: 'o2', name: 'Globex', slug: 'globex', suspended_at: '2026-04-01T00:00:00Z' }),
  org({ id: 'o3', name: 'Initech', slug: 'initech', active: false }),
];

beforeEach(() => {
  vi.clearAllMocks();
  api.listOrgs.mockResolvedValue(ORGS);
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show() {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/system/organisations']}>
      <Routes><Route path="/system/organisations" element={<Organisations />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));
const rowFor = (name: string) => screen.getByRole('row', { name: new RegExp(name) });
/** Opens the ⋯ menu on `name`'s row. */
const openMenu = async (user: Awaited<ReturnType<typeof show>>, name: string) =>
  user.click(rowFor(name).querySelector('button')!);

describe('the table', () => {
  it('lists every tenant with its slug and creation date', async () => {
    show();

    expect(await screen.findByText('Acme Corp')).toBeInTheDocument();
    expect(screen.getByText('acme-corp')).toBeInTheDocument();
    expect(screen.getAllByText(fmtDateShort('2026-01-02T00:00:00Z'))).toHaveLength(3);
  });

  it.each([
    ['Acme Corp', 'Active'],
    // Suspended is an operator action; inactive is a flag. Merging them hides which happened.
    ['Globex', 'Suspended'],
    ['Initech', 'Inactive'],
  ])('marks %s as %s', async (name, status) => {
    show();

    await screen.findByText(name);
    expect(rowFor(name)).toHaveTextContent(status);
  });

  it('opens an organisation from its row', async () => {
    const user = show();

    await user.click(await screen.findByText('Acme Corp'));

    await arrivedAt('/system/organisations/o1');
  });

  it('shows placeholder rows while loading', () => {
    api.listOrgs.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(5);
    expect(screen.queryByText('No organisations found')).not.toBeInTheDocument();
  });

  it('says there are none when there are none', async () => {
    api.listOrgs.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No organisations found')).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listOrgs.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No organisations found')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('the search box', () => {
  it.each([
    ['a name', 'globex', 'Globex'],
    ['a slug', 'ACME', 'Acme Corp'],
  ])('matches on %s, ignoring case', async (_n, query, expected) => {
    const user = show();
    await screen.findByText('Acme Corp');

    await user.fill(screen.getByPlaceholderText('Search by name or slug…'), query);

    expect(screen.getAllByRole('row')).toHaveLength(2);
    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it('says nothing matched', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await user.fill(screen.getByPlaceholderText('Search by name or slug…'), 'zzz');

    expect(screen.getByText('No organisations found')).toBeInTheDocument();
  });
});

describe('creating a tenant', () => {
  it('creates it and reloads the list', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await user.click(screen.getByRole('button', { name: /New Organisation/ }));
    await user.fill(screen.getByLabelText('Name'), 'Umbrella');
    await user.fill(screen.getByLabelText('Slug'), 'umbrella');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createOrg).toHaveBeenCalledWith({ name: 'Umbrella', slug: 'umbrella' }));
    expect(api.listOrgs).toHaveBeenCalledTimes(2);
  });

  it('turns a typed slug into a legal one as it is typed', async () => {
    // The field carries a pattern too, but a slug with spaces is the ordinary mistake and
    // rejecting it after the fact is worse than fixing it.
    const user = show();
    await screen.findByText('Acme Corp');
    await user.click(screen.getByRole('button', { name: /New Organisation/ }));

    await user.fill(screen.getByLabelText('Slug'), 'Umbrella Corp');

    expect(screen.getByLabelText('Slug')).toHaveValue('umbrella-corp');
  });

  it('requires both fields, and constrains the slug', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await user.click(screen.getByRole('button', { name: /New Organisation/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
    expect(screen.getByLabelText('Slug')).toHaveAttribute('pattern', '[a-z0-9]+(-[a-z0-9]+)*');
  });

  it('says so, and keeps the form open, when the slug is taken', async () => {
    api.createOrg.mockRejectedValue(new Error('409'));
    const user = show();
    await screen.findByText('Acme Corp');
    await user.click(screen.getByRole('button', { name: /New Organisation/ }));
    await user.fill(screen.getByLabelText('Name'), 'Umbrella');
    await user.fill(screen.getByLabelText('Slug'), 'acme-corp');

    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Failed to create organisation.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Umbrella');
  });

  it('closes without creating anything', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await user.click(screen.getByRole('button', { name: /New Organisation/ }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.createOrg).not.toHaveBeenCalled();
  });
});

describe('the row menu', () => {
  it('offers to suspend a running tenant and to unsuspend a suspended one', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await openMenu(user, 'Acme Corp');
    expect(screen.getByRole('button', { name: 'Suspend' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await openMenu(user, 'Globex');
    expect(screen.getByRole('button', { name: 'Unsuspend' })).toBeInTheDocument();
  });

  it('asks before suspending, because it signs the tenant out', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');

    await user.click(screen.getByRole('button', { name: 'Suspend' }));

    // The menu item opens the confirmation; it does not act. Suspending revokes every live session
    // of the tenant, which is a destructive act on other people's work and used to take one click.
    expect(api.suspendOrg).not.toHaveBeenCalled();
    expect(await screen.findByRole('dialog')).toHaveTextContent(/Suspend .Acme Corp.\?/);
  });

  it('suspends once confirmed, then reloads so the status is the server\'s and not a guess', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Suspend' }));

    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Suspend' }));

    await vi.waitFor(() => expect(api.suspendOrg).toHaveBeenCalledWith('o1'));
    expect(api.listOrgs).toHaveBeenCalledTimes(2);
  });

  it('leaves the tenant alone when the confirmation is cancelled', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Suspend' }));

    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));

    expect(api.suspendOrg).not.toHaveBeenCalled();
  });

  it('unsuspends the one that is suspended', async () => {
    const user = show();
    await screen.findByText('Globex');
    await openMenu(user, 'Globex');

    await user.click(screen.getByRole('button', { name: 'Unsuspend' }));

    await vi.waitFor(() => expect(api.unsuspendOrg).toHaveBeenCalledWith('o2'));
  });

  it('closes when the operator clicks away', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'Suspend' })).not.toBeInTheDocument();
  });

  it('closes on Escape', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('button', { name: 'Suspend' })).not.toBeInTheDocument();
  });

  it('does not open the organisation when its menu is used', async () => {
    const user = show();
    await screen.findByText('Acme Corp');

    await openMenu(user, 'Acme Corp');

    expect(screen.getByTestId('here').textContent).toBe('/system/organisations');
  });
});

describe('deleting a tenant', () => {
  it('names it and warns that everything goes with it', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/Delete .Acme Corp.\?/)).toBeInTheDocument();
    expect(screen.getByText(/cannot be undone/)).toBeInTheDocument();
    expect(api.deleteOrg).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and reloads', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await user.click(await screen.findByRole('button', { name: 'Delete for good' }));

    await vi.waitFor(() => expect(api.deleteOrg).toHaveBeenCalledWith('o1'));
    expect(api.listOrgs).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText(/Delete .Acme Corp.\?/);

    await user.click(screen.getAllByRole('button', { name: 'Cancel' }).at(-1)!);

    expect(api.deleteOrg).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  it('closes the create form, and clears the error it was showing', async () => {
    api.createOrg.mockRejectedValue(new Error('409'));
    const user = show();
    await screen.findByText('Acme Corp');
    await user.click(screen.getByRole('button', { name: /New Organisation/ }));
    await user.fill(screen.getByLabelText('Name'), 'Umbrella');
    await user.fill(screen.getByLabelText('Slug'), 'acme-corp');
    await user.click(screen.getByRole('button', { name: 'Create' }));
    await screen.findByText('Failed to create organisation.');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText('Slug')).toBeNull());
    await user.click(screen.getByRole('button', { name: /New Organisation/ }));

    expect(screen.queryByText('Failed to create organisation.')).not.toBeInTheDocument();
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = show();
    await screen.findByText('Acme Corp');
    await openMenu(user, 'Acme Corp');
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText(/Delete .Acme Corp.\?/);

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText(/Delete .Acme Corp.\?/)).toBeNull());
    expect(api.deleteOrg).not.toHaveBeenCalled();
  });
});
