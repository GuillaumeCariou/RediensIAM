import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import ServiceAccountDetail from './ServiceAccountDetail';
import { fmtDateShort } from '@/lib/utils';
import { ApiError } from '@/auth';

/**
 * Two secrets are handed out here and each is shown exactly once: a PAT, and the private half of
 * an RSA keypair that is generated in the browser and downloaded — the server only ever receives
 * the public JWK. Neither may be recoverable from the page afterwards.
 *
 * The assign-role dialog is the other trap. A super admin picks the organisation; an org admin
 * has it pre-filled and sees no picker at all, so the absence of the field is not the absence of
 * an org_id, and the submit guard has to know the difference.
 */

const api = vi.hoisted(() => ({
  getServiceAccount: vi.fn(), deleteServiceAccount: vi.fn(),
  generatePat: vi.fn(), revokePat: vi.fn(),
  assignSaRole: vi.fn(), removeSaRole: vi.fn(), listSaRoles: vi.fn(),
  getSaApiKeys: vi.fn(), addSaApiKey: vi.fn(), removeSaApiKey: vi.fn(),
  listOrgs: vi.fn(), listProjects: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '', isSuperAdmin: false, isOrgAdmin: true }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const SA = {
  id: 's1', name: 'ci-deploy', description: 'CI pipeline', active: true,
  last_used_at: '2026-03-04T05:06:07Z', created_at: '2026-01-02T00:00:00Z',
  pats: [
    { id: 't1', name: 'ci-token', expires_at: '2027-01-01T00:00:00Z', last_used_at: null, created_at: '2026-01-02T00:00:00Z' },
    { id: 't2', name: 'forever', expires_at: null, last_used_at: '2026-02-01T00:00:00Z', created_at: '2026-01-02T00:00:00Z' },
  ],
  roles: [
    { id: 'r1', role: 'org_admin', org_id: 'o1', project_id: null, granted_at: '2026-01-02T00:00:00Z' },
    { id: 'r2', role: 'project_admin', org_id: 'o1', project_id: 'p1', granted_at: '2026-01-02T00:00:00Z' },
    { id: 'r3', role: 'super_admin', org_id: null, project_id: null, granted_at: '2026-01-02T00:00:00Z' },
  ],
};

const ORGS = [{ id: 'o1', name: 'Acme' }, { id: 'o2', name: 'Globex' }];
const PROJECTS = [{ id: 'p1', name: 'Portal' }, { id: 'p2', name: 'Tools' }];

let click: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(auth, { orgId: 'o1', projectId: '', isSuperAdmin: false, isOrgAdmin: true });
  api.getServiceAccount.mockResolvedValue(SA);
  api.getSaApiKeys.mockResolvedValue({ client_id: null, has_key: false, kid: null });
  api.listSaRoles.mockResolvedValue(SA.roles);
  api.generatePat.mockResolvedValue({ token: 'riam_pat_secret' });
  api.addSaApiKey.mockResolvedValue({ client_id: 'sa-client-1' });
  api.listOrgs.mockResolvedValue(ORGS);
  api.listProjects.mockResolvedValue({ projects: PROJECTS });
  click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});

function Here() {
  const { pathname, search } = useLocation();
  return <output data-testid="here">{pathname}{search}</output>;
}

const ROUTES = {
  org: ['/org/service-accounts/s1', '/org/service-accounts/:saId'],
  system: ['/system/service-accounts/s1', '/system/service-accounts/:id'],
  tenant: ['/system/organisations/o9/service-accounts/s1', '/system/organisations/:id/service-accounts/:saId'],
  project: ['/project/service-accounts/s1?project_id=p9', '/project/service-accounts/:saId'],
  systemProject: ['/system/organisations/o9/projects/p9/service-accounts/s1',
                  '/system/organisations/:oid/projects/:pid/service-accounts/:saId'],
} as const;

function show(route: keyof typeof ROUTES = 'org') {
  const [path, pattern] = ROUTES[route];
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<ServiceAccountDetail />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));
const section = (heading: string) =>
  within(screen.getByRole('heading', { name: heading }).closest('.rounded-xl')!);
