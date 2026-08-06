import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import ServiceAccounts from './ServiceAccounts';

/**
 * One page, three levels. What each level owes the page is a single question — *which accounts
 * belong here* — and the answers are the subject of the first block below, because getting one
 * wrong shows another tenant's automation identities with a delete button beside each. That is not
 * hypothetical: it is what the project page did until 0.6.1.
 *
 * `/service-accounts` answers for the **caller**, never for a place. Every test here therefore
 * hands the API a deliberately over-broad answer — accounts from three levels at once — and
 * asserts what survives.
 */

const api = vi.hoisted(() => ({
  listServiceAccounts: vi.fn(), createServiceAccount: vi.fn(), deleteServiceAccount: vi.fn(),
  listUserLists: vi.fn(), getProjectInfo: vi.fn(),
  listPats: vi.fn(), generatePat: vi.fn(), revokePat: vi.fn(),
}));
vi.mock('@/api', () => api);

const ctx = vi.hoisted(() => ({ orgId: 'o1', projectId: 'p1' }));
vi.mock('@/hooks/useOrgContext', () => ({
  useOrgContext: () => ({ orgId: ctx.orgId, isSystemCtx: false, orgBase: '/org', userListBase: '/org/userlists', projectUrl: () => '' }),
  useProjectContext: () => ({ projectId: ctx.projectId, isSystemCtx: false, projectBase: '/project' }),
}));

const sa = (over: Partial<Record<string, unknown>> = {}) => ({
  id: 's1', name: 'ci-deploy', description: null, active: true,
  last_used_at: null, created_at: '2026-01-02T00:00:00Z',
  user_list_id: 'l-project', org_id: 'o1', is_system: false, ...over,
});

/** One account per level, all returned at once — the shape the endpoint really answers with. */
const ALL = [
  sa({ id: 'sys', name: 'deployment-bot', org_id: null, is_system: true, user_list_id: 'l-system' }),
  sa({ id: 'org', name: 'org-bot', user_list_id: 'l-org' }),
  sa({ id: 'prj', name: 'project-bot', user_list_id: 'l-project' }),
  sa({ id: 'other', name: 'other-tenant-bot', org_id: 'o2', user_list_id: 'l-other' }),
];

beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(ctx, { orgId: 'o1', projectId: 'p1' });
  api.listServiceAccounts.mockResolvedValue(ALL);
  api.listUserLists.mockResolvedValue([
    { id: 'l-system', org_id: null, immovable: true, name: 'System' },
    { id: 'l-org', org_id: 'o1', immovable: false, name: 'Staff' },
  ]);
  api.getProjectInfo.mockResolvedValue({ assigned_user_list_id: 'l-project' });
  api.listPats.mockResolvedValue({ pats: [] });
  api.generatePat.mockResolvedValue({ token: 'rediens_pat_shown_once' });
});

function show(level: 'deployment' | 'org' | 'project') {
  const user = userEvent.setup();
  render(<MemoryRouter><ServiceAccounts level={level} /></MemoryRouter>);
  return user;
}

const row = (name: string) => screen.queryByText(name);

describe('which accounts belong here', () => {
  it('shows the deployment only its own', async () => {
    show('deployment');

    expect(await screen.findByText('deployment-bot')).toBeInTheDocument();
    expect(row('org-bot')).not.toBeInTheDocument();
    expect(row('project-bot')).not.toBeInTheDocument();
  });

  it('shows an organisation its own and its projects\', and never another tenant\'s', async () => {
    show('org');

    expect(await screen.findByText('org-bot')).toBeInTheDocument();
    expect(row('project-bot')).toBeInTheDocument();
    expect(row('other-tenant-bot')).not.toBeInTheDocument();
    expect(row('deployment-bot')).not.toBeInTheDocument();
  });

  it('shows a project only the accounts on its assigned list', async () => {
    show('project');

    expect(await screen.findByText('project-bot')).toBeInTheDocument();
    expect(row('org-bot')).not.toBeInTheDocument();
    expect(row('deployment-bot')).not.toBeInTheDocument();
  });

  /**
   * With no list assigned, nothing can belong to the project yet. Falling back to "show what the
   * caller can see" is how the 0.6.1 defect worked, so the empty answer is asserted rather than
   * assumed.
   */
  it('shows a project with no assigned list nothing at all', async () => {
    api.getProjectInfo.mockResolvedValue({ assigned_user_list_id: null });
    show('project');

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
    expect(row('project-bot')).not.toBeInTheDocument();
  });
});

describe('creating one', () => {
  it('puts a deployment account on the system list, without asking', async () => {
    const user = show('deployment');
    await user.click(await screen.findByRole('button', { name: /New service account/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-bot');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'new-bot', user_list_id: 'l-system' })));
  });

  /** An organisation has several lists and the choice is real, so it is offered. */
  it('asks an organisation which list', async () => {
    const user = show('org');
    await user.click(await screen.findByRole('button', { name: /New service account/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-bot');
    await user.selectOptions(screen.getByLabelText('User list'), 'l-org');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ user_list_id: 'l-org' })));
  });

  it('puts a project account on the project\'s list, without asking', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: /New service account/ }));
    await user.fill(screen.getByLabelText('Name'), 'new-bot');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ user_list_id: 'l-project' })));
    expect(screen.queryByLabelText('User list')).not.toBeInTheDocument();
  });

  /**
   * Nowhere to put it means no button. A control that is present and fails is worse than an absent
   * one: it reads as a feature the operator is doing wrong.
   */
  it('offers no creation where there is no list to create on', async () => {
    api.getProjectInfo.mockResolvedValue({ assigned_user_list_id: null });
    show('project');

    await screen.findByText('No service accounts');
    expect(screen.queryByRole('button', { name: /New service account/ })).not.toBeInTheDocument();
  });
});

