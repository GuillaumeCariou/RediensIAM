import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import ProjectStatsCards, { type ProjectStats } from './ProjectStatsCards';

const STATS: ProjectStats = {
  total_users: 200,
  active_users: 150,
  users_by_role: [
    { role_id: 'r1', role_name: 'viewer', count: 40 },
    { role_id: 'r2', role_name: 'admin', count: 120 },
  ],
};

const show = (props: Partial<React.ComponentProps<typeof ProjectStatsCards>> = {}) =>
  render(<MemoryRouter><ProjectStatsCards stats={STATS} loading={false} {...props} /></MemoryRouter>);

describe('the three counters', () => {
  it('shows the totals and the share that is active', () => {
    show();

    expect(screen.getByText('200')).toBeInTheDocument();
    expect(screen.getByText('150')).toBeInTheDocument();
    expect(screen.getByText('75% active')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('shows placeholders, and no numbers, while loading', () => {
    const { container } = show({ stats: null, loading: true });

    expect(container.querySelectorAll('.iam-skeleton')).toHaveLength(3);
    expect(screen.queryByText('—')).not.toBeInTheDocument();
  });

  it('shows an em dash where a finished load produced nothing', () => {
    show({ stats: null });
    expect(screen.getAllByText('—')).toHaveLength(3);
  });

  it('does not divide by zero on a project nobody has joined', () => {
    show({ stats: { total_users: 0, active_users: 0, users_by_role: [] } });
    expect(screen.queryByText(/% active/)).not.toBeInTheDocument();
  });
});

describe('the manage links', () => {
  it('appear only where the caller gave a destination', () => {
    show({ usersLink: '/project/users' });

    const links = screen.getAllByRole('link', { name: 'Manage' });
    expect(links).toHaveLength(1);
    expect(links[0]).toHaveAttribute('href', '/project/users');
  });

  it('appear for both when both are given', () => {
    show({ usersLink: '/project/users', rolesLink: '/project/roles' });
    expect(screen.getAllByRole('link', { name: 'Manage' })).toHaveLength(2);
  });
});

describe('the breakdown by role', () => {
  it('lists the roles largest first, whatever order they arrived in', () => {
    show();

    const names = screen.getAllByText(/^(admin|viewer)$/).map(e => e.textContent);
    expect(names).toEqual(['admin', 'viewer']);
  });

  it('sizes each bar by its share of the project', () => {
    const { container } = show();
    const widths = [...container.querySelectorAll<HTMLElement>('.bg-primary')].map(e => e.style.width);

    expect(widths).toEqual(['60%', '20%']);
  });

  it('draws no bar at all rather than a NaN width on an empty project', () => {
    const { container } = show({
      stats: { total_users: 0, active_users: 0, users_by_role: [{ role_id: 'r1', role_name: 'viewer', count: 0 }] },
    });

    expect([...container.querySelectorAll<HTMLElement>('.bg-primary')].map(e => e.style.width)).toEqual(['0%']);
  });

  it('is left out entirely when the project has no roles', () => {
    show({ stats: { ...STATS, users_by_role: [] } });
    expect(screen.queryByText('Users by Role')).not.toBeInTheDocument();
  });
});