/** The row menu inside `heading`'s table, for the row matching `label`. */
const rowMenu = async (user: Awaited<ReturnType<typeof show>>, heading: string, label: string) => {
  const row = section(heading).getByRole('row', { name: new RegExp(label) });
  await user.click([...row.querySelectorAll('button')].at(-1)!);
};

describe('which account, under which route', () => {
  it.each([
    ['the org route, where the param is :saId', 'org'],
    ['the system route, where it is :id', 'system'],
    ['the tenant route, which has both', 'tenant'],
  ] as const)('resolves it from %s', async (_n, route) => {
    show(route);

    expect(await screen.findByRole('heading', { name: 'ci-deploy' })).toBeInTheDocument();
    expect(api.getServiceAccount).toHaveBeenCalledWith('s1');
  });
});

describe('the header', () => {
  it('names the account, its description and when it was made', async () => {
    show();

    expect(await screen.findByRole('heading', { name: 'ci-deploy' })).toBeInTheDocument();
    expect(screen.getByText('CI pipeline')).toBeInTheDocument();
    expect(screen.getByText(`Created ${fmtDateShort(SA.created_at)}`)).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
  });

  it('marks an inactive account, and omits an absent description', async () => {
    api.getServiceAccount.mockResolvedValue({ ...SA, active: false, description: null });
    show();

    expect(await screen.findByText('Inactive')).toBeInTheDocument();
    expect(screen.queryByText('CI pipeline')).not.toBeInTheDocument();
  });

  it('shows placeholders while loading', () => {
    api.getServiceAccount.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-skeleton').length).toBeGreaterThan(0);
    expect(screen.queryByRole('heading', { name: 'ci-deploy' })).not.toBeInTheDocument();
  });

  it('finishes loading when the account cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getServiceAccount.mockRejectedValue(new Error('404'));
    show();

    expect(await screen.findByText('No roles assigned.')).toBeInTheDocument();
    expect(screen.getByText('No tokens generated yet.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('goes back to the list in the scope it was reached from', async () => {
    const user = show('tenant');
    await screen.findByRole('heading', { name: 'ci-deploy' });

    await user.click(screen.getByRole('button', { name: /Back to Service Accounts/ }));

    await arrivedAt('/system/organisations/o9/service-accounts');
  });
});

describe('the assigned roles', () => {
  it('names each role and the thing it is scoped to', async () => {
    show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    const roles = section('Assigned Roles');
    expect(roles.getByText('org: o1')).toBeInTheDocument();
    expect(roles.getByText('project: p1')).toBeInTheDocument();
    // A deployment-wide role is scoped to nothing, which is not the same as an unknown scope.
    expect(roles.getByText('—')).toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.getServiceAccount.mockResolvedValue({ ...SA, roles: [] });
    show();

    expect(await screen.findByText('No roles assigned.')).toBeInTheDocument();
  });

  it('revokes one after asking, and re-reads', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Assigned Roles', 'org_admin');

    await user.click(screen.getByRole('button', { name: /Remove/ }));
    expect(await screen.findByText('Remove role "org_admin"?')).toBeInTheDocument();
    expect(api.removeSaRole).not.toHaveBeenCalled();

    await user.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);

    await vi.waitFor(() => expect(api.removeSaRole).toHaveBeenCalledWith('s1', 'r1'));
    // Les rôles seuls : recharger le compte entier repassait les PAT et la clé en squelette.
    expect(api.listSaRoles).toHaveBeenCalledWith('s1');
    expect(api.getServiceAccount).toHaveBeenCalledTimes(1);
  });

  it('takes the new list from the roles route, not from a stale render', async () => {
    api.listSaRoles.mockResolvedValue([SA.roles[1]]);
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Assigned Roles', 'org_admin');
    await user.click(screen.getByRole('button', { name: /Remove/ }));

    await user.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);

    await vi.waitFor(() => expect(section('Assigned Roles').queryByText('org: o1')).toBeNull());
    expect(section('Assigned Roles').getByText('project: p1')).toBeInTheDocument();
  });

  it('says so when the revoke is refused, instead of leaving the row as if it had worked', async () => {
    api.removeSaRole.mockRejectedValue(new Error('403'));
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Assigned Roles', 'org_admin');
    await user.click(screen.getByRole('button', { name: /Remove/ }));

    await user.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);

    expect(await screen.findByText('Failed to remove this role.')).toBeInTheDocument();
    expect(section('Assigned Roles').getByText('org: o1')).toBeInTheDocument();
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Assigned Roles', 'org_admin');
    await user.click(screen.getByRole('button', { name: /Remove/ }));
    await screen.findByText('Remove role "org_admin"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.removeSaRole).not.toHaveBeenCalled();
  });
});

