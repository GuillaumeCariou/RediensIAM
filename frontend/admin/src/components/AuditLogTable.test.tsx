import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import AuditLogTable, { type AuditEntry } from './AuditLogTable';
import { fmtDate } from '@/lib/utils';

const ENTRY: AuditEntry = {
  id: 'e1',
  action: 'user.deleted',
  actor_id: '0123456789abcdef',
  target_type: 'user',
  target_id: 'fedcba9876543210',
  ip_address: '203.0.113.7',
  created_at: '2026-03-04T05:06:07Z',
};

const handlers = () => ({ onPrev: vi.fn(), onNext: vi.fn(), onExport: vi.fn() });

function show(props: Partial<React.ComponentProps<typeof AuditLogTable>> = {}) {
  const h = handlers();
  render(
    <AuditLogTable
      entries={[ENTRY]} loading={false} offset={0} hasMore={false} exporting={false} {...h} {...props}
    />,
  );
  return h;
}

describe('the rows', () => {
  it('shows the time, action, shortened ids and IP', () => {
    show();

    expect(screen.getByText(fmtDate(ENTRY.created_at))).toBeInTheDocument();
    expect(screen.getByText('user.deleted')).toBeInTheDocument();
    // Full UUIDs make every column unreadable; eight characters still identify the row.
    expect(screen.getByText('01234567…')).toBeInTheDocument();
    expect(screen.getByText('fedcba98…')).toBeInTheDocument();
    expect(screen.getByText('203.0.113.7')).toBeInTheDocument();
  });

  it('puts an em dash where an entry has no actor or no IP', () => {
    show({ entries: [{ ...ENTRY, actor_id: null, target_type: null, target_id: null, ip_address: null }] });
    expect(screen.getAllByText('—')).toHaveLength(2);
  });

  it('tones an action the caller has classified, and leaves the rest neutral', () => {
    show({
      entries: [ENTRY, { ...ENTRY, id: 'e2', action: 'user.viewed' }],
      actionColors: { 'user.deleted': 'destructive' },
    });

    expect(screen.getByText('user.deleted').className).toContain('danger');
    expect(screen.getByText('user.viewed').className).not.toContain('danger');
  });

  it('shows placeholder rows while loading rather than an empty table', () => {
    const { container } = render(
      <AuditLogTable entries={[]} loading offset={0} hasMore={false} exporting={false} {...handlers()} />,
    );

    expect(container.querySelectorAll('.iam-skeleton')).toHaveLength(40);
    expect(screen.queryByText('No audit events found')).not.toBeInTheDocument();
  });

  it('says the log is empty when it is, and not while it is still loading', () => {
    show({ entries: [] });
    expect(screen.getByText('No audit events found')).toBeInTheDocument();
  });
});

describe('paging', () => {
  it('counts from one, not from the zero-based offset', () => {
    show({ offset: 50, entries: [ENTRY, { ...ENTRY, id: 'e2' }] });
    expect(screen.getByText('Showing 51–52')).toBeInTheDocument();
  });

  it('says so instead when the page is empty', () => {
    show({ entries: [] });
    expect(screen.getByText('No results')).toBeInTheDocument();
  });

  it('cannot go back from the first page, nor forward past the last', () => {
    show();
    expect(screen.getByRole('button', { name: /Previous/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Next/ })).toBeDisabled();
  });

  it('disables both while a page is loading, so a double click cannot skip one', () => {
    show({ loading: true, offset: 50, hasMore: true });
    expect(screen.getByRole('button', { name: /Previous/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Next/ })).toBeDisabled();
  });

  it('asks for the next and previous page', async () => {
    const user = userEvent.setup();
    const h = show({ offset: 50, hasMore: true });

    await user.click(screen.getByRole('button', { name: /Previous/ }));
    await user.click(screen.getByRole('button', { name: /Next/ }));

    expect(h.onPrev).toHaveBeenCalledOnce();
    expect(h.onNext).toHaveBeenCalledOnce();
  });
});

describe('export', () => {
  it('asks for the CSV', async () => {
    const user = userEvent.setup();
    const h = show();

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    expect(h.onExport).toHaveBeenCalledOnce();
  });

  it('says it is working and refuses a second click while it is', () => {
    show({ exporting: true });
    expect(screen.getByRole('button', { name: 'Exporting…' })).toBeDisabled();
  });
});
