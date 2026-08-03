import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import Shell from './Shell';

vi.mock('@/context/AuthContext', () => ({
  useAuth: () => ({ isSuperAdmin: true, isOrgAdmin: true, isProjectManager: true, logout: vi.fn() }),
}));
// The reminder does its own fetching and has its own tests; here it only has to not explode.
vi.mock('@/components/MfaReminder', () => ({ default: () => null }));
vi.mock('@/api', () => ({ getMfaStatus: vi.fn(), listWebAuthnCredentials: vi.fn() }));

const show = () => {
  const user = userEvent.setup();
  render(<MemoryRouter initialEntries={['/system']}><Shell><p>page body</p></Shell></MemoryRouter>);
  return user;
};

const palette = () => screen.queryByRole('dialog', { name: 'Command palette' });

beforeEach(() => vi.clearAllMocks());

describe('the frame', () => {
  it('renders the page inside the navigation', () => {
    show();

    expect(screen.getByText('page body')).toBeInTheDocument();
    expect(screen.getByRole('complementary')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Search anywhere/ })).toBeInTheDocument();
  });

  it('keeps the palette closed until it is asked for', () => {
    show();
    expect(palette()).toBeNull();
  });
});

describe('the command palette', () => {
  it.each([
    ['Meta', '{Meta>}k{/Meta}'],
    ['Control', '{Control>}k{/Control}'],
  ])('opens on %s+K, wherever the operating system puts the key', async (_n, keys) => {
    const user = show();

    await user.keyboard(keys);

    expect(palette()).toBeInTheDocument();
  });

  it('opens from the search button too', async () => {
    const user = show();

    await user.click(screen.getByRole('button', { name: /Search anywhere/ }));

    expect(palette()).toBeInTheDocument();
  });

  it('toggles shut on a second Meta+K', async () => {
    const user = show();

    await user.keyboard('{Meta>}k{/Meta}');
    await user.keyboard('{Meta>}k{/Meta}');

    expect(palette()).toBeNull();
  });

  it('ignores a bare k, which is a letter someone is typing', async () => {
    const user = show();

    await user.keyboard('k');

    expect(palette()).toBeNull();
  });

  it('comes back empty after being closed', async () => {
    // It is unmounted rather than hidden, and that unmount is what resets the query. Hoisting it
    // out of the conditional gives a palette that reopens showing the last search.
    const user = show();
    await user.keyboard('{Meta>}k{/Meta}');
    await user.keyboard('audit');
    expect(screen.getByRole('combobox')).toHaveValue('audit');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(palette()).toBeNull());
    await user.keyboard('{Meta>}k{/Meta}');

    expect(screen.getByRole('combobox')).toHaveValue('');
  });

  it('takes its window listener back down with it', () => {
    // The handler is on the window, so an unmount that leaves it behind accumulates one per
    // navigation, each holding a dead tree's setState.
    const add = vi.spyOn(globalThis, 'addEventListener');
    const remove = vi.spyOn(globalThis, 'removeEventListener');
    const { unmount } = render(<MemoryRouter><Shell><p>body</p></Shell></MemoryRouter>);
    const handler = add.mock.calls.find(([type]) => type === 'keydown')![1];

    unmount();

    expect(remove).toHaveBeenCalledWith('keydown', handler);
    add.mockRestore();
    remove.mockRestore();
  });
});
