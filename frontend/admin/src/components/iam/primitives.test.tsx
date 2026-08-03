import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import IamAvatar from './IamAvatar';
import StatCard from './StatCard';
import ActivityChart from './ActivityChart';

describe('IamAvatar', () => {
  it('takes the first letter of the first two words', () => {
    render(<IamAvatar name="ada lovelace king" />);
    expect(screen.getByText('AL')).toBeInTheDocument();
  });

  it('falls back to a question mark rather than rendering an empty disc', () => {
    render(<IamAvatar name="" />);
    expect(screen.getByText('?')).toBeInTheDocument();
  });

  it('gives the same name the same hue, and different names different ones', () => {
    // The hue is the only thing distinguishing two "AB" discs in a list, so it has to be stable
    // per name and not per render.
    const { container } = render(
      <>
        <IamAvatar name="Ada Lovelace" /><IamAvatar name="Ada Lovelace" /><IamAvatar name="Grace Hopper" />
      </>,
    );
    const [a, b, c] = [...container.querySelectorAll<HTMLElement>('.iam-avatar')].map(e => e.style.background);

    expect(a).toBe(b);
    expect(a).not.toBe(c);
  });

  it('takes its lightness from the theme, never a fixed colour', () => {
    // A hard-coded pair renders as a bright pastel disc on a dark table.
    const { container } = render(<IamAvatar name="Ada" />);
    const el = container.querySelector<HTMLElement>('.iam-avatar')!;

    expect(el.style.background).toContain('--iam-avatar-bg-l');
    expect(el.style.color).toContain('--iam-avatar-fg-l');
  });

  it.each([
    ['sm', 'iam-avatar-sm'],
    ['lg', 'iam-avatar-lg'],
  ] as const)('carries the %s size class', (size, cls) => {
    const { container } = render(<IamAvatar name="Ada" size={size} />);
    expect(container.querySelector('.iam-avatar')).toHaveClass(cls);
  });

  it('has neither size class at the default size, and keeps the caller\'s own', () => {
    const { container } = render(<IamAvatar name="Ada" className="mine" />);
    const el = container.querySelector('.iam-avatar')!;

    expect(el).toHaveClass('mine');
    expect(el.className).not.toMatch(/iam-avatar-(sm|lg)/);
  });
});

describe('StatCard', () => {
  it('shows a label and a value', () => {
    render(<StatCard label="Users" value={42} />);
    expect(screen.getByText('Users')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
  });

  it('omits the sub-line and the sparkline when there is none', () => {
    const { container } = render(<StatCard label="Users" value={42} />);
    expect(container.querySelector('.iam-stat-sub')).toBeNull();
    expect(container.querySelector('.iam-stat-spark')).toBeNull();
  });

  it.each([
    ['muted', ''],
    ['up', 'iam-stat-trend-up'],
    ['down', 'iam-stat-trend-down'],
  ] as const)('tones a %s sub-line', (subTone, cls) => {
    const { container } = render(<StatCard label="Users" value={42} sub="+3" subTone={subTone} />);
    const sub = container.querySelector('.iam-stat-sub')!;

    if (cls) expect(sub).toHaveClass(cls);
    else expect(sub.className).toBe('iam-stat-sub');
  });

  it('renders a sparkline when given one', () => {
    const { container } = render(<StatCard label="Users" value={42} spark={<svg role="img" />} />);
    expect(container.querySelector('.iam-stat-spark')).not.toBeNull();
  });
});

describe('ActivityChart', () => {
  it.each([
    ['no data at all', undefined],
    ['an empty window', []],
  ])('says so plainly given %s', (_n, data) => {
    // It used to draw bars off a sine wave under a heading claiming they were real sign-ins.
    render(<ActivityChart data={data} />);
    expect(screen.getByText('No sign-ins recorded in this window')).toBeInTheDocument();
  });

  it('draws one column per hour, described for a pointer', () => {
    const { container } = render(<ActivityChart data={[
      { hour: '09:00', succeeded: 10, failed: 2 },
      { hour: '10:00', succeeded: 4, failed: 0 },
    ]} />);

    expect(container.querySelectorAll('[title]')).toHaveLength(2);
    expect(container.querySelector('[title]')).toHaveAttribute('title', '09:00: 10 succeeded, 2 failed');
  });

  it('omits the half of a column that has no events', () => {
    const { container } = render(<ActivityChart data={[{ hour: '09:00', succeeded: 3, failed: 0 }]} />);
    expect(container.querySelector('[title]')!.children).toHaveLength(1);
  });

  it('scales the tallest column to the plot and keeps the smallest visible', () => {
    // A single failure among ten thousand successes still has to be a pixel or two, not nothing.
    const { container } = render(<ActivityChart data={[
      { hour: '09:00', succeeded: 10000, failed: 1 },
    ]} height={110} />);
    const [failed, succeeded] = [...container.querySelectorAll<HTMLElement>('[title] > div')];

    expect(Number.parseFloat(succeeded.style.height)).toBeCloseTo(100, 0);
    expect(Number.parseFloat(failed.style.height)).toBe(2);
  });
});
