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

describe('what a super admin sees', () => {
  beforeEach(() => { auth.isSuperAdmin = true; auth.isOrgAdmin = true; auth.isProjectManager = true; });

  it('has the system section only, until the URL names an organisation', () => {
    show('/system');
    expect(sections()).toEqual(['System']);
  });

  it('gains an organisation section, labelled by the id in the URL', () => {
    show('/system/organisations/0123456789abcdef');
    expect(sections()).toEqual(['System', 'Org · 01234567…']);
  });

  it('gains a project section once the URL names a project as well', () => {
    show('/system/organisations/o1/projects/0123456789abcdef');
    expect(sections()).toEqual(['System', 'Org · o1…', 'Proj · 01234567…']);
  });

  it('points the nested sections at the system routes, not the tenant\'s own', () => {
    // /org/projects would be the super admin's own organisation, which is not the one on screen.
    show('/system/organisations/o1');
    expect(links()).toContain('/system/organisations/o1/projects');
    expect(links()).not.toContain('/org/projects');
  });

  it('includes the entries reserved for a super admin', () => {
    show('/system');
    expect(linkNames()).toContain('Health');
    expect(links()).toContain('/system/users');
  });
});

describe('what an org admin sees', () => {
  beforeEach(() => { auth.isOrgAdmin = true; auth.isProjectManager = true; });

  it('has their own organisation and no system section', () => {
    show('/org');
    expect(sections()).toEqual(['Organisation']);
  });

  it('links to the tenant-scoped routes', () => {
    show('/org');
    expect(links()).toContain('/org/projects');
    expect(links()).toContain('/org/webhooks');
  });

  it('gains a project section only once they are on a project page', () => {
    show('/project/users');
    expect(sections()).toEqual(['Organisation', 'Project']);
  });
});

describe('what a project manager sees', () => {
  beforeEach(() => { auth.isProjectManager = true; });

  it('has the project section, and only that', () => {
    show('/project');
    expect(sections()).toEqual(['Project']);
  });

  it('is never offered a super-admin-only entry', () => {
    // These entries render nothing at all rather than rendering a link to a 403.
    show('/project');
    expect(linkNames()).not.toContain('Health');
  });
});

describe('the active entry', () => {
  beforeEach(() => { auth.isOrgAdmin = true; });

  it('marks the section the route is in', () => {
    show('/org/projects');
    expect(document.querySelector('.iam-nav-section-highlight')).not.toBeNull();
  });

  it('marks a nested route as being under its parent entry', () => {
    show('/org/userlists/l1');
    expect(navLink('User Lists')!.className).toContain('active');
  });

  it('does not mark the overview entry for every route beneath it', () => {
    // `/org` is a prefix of every org route, so it is matched exactly or it is always active.
    show('/org/projects');
    expect(navLink('Overview')!.className).not.toContain('active');
  });
});

describe('the sections', () => {
  beforeEach(() => { auth.isSuperAdmin = true; auth.isOrgAdmin = true; auth.isProjectManager = true; });

  it('can be collapsed by hand', async () => {
    const user = show('/system');

    await user.click(screen.getByRole('button', { name: /System/ }));

    expect(navLink('Organisations')).toBeUndefined();
  });

  it('opens the one the route moved into, undoing a manual collapse', async () => {
    // Which section is open follows the route, except while the operator has overridden it — so
    // the override has to end when the route leaves and comes back, not persist for the session.
    auth.isSuperAdmin = false;
    const user = show('/org/projects');
    await user.click(screen.getByRole('button', { name: /Organisation/ }));
    expect(navLink('Webhooks')).toBeUndefined();

    await user.click(screen.getByRole('link', { name: 'page: project' }));
    await user.click(screen.getByRole('link', { name: 'page: org' }));

    await vi.waitFor(() => expect(navLink('Webhooks')).toBeDefined());
  });
});

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

describe('the project section', () => {
  it('can be collapsed by hand like the others', async () => {
    auth.isProjectManager = true;
    const user = show('/project');

    await user.click(screen.getByRole('button', { name: /Project/ }));

    expect(navLink('Roles')).toBeUndefined();
  });
});