describe('assigning a role as an org admin', () => {
  const openAssign = async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));
    return user;
  };

  it('offers no super_admin, and no organisation picker', async () => {
    // The organisation is theirs and is filled in behind the scenes.
    await openAssign();

    const roles = [...screen.getByLabelText('Role').querySelectorAll('option')].map(o => o.textContent);
    expect(roles).toEqual(['Select a role…', 'org_admin', 'project_admin']);
    expect(screen.queryByLabelText('Organisation')).not.toBeInTheDocument();
  });

  it('loads their own organisation\'s projects up front', async () => {
    await openAssign();

    await vi.waitFor(() => expect(api.listProjects).toHaveBeenCalledWith('o1'));
    expect(api.listOrgs).not.toHaveBeenCalled();
  });

  it('grants an organisation-wide role against their own organisation', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole)
      .toHaveBeenCalledWith('s1', { role: 'org_admin', org_id: 'o1', project_id: undefined }));
    expect(api.listSaRoles).toHaveBeenCalledWith('s1');
    expect(api.getServiceAccount).toHaveBeenCalledTimes(1);
  });

  it('shows what the server refused, and keeps the roles as they were', async () => {
    // Sans catch, l'assignation refusée fermait la boîte comme une réussite.
    api.assignSaRole.mockRejectedValue(new ApiError(403, { error: 'cannot_grant_super_admin' }));
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    expect(await screen.findByText('cannot_grant_super_admin')).toBeInTheDocument();
    expect(api.listSaRoles).not.toHaveBeenCalled();
  });

  it('asks which project a project role is for, and will not submit without one', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');

    expect(screen.getByRole('button', { name: 'Assign' })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText('Project'), 'p2');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole)
      .toHaveBeenCalledWith('s1', { role: 'project_admin', org_id: 'o1', project_id: 'p2' }));
  });

  it('will not submit with no role chosen at all', async () => {
    await openAssign();
    expect(screen.getByRole('button', { name: 'Assign' })).toBeDisabled();
  });

  it('forgets a chosen project when the role changes', async () => {
    const user = await openAssign();
    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    await user.selectOptions(screen.getByLabelText('Project'), 'p2');

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole)
      .toHaveBeenCalledWith('s1', expect.objectContaining({ project_id: undefined })));
  });

  it('closes without granting anything', async () => {
    const user = await openAssign();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.assignSaRole).not.toHaveBeenCalled();
  });
});

