import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import SystemUsers from './Users';
import { ApiError } from '@/auth';
import { fmtDate } from '@/lib/utils';

/**
 * La page Users du déploiement.
 *
 * Ce que ces tests gardent : chaque critère part au SERVEUR. La page ne trie ni ne restreint les
 * lignes qu'elle a reçues — elle redemande. C'est vérifiable d'ici : à chaque filtre correspond un
 * appel, et l'appel porte le critère. Un filtre appliqué côté client passerait tous les tests
 * visuels et mentirait sur les compteurs.
 *
 * La fabrique remplace `@/api` en entier : tout export que la page importe doit y figurer, sinon
 * l'import est `undefined` et l'erreur ne ressemble pas à sa cause.
 */

const api = vi.hoisted(() => ({
  searchUsers: vi.fn(), orgSearchUsers: vi.fn(),
  adminGetUser: vi.fn(), adminUpdateUser: vi.fn(),
  orgGetUser: vi.fn(), orgUpdateUser: vi.fn(),
  unlockUser: vi.fn(), getUserSessions: vi.fn(), revokeAllUserSessions: vi.fn(),
  listOrgs: vi.fn(), listUserLists: vi.fn(), listOrgUserLists: vi.fn(),
}));
vi.mock('@/api', () => api);

const HOUR = 3600_000;
const future = () => new Date(Date.now() + HOUR).toISOString();
const past = () => new Date(Date.now() - HOUR).toISOString();

const ADA = {
  id: 'u1', email: 'ada@acme.test', username: 'ada', discriminator: '0001',
  display_name: 'Ada Lovelace', active: true, last_login_at: '2026-03-04T05:06:07Z',
  org_name: 'Acme', user_list_name: 'Staff', org_id: 'o1', user_list_id: 'l1',
  totp_enabled: true, web_authn_enabled: false,
  roles: [{ role_id: 'r1', name: 'admin', project_id: 'p1', project_name: 'Portal' }],
};
const GRACE = {
  ...ADA, id: 'u2', email: 'grace@northwind.test', username: 'grace', discriminator: '0002',
  display_name: 'Grace Hopper', org_name: 'Northwind', user_list_name: 'Shoppers',
  org_id: 'o2', user_list_id: 'l2', totp_enabled: false, roles: [],
};

const results = (users: unknown[], over: Record<string, unknown> = {}) => ({
  users, total: users.length, lists: new Set(users.map(u => (u as { user_list_id: string }).user_list_id)).size,
  tenants: 1, page: 1, page_size: 50, ...over,
});

const LISTS = { user_lists: [
  { id: 'l1', name: 'Staff', org_id: 'o1' },
  { id: 'l2', name: 'Shoppers', org_id: 'o2' },
] };

beforeEach(() => {
  vi.clearAllMocks();
  api.searchUsers.mockResolvedValue(results([ADA]));
  api.orgSearchUsers.mockResolvedValue(results([ADA]));
  api.adminGetUser.mockResolvedValue({ ...ADA, phone: '+33600000000', email_verified: true });
  api.orgGetUser.mockResolvedValue({ ...ADA, phone: '+33600000000', email_verified: true });
  api.getUserSessions.mockResolvedValue({ sessions: [{ client_id: 'portal', client_name: 'Portal' }] });
  api.listOrgs.mockResolvedValue([{ id: 'o1', name: 'Acme' }, { id: 'o2', name: 'Northwind' }]);
  api.listUserLists.mockResolvedValue(LISTS);
  api.listOrgUserLists.mockResolvedValue(LISTS);
});

function show(path = '/system/users') {
  const user = userEvent.setup();
  render(<MemoryRouter initialEntries={[path]}><SystemUsers /></MemoryRouter>);
  return user;
}

/** Rend la page et attend la première réponse. */
async function loaded(path?: string) {
  const user = show(path);
  await screen.findByText('Ada Lovelace');
  return user;
}

