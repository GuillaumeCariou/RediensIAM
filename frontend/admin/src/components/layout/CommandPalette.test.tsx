import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
// Vitest's own interactivity API, driven by Playwright: real clicks, real key events, real focus.
import { userEvent } from 'vitest/browser';
import { MemoryRouter, useLocation } from 'react-router';
import CommandPalette from './CommandPalette';

/**
 * The palette's contents are a function of the signed-in admin's roles, which normally come from
 * a decoded access token. Only the roles matter here, so the whole AuthContext is replaced by
 * this mutable object — `open()` reassigns it per test.
 */
const roles = { isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false };
vi.mock('@/context/AuthContext', () => ({ useAuth: () => roles }));

function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
}

function open(opts: Partial<typeof roles> = {}) {
  Object.assign(roles, { isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false }, opts);
  const onClose = vi.fn();
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/start']}>
      <Where />
      <button>a button behind the palette</button>
      <CommandPalette onClose={onClose} />
    </MemoryRouter>,
  );
  return { user, onClose, dialog: document.querySelector('dialog')! };
}

const search = () => screen.getByRole('combobox');
const options = () => screen.getAllByRole('option');
const active = () => {
  const id = search().getAttribute('aria-activedescendant');
  return id ? document.getElementById(id) : null;
};

describe('opening', () => {
  it('opens as a modal dialog, not a plain one', () => {
    // The defect this replaced: a non-modal <dialog> behind a scrim div still let Tab walk into
    // the page behind it. Focus containment and inertness are the platform's job — but only if
    // the dialog is actually opened with showModal().
    const { dialog } = open({ isOrgAdmin: true });

    expect(dialog.open).toBe(true);
    // STRENGTHENED: `:modal` is Chromium's own answer to "is this in the top layer". Under jsdom
    // this could only be a shim's record of which method had been called.
    expect(dialog.matches(':modal')).toBe(true);
  });

  it('keeps Tab inside the palette instead of letting it reach the page behind', async () => {
    // STRENGTHENED: the containment itself, which is the defect above rather than a proxy for it.
    // jsdom has no top layer and no inertness, so it could not have caught the non-modal version.
    const { user, dialog } = open({ isOrgAdmin: true });
    const behind = screen.getByRole('button', { name: 'a button behind the palette' });

    const visited: Element[] = [];
    for (let i = 0; i < 5; i++) {
      await user.tab();
      visited.push(document.activeElement!);
    }

    expect(visited).not.toContain(behind);
    // <body> is where Chromium parks focus for one press as the cycle wraps; the options are
    // tabIndex={-1} by design, so the search box is the only stop inside.
    for (const el of visited) expect(dialog.contains(el) || el === document.body).toBe(true);
    expect(visited).toContain(search());
  });

  it('inerts the page behind, so it cannot even be focused programmatically', () => {
    // STRENGTHENED: inertness is a top-layer property Chromium enforces and jsdom does not model.
    open({ isOrgAdmin: true });
    const behind = screen.getByRole('button', { name: 'a button behind the palette' });

    behind.focus();

    expect(behind).not.toHaveFocus();
  });

  it('puts the cursor in the search box', () => {
    open({ isOrgAdmin: true });
    expect(search()).toHaveFocus();
  });

  it('wires the combobox to the listbox it controls', () => {
    open({ isOrgAdmin: true });
    const listbox = screen.getByRole('listbox');
    expect(search()).toHaveAttribute('aria-controls', listbox.id);
    expect(search()).toHaveAttribute('aria-expanded', 'true');
  });
});

