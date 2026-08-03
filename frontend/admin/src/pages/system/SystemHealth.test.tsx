import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import SystemHealth from './SystemHealth';

const api = vi.hoisted(() => ({ getSystemHealth: vi.fn() }));
vi.mock('@/api', () => api);

const check = (over: Partial<Record<string, unknown>> = {}) => ({
  name: 'PostgreSQL', category: 'Storage', status: 'ok',
  latency_ms: 3, detail: null, stats: null, ...over,
});

const HEALTHY = {
  overall: 'ok',
  checks: [
    check(),
    check({ name: 'Redis', latency_ms: 1, stats: { used_memory: '2.1M', connected_clients: '4' } }),
    check({ name: 'Ory Hydra', category: 'Identity', latency_ms: null }),
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getSystemHealth.mockResolvedValue(HEALTHY);
});

const show = () => {
  const user = userEvent.setup();
  render(<SystemHealth />);
  return user;
};

describe('a healthy platform', () => {
  it('says so, once', async () => {
    show();
    expect(await screen.findByText('All systems operational')).toBeInTheDocument();
  });

  it('groups the components by category, each named once', async () => {
    show();

    await screen.findByText('PostgreSQL');
    expect(screen.getByText('Storage')).toBeInTheDocument();
    expect(screen.getByText('Identity')).toBeInTheDocument();
    expect(screen.getAllByText('OK')).toHaveLength(3);
  });

  it('shows the latency it measured, and nothing where it measured none', async () => {
    show();

    await screen.findByText('PostgreSQL');
    expect(screen.getByText('3 ms')).toBeInTheDocument();
    expect(screen.getAllByText(/ ms$/)).toHaveLength(2);
  });

  it('shows a component\'s statistics with their keys made readable', async () => {
    show();

    expect(await screen.findByText('used memory')).toBeInTheDocument();
    expect(screen.getByText('2.1M')).toBeInTheDocument();
  });

  it('says when the check was run', async () => {
    show();
    expect(await screen.findByText(/^Checked /)).toBeInTheDocument();
  });
});

describe('a platform with a fault', () => {
  it('counts the components that are failing', async () => {
    api.getSystemHealth.mockResolvedValue({
      overall: 'error',
      checks: [
        check({ status: 'error', detail: 'connection refused' }),
        check({ name: 'Redis', status: 'error', detail: 'timeout' }),
        check({ name: 'Ory Keto' }),
      ],
    });
    show();

    expect(await screen.findByText('2 component(s) have errors')).toBeInTheDocument();
  });

  it('shows the reason each failing component gave', async () => {
    api.getSystemHealth.mockResolvedValue({
      overall: 'error', checks: [check({ status: 'error', detail: 'connection refused' })],
    });
    show();

    expect(await screen.findByText('connection refused')).toBeInTheDocument();
    expect(screen.getByText('Error')).toBeInTheDocument();
  });

  it('distinguishes a component that was never set up from one that is broken', async () => {
    // "Not configured" is an operator decision; "Error" is an incident. Merging them would page
    // somebody about an SMS provider nobody ever intended to have.
    api.getSystemHealth.mockResolvedValue({
      overall: 'ok', checks: [check({ name: 'Twilio', status: 'not_configured', latency_ms: null })],
    });
    show();

    expect(await screen.findByText('Not configured')).toBeInTheDocument();
    expect(screen.queryByText('Error')).not.toBeInTheDocument();
  });
});

describe('loading and re-running', () => {
  it('shows placeholders, and claims nothing about health, before the first answer', () => {
    api.getSystemHealth.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByText('All systems operational')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeDisabled();
    expect(screen.queryByText(/^Checked /)).not.toBeInTheDocument();
  });

  it('re-runs the checks on demand', async () => {
    const user = show();
    await screen.findByText('All systems operational');

    await user.click(screen.getByRole('button', { name: 'Refresh' }));

    await vi.waitFor(() => expect(api.getSystemHealth).toHaveBeenCalledTimes(2));
  });

  it('survives a health endpoint that is itself down', async () => {
    // The one request that must not leave a blank page is the one that reports outages.
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getSystemHealth.mockRejectedValue(new Error('503'));
    show();

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled());
    expect(screen.queryByText('All systems operational')).not.toBeInTheDocument();
    vi.restoreAllMocks();
  });
});
