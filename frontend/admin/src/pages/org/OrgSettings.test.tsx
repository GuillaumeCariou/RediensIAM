import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import OrgSettings from './OrgSettings';
import { ApiError } from '@/auth';

/**
 * One page at two routes, and the pair it calls is the whole point: it used to call the
 * token-scoped /org routes in both, so a super admin opening another organisation's settings read
 * and wrote their OWN organisation's retention while the URL said otherwise.
 *
 * Two more traps live here. `PATCH /org/settings` reads the retention through `HasValue`, so the
 * old "Forever" option — which sent `null` — was dropped by the binder and wrote nothing at all;
 * the sentinel that resets an organisation to the deployment default is `-1`. And the rename, the
 * suspension and the deletion are `/admin/organizations/*`: a tenant admin who is offered them
 * gets a 403, so they are not offered.
 */

const api = vi.hoisted(() => ({
  getOrg: vi.fn(), getOrgInfo: vi.fn(), updateOrg: vi.fn(), updateOrgInfo: vi.fn(),
  getOrgSmtp: vi.fn(), adminGetOrgSmtp: vi.fn(),
  suspendOrg: vi.fn(), unsuspendOrg: vi.fn(), deleteOrg: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const OWN = {
  id: 'o1', name: 'Yandee Infrastructure', slug: 'yandee-infra',
  active: true, suspended_at: null, audit_retention_days: 365,
};
const OTHER = {
  id: 'o9', name: 'Contoso', slug: 'contoso',
  active: true, suspended_at: null, audit_retention_days: 180,
};

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.getOrgInfo.mockResolvedValue({ ...OWN });
  api.getOrg.mockResolvedValue({ ...OTHER });
  api.getOrgSmtp.mockResolvedValue({ configured: false });
  api.adminGetOrgSmtp.mockResolvedValue({ configured: false });
  api.updateOrgInfo.mockResolvedValue({});
  api.updateOrg.mockResolvedValue({});
  api.suspendOrg.mockResolvedValue({});
  api.unsuspendOrg.mockResolvedValue({});
  api.deleteOrg.mockResolvedValue({});
});

function show(path = '/org/settings', pattern = '/org/settings') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path={pattern} element={<OrgSettings />} />
        <Route path="/system/organisations" element={<div>Organisations list</div>} />
      </Routes>
    </MemoryRouter>,
  );
  return user;
}

const SYSTEM = ['/system/organisations/o9/settings', '/system/organisations/:id/settings'] as const;
const showSystem = () => show(...SYSTEM);

const retention = () => screen.getByLabelText('Retention (days)');
const followDefault = () => screen.getByRole('checkbox', { name: 'Follow the deployment default' });
const saveBtn = () => screen.getByRole('button', { name: 'Save changes' });

const refusal = (code: string) => new ApiError(400, { error: code });

describe('which organisation the page reads and writes', () => {
  it('uses the token-scoped routes for an org admin', async () => {
    show();

    await vi.waitFor(() => expect(retention()).toHaveValue(365));
    expect(api.getOrgInfo).toHaveBeenCalledOnce();
    expect(api.getOrgSmtp).toHaveBeenCalledOnce();
    expect(api.getOrg).not.toHaveBeenCalled();
    expect(api.adminGetOrgSmtp).not.toHaveBeenCalled();
  });

  it('uses the named organisation for a super admin browsing one', async () => {
    showSystem();

    await vi.waitFor(() => expect(retention()).toHaveValue(180));
    expect(api.getOrg).toHaveBeenCalledWith('o9');
    expect(api.adminGetOrgSmtp).toHaveBeenCalledWith('o9');
    expect(api.getOrgInfo).not.toHaveBeenCalled();
  });

  it('saves through the token-scoped route for an org admin', async () => {
    const user = show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    await user.fill(retention(), '120');
    await user.click(saveBtn());

    await vi.waitFor(() => expect(api.updateOrgInfo).toHaveBeenCalledWith({ audit_retention_days: 120 }));
    expect(api.updateOrg).not.toHaveBeenCalled();
  });

  it('saves the name as well against the named organisation, which is the only route that binds it', async () => {
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.fill(screen.getByLabelText('Name'), 'Contoso Ltd');
    await user.click(saveBtn());

    await vi.waitFor(() => expect(api.updateOrg).toHaveBeenCalledWith('o9', {
      name: 'Contoso Ltd', audit_retention_days: 180,
    }));
    expect(api.updateOrgInfo).not.toHaveBeenCalled();
  });
});

describe('general', () => {
  it('shows the slug, and refuses to let anyone edit it', async () => {
    show();

    const slug = await screen.findByLabelText('Slug');
    expect(slug).toHaveValue('yandee-infra');
    expect(slug).toBeDisabled();
  });

  it('shows the name read-only to a tenant admin, and says who can change it', async () => {
    show();

    expect(await screen.findByLabelText('Name')).toBeDisabled();
    expect(screen.getByText('Only a deployment administrator can rename an organisation.')).toBeInTheDocument();
  });

  it('lets a super admin edit the name', async () => {
    showSystem();

    expect(await screen.findByLabelText('Name')).toBeEnabled();
  });

  it('says when the tenant is suspended', async () => {
    api.getOrgInfo.mockResolvedValue({ ...OWN, active: false, suspended_at: '2026-08-01T00:00:00Z' });
    show();

    expect(await screen.findByText('Suspended')).toBeInTheDocument();
  });
});

