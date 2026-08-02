import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { ThemeProvider } from './ThemeProvider';
import { STORAGE_KEY, useTheme } from './ThemeContext';
import { ScopeProvider } from './ScopeProvider';
import { useScope } from './ScopeContext';

function ThemeProbe() {
  const { dark, toggleDark } = useTheme();
  return <button type="button" onClick={toggleDark}>{dark ? 'dark' : 'light'}</button>;
}

const prefersDark = (matches: boolean) =>
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches })));

beforeEach(() => {
  localStorage.removeItem(STORAGE_KEY);
  delete document.documentElement.dataset['theme'];
});

afterEach(() => {
  vi.unstubAllGlobals();
  delete document.documentElement.dataset['theme'];
});

describe('the first visit, with nothing stored', () => {
  it.each([
    ['dark', true, 'dark'],
    ['light', false, 'light'],
  ])('follows a %s operating system', (_n, matches, expected) => {
    prefersDark(matches);
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>);
    expect(screen.getByRole('button')).toHaveTextContent(expected);
  });
});

describe('a stored choice', () => {
  it.each([
    ['1', 'dark'],
    ['0', 'light'],
  ])('wins over the operating system (stored %s)', (saved, expected) => {
    // Otherwise an operator on a dark desktop can never keep the light console.
    prefersDark(saved === '0');
    localStorage.setItem(STORAGE_KEY, saved);

    render(<ThemeProvider><ThemeProbe /></ThemeProvider>);

    expect(screen.getByRole('button')).toHaveTextContent(expected);
  });
});

describe('toggling', () => {
  it('flips the palette, records it and survives a remount', async () => {
    prefersDark(false);
    const user = userEvent.setup();
    const { unmount } = render(<ThemeProvider><ThemeProbe /></ThemeProvider>);

    await user.click(screen.getByRole('button'));

    expect(screen.getByRole('button')).toHaveTextContent('dark');
    // index.css keys the dark palette off this attribute; its absence is the light one.
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(localStorage.getItem(STORAGE_KEY)).toBe('1');

    unmount();
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>);
    expect(screen.getByRole('button')).toHaveTextContent('dark');
  });

  it('removes the attribute again on the way back to light', async () => {
    prefersDark(true);
    const user = userEvent.setup();
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>);

    await user.click(screen.getByRole('button'));

    expect(document.documentElement.dataset['theme']).toBeUndefined();
    expect(localStorage.getItem(STORAGE_KEY)).toBe('0');
  });
});

function ScopeProbe() {
  const { orgName, setOrgName, projectName, setProjectName } = useScope();
  return (
    <>
      <output>{orgName || '—'} / {projectName || '—'}</output>
      <button type="button" onClick={() => { setOrgName('Acme'); setProjectName('Portal'); }}>name</button>
    </>
  );
}

describe('ScopeProvider', () => {
  it('starts empty and carries the names the pages set for the breadcrumb', async () => {
    const user = userEvent.setup();
    render(<ScopeProvider><ScopeProbe /></ScopeProvider>);

    expect(screen.getByRole('status')).toHaveTextContent('— / —');
    await user.click(screen.getByRole('button'));

    expect(screen.getByRole('status')).toHaveTextContent('Acme / Portal');
  });
});