const box = () => screen.getByLabelText('Search');
const criteria = () => api.searchUsers.mock.calls.at(-1);

/** Une pastille de ligne, et non l'option de même nom dans le filtre Statut. */
const badge = (label: string) => screen.getByText(label, { selector: 'span' });
const noBadge = (label: string) => screen.queryByText(label, { selector: 'span' });

const openMenu = (user: Awaited<ReturnType<typeof show>>) =>
  user.click([...screen.getByRole('row', { name: /ada/i }).querySelectorAll('button')].at(-1)!);

// ── Le chargement ───────────────────────────────────────────────────────────

describe('on arrival', () => {
  it('lists the deployment without being asked — there is a first page to show', async () => {
    await loaded();

    expect(api.searchUsers).toHaveBeenCalledWith('', { page: 1 });
  });

  it('shows placeholder rows while the first page is in flight', () => {
    api.searchUsers.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(3);
    expect(screen.queryByText('No users found')).not.toBeInTheDocument();
  });

  it('groups the results by the list each account belongs to', async () => {
    api.searchUsers.mockResolvedValue(results([ADA, GRACE]));
    await loaded();

    // `{ selector: 'span' }` : « Staff » est aussi une option du filtre par liste.
    const staff = screen.getByText('Staff', { selector: 'span' }).closest('.iam-card') as HTMLElement;
    const shoppers = screen.getByText('Shoppers', { selector: 'span' }).closest('.iam-card') as HTMLElement;
    expect(within(staff).getByText('Ada Lovelace')).toBeInTheDocument();
    expect(within(staff).queryByText('Grace Hopper')).not.toBeInTheDocument();
    // L'en-tête d'un groupe EST l'adresse du compte : le locataire, puis la liste.
    expect(within(staff).getByText(/Acme/)).toBeInTheDocument();
    expect(within(shoppers).getByText(/Northwind/)).toBeInTheDocument();
  });

  it('names the counts the server added up, not the rows it happened to send', async () => {
    api.searchUsers.mockResolvedValue(results([ADA], { total: 1284, lists: 6, tenants: 4 }));
    await loaded();

    expect(screen.getByText('1284 accounts · 6 lists · 4 tenants')).toBeInTheDocument();
  });

  it('shows the roles, the second factor and the last sign-in of a match', async () => {
    await loaded();

    expect(screen.getByText('Portal / admin')).toBeInTheDocument();
    expect(screen.getByText('TOTP')).toBeInTheDocument();
    expect(screen.getByText(fmtDate(ADA.last_login_at))).toBeInTheDocument();
    expect(screen.getByText('ada#0001')).toBeInTheDocument();
  });

  it('says an account has no second factor rather than leaving the cell blank', async () => {
    api.searchUsers.mockResolvedValue(results([GRACE]));
    const user = show();
    await screen.findByText('Grace Hopper');

    expect(screen.getByText('None')).toBeInTheDocument();
    expect(screen.getByText('No role')).toBeInTheDocument();
    expect(user).toBeDefined();
  });

  it('says nothing matched when the deployment is empty', async () => {
    api.searchUsers.mockResolvedValue(results([]));
    show();

    expect(await screen.findByText('No users found')).toBeInTheDocument();
  });
});

// ── Les refus ───────────────────────────────────────────────────────────────