describe('assigning a role as a super admin', () => {
  const openAssign = async () => {
    auth.isSuperAdmin = true;
    auth.isOrgAdmin = true;
    const user = show('system');
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));
    return user;
  };

  it('offers super_admin, which needs no scope at all', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'super_admin');
    expect(screen.queryByLabelText('Organisation')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole)
      .toHaveBeenCalledWith('s1', { role: 'super_admin', org_id: undefined, project_id: undefined }));
  });

  it('asks which organisation, and will not submit without one', async () => {
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');

    await vi.waitFor(() => expect(api.listOrgs).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'Assign' })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText('Organisation'), 'o2');
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole)
      .toHaveBeenCalledWith('s1', { role: 'org_admin', org_id: 'o2', project_id: undefined }));
  });

  it('loads that organisation\'s projects, and forgets them when it changes', async () => {
    const user = await openAssign();
    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    await vi.waitFor(() => expect(api.listOrgs).toHaveBeenCalled());

    await user.selectOptions(screen.getByLabelText('Organisation'), 'o2');
    await vi.waitFor(() => expect(api.listProjects).toHaveBeenCalledWith('o2'));
    await user.selectOptions(screen.getByLabelText('Project'), 'p1');

    await user.selectOptions(screen.getByLabelText('Organisation'), 'o1');

    // A project of the previous organisation must not survive the switch.
    expect(screen.getByLabelText('Project')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Assign' })).toBeDisabled();
  });

  it('survives an organisation list that cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listOrgs.mockRejectedValue(new Error('500'));
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');

    expect(screen.getByLabelText('Organisation')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('treats a null organisation list as none', async () => {
    api.listOrgs.mockResolvedValue(null);
    const user = await openAssign();

    await user.selectOptions(screen.getByLabelText('Role'), 'org_admin');

    expect([...screen.getByLabelText('Organisation').querySelectorAll('option')]).toHaveLength(1);
  });
});

describe('the personal access tokens', () => {
  it('lists them with their expiry and last use', async () => {
    show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    const pats = section('Personal Access Tokens');
    expect(pats.getByText('ci-token')).toBeInTheDocument();
    expect(pats.getByText(fmtDateShort('2027-01-01T00:00:00Z'))).toBeInTheDocument();
    // "Never" rather than an em dash: a token with no expiry is a decision, not missing data.
    expect(pats.getByText('Never')).toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.getServiceAccount.mockResolvedValue({ ...SA, pats: [] });
    show();

    expect(await screen.findByText('No tokens generated yet.')).toBeInTheDocument();
  });

  it('generates one and shows the raw token once', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-token');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await vi.waitFor(() => expect(api.generatePat)
      .toHaveBeenCalledWith('s1', { name: 'new-token', expires_at: undefined }));
    expect(await screen.findByDisplayValue('riam_pat_secret')).toBeInTheDocument();
    expect(screen.getByText('This token will not be shown again. Copy it now.')).toBeInTheDocument();
  });

  it('sends the expiry when one was given', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-token');
    await user.fill(screen.getByLabelText(/Expiry date/), '2027-06-01T12:00');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await vi.waitFor(() => expect(api.generatePat)
      .toHaveBeenCalledWith('s1', { name: 'new-token', expires_at: '2027-06-01T12:00' }));
  });

  it('copies the token', async () => {
    const writeText = vi.fn(async () => {});
    vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-token');
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByDisplayValue('riam_pat_secret');

    // The copy button sits beside the readonly token field inside the dialog.
    await user.click(screen.getByDisplayValue('riam_pat_secret')
      .parentElement!.querySelector<HTMLButtonElement>('.iam-btn-icon')!);

    expect(writeText).toHaveBeenCalledWith('riam_pat_secret');
    vi.restoreAllMocks();
  });

  it('re-reads the account once the token has been acknowledged', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-token');
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByDisplayValue('riam_pat_secret');

    await user.click(screen.getByRole('button', { name: 'Done' }));

    await vi.waitFor(() => expect(api.getServiceAccount).toHaveBeenCalledTimes(2));
    expect(screen.queryByDisplayValue('riam_pat_secret')).not.toBeInTheDocument();
  });

  it('requires a name', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));

    expect(screen.getByLabelText('Name')).toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.generatePat).not.toHaveBeenCalled();
  });

  it('revokes one after warning what it breaks, and re-reads', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Personal Access Tokens', 'ci-token');

    await user.click(screen.getByRole('button', { name: /Revoke/ }));
    expect(await screen.findByText('Revoke "ci-token"?')).toBeInTheDocument();
    expect(screen.getByText(/lose access immediately/)).toBeInTheDocument();

    await user.click(screen.getAllByRole('button', { name: 'Revoke' }).at(-1)!);

    await vi.waitFor(() => expect(api.revokePat).toHaveBeenCalledWith('s1', 't1'));
    expect(api.getServiceAccount).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await rowMenu(user, 'Personal Access Tokens', 'ci-token');
    await user.click(screen.getByRole('button', { name: /Revoke/ }));
    await screen.findByText('Revoke "ci-token"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.revokePat).not.toHaveBeenCalled();
  });
});