describe('tokens', () => {
  /**
   * Token management existed on the project page alone. It is a property of an account, not of the
   * level you happen to be looking from, and its absence elsewhere was drift rather than design.
   */
  it.each(['deployment', 'org', 'project'] as const)('are reachable at %s level', async level => {
    const user = show(level);
    // The org level lists more than one account, so the first row's button is named explicitly
    // rather than assumed unique.
    const buttons = await screen.findAllByRole('button', { name: 'Tokens' });
    await user.click(buttons[0]);

    expect(await screen.findByText(/^Tokens ·/)).toBeInTheDocument();
    expect(api.listPats).toHaveBeenCalled();
  });

  it('shows a new token once, and says so', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByRole('button', { name: 'Generate token' }));
    await user.fill(screen.getByLabelText('Name'), 'ci');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    expect(await screen.findByText('rediens_pat_shown_once')).toBeInTheDocument();
    expect(screen.getByText(/not shown again/)).toBeInTheDocument();
  });

  it('revokes one', async () => {
    api.listPats.mockResolvedValue({ pats: [{ id: 't1', name: 'ci', expires_at: null, last_used_at: null, created_at: '2026-01-01' }] });
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByRole('button', { name: 'Revoke' }));

    await waitFor(() => expect(api.revokePat).toHaveBeenCalledWith('prj', 't1'));
    expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument();
  });
});

describe('deleting one', () => {
  it('asks first, and says what else goes', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/Delete project-bot\?/)).toBeInTheDocument();
    expect(screen.getByText(/tokens for this service account will also be revoked/)).toBeInTheDocument();
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('deletes on confirmation', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    // Two buttons read "Delete" once the dialog is open — the row's and the confirmation's. The
    // one that matters is inside the dialog.
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(api.deleteServiceAccount).toHaveBeenCalledWith('prj'));
  });
});

/**
 * Ported from the three pages this one replaced.
 *
 * The consolidation traded 57 tests over three copies for one file, and these are the cases that
 * were not merely the same assertion three times: what the page does with a broken answer, an
 * empty field, a cancelled dialog. Dropping them would have been the consolidation quietly paying
 * for itself with coverage.
 */
describe('answers the page has to survive', () => {
  it('treats a null body as no accounts', async () => {
    api.listServiceAccounts.mockResolvedValue(null);
    show('org');

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    api.listServiceAccounts.mockRejectedValue(new Error('boom'));
    show('org');

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
  });

  it('asks for nothing when no project is in scope', async () => {
    ctx.projectId = '';
    show('project');

    await waitFor(() => expect(api.getProjectInfo).not.toHaveBeenCalled());
    expect(api.listServiceAccounts).not.toHaveBeenCalled();
  });

  it('accepts a bare array of tokens as well as an envelope', async () => {
    api.listPats.mockResolvedValue([{ id: 't1', name: 'bare', expires_at: null, last_used_at: null, created_at: '2026-01-01' }]);
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));

    expect(await screen.findByText('bare')).toBeInTheDocument();
  });

  /** A token that never expires and was never used has two facts to state, not two blanks. */
  it('writes an em dash for a token with no expiry and no use', async () => {
    api.listPats.mockResolvedValue({ pats: [{ id: 't1', name: 'ci', expires_at: null, last_used_at: null, created_at: '2026-01-01' }] });
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getAllByText('—')).toHaveLength(2);
  });
});

describe('what the page sends', () => {
  it('sends no description rather than an empty one', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: /New service account/ }));
    await user.fill(screen.getByLabelText('Name'), 'bot');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ description: undefined })));
  });

  it('sends no expiry rather than an empty string', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByRole('button', { name: 'Generate token' }));
    await user.fill(screen.getByLabelText('Name'), 'ci');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await waitFor(() => expect(api.generatePat).toHaveBeenCalledWith('prj',
      expect.objectContaining({ expires_at: undefined })));
  });

  it('sends the expiry when one was set', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByRole('button', { name: 'Generate token' }));
    await user.fill(screen.getByLabelText('Name'), 'ci');
    await user.fill(screen.getByLabelText(/Expires/), '2027-01-01');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await waitFor(() => expect(api.generatePat).toHaveBeenCalledWith('prj',
      expect.objectContaining({ expires_at: '2027-01-01' })));
  });
});

describe('the issued token', () => {
  async function issueOne() {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByRole('button', { name: 'Generate token' }));
    await user.fill(screen.getByLabelText('Name'), 'ci');
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByText('rediens_pat_shown_once');
    return user;
  }

  it('copies to the clipboard and says it did', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    const user = await issueOne();
    await user.click(screen.getByRole('button', { name: 'Copy' }));

    expect(writeText).toHaveBeenCalledWith('rediens_pat_shown_once');
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeInTheDocument();
  });

  /** The next token must not inherit the previous one's name, nor its "Copied" state. */
  it('clears the form behind itself', async () => {
    const user = await issueOne();
    await user.click(screen.getByRole('button', { name: 'Done' }));
    await user.click(screen.getAllByRole('button', { name: 'Tokens' })[0]);
    await user.click(await screen.findByRole('button', { name: 'Generate token' }));

    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('');
  });
});

describe('cancelling', () => {
  it('deletes nothing on cancel', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('creates nothing on cancel', async () => {
    const user = show('project');
    await user.click(await screen.findByRole('button', { name: /New service account/ }));
    await user.fill(screen.getByLabelText('Name'), 'bot');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });
});