describe('when the API refuses', () => {
  it('shows the refusal instead of the last results, which answered other criteria', async () => {
    const user = await loaded();
    api.searchUsers.mockRejectedValue(new Error('500'));

    await user.fill(box(), 'grace');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('Could not search the deployment’s accounts.')).toBeInTheDocument();
    expect(screen.queryByText('Ada Lovelace')).not.toBeInTheDocument();
  });

  it('says how long a query has to be, in the server’s own number', async () => {
    api.searchUsers.mockRejectedValue(new ApiError(400, { error: 'query_too_short', min_length: 3 }));
    show();

    expect(await screen.findByText('Type at least 3 characters to search.')).toBeInTheDocument();
  });

  it('says so when a filter value is one the server does not know', async () => {
    api.searchUsers.mockRejectedValue(new ApiError(400, { error: 'invalid_filter', filter: 'status' }));
    show();

    expect(await screen.findByText(/does not know that value for “status”/)).toBeInTheDocument();
  });

  it('says so when the tenants and lists cannot be read, and still lists the accounts', async () => {
    api.listOrgs.mockRejectedValue(new Error('500'));
    await loaded();

    expect(await screen.findByText('Could not read the tenants and lists to filter by.')).toBeInTheDocument();
    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
  });
});

// ── La recherche ────────────────────────────────────────────────────────────

describe('searching', () => {
  it('sends what was typed, from the button', async () => {
    const user = await loaded();

    await user.fill(box(), 'marie');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await vi.waitFor(() => expect(criteria()).toEqual(['marie', { page: 1 }]));
  });

  it('searches on Enter too, which is what anyone typing will press', async () => {
    const user = await loaded();

    await user.fill(box(), 'marie');
    await user.keyboard('{Enter}');

    await vi.waitFor(() => expect(criteria()![0]).toBe('marie'));
  });

  it('trims, so a stray space is not searched for', async () => {
    const user = await loaded();

    await user.fill(box(), '  marie  ');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await vi.waitFor(() => expect(criteria()![0]).toBe('marie'));
  });

  it('names what was searched for beside the count', async () => {
    const user = await loaded();

    await user.fill(box(), 'marie');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('marie')).toBeInTheDocument();
  });
});

// ── Les filtres ─────────────────────────────────────────────────────────────

describe('filtering', () => {
  it.each([
    ['Tenant', 'o2', { org_id: 'o2', user_list_id: undefined }],
    ['User list', 'l2', { user_list_id: 'l2' }],
    ['Status', 'locked', { status: 'locked' }],
    ['Second factor', 'no', { mfa: 'no' }],
    ['Signed in', 'never', { signed_in: 'never' }],
  ])('sends %s to the server rather than sifting the rows it already has', async (label, value, expected) => {
    const user = await loaded();

    await user.selectOptions(screen.getByLabelText(label), value);

    await vi.waitFor(() => expect(criteria()).toEqual(['', { page: 1, ...expected }]));
  });

  it('combines the filters into one request', async () => {
    const user = await loaded();

    await user.selectOptions(screen.getByLabelText('Status'), 'disabled');
    await user.selectOptions(screen.getByLabelText('Second factor'), 'no');

    await vi.waitFor(() => expect(criteria()).toEqual(['', { page: 1, status: 'disabled', mfa: 'no' }]));
  });

  it('offers only the chosen tenant’s lists once a tenant is picked', async () => {
    const user = await loaded();

    await user.selectOptions(screen.getByLabelText('Tenant'), 'o2');

    const lists = screen.getByLabelText('User list');
    expect(within(lists).queryByRole('option', { name: 'Staff' })).not.toBeInTheDocument();
    expect(within(lists).getByRole('option', { name: 'Shoppers' })).toBeInTheDocument();
  });

  it('drops the list when the tenant changes — a list of another tenant matches nothing', async () => {
    const user = await loaded();
    await user.selectOptions(screen.getByLabelText('User list'), 'l1');

    await user.selectOptions(screen.getByLabelText('Tenant'), 'o2');

    await vi.waitFor(() => expect(criteria()![1]).toEqual({ page: 1, org_id: 'o2' }));
  });

  it('applies a saved search as a whole set of criteria, dropping the rest', async () => {
    const user = await loaded();
    await user.selectOptions(screen.getByLabelText('Tenant'), 'o2');

    await user.click(screen.getByRole('button', { name: 'No second factor' }));

    await vi.waitFor(() => expect(criteria()).toEqual(['', { page: 1, mfa: 'no' }]));
  });

  it('clears every filter at once', async () => {
    const user = await loaded();
    await user.selectOptions(screen.getByLabelText('Status'), 'locked');

    await user.click(await screen.findByRole('button', { name: 'Clear every filter' }));

    await vi.waitFor(() => expect(criteria()).toEqual(['', { page: 1 }]));
  });

  it('offers no clear button when nothing is filtered', async () => {
    await loaded();

    expect(screen.queryByRole('button', { name: 'Clear every filter' })).not.toBeInTheDocument();
  });
});