describe('the JWT profile', () => {
  it('says there is no key, and offers to make one', async () => {
    show();

    expect(await screen.findByText(/No key configured/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Generate keypair/ })).toBeInTheDocument();
  });

  it('shows the registered key without ever showing the private half', async () => {
    api.getSaApiKeys.mockResolvedValue({ client_id: 'sa-client-1', has_key: true, kid: 's1-1700000000' });
    show();

    expect(await screen.findByText('sa-client-1')).toBeInTheDocument();
    expect(screen.getByText('s1-1700000000')).toBeInTheDocument();
    expect(screen.getByText('RS256')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Remove key/ })).toBeInTheDocument();
  });

  it('shows an em dash where the server reported no key id', async () => {
    api.getSaApiKeys.mockResolvedValue({ client_id: 'sa-client-1', has_key: true, kid: null });
    show();

    await screen.findByText('sa-client-1');
    expect(screen.getByText('—', { selector: 'code' })).toBeInTheDocument();
  });

  it('sends only the public key, and downloads the private one', async () => {
    // The private key is generated in this browser and never leaves it except as a download.
    const user = show();
    await screen.findByText(/No key configured/);

    await user.click(screen.getByRole('button', { name: /Generate keypair/ }));

    await vi.waitFor(() => expect(api.addSaApiKey).toHaveBeenCalled());
    const [, jwk] = api.addSaApiKey.mock.calls[0] as [string, Record<string, unknown>];
    expect(jwk['kty']).toBe('RSA');
    expect(jwk['use']).toBe('sig');
    expect(jwk['kid']).toMatch(/^s1-\d+$/);
    // The private exponent and primes must not be in what was uploaded.
    for (const secret of ['d', 'p', 'q', 'dp', 'dq', 'qi']) expect(jwk[secret]).toBeUndefined();
    expect(click).toHaveBeenCalledOnce();
  }, 20_000);

  it('re-reads the key after generating one', async () => {
    const user = show();
    await screen.findByText(/No key configured/);

    await user.click(screen.getByRole('button', { name: /Generate keypair/ }));

    await vi.waitFor(() => expect(api.getSaApiKeys).toHaveBeenCalledTimes(2));
  }, 20_000);

  it('reports a registration the server refused, and downloads nothing', async () => {
    api.addSaApiKey.mockResolvedValue({ error: 'key_already_registered' });
    const user = show();
    await screen.findByText(/No key configured/);

    await user.click(screen.getByRole('button', { name: /Generate keypair/ }));

    expect(await screen.findByText('Failed: key_already_registered')).toBeInTheDocument();
    expect(click).not.toHaveBeenCalled();
  }, 20_000);

  it('reports a failure in the browser\'s own key generation', async () => {
    vi.spyOn(crypto.subtle, 'generateKey').mockRejectedValue(new Error('not allowed'));
    const user = show();
    await screen.findByText(/No key configured/);

    await user.click(screen.getByRole('button', { name: /Generate keypair/ }));

    expect(await screen.findByText('Key generation failed: not allowed')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('removes the key and re-reads', async () => {
    api.getSaApiKeys.mockResolvedValue({ client_id: 'sa-client-1', has_key: true, kid: 'k1' });
    const user = show();
    await screen.findByText('sa-client-1');

    await user.click(screen.getByRole('button', { name: /Remove key/ }));

    await vi.waitFor(() => expect(api.removeSaApiKey).toHaveBeenCalledWith('s1'));
    expect(api.getSaApiKeys).toHaveBeenCalledTimes(2);
  });

  it('survives a key endpoint that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getSaApiKeys.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText(/No key configured/)).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('deleting the account', () => {
  it('warns that the tokens go with it, and asks first', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });

    await user.click(screen.getByRole('button', { name: /Delete/ }));

    expect(await screen.findByText('Delete "ci-deploy"?')).toBeInTheDocument();
    expect(screen.getByText('All PATs will be revoked. This cannot be undone.')).toBeInTheDocument();
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('deletes and returns to the list', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Delete/ }));

    await user.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);

    await vi.waitFor(() => expect(api.deleteServiceAccount).toHaveBeenCalledWith('s1'));
    await arrivedAt('/org/service-accounts');
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    await user.click(screen.getByRole('button', { name: /Delete/ }));
    await screen.findByText('Delete "ci-deploy"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });
});


