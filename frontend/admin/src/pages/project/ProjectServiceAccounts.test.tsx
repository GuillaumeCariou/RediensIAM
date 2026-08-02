import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import ProjectServiceAccounts from './ProjectServiceAccounts';
import { fmtDate } from '@/lib/utils';

/**
 * Personal access tokens are shown exactly once, at generation. Everything about that path — the
 * dialog that displays it, the copy button, and the fact that the list never shows a raw token —
 * is the security-relevant part of this page.
 *
 * The other one is that a service account belongs to the project's assigned user list. With no
 * list assigned there is nothing to create the account in, so creation is refused rather than
 * attempted with a null.
 */

const api = vi.hoisted(() => ({
  listServiceAccounts: vi.fn(), createServiceAccount: vi.fn(), deleteServiceAccount: vi.fn(),
  generatePat: vi.fn(), listPats: vi.fn(), revokePat: vi.fn(), getProjectInfo: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const SA = {
  id: 's1', name: 'ci-deploy', description: 'CI pipeline',
  active: true, last_used_at: '2026-03-04T05:06:07Z',
};
const PATS = [
  { id: 't1', name: 'ci-token', expires_at: '2027-01-01T00:00:00Z', last_used_at: null, created_at: '2026-01-02T00:00:00Z' },
  { id: 't2', name: 'old-token', expires_at: null, last_used_at: '2026-02-01T00:00:00Z', created_at: '2026-01-02T00:00:00Z' },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.listServiceAccounts.mockResolvedValue([SA]);
  api.getProjectInfo.mockResolvedValue({ assigned_user_list_id: 'l1' });
  api.listPats.mockResolvedValue({ pats: PATS });
  api.generatePat.mockResolvedValue({ token: 'riam_pat_secret_value' });
});

function show() {
  const user = userEvent.setup();
  render(<MemoryRouter><ProjectServiceAccounts /></MemoryRouter>);
  return user;
}

const openMenu = (user: Awaited<ReturnType<typeof show>>) =>
  user.click([...screen.getByRole('row', { name: /ci-deploy/ }).querySelectorAll('button')].at(-1)!);

describe('the table', () => {
  it('lists the accounts with their status and last use', async () => {
    show();

    expect(await screen.findByText('ci-deploy')).toBeInTheDocument();
    expect(screen.getByText('CI pipeline')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText(fmtDate(SA.last_used_at))).toBeInTheDocument();
  });

  it('marks an inactive account', async () => {
    api.listServiceAccounts.mockResolvedValue([{ ...SA, active: false, description: null }]);
    show();

    expect(await screen.findByText('Inactive')).toBeInTheDocument();
  });

  it('shows placeholder rows while loading', () => {
    api.listServiceAccounts.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('tbody tr')).toHaveLength(3);
  });

  it('treats a null body as no accounts', async () => {
    api.listServiceAccounts.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listServiceAccounts.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('asks for nothing when no project is in scope', async () => {
    auth.projectId = '';
    show();

    expect(await screen.findByText('No service accounts')).toBeInTheDocument();
    expect(api.listServiceAccounts).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /New Service Account/ })).not.toBeInTheDocument();
  });
});

describe('a project with no user list assigned', () => {
  beforeEach(() => {
    api.getProjectInfo.mockResolvedValue({ assigned_user_list_id: null });
    api.listServiceAccounts.mockResolvedValue([]);
  });

  it('says what has to happen first rather than offering an impossible create', async () => {
    show();

    expect(await screen.findByText('Assign a user list to this project first.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /New Service Account/ })).toBeDisabled();
  });
});

describe('creating an account', () => {
  const openForm = async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await user.click(screen.getByRole('button', { name: /New Service Account/ }));
    return user;
  };

  it('creates it against the project\'s assigned list', async () => {
    const user = await openForm();

    await user.fill(screen.getByLabelText('Name'), 'reporting-bot');
    await user.fill(screen.getByLabelText('Description (optional)'), 'nightly');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith({
      name: 'reporting-bot', description: 'nightly', user_list_id: 'l1',
    }));
    expect(api.listServiceAccounts).toHaveBeenCalledTimes(2);
  });

  it('sends no description rather than an empty one', async () => {
    const user = await openForm();

    await user.fill(screen.getByLabelText('Name'), 'reporting-bot');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(api.createServiceAccount).toHaveBeenCalledWith(
      expect.objectContaining({ description: undefined })));
  });

  it('requires a name', async () => {
    const user = await openForm();
    expect(screen.getByLabelText('Name')).toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });
});

describe('the tokens of an account', () => {
  const openPats = async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View PATs' }));
    await screen.findByText('ci-token');
    return user;
  };

  it('lists them with their expiry and last use, and no token value', async () => {
    await openPats();

    expect(screen.getByText(`Expires: ${fmtDate('2027-01-01T00:00:00Z')} · Last used: —`)).toBeInTheDocument();
    // A raw token exists only in the generation dialog; the list has hashes on the server.
    expect(document.body.textContent).not.toContain('riam_pat');
  });

  it('shows an em dash for a token that never expires and one never used', async () => {
    await openPats();
    expect(screen.getByText(`Expires: — · Last used: ${fmtDate('2026-02-01T00:00:00Z')}`)).toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listPats.mockResolvedValue(PATS);
    await openPats();

    expect(screen.getByText('ci-token')).toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.listPats.mockResolvedValue({ pats: [] });
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'View PATs' }));

    expect(await screen.findByText('No PATs yet.')).toBeInTheDocument();
  });

  it('revokes one and drops it from the list', async () => {
    const user = await openPats();

    await user.click(screen.getAllByRole('button', { name: 'Revoke' })[0]);

    await vi.waitFor(() => expect(api.revokePat).toHaveBeenCalledWith('s1', 't1'));
    await vi.waitFor(() => expect(screen.queryByText('ci-token')).toBeNull());
    expect(screen.getByText('old-token')).toBeInTheDocument();
  });
});

