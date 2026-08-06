import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { Link, MemoryRouter, Route, Routes, useLocation } from 'react-router';
import Sidebar from './Sidebar';
import { ThemeCtx } from '@/context/ThemeContext';

/**
 * The sidebar is the console's access-control surface as the operator experiences it: a link that
 * appears for the wrong role is a 403 waiting to be clicked, and a link that fails to appear is a
 * page nobody can reach. Which of the three sections exist, and which entries each contains,
 * therefore depends on both the roles and the path — and the two disagree, because a super admin
 * is in an organisation's scope only while the URL names one.
 */

const auth = vi.hoisted(() => ({
  isSuperAdmin: false, isOrgAdmin: false, isProjectManager: false, logout: vi.fn(),
}));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const version = vi.hoisted(() => ({ get: vi.fn<() => string | null>(() => null) }));
vi.mock('@/auth', () => ({ getServerVersion: () => version.get() }));
// The sidebar renders the tree, and the tree asks the server which tenants exist. Stubbed to
// nothing here: what those calls produce is NavTree.test's subject, not this file's.
vi.mock('@/api', () => ({ listOrgs: vi.fn().mockResolvedValue([]), listProjects: vi.fn().mockResolvedValue([]) }));

const toggleDark = vi.fn();

/**
 * The page beside the sidebar. Its two links stand in for whatever a page links to, so a test can
 * move between scopes the way the operator does — the sidebar's own links cannot leave a section
 * it has collapsed.
 */
function Here() {
  return (
    <>
      <output data-testid="here">{useLocation().pathname}</output>
      <Link to="/org/projects">page: org</Link>
      <Link to="/project/users">page: project</Link>
    </>
  );
}

function show(path: string, dark = false) {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <ThemeCtx.Provider value={{ dark, toggleDark }}>
        <Routes><Route path="*" element={<><Sidebar /><Here /></>} /></Routes>
      </ThemeCtx.Provider>
    </MemoryRouter>,
  );
  return user;
}

/** The section headers, which are the only buttons carrying that class. */
const sections = () =>
  [...document.querySelectorAll('.iam-nav-section-header')].map(b => b.textContent);
/** Scoped to the sidebar: the stand-in page beside it has links of its own. */
const navLinks = () => [...document.querySelectorAll<HTMLAnchorElement>('aside a')];
const links = () => navLinks().map(a => a.getAttribute('href'));
const linkNames = () => navLinks().map(a => a.textContent);
const navLink = (name: string) => navLinks().find(a => a.textContent === name);

beforeEach(() => {
  vi.clearAllMocks();
  auth.isSuperAdmin = false;
  auth.isOrgAdmin = false;
  auth.isProjectManager = false;
  version.get.mockReturnValue(null);
});

/**
 * The sidebar is the frame: brand, version, the account menu and the theme toggle. The navigation
 * inside it moved to `NavTree`, and so did its tests — what a super admin, a tenant admin and a
 * project admin each see, which entry is lit, and what a node opens are asserted in
 * `NavTree.test.tsx` against the component that now decides them. They were not dropped; asserting
 * them here would be asserting them about a component that no longer makes the decision.
 */

describe('the brand line', () => {
  it('shows the version of the server that served the console', () => {
    // Never this SPA's own build: a console built against one release and served by another
    // would report the wrong number.
    version.get.mockReturnValue('0.5.0');
    show('/project');

    expect(screen.getByText('v0.5.0')).toBeInTheDocument();
  });

  it('shows nothing where the version is not known yet', () => {
    show('/project');
    expect(screen.queryByText(/^v/)).not.toBeInTheDocument();
  });

  it.each([
    [false, 'Switch to dark theme'],
    [true, 'Switch to light theme'],
  ])('offers the other theme (currently dark=%s)', async (dark, title) => {
    const user = show('/project', dark);
    const button = screen.getByTitle(title);

    expect(button).toHaveAttribute('aria-pressed', String(dark));
    await user.click(button);

    expect(toggleDark).toHaveBeenCalledOnce();
  });
});

describe('the account menu', () => {
  beforeEach(() => { auth.isProjectManager = true; });

  it.each([
    [{ isSuperAdmin: true, isOrgAdmin: true }, 'super_admin'],
    [{ isSuperAdmin: false, isOrgAdmin: true }, 'org_admin'],
    [{ isSuperAdmin: false, isOrgAdmin: false }, 'project_admin'],
  ])('names the strongest role held', (roles, expected) => {
    Object.assign(auth, roles);
    show('/project');
    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it('is closed until asked for', () => {
    show('/project');
    expect(screen.queryByRole('button', { name: /My Account/ })).not.toBeInTheDocument();
  });

  it('goes to the account page and closes behind itself', async () => {
    const user = show('/project');

    await user.click(screen.getByTitle('Account & sign out'));
    await user.click(screen.getByRole('button', { name: /My Account/ }));

    await vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe('/account'));
    expect(screen.queryByRole('button', { name: /My Account/ })).not.toBeInTheDocument();
  });

  it('signs out', async () => {
    const user = show('/project');

    await user.click(screen.getByTitle('Account & sign out'));
    await user.click(screen.getByRole('button', { name: /Sign out/ }));

    expect(auth.logout).toHaveBeenCalledOnce();
  });

  it('closes when the operator clicks anywhere else', async () => {
    const user = show('/project');
    await user.click(screen.getByTitle('Account & sign out'));

    await user.click(screen.getByTestId('here'));

    expect(screen.queryByRole('button', { name: /My Account/ })).not.toBeInTheDocument();
  });

  it('stops watching for that click once it is closed again', async () => {
    // The listener is on the document; one left behind per open would pile up over a session.
    const remove = vi.spyOn(document, 'removeEventListener');
    const user = show('/project');

    await user.click(screen.getByTitle('Account & sign out'));
    await user.click(screen.getByTitle('Account & sign out'));

    expect(remove).toHaveBeenCalledWith('mousedown', expect.any(Function));
    remove.mockRestore();
  });
});
