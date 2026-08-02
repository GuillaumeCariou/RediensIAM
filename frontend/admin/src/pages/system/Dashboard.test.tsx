import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import SystemDashboard from './Dashboard';
import SystemMetrics from './Metrics';
import { fmtDate } from '@/lib/utils';

/**
 * Both pages read the same /admin/metrics payload. The thing worth pinning is that neither
 * invents a number: the activity chart used to draw a sine wave under a heading claiming it was
 * the last 24 hours of sign-ins, and a counter that shows 0 for "not loaded yet" reads as a
 * platform with no users rather than as a page that has not finished.
 */

const api = vi.hoisted(() => ({ getMetrics: vi.fn() }));
vi.mock('@/api', () => api);

const METRICS = {
  organisations: 12, active_organisations: 9,
  total_users: 4310, active_users: 3900,
  projects: 41, service_accounts: 7,
  recent_logins: 128, audit_events_today: 512,
  uptime_since: '2026-03-04T05:06:07Z',
  logins_by_hour: [{ hour: '09:00', succeeded: 10, failed: 1 }],
  users_by_org: [{ org: 'Acme', count: 300 }, { org: 'Globex', count: 150 }],
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getMetrics.mockResolvedValue(METRICS);
});

/** Nothing has resolved yet, so the page is still in its loading state. */
const pending = () => api.getMetrics.mockReturnValue(new Promise(() => {}));

describe.each([
  ['the system dashboard', SystemDashboard],
  ['the metrics page', SystemMetrics],
] as const)('%s', (_name, Page) => {
  it('shows the platform totals once they arrive', async () => {
    render(<Page />);

    expect(await screen.findByText('12')).toBeInTheDocument();
    expect(screen.getByText('9 active')).toBeInTheDocument();
    expect(screen.getByText('4310')).toBeInTheDocument();
    expect(screen.getByText('3900 active')).toBeInTheDocument();
    expect(screen.getByText('41')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
  });

  it('shows an em dash rather than a zero while it is still loading', async () => {
    pending();
    render(<Page />);

    expect(await screen.findAllByText('—')).toHaveLength(6);
    expect(screen.queryByText(/active$/)).not.toBeInTheDocument();
  });

  it('falls back to zero for a field the payload omits', async () => {
    api.getMetrics.mockResolvedValue({});
    render(<Page />);

    expect(await screen.findAllByText('0')).not.toHaveLength(0);
  });

  it('survives a metrics endpoint that fails, without a blank page', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getMetrics.mockRejectedValue(new Error('500'));
    render(<Page />);

    // The load finished, so the counters leave the dash behind and read zero.
    expect(await screen.findAllByText('0')).not.toHaveLength(0);
    vi.restoreAllMocks();
  });

  it('draws only the sign-ins the server reported', async () => {
    render(<Page />);

    expect(await screen.findByTitle('09:00: 10 succeeded, 1 failed')).toBeInTheDocument();
  });

  it('says so when the window holds no sign-ins, rather than drawing something', async () => {
    api.getMetrics.mockResolvedValue({ ...METRICS, logins_by_hour: [] });
    render(<Page />);

    expect(await screen.findByText('No sign-ins recorded in this window')).toBeInTheDocument();
  });

  it("shows today's counters", async () => {
    render(<Page />);

    expect(await screen.findByText('Logins')).toBeInTheDocument();
    expect(screen.getByText('Audit events')).toBeInTheDocument();
    expect(screen.getByText('512')).toBeInTheDocument();
  });
});

describe('the system dashboard only', () => {
  it('reports the platform as operational, and since when', async () => {
    render(<SystemDashboard />);

    expect(await screen.findByText('Operational')).toBeInTheDocument();
    expect(screen.getByText(`Since ${fmtDate(METRICS.uptime_since)}`)).toBeInTheDocument();
  });

  it('claims nothing about uptime the server did not report', async () => {
    api.getMetrics.mockResolvedValue({ ...METRICS, uptime_since: undefined });
    render(<SystemDashboard />);

    await screen.findByText('12');
    expect(screen.queryByText('Operational')).not.toBeInTheDocument();
  });
});

describe('the metrics page only', () => {
  it('breaks the user count down by organisation', async () => {
    render(<SystemMetrics />);

    expect(await screen.findByText('Acme')).toBeInTheDocument();
    expect(screen.getByText('Globex')).toBeInTheDocument();
    expect(screen.getByText('300')).toBeInTheDocument();
  });

  it('scales each bar against the largest organisation, not the total', async () => {
    const { container } = render(<SystemMetrics />);
    await screen.findByText('Acme');

    const widths = [...container.querySelectorAll<HTMLElement>('[style*="--ia-accent"]')]
      .map(e => e.style.width).filter(w => w.endsWith('%'));
    expect(widths).toEqual(['100%', '50%']);
  });

  it('draws no bar rather than a NaN width when every organisation is empty', async () => {
    api.getMetrics.mockResolvedValue({ ...METRICS, users_by_org: [{ org: 'Acme', count: 0 }] });
    const { container } = render(<SystemMetrics />);
    await screen.findByText('Acme');

    const widths = [...container.querySelectorAll<HTMLElement>('[style*="--ia-accent"]')]
      .map(e => e.style.width).filter(w => w.endsWith('%'));
    expect(widths).toEqual(['0%']);
  });

  it('leaves the breakdown out when the server sends none', async () => {
    api.getMetrics.mockResolvedValue({ ...METRICS, users_by_org: [] });
    render(<SystemMetrics />);

    await screen.findByText('12');
    expect(screen.queryByText('Users per organisation')).not.toBeInTheDocument();
  });
});
