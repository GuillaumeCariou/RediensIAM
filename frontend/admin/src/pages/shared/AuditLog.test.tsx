import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import AuditLog from '@/pages/shared/AuditLog';

/**
 * One page over two levels, and the difference between them is the route each reads. `/admin/audit-log` is super-admin-only and binds nothing but limit/offset: passing
 * `org_id` to it filtered nothing, so an org admin got a 403 and a super admin browsing one
 * organisation saw every tenant's entries under a heading that said otherwise. The org-scoped
 * page has to ask for `scope: 'org'`, which is a different controller action.
 */

const api = vi.hoisted(() => ({
  getAuditLog: vi.fn(), exportSystemAuditLog: vi.fn(), exportOrgAuditLog: vi.fn(),
  // Imported by the chain-integrity button in the header; the factory replaces the module, so it
  // has to be here even though this file never clicks it. Its own suite is AuditChainCheck.test.
  verifyAuditChain: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const entry = (i: number) => ({
  id: `e${i}`, action: 'user_deleted', actor_id: 'a', target_type: 'user', target_id: 't',
  ip_address: '203.0.113.7', created_at: '2026-03-04T05:06:07Z',
});
const PAGE = Array.from({ length: 50 }, (_, i) => entry(i));

const BLOB = new Blob(['id,action\n']);
let click: ReturnType<typeof vi.spyOn>;
let createObjectURL: ReturnType<typeof vi.spyOn>;
let revokeObjectURL: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.getAuditLog.mockResolvedValue({ entries: [entry(0)] });
  api.exportSystemAuditLog.mockResolvedValue(BLOB);
  api.exportOrgAuditLog.mockResolvedValue(BLOB);
  // A real <a>.click() would navigate the test runner out of the page.
  click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
  createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
  revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});

function showSystem() {
  const user = userEvent.setup();
  render(<MemoryRouter initialEntries={['/system/audit-log']}><AuditLog level="deployment" /></MemoryRouter>);
  return user;
}

function showOrg(path = '/org/audit-log', pattern = '/org/audit-log') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<AuditLog level="org" />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

describe('which log each page reads', () => {
  it('the system page asks for the whole platform, with no org filter', async () => {
    showSystem();

    await vi.waitFor(() => expect(api.getAuditLog).toHaveBeenCalledWith({ limit: 50, offset: 0 }));
  });

  it('the org page asks for the org-scoped route', async () => {
    showOrg();

    await vi.waitFor(() => expect(api.getAuditLog)
      .toHaveBeenCalledWith({ scope: 'org', org_id: 'o1', limit: 50, offset: 0 }));
  });

  it('the org page asks for the system route when a super admin is browsing a tenant', async () => {
    // Same page, different scope: there the caller has the rights to read the system log filtered
    // by org, and the org-scoped route would answer about the super admin's own organisation.
    showOrg('/system/organisations/o9/audit-log', '/system/organisations/:id/audit-log');

    await vi.waitFor(() => expect(api.getAuditLog)
      .toHaveBeenCalledWith({ scope: 'system', org_id: 'o9', limit: 50, offset: 0 }));
  });
});

describe.each([
  ['the system page', showSystem],
  ['the org page', () => showOrg()],
] as const)('%s', (name, show) => {
  it('shows the entries it was given', async () => {
    show();
    expect(await screen.findByText('user_deleted')).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.getAuditLog.mockResolvedValue([entry(0)]);
    show();

    expect(await screen.findByText('user_deleted')).toBeInTheDocument();
  });

  it('treats a payload it cannot read as an empty page, not a crash', async () => {
    api.getAuditLog.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No audit events found')).toBeInTheDocument();
  });

  it('finishes loading when the request fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getAuditLog.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No audit events found')).toBeInTheDocument();
  });

  it('offers a next page only when the page came back full', async () => {
    // A short page is the last one; offering Next there pages into nothing.
    show();
    await screen.findByText('user_deleted');
    expect(screen.getByRole('button', { name: /Next/ })).toBeDisabled();
  });

  it('pages forward and back, asking for the right offsets', async () => {
    api.getAuditLog.mockResolvedValue({ entries: PAGE });
    const user = show();
    await screen.findAllByText('user_deleted');

    await user.click(screen.getByRole('button', { name: /Next/ }));
    await vi.waitFor(() => expect(api.getAuditLog).toHaveBeenLastCalledWith(
      expect.objectContaining({ offset: 50 })));
    expect(await screen.findByText('Showing 51–100')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Previous/ }));
    await vi.waitFor(() => expect(api.getAuditLog).toHaveBeenLastCalledWith(
      expect.objectContaining({ offset: 0 })));
  });

  it('downloads the CSV under a dated filename and releases the object URL', async () => {
    const user = show();
    await screen.findByText('user_deleted');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(createObjectURL).toHaveBeenCalledWith(BLOB);
    // Left unrevoked, every export leaks the whole log for as long as the tab lives.
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:fake');
  });

  it('KNOWN GAP: a failed export says nothing, it only re-enables the button', async () => {
    // handleExport used to have a `finally` and no `catch`: the button re-enabled itself, the
    // operator could not tell a refusal from a browser that blocked the download, and the rejection
    // escaped as an unhandled promise. Both pages now name the failure, and this asserts it.
    const failing = name === 'the system page' ? api.exportSystemAuditLog : api.exportOrgAuditLog;
    failing.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('user_deleted');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Export CSV' })).toBeEnabled());
    expect(click).not.toHaveBeenCalled();
    expect(await screen.findByText('Could not export the audit log. Nothing was downloaded.')).toBeInTheDocument();
  });
});

describe('the export route', () => {
  it('is the org-scoped one for an org admin', async () => {
    const user = showOrg();
    await screen.findByText('user_deleted');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(api.exportOrgAuditLog).toHaveBeenCalledWith('o1', false));
  });

  it('is the system-scoped one when a super admin is browsing a tenant', async () => {
    const user = showOrg('/system/organisations/o9/audit-log', '/system/organisations/:id/audit-log');
    await screen.findByText('user_deleted');

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await vi.waitFor(() => expect(api.exportOrgAuditLog).toHaveBeenCalledWith('o9', true));
  });
});

/**
 * Was "the system page only". The colours now come from the shared page, which is the point of
 * merging: a destructive action must not change colour with who is reading it.
 */
describe('action severity', () => {
  it('tones the actions that matter by severity', async () => {
    api.getAuditLog.mockResolvedValue({
      entries: [entry(0), { ...entry(1), action: 'login' }, { ...entry(2), action: 'org_suspended' }],
    });
    showSystem();

    await screen.findByText('login');
    expect(screen.getByText('user_deleted').className).toContain('danger');
    expect(screen.getByText('login').className).toContain('success');
    expect(screen.getByText('org_suspended').className).toContain('warn');
  });
});