describe('closing', () => {
  it('Escape closes it and tells the parent', async () => {
    const { user, onClose, dialog } = open({ isOrgAdmin: true });

    await user.keyboard('{Escape}');

    expect(dialog.open).toBe(false);
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

describe('what it offers', () => {
  it('shows only the sections the admin actually has', () => {
    open({ isOrgAdmin: true });

    expect(screen.getByRole('group', { name: 'Organisation' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'System' })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Project' })).not.toBeInTheDocument();
  });

  it('offers the account page to everyone', () => {
    open();
    expect(within(screen.getByRole('group', { name: 'Account' })).getByRole('option', { name: /My Account/ })).toBeInTheDocument();
  });

  it('gives a super admin the system section', () => {
    open({ isSuperAdmin: true });
    expect(within(screen.getByRole('group', { name: 'System' })).getByRole('option', { name: /Audit Log/ })).toBeInTheDocument();
  });
});

describe('keyboard navigation', () => {
  it('starts on the first result', () => {
    open({ isOrgAdmin: true });
    expect(active()).toBe(options()[0]);
    expect(options()[0]).toHaveAttribute('aria-selected', 'true');
  });

  it('moves the active option down and up with the arrow keys', async () => {
    const { user } = open({ isOrgAdmin: true });

    await user.keyboard('{ArrowDown}{ArrowDown}');
    expect(active()).toBe(options()[2]);

    await user.keyboard('{ArrowUp}');
    expect(active()).toBe(options()[1]);
  });

  it('marks exactly one option selected at a time', async () => {
    const { user } = open({ isOrgAdmin: true });
    await user.keyboard('{ArrowDown}');

    expect(options().filter(o => o.getAttribute('aria-selected') === 'true')).toHaveLength(1);
  });

  it('stops at both ends instead of wrapping into nothing', async () => {
    const { user } = open({ isOrgAdmin: true });

    await user.keyboard('{ArrowUp}{ArrowUp}');
    expect(active()).toBe(options()[0]);

    const last = options().length - 1;
    await user.keyboard('{ArrowDown}'.repeat(last + 5));
    expect(active()).toBe(options()[last]);
  });

  it('keeps the cursor on a real option after the list is filtered under it', async () => {
    const { user } = open({ isOrgAdmin: true });

    await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}');
    await user.type(search(), 'audit');

    // A stale index would point aria-activedescendant at an id no longer in the document.
    expect(options()).toHaveLength(1);
    expect(active()).toBe(options()[0]);
  });

  it('Enter navigates to the active option and closes', async () => {
    const { user, onClose } = open({ isOrgAdmin: true });

    await user.type(search(), 'webhooks');
    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/org/webhooks'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Enter does nothing when nothing matches', async () => {
    const { user, onClose } = open({ isOrgAdmin: true });

    await user.type(search(), 'zzzzz');
    await user.keyboard('{Enter}');

    expect(screen.getByTestId('where')).toHaveTextContent('/start');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('does not let the arrow keys move the text caret instead of the cursor', async () => {
    // Without preventDefault the browser would move the caret in the input and the list
    // would sit still.
    const { user } = open({ isOrgAdmin: true });
    await user.type(search(), 'a');
    await user.keyboard('{ArrowDown}');

    expect(active()).toBe(options()[1]);
  });
});

describe('filtering', () => {
  it('narrows to matching labels, case-insensitively', async () => {
    const { user } = open({ isOrgAdmin: true });
    const before = options().length;

    await user.type(search(), 'PROJ');

    expect(options().length).toBeLessThan(before);
    for (const o of options()) expect(o.textContent?.toLowerCase()).toContain('proj');
  });

  it('says so when nothing matches', async () => {
    const { user } = open({ isOrgAdmin: true });

    await user.type(search(), 'nothing here');

    expect(screen.getByText('No matches')).toBeInTheDocument();
    expect(screen.queryAllByRole('option')).toHaveLength(0);
    expect(search()).not.toHaveAttribute('aria-activedescendant');
  });
});

describe('the mouse', () => {
  it('clicking a result navigates and closes', async () => {
    const { user, onClose } = open({ isProjectManager: true });

    await user.click(screen.getByRole('option', { name: /Roles/ }));

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/project/roles'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('hovering moves the keyboard cursor so Enter goes where the pointer is', async () => {
    const { user } = open({ isProjectManager: true });

    await user.hover(screen.getByRole('option', { name: /Settings/ }));
    expect(active()).toBe(screen.getByRole('option', { name: /Settings/ }));
  });
});
