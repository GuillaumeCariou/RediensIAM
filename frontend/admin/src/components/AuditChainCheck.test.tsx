import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import AuditChainCheck from '@/components/AuditChainCheck';
import { ApiError } from '@/auth';

/**
 * The chain is an HMAC whose key is not in the database, so a break means an entry was rewritten
 * or removed — not a flag to invert. What these tests hold in place is that the broken case says
 * *which* chain, *which* entry and how far the walk got, because that is what an operator has to
 * act on, and that a refused verification is never silently read as "clean".
 */

const api = vi.hoisted(() => ({ verifyAuditChain: vi.fn() }));
vi.mock('@/api', () => api);

const chain = (over: Partial<{ org_id: string | null; first_break: number | null; verified: number; unverifiable: number }> = {}) => {
  const c = { org_id: 'o1', first_break: null as number | null, verified: 12, unverifiable: 0, ...over };
  return { ...c, intact: c.first_break === null, fully_verified: c.first_break === null && c.unverifiable === 0 };
};

beforeEach(() => {
  vi.clearAllMocks();
  api.verifyAuditChain.mockResolvedValue({ chains: [chain()], broken: 0 });
});

const show = () => {
  const user = userEvent.setup();
  render(<AuditChainCheck />);
  return user;
};

const verify = async () => {
  const user = show();
  await user.click(screen.getByRole('button', { name: 'Verify integrity' }));
  return user;
};

it('checks nothing until asked', () => {
  show();
  expect(api.verifyAuditChain).not.toHaveBeenCalled();
});

it('says it is working while the walk runs, and does not ask twice', async () => {
  let release: (v: unknown) => void = () => {};
  api.verifyAuditChain.mockReturnValue(new Promise(res => { release = res; }));
  const user = show();

  await user.click(screen.getByRole('button', { name: 'Verify integrity' }));

  const busy = await screen.findByRole('button', { name: 'Verifying…' });
  expect(busy).toBeDisabled();
  release({ chains: [chain()], broken: 0 });
  expect(await screen.findByText(/No broken link in 1 chains/)).toBeInTheDocument();
});

describe('an intact chain', () => {
  it('reports it without claiming more than the walk established', async () => {
    await verify();

    expect(await screen.findByText(/No broken link in 1 chains/)).toBeInTheDocument();
    expect(screen.getByText('Fully verified')).toBeInTheDocument();
    expect(screen.getByText(/height of each chain/)).toBeInTheDocument();
  });

  it('separates "no break" from "vouched for" when rows are unverifiable', async () => {
    // Rows written before the chain was keyed are forgeable by anyone with database write access.
    // Calling that green would be the exact overstatement this panel exists to avoid.
    api.verifyAuditChain.mockResolvedValue({ chains: [chain({ unverifiable: 4 })], broken: 0 });
    await verify();

    expect(await screen.findByText('Intact, partly unverifiable')).toBeInTheDocument();
    expect(screen.queryByText('Fully verified')).not.toBeInTheDocument();
  });
});

describe('a broken chain', () => {
  beforeEach(() => {
    api.verifyAuditChain.mockResolvedValue({
      chains: [chain(), chain({ org_id: 'o2', first_break: 4711, verified: 87 }), chain({ org_id: null })],
      broken: 1,
    });
  });

  it('names the organisation, the entry and how far the walk got', async () => {
    await verify();

    expect(await screen.findByText(/1 of 3 chains break/)).toBeInTheDocument();
    expect(screen.getByText('entry #4711')).toBeInTheDocument();
    expect(screen.getByText('Broken')).toBeInTheDocument();
    expect(screen.getByText('o2')).toBeInTheDocument();
    // 87 is what was walked before the break, not the height of the chain — the note has to say so.
    expect(screen.getByText('87')).toBeInTheDocument();
    expect(screen.getByText(/height walked before the break/)).toBeInTheDocument();
  });

  it('puts the broken chain first, above the ones that hold', async () => {
    await verify();
    await screen.findByText('entry #4711');

    const chains = screen.getAllByRole('row').slice(1).map(r => r.textContent ?? '');
    expect(chains[0]).toContain('o2');
  });

  it('labels the chain that belongs to no organisation', async () => {
    await verify();

    expect(await screen.findByText('Deployment-wide')).toBeInTheDocument();
  });
});

describe('when the API refuses', () => {
  it('shows what it said instead of an empty dialog', async () => {
    api.verifyAuditChain.mockRejectedValue(new ApiError(403, { detail: 'Super admin required.' }));
    await verify();

    expect(await screen.findByText('Super admin required.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('falls back to a sentence of its own when there is no body', async () => {
    api.verifyAuditChain.mockRejectedValue(new Error('network'));
    await verify();

    expect(await screen.findByText('Could not verify the audit chain. Nothing was checked.')).toBeInTheDocument();
  });

  it('re-enables the button so the check can be retried', async () => {
    api.verifyAuditChain.mockRejectedValue(new Error('network'));
    await verify();

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Verify integrity' })).toBeEnabled());
  });
});
