import { describe, expect, it } from 'vitest';
import { cn, fmtDate, fmtDateShort } from './utils';

describe('cn', () => {
  it('joins what it is given', () => {
    expect(cn('a', 'b')).toBe('a b');
  });

  it('drops the falsy branches a conditional class produces', () => {
    // Named rather than written `false && 'b'` inline: a literal on the left of `&&` is a constant
    // condition, which eslint flags — rightly, since that shape is usually a mistake. Here the
    // switch being off is the point, so it gets a name instead of a suppression.
    const active = false;
    expect(cn('a', active && 'b', null, undefined, 'c')).toBe('a c');
  });

  it('lets the later Tailwind class win over the earlier one it contradicts', () => {
    // The whole reason twMerge is here: `cn(base, props.className)` must let a caller override,
    // and plain concatenation leaves both in the attribute where the CSS order decides instead.
    expect(cn('px-2 py-1', 'px-4')).toBe('py-1 px-4');
  });
});

describe.each([
  ['fmtDate', fmtDate],
  ['fmtDateShort', fmtDateShort],
] as const)('%s', (_name, fmt) => {
  it('prints an em dash rather than "Invalid Date" when there is no timestamp', () => {
    // These render straight into table cells; every row without the field would otherwise read
    // "Invalid Date", which looks like a fault rather than an absence.
    expect(fmt(null)).toBe('—');
    expect(fmt(undefined)).toBe('—');
    expect(fmt('')).toBe('—');
  });

  it('formats a real timestamp in the operator\'s locale', () => {
    const iso = '2026-03-04T05:06:07Z';
    expect(fmt(iso)).toBe(_name === 'fmtDate'
      ? new Date(iso).toLocaleString()
      : new Date(iso).toLocaleDateString());
  });
});
