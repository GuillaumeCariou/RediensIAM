import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import OrgSettings from './OrgSettings';

/**
 * One page at two routes, and the pair it calls is the whole point: it used to call the
 * token-scoped /org routes in both, so a super admin opening another organisation's settings read
 * and wrote their OWN organisation's retention while the URL said otherwise.
 *
 * The other trap is that null means "keep forever", which is also what an unread page holds — so
 * a failed load that stayed silent showed "Forever" and saving it wiped a real setting.
 */

const api = vi.hoisted(() => ({
  getOrg: vi.fn(), getOrgInfo: vi.fn(), updateOrg: vi.fn(), updateOrgInfo: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.getOrgInfo.mockResolvedValue({ audit_retention_days: 90 });
  api.getOrg.mockResolvedValue({ audit_retention_days: 30 });
});

function show(path = '/org/settings', pattern = '/org/settings') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<OrgSettings />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const SYSTEM = ['/system/organisations/o9/settings', '/system/organisations/:id/settings'] as const;
const field = () => screen.getByLabelText('Retention period');

describe('which organisation the page reads and writes', () => {
  it('uses the token-scoped route for an org admin', async () => {
    show();

    await vi.waitFor(() => expect(field()).toHaveValue('90'));
    expect(api.getOrgInfo).toHaveBeenCalledOnce();
    expect(api.getOrg).not.toHaveBeenCalled();
  });

  it('uses the named organisation for a super admin browsing one', async () => {
    show(...SYSTEM);

    await vi.waitFor(() => expect(field()).toHaveValue('30'));
    expect(api.getOrg).toHaveBeenCalledWith('o9');
    expect(api.getOrgInfo).not.toHaveBeenCalled();
  });

  it('saves through the token-scoped route for an org admin', async () => {
    const user = show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    await user.selectOptions(field(), '180');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateOrgInfo).toHaveBeenCalledWith({ audit_retention_days: 180 }));
    expect(api.updateOrg).not.toHaveBeenCalled();
  });

  it('saves against the named organisation for a super admin', async () => {
    const user = show(...SYSTEM);
    await vi.waitFor(() => expect(field()).toHaveValue('30'));

    await user.selectOptions(field(), '365');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateOrg).toHaveBeenCalledWith('o9', { audit_retention_days: 365 }));
    expect(api.updateOrgInfo).not.toHaveBeenCalled();
  });
});

describe('the retention value', () => {
  it('offers "Forever" as the way to disable deletion', async () => {
    show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    const options = [...field().querySelectorAll('option')].map(o => o.textContent);
    expect(options).toEqual(['30 days', '60 days', '90 days', '180 days', '1 year', 'Forever']);
  });

  it('sends null, not an empty string or a zero, for "Forever"', async () => {
    const user = show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    await user.selectOptions(field(), '');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(api.updateOrgInfo).toHaveBeenCalledWith({ audit_retention_days: null }));
  });

  it('shows "Forever" for an organisation that genuinely has no limit', async () => {
    api.getOrgInfo.mockResolvedValue({ audit_retention_days: null });
    show();

    await vi.waitFor(() => expect(field()).toHaveValue(''));
  });

  it('shows a number as a number, not as a duration it has to guess', async () => {
    api.getOrgInfo.mockResolvedValue({ audit_retention_days: 60 });
    show();

    await vi.waitFor(() => expect(field()).toHaveValue('60'));
  });
});

describe('saving', () => {
  it('confirms, then takes the confirmation back down', async () => {
    const user = show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Saved!')).toBeInTheDocument();
    await vi.waitFor(() => expect(screen.queryByText('Saved!')).toBeNull(), { timeout: 6000 });
  }, 10_000);

  it('says nothing was changed when the write is refused', async () => {
    api.updateOrgInfo.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Could not save. Nothing was changed.')).toBeInTheDocument();
    expect(screen.queryByText('Saved!')).not.toBeInTheDocument();
  });

  it('re-enables the button after a refusal', async () => {
    api.updateOrgInfo.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(field()).toHaveValue('90'));

    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
  });
});

describe('when the settings cannot be read', () => {
  it('says so instead of showing "Forever", which is a real setting', async () => {
    api.getOrgInfo.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('Could not load these settings. Reload before changing anything.'))
      .toBeInTheDocument();
  });

  it('shows a placeholder, and no form to save from, while loading', () => {
    api.getOrgInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByLabelText('Retention period')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });
});