describe('mail', () => {
  it('says the tenant falls back to the deployment relay when it has none of its own', async () => {
    show();

    expect(await screen.findByText(/sends through the deployment relay/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Configure own SMTP' })).toHaveAttribute('href', '/org/email');
  });

  it('names the tenant’s own relay when there is one', async () => {
    api.getOrgSmtp.mockResolvedValue({ configured: true, host: 'smtp.eu.mailgun.org', port: 587 });
    show();

    expect(await screen.findByText('smtp.eu.mailgun.org:587')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Edit the relay' })).toBeInTheDocument();
  });

  it('points a super admin at the tenant’s own Email page, not their own', async () => {
    showSystem();

    await vi.waitFor(() => expect(screen.getByRole('link', { name: 'Configure own SMTP' }))
      .toHaveAttribute('href', '/system/organisations/o9/email'));
  });

  it('says the relay could not be read, and still shows the rest of the page', async () => {
    api.getOrgSmtp.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('Could not read this organisation’s mail relay.')).toBeInTheDocument();
    expect(retention()).toHaveValue(365);
  });
});

describe('audit retention', () => {
  it('ticks "follow the deployment default" for an organisation that has no own value', async () => {
    api.getOrgInfo.mockResolvedValue({ ...OWN, audit_retention_days: null });
    show();

    await vi.waitFor(() => expect(followDefault()).toBeChecked());
    expect(retention()).toBeDisabled();
  });

  it('sends -1, not null, to go back to the deployment default', async () => {
    const user = show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    await user.click(followDefault());
    await user.click(saveBtn());

    await vi.waitFor(() => expect(api.updateOrgInfo).toHaveBeenCalledWith({ audit_retention_days: -1 }));
  });

  it('says in words what the server refuses, instead of a bare "could not save"', async () => {
    api.updateOrgInfo.mockRejectedValue(refusal('audit_retention_too_short'));
    const user = show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    await user.fill(retention(), '30');
    await user.click(saveBtn());

    expect(await screen.findByText('Retention must be at least 90 days. Nothing was changed.')).toBeInTheDocument();
  });

  it('confirms a save, and re-reads rather than trusting what was typed', async () => {
    const user = show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    await user.fill(retention(), '120');
    await user.click(saveBtn());

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    await vi.waitFor(() => expect(api.getOrgInfo).toHaveBeenCalledTimes(2));
  });

  it('says nothing was changed when the write is refused for a reason it does not know', async () => {
    api.updateOrgInfo.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    await user.click(saveBtn());

    expect(await screen.findByText('Could not save. Nothing was changed.')).toBeInTheDocument();
    await vi.waitFor(() => expect(saveBtn()).toBeEnabled());
  });
});

describe('when the settings cannot be read', () => {
  it('says so instead of showing a default, which is a real setting', async () => {
    api.getOrgInfo.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('Could not load these settings. Reload before changing anything.'))
      .toBeInTheDocument();
  });

  it('shows a placeholder, and no form to save from, while loading', () => {
    api.getOrgInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByLabelText('Retention (days)')).not.toBeInTheDocument();
  });
});

describe('the danger zone', () => {
  it('is not offered to a tenant admin, whose token is refused by those routes', async () => {
    show();
    await vi.waitFor(() => expect(retention()).toHaveValue(365));

    expect(screen.queryByText('Danger zone')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('is offered to a super admin browsing the tenant', async () => {
    showSystem();

    expect(await screen.findByText('Danger zone')).toBeInTheDocument();
  });

  it('names what a suspension revokes before doing it', async () => {
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Suspend' }));

    expect(await screen.findByText(/its own administrators included/)).toBeInTheDocument();
    expect(api.suspendOrg).not.toHaveBeenCalled();
  });

  it('suspends once confirmed, and re-reads', async () => {
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Suspend' }));
    await user.click(screen.getAllByRole('button', { name: 'Suspend' }).at(-1)!);

    await vi.waitFor(() => expect(api.suspendOrg).toHaveBeenCalledWith('o9'));
    await vi.waitFor(() => expect(api.getOrg).toHaveBeenCalledTimes(2));
  });

  it('offers the way back for an already suspended tenant', async () => {
    api.getOrg.mockResolvedValue({ ...OTHER, active: false, suspended_at: '2026-08-01T00:00:00Z' });
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Unsuspend' }));
    await user.click(screen.getAllByRole('button', { name: 'Unsuspend' }).at(-1)!);

    await vi.waitFor(() => expect(api.unsuspendOrg).toHaveBeenCalledWith('o9'));
    expect(api.suspendOrg).not.toHaveBeenCalled();
  });

  it('shows a refused suspension instead of leaving the screen unchanged', async () => {
    api.suspendOrg.mockRejectedValue(new ApiError(409, { detail: 'Hydra is unreachable.' }));
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Suspend' }));
    await user.click(screen.getAllByRole('button', { name: 'Suspend' }).at(-1)!);

    expect(await screen.findByText('Hydra is unreachable.')).toBeInTheDocument();
  });

  it('names the whole cascade before deleting', async () => {
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/every project and its OAuth2 client.*every admin grant in Keto.*whole audit chain/s))
      .toBeInTheDocument();
    expect(api.deleteOrg).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and leaves for the list', async () => {
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await user.click(screen.getByRole('button', { name: 'Delete for good' }));

    await vi.waitFor(() => expect(api.deleteOrg).toHaveBeenCalledWith('o9'));
    expect(await screen.findByText('Organisations list')).toBeInTheDocument();
  });

  it('shows a refused deletion instead of pretending it worked', async () => {
    api.deleteOrg.mockRejectedValue(new Error('500'));
    const user = showSystem();
    await vi.waitFor(() => expect(retention()).toHaveValue(180));

    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await user.click(screen.getByRole('button', { name: 'Delete for good' }));

    expect(await screen.findByText('Could not delete this organisation. Nothing was destroyed.')).toBeInTheDocument();
  });
});
