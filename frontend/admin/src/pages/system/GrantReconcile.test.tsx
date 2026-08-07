import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import GrantReconcile from './GrantReconcile';
import { ApiError } from '@/auth';

/**
 * This page is a diagnostic, so the assertions are about what an operator can read before deciding:
 * every divergent grant in full, and which store is missing it. A count would not be enough to
 * choose between repairing and investigating.
 *
 * The other half is the refusal, which arrives as a 200 with `repair_refused` set. A page that
 * only watched for a rejection would announce a successful repair of nothing.
 */

const api = vi.hoisted(() => ({ scanGrantReconcile: vi.fn(), repairGrantReconcile: vi.fn() }));
vi.mock('@/api', () => api);

const TUPLE = { namespace: 'Organisations', object: 'org-1', relation: 'org_admin', subject: 'user:u1' };
const ROW = { namespace: 'Projects', object: 'proj-9', relation: 'role:editor', subject: 'user:u2' };

const report = (over: Record<string, unknown> = {}) => ({
  orphan_tuples: [TUPLE], orphan_rows: [ROW],
  tuples_revoked: 0, rows_removed: 0, repair_refused: null,
  ...over,
});

const CLEAN = report({ orphan_tuples: [], orphan_rows: [] });

beforeEach(() => {
  vi.clearAllMocks();
  api.scanGrantReconcile.mockResolvedValue(report());
  api.repairGrantReconcile.mockResolvedValue(report({ tuples_revoked: 1, rows_removed: 1 }));
});

function show() {
  const user = userEvent.setup();
  render(<GrantReconcile />);
  return user;
}

const repairButton = () => screen.getAllByRole('button', { name: 'Repair' })[0];

describe('what the scan shows', () => {
  it('claims nothing while it is still reading the two stores', () => {
    api.scanGrantReconcile.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByText('Nothing to reconcile')).not.toBeInTheDocument();
    expect(repairButton()).toBeDisabled();
  });

  it('names the subject, object and relation of each divergence', async () => {
    show();

    expect(await screen.findByText('user:u1')).toBeInTheDocument();
    expect(screen.getByText('org_admin')).toBeInTheDocument();
    expect(screen.getByText('org-1')).toBeInTheDocument();
    expect(screen.getByText('user:u2')).toBeInTheDocument();
    expect(screen.getByText('role:editor')).toBeInTheDocument();
  });

  it('says which store each grant is missing from', async () => {
    show();

    expect(await screen.findByText('Keto only')).toBeInTheDocument();
    expect(screen.getByText('Database only')).toBeInTheDocument();
  });

  it('counts the two classes apart, because the repairs are not the same', async () => {
    api.scanGrantReconcile.mockResolvedValue(report({ orphan_tuples: [TUPLE, { ...TUPLE, subject: 'user:u3' }] }));
    show();

    await screen.findByText('In Keto only');
    expect(screen.getByText('In Keto only').parentElement).toHaveTextContent(/^In Keto only2$/);
    expect(screen.getByText('In the database only').parentElement).toHaveTextContent(/^In the database only1$/);
  });

  it('states that agreement is a result, not an empty page', async () => {
    api.scanGrantReconcile.mockResolvedValue(CLEAN);
    show();

    expect(await screen.findByText('Nothing to reconcile')).toBeInTheDocument();
    expect(repairButton()).toBeDisabled();
  });

  it('treats absent lists as agreement rather than crashing on them', async () => {
    api.scanGrantReconcile.mockResolvedValue(report({ orphan_tuples: null, orphan_rows: null }));
    show();

    expect(await screen.findByText('Nothing to reconcile')).toBeInTheDocument();
  });

  it('shows the reason the scan was refused', async () => {
    api.scanGrantReconcile.mockRejectedValue(new ApiError(403, { detail: 'Keto is unreachable' }));
    show();

    expect(await screen.findByText('Keto is unreachable')).toBeInTheDocument();
    expect(screen.queryByText('Nothing to reconcile')).not.toBeInTheDocument();
  });

  it('rescans on demand', async () => {
    const user = show();
    await screen.findByText('user:u1');

    await user.click(screen.getByRole('button', { name: 'Rescan' }));

    await waitFor(() => expect(api.scanGrantReconcile).toHaveBeenCalledTimes(2));
  });
});

describe('repairing', () => {
  it('asks first, and says what will be written to each store', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Repair' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/tuple\(s\) will be deleted from Keto/)).toBeInTheDocument();
    expect(within(dialog).getByText(/row\(s\) will be deleted from the database/)).toBeInTheDocument();
    expect(api.repairGrantReconcile).not.toHaveBeenCalled();
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Repair' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(api.repairGrantReconcile).not.toHaveBeenCalled();
  });

  it('repairs on confirmation, reports what it wrote and rescans', async () => {
    api.scanGrantReconcile.mockResolvedValueOnce(report()).mockResolvedValue(CLEAN);
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Repair' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Repair' }));

    await waitFor(() => expect(api.repairGrantReconcile).toHaveBeenCalledOnce());
    expect(await screen.findByText('Revoked 1 tuple(s) and removed 1 row(s).')).toBeInTheDocument();
    expect(api.scanGrantReconcile).toHaveBeenCalledTimes(2);
  });

  it('reports a refusal that came back with a 200 as a refusal', async () => {
    // The server declines above its bound and writes nothing, in a successful response. Calling
    // that a repair would tell the operator the divergence is gone.
    api.repairGrantReconcile.mockResolvedValue(report({ repair_refused: '412 divergent grants exceeds the 100 bound.' }));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Repair' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Repair' }));

    expect(await screen.findByText('412 divergent grants exceeds the 100 bound.')).toBeInTheDocument();
    expect(screen.queryByText(/^Revoked /)).not.toBeInTheDocument();
  });

  it('shows the reason the repair itself failed', async () => {
    api.repairGrantReconcile.mockRejectedValue(new ApiError(500, { error: 'keto_unavailable' }));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Repair' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Repair' }));

    expect(await within(await screen.findByRole('dialog')).findByText('keto_unavailable')).toBeInTheDocument();
  });
});
