import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useTheme } from './useTheme';

/**
 * index.css keys the dark palette off `data-theme`; its absence means "follow the operating
 * system". So "system" has to remove the attribute rather than write a third value, or a visitor
 * who chooses it stays on whichever palette was set last.
 */

beforeEach(() => localStorage.removeItem('theme'));
afterEach(() => delete document.documentElement.dataset['theme']);

const attr = () => document.documentElement.dataset['theme'];

describe('the stored choice', () => {
  it('follows the operating system when nothing was stored', () => {
    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('system');
    expect(attr()).toBeUndefined();
  });

  it.each(['light', 'dark'] as const)('restores a stored %s choice', t => {
    localStorage.setItem('theme', t);

    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe(t);
    expect(attr()).toBe(t);
  });
});

describe('changing it', () => {
  it('applies the choice, records it, and survives a remount', () => {
    const { result, unmount } = renderHook(() => useTheme());

    act(() => result.current.setTheme('dark'));

    expect(attr()).toBe('dark');
    expect(localStorage.getItem('theme')).toBe('dark');

    unmount();
    const again = renderHook(() => useTheme());
    expect(again.result.current.theme).toBe('dark');
  });

  it('removes the attribute again on the way back to the system palette', () => {
    localStorage.setItem('theme', 'dark');
    const { result } = renderHook(() => useTheme());

    act(() => result.current.setTheme('system'));

    expect(attr()).toBeUndefined();
    expect(localStorage.getItem('theme')).toBe('system');
  });
});