describe('dismissing a dialog with Escape', () => {
  const open = async () => {
    const user = show();
    await screen.findByRole('heading', { name: 'ci-deploy' });
    return user;
  };

  it('closes the generate-token form', async () => {
    const user = await open();
    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
    expect(api.generatePat).not.toHaveBeenCalled();
  });

  it('closes the new-token dialog and re-reads the account', async () => {
    const user = await open();
    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-token');
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByDisplayValue('riam_pat_secret');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByDisplayValue('riam_pat_secret')).toBeNull());
    expect(api.getServiceAccount).toHaveBeenCalledTimes(2);
  });

  it('closes the revoke confirmation without revoking', async () => {
    const user = await open();
    await rowMenu(user, 'Personal Access Tokens', 'ci-token');
    await user.click(screen.getByRole('button', { name: /Revoke/ }));
    await screen.findByText('Revoke "ci-token"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Revoke "ci-token"?')).toBeNull());
    expect(api.revokePat).not.toHaveBeenCalled();
  });

  it('closes the assign-role form without granting anything', async () => {
    const user = await open();
    await user.click(screen.getByRole('button', { name: /Assign Role/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Role')).toBeNull());
    expect(api.assignSaRole).not.toHaveBeenCalled();
  });

  it('closes the remove-role confirmation without revoking', async () => {
    const user = await open();
    await rowMenu(user, 'Assigned Roles', 'org_admin');
    await user.click(screen.getByRole('button', { name: /Remove/ }));
    await screen.findByText('Remove role "org_admin"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Remove role "org_admin"?')).toBeNull());
    expect(api.removeSaRole).not.toHaveBeenCalled();
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = await open();
    await user.click(screen.getByRole('button', { name: /Delete/ }));
    await screen.findByText('Delete "ci-deploy"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete "ci-deploy"?')).toBeNull());
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });
});

describe('going back to the list it came from', () => {
  /**
   * Le retour partait de `orgBase`, donc toujours vers les comptes de service de l'ORGANISATION.
   * Depuis un projet c'était la mauvaise liste, et pour un project_admin `/org/*` est fermé : le
   * bouton « retour » le renvoyait à l'accueil.
   */
  it.each([
    ['a project manager keeps their project, query and all', 'project', '/project/service-accounts?project_id=p9'],
    ['a super admin keeps the project they are browsing', 'systemProject', '/system/organisations/o9/projects/p9/service-accounts'],
    ['an org admin goes back to their organisation', 'org', '/org/service-accounts'],
    ['a super admin browsing a tenant stays in it', 'tenant', '/system/organisations/o9/service-accounts'],
    ['the deployment list stays the deployment list', 'system', '/system/service-accounts'],
  ] as const)('%s', async (_n, route, expected) => {
    const user = show(route);
    await screen.findByRole('button', { name: /Back to Service Accounts/ });

    await user.click(screen.getByRole('button', { name: /Back to Service Accounts/ }));

    await arrivedAt(expected);
  });
});

describe('assigning a role as a project admin', () => {
  beforeEach(() => { auth.isOrgAdmin = false; auth.projectId = 'p9'; });

  const openDialog = async () => {
    const user = show('project');
    await screen.findByRole('button', { name: /Assign role/i });
    await user.click(screen.getByRole('button', { name: /Assign role/i }));
    return user;
  };

  /**
   * `listProjects` frappe `/org/projects`, gardé en OrgAdmin : un project_admin recevait 403, le
   * `catch(console.error)` l'avalait, le sélecteur restait vide et « Assign » ne s'activait jamais.
   */
  it('does not ask a list it is not allowed to read', async () => {
    await openDialog();

    expect(api.listProjects).not.toHaveBeenCalled();
    expect(api.listOrgs).not.toHaveBeenCalled();
  });

  it('offers only the role the server would accept from it', async () => {
    await openDialog();

    expect(screen.queryByRole('option', { name: 'super_admin' })).toBeNull();
    expect(screen.queryByRole('option', { name: 'org_admin' })).toBeNull();
    expect(screen.getByRole('option', { name: 'project_admin' })).toBeInTheDocument();
  });

  it('grants it on their own project, with no picker to fill', async () => {
    const user = await openDialog();

    await user.selectOptions(screen.getByLabelText('Role'), 'project_admin');
    expect(screen.queryByLabelText('Project')).toBeNull();
    expect(screen.getByText(/only grant it here/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await vi.waitFor(() => expect(api.assignSaRole).toHaveBeenCalledWith('s1', {
      role: 'project_admin', org_id: 'o1', project_id: 'p9',
    }));
  });
});