// ── La pagination ───────────────────────────────────────────────────────────

describe('paging', () => {
  it('asks the server for the next page, keeping the filter', async () => {
    api.searchUsers.mockResolvedValue(results([ADA], { total: 120, page: 1, page_size: 50 }));
    const user = await loaded();
    await user.selectOptions(screen.getByLabelText('Status'), 'locked');

    await user.click(screen.getByRole('button', { name: 'Next' }));

    await vi.waitFor(() => expect(criteria()).toEqual(['', { page: 2, status: 'locked' }]));
  });

  it('says which slice of the whole filtered set is on screen', async () => {
    api.searchUsers.mockResolvedValue(results([ADA], { total: 120, lists: 6, page: 2, page_size: 50 }));
    await loaded();

    expect(screen.getByText('51–100 of 120 in 6 lists')).toBeInTheDocument();
  });

  it('does not offer a page that is not there', async () => {
    await loaded();

    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('goes back to the first page whenever a criterion changes', async () => {
    api.searchUsers.mockResolvedValue(results([ADA], { total: 120, page: 1, page_size: 50 }));
    const user = await loaded();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await vi.waitFor(() => expect(criteria()![1]).toEqual({ page: 2 }));

    await user.selectOptions(screen.getByLabelText('Status'), 'locked');

    await vi.waitFor(() => expect(criteria()![1]).toEqual({ page: 1, status: 'locked' }));
  });
});

// ── L'état d'un compte ──────────────────────────────────────────────────────

describe('what a row says about an account', () => {
  it.each([
    ['a disabled account', { active: false }, 'Disabled'],
    ['a locked one', { locked_until: future() }, 'Locked'],
  ])('marks %s', async (_n, patch, label) => {
    api.searchUsers.mockResolvedValue(results([{ ...ADA, ...patch }]));
    await loaded();

    expect(badge(label)).toBeInTheDocument();
  });

  it('treats a lock that has run out as no lock', async () => {
    api.searchUsers.mockResolvedValue(results([{ ...ADA, locked_until: past() }]));
    await loaded();

    expect(noBadge('Locked')).toBeNull();
  });

  it('falls back to the username where there is no display name', async () => {
    api.searchUsers.mockResolvedValue(results([{ ...ADA, display_name: null }]));
    show();

    expect(await screen.findByText('ada', { selector: 'div' })).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.searchUsers.mockResolvedValue([ADA]);
    show();

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
  });
});

// ── Les actions sur une ligne ───────────────────────────────────────────────

describe('editing a match', () => {
  it('opens from the row and loads the full account', async () => {
    const user = await loaded();

    await user.click(screen.getByText('Ada Lovelace'));

    expect(await screen.findByLabelText('Phone')).toHaveValue('+33600000000');
    expect(api.adminGetUser).toHaveBeenCalledWith('u1');
  });

  it('opens from the row menu too', async () => {
    const user = await loaded();
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Edit' }));

    expect(await screen.findByLabelText('Phone')).toBeInTheDocument();
  });

  it('saves, and reflects the change in the row without a second search', async () => {
    const user = await loaded();
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.fill(screen.getByLabelText('Display name'), 'Ada L');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await vi.waitFor(() => expect(api.adminUpdateUser).toHaveBeenCalledWith('u1',
      expect.objectContaining({ display_name: 'Ada L', new_password: undefined })));
    expect(await screen.findByText('Ada L')).toBeInTheDocument();
    expect(api.searchUsers).toHaveBeenCalledOnce();
  });

  it('says so when the account cannot be read', async () => {
    api.adminGetUser.mockRejectedValue(new Error('500'));
    const user = await loaded();

    await user.click(screen.getByText('Ada Lovelace'));

    expect(await screen.findByText('Failed to load user details.')).toBeInTheDocument();
  });

  it('says so, and stays open, when the save is refused', async () => {
    api.adminUpdateUser.mockRejectedValue(new ApiError(409, { error: 'email_already_exists' }));
    const user = await loaded();
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('email_already_exists')).toBeInTheDocument();
    expect(screen.getByLabelText('Phone')).toBeInTheDocument();
  });

  it('closes on Escape without saving', async () => {
    const user = await loaded();
    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Phone')).toBeNull());
    expect(api.adminUpdateUser).not.toHaveBeenCalled();
  });
});

describe('the row menu', () => {
  it('offers an unlock only to a locked account', async () => {
    const user = await loaded();

    await openMenu(user);

    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
  });

  it('unlocks through the system route and clears the badge without re-searching', async () => {
    api.searchUsers.mockResolvedValue(results([{ ...ADA, locked_until: future() }]));
    const user = await loaded();
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    // null, not a list id: the system-scoped unlock takes the user alone.
    await vi.waitFor(() => expect(api.unlockUser).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('Account unlocked.')).toBeInTheDocument();
    expect(noBadge('Locked')).toBeNull();
  });

  it('says so when the unlock fails, and leaves the badge alone', async () => {
    api.searchUsers.mockResolvedValue(results([{ ...ADA, locked_until: future() }]));
    api.unlockUser.mockRejectedValue(new Error('500'));
    const user = await loaded();
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    expect(await screen.findByText('Failed to unlock account.')).toBeInTheDocument();
    expect(badge('Locked')).toBeInTheDocument();
  });

  it('closes when the operator clicks away', async () => {
    const user = await loaded();
    await openMenu(user);

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
  });

  it('does not open the editor when the menu itself is used', async () => {
    const user = await loaded();

    await openMenu(user);

    expect(screen.queryByLabelText('Phone')).not.toBeInTheDocument();
  });
});

describe('a user’s sessions', () => {
  const openSessions = async () => {
    const user = await loaded();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View sessions' }));
    return user;
  };

  it('reads them through the system route', async () => {
    await openSessions();

    await vi.waitFor(() => expect(api.getUserSessions).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('Portal')).toBeInTheDocument();
  });

  it('shows none rather than stale sessions when the read fails', async () => {
    api.getUserSessions.mockRejectedValue(new Error('500'));
    await openSessions();

    expect(await screen.findByText('No active sessions.')).toBeInTheDocument();
  });

  it('revokes them all', async () => {
    const user = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    await vi.waitFor(() => expect(api.revokeAllUserSessions).toHaveBeenCalledWith(null, 'u1'));
    expect(await screen.findByText('All sessions revoked.')).toBeInTheDocument();
  });

  it('says so when the revoke fails', async () => {
    api.revokeAllUserSessions.mockRejectedValue(new Error('500'));
    const user = await openSessions();
    await screen.findByText('Portal');

    await user.click(screen.getByRole('button', { name: 'Revoke all sessions' }));

    expect(await screen.findByText('Failed to revoke sessions.')).toBeInTheDocument();
  });
});

// ── La portée ───────────────────────────────────────────────────────────────

/**
 * La même page sert trois URL, et le CHEMIN décide de la route appelée. C'est la seule chose qui
 * change entre les portées, et c'est le contrôle : `/org/users` confine la recherche au locataire
 * du jeton, `/admin/users` ne confine rien. Appeler la route système depuis la portée organisation
 * répondrait 403, et le seul symptôme serait une page titrée et vide.
 *
 * Le filtre Tenant suit la même règle : dans une organisation il n'a pas lieu d'être — masqué,
 * plutôt qu'envoyé à une route qui ne le lit pas.
 */
describe('the scope the page is opened in', () => {
  it('searches the deployment through the system route', async () => {
    await loaded('/system/users');

    expect(api.searchUsers).toHaveBeenCalledWith('', { page: 1 });
    expect(api.orgSearchUsers).not.toHaveBeenCalled();
  });

  it('searches a tenant through the organisation route, which reads the tenant from the token', async () => {
    await loaded('/org/users');

    expect(api.orgSearchUsers).toHaveBeenCalledWith('', { page: 1 });
    expect(api.searchUsers).not.toHaveBeenCalled();
  });

  it('keeps the system route, pinned to the tenant, when a super-admin browses into one', async () => {
    await loaded('/system/organisations/o2/users');

    expect(api.searchUsers).toHaveBeenCalledWith('', { page: 1, org_id: 'o2' });
    expect(api.orgSearchUsers).not.toHaveBeenCalled();
  });

  it('offers the Tenant filter where there is more than one tenant to choose from', async () => {
    await loaded('/system/users');

    expect(screen.getByLabelText('Tenant')).toBeInTheDocument();
  });

  it.each(['/org/users', '/system/organisations/o2/users'])(
    'hides the Tenant filter on %s — the tenant is implicit there', async path => {
      await loaded(path);

      expect(screen.queryByLabelText('Tenant')).not.toBeInTheDocument();
      expect(screen.getByLabelText('User list')).toBeInTheDocument();
    });

  it('reads the lists to filter by through the organisation route too', async () => {
    await loaded('/org/users');

    expect(api.listOrgUserLists).toHaveBeenCalled();
    expect(api.listUserLists).not.toHaveBeenCalled();
    expect(api.listOrgs).not.toHaveBeenCalled();
  });

  it('sends the other filters unchanged from the organisation scope', async () => {
    const user = await loaded('/org/users');

    await user.selectOptions(screen.getByLabelText('Status'), 'locked');

    await vi.waitFor(() => expect(api.orgSearchUsers.mock.calls.at(-1))
      .toEqual(['', { page: 1, status: 'locked' }]));
  });

  it('says nothing matched when the organisation is empty', async () => {
    api.orgSearchUsers.mockResolvedValue(results([]));
    show('/org/users');

    expect(await screen.findByText('No users found')).toBeInTheDocument();
    expect(await screen.findByText(/This organisation holds no accounts yet/)).toBeInTheDocument();
  });

  it('shows the refusal rather than the last results, in the organisation scope as well', async () => {
    api.orgSearchUsers.mockRejectedValue(new Error('500'));
    show('/org/users');

    expect(await screen.findByText('Could not search this organisation’s accounts.')).toBeInTheDocument();
    expect(screen.queryByText('Ada Lovelace')).not.toBeInTheDocument();
  });

  it('opens and saves an account through the organisation routes', async () => {
    const user = await loaded('/org/users');

    await user.click(screen.getByText('Ada Lovelace'));
    await screen.findByLabelText('Phone');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(api.orgGetUser).toHaveBeenCalledWith('u1');
    await vi.waitFor(() => expect(api.orgUpdateUser).toHaveBeenCalledWith('u1', expect.anything()));
    expect(api.adminGetUser).not.toHaveBeenCalled();
    expect(api.adminUpdateUser).not.toHaveBeenCalled();
  });

  it('names the account’s own list on the actions the organisation routes hang off a list', async () => {
    api.orgSearchUsers.mockResolvedValue(results([{ ...ADA, locked_until: future() }]));
    const user = await loaded('/org/users');
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Unlock account' }));

    // `l1`, pas `null` : `/org/userlists/{list}/users/{uid}/unlock` nomme la liste, la route
    // système prend le compte seul.
    await vi.waitFor(() => expect(api.unlockUser).toHaveBeenCalledWith('l1', 'u1'));
  });
});