describe('generating a token', () => {
  const openGen = async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));
    await user.fill(screen.getByLabelText('Token Name'), 'ci-pipeline-token');
    return user;
  };

  it('shows the raw token once, and warns that it is once', async () => {
    const user = await openGen();

    await user.click(screen.getByRole('button', { name: 'Generate' }));

    expect(await screen.findByText('riam_pat_secret_value')).toBeInTheDocument();
    expect(screen.getByText('Copy this token now — it will not be shown again.')).toBeInTheDocument();
  });

  it('sends no expiry rather than an empty string when none was set', async () => {
    const user = await openGen();

    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await vi.waitFor(() => expect(api.generatePat)
      .toHaveBeenCalledWith('s1', { name: 'ci-pipeline-token', expires_at: undefined }));
  });

  it('sends the expiry when one was set', async () => {
    const user = await openGen();

    await user.fill(screen.getByLabelText('Expires At (optional)'), '2027-06-01T12:00');
    await user.click(screen.getByRole('button', { name: 'Generate' }));

    await vi.waitFor(() => expect(api.generatePat)
      .toHaveBeenCalledWith('s1', { name: 'ci-pipeline-token', expires_at: '2027-06-01T12:00' }));
  });

  it('copies the token to the clipboard and says it did', async () => {
    const writeText = vi.fn(async () => {});
    vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
    const user = await openGen();
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByText('riam_pat_secret_value');

    // The copy button lives in the new-token dialog; the row menus use the same class.
    await user.click([...document.querySelectorAll<HTMLButtonElement>('.iam-btn-icon')].at(-1)!);

    expect(writeText).toHaveBeenCalledWith('riam_pat_secret_value');
    vi.restoreAllMocks();
  });

  it('clears the name so the next token does not inherit it', async () => {
    const user = await openGen();
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByText('riam_pat_secret_value');

    await user.click(screen.getByRole('button', { name: 'Done' }));
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));

    expect(screen.getByLabelText('Token Name')).toHaveValue('');
  });

  it('can be reached from the token list, which it closes behind itself', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View PATs' }));
    await screen.findByText('ci-token');

    await user.click(screen.getByRole('button', { name: /Generate PAT/ }));

    expect(screen.getByLabelText('Token Name')).toBeInTheDocument();
    expect(screen.queryByText('ci-token')).not.toBeInTheDocument();
  });

  it('requires a name', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));

    expect(screen.getByLabelText('Token Name')).toBeRequired();
  });
});

describe('deleting an account', () => {
  it('warns that its tokens go with it, and asks first', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText('Delete ci-deploy?')).toBeInTheDocument();
    expect(screen.getByText(/PATs for this service account will also be revoked/)).toBeInTheDocument();
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });

  it('deletes once confirmed, and reloads', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await user.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);

    await vi.waitFor(() => expect(api.deleteServiceAccount).toHaveBeenCalledWith('s1'));
    expect(api.listServiceAccounts).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText('Delete ci-deploy?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });
});

describe('the row menu', () => {
  it('closes on Escape', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('button', { name: 'View PATs' })).not.toBeInTheDocument();
  });

  it('closes when the operator clicks away', async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    await openMenu(user);

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'View PATs' })).not.toBeInTheDocument();
  });
});


describe('dismissing a dialog with Escape', () => {
  const open = async () => {
    const user = show();
    await screen.findByText('ci-deploy');
    return user;
  };

  it('closes the create form', async () => {
    const user = await open();
    await user.click(screen.getByRole('button', { name: /New Service Account/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
    expect(api.createServiceAccount).not.toHaveBeenCalled();
  });

  it('closes the token list', async () => {
    const user = await open();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View PATs' }));
    await screen.findByText('ci-token');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('ci-token')).toBeNull());
  });

  it('closes the token list from its own button', async () => {
    const user = await open();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'View PATs' }));
    await screen.findByText('ci-token');

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(screen.queryByText('ci-token')).not.toBeInTheDocument();
  });

  it('closes the generate form, from Escape and from Cancel', async () => {
    const user = await open();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText('Token Name')).toBeNull());

    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Token Name')).not.toBeInTheDocument();
    expect(api.generatePat).not.toHaveBeenCalled();
  });

  it('closes the new-token dialog, which is the only sight of the token', async () => {
    const user = await open();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Generate PAT' }));
    await user.fill(screen.getByLabelText('Token Name'), 'ci-pipeline-token');
    await user.click(screen.getByRole('button', { name: 'Generate' }));
    await screen.findByText('riam_pat_secret_value');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('riam_pat_secret_value')).toBeNull());
  });

  it('closes the delete confirmation without deleting', async () => {
    const user = await open();
    await openMenu(user);
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText('Delete ci-deploy?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete ci-deploy?')).toBeNull());
    expect(api.deleteServiceAccount).not.toHaveBeenCalled();
  });
});
