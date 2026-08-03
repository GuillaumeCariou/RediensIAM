import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import Topbar from './Topbar';
import { ScopeCtx } from '@/context/ScopeContext';

/**
 * The breadcrumb is the only thing on screen that says which tenant the page below it is about.
 * Which chips appear depends on the operator's roles and on the path, and the two disagree: a
 * super admin browsing an organisation is in the org scope without holding the org role, and an
 * org admin is in it without the path saying so.
 */

const auth = vi.hoisted(() => ({ isSuperAdmin: false, isOrgAdmin: false }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

const onCmdK = vi.fn();

function show(path: string, names: { orgName?: string; projectName?: string } = {}) {
  const scope = { orgName: '', projectName: '', setOrgName: () => {}, setProjectName: () => {}, ...names };
  render(
    <MemoryRouter initialEntries={[path]}>
      <ScopeCtx.Provider value={scope}>
        <Routes><Route path="*" element={<><Topbar onCmdK={onCmdK} /><Here /></>} /></Routes>
      </ScopeCtx.Provider>
    </MemoryRouter>,
  );
}

const chips = () => screen.getAllByRole('button').filter(b => b.className.includes('iam-scope-chip'));
const kinds = () => chips().map(c => c.querySelector('.iam-scope-kind')!.textContent);
/** Router navigation is a state update, so the address is asserted once it has settled. */
const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));

beforeEach(() => {
  vi.clearAllMocks();
  auth.isSuperAdmin = false;
  auth.isOrgAdmin = false;
});

describe('a super admin', () => {
  beforeEach(() => { auth.isSuperAdmin = true; auth.isOrgAdmin = true; });

  it('sees the system chip alone at the top level', () => {
    show('/system');
    expect(kinds()).toEqual(['SYS']);
  });

  it('gains the org chip only once the URL names an organisation', () => {
    // Holding org_admin implicitly must not put a chip there — there is no organisation in scope.
    show('/system/organisations/0123456789abcdef');
    expect(kinds()).toEqual(['SYS', 'ORG']);
  });

  it('gains the project chip once the URL names a project too', () => {
    show('/system/organisations/o1/projects/p1');
    expect(kinds()).toEqual(['SYS', 'ORG', 'PRJ']);
  });
});

describe('an org admin', () => {
  beforeEach(() => { auth.isOrgAdmin = true; });

  it('sees their own organisation without the system chip', () => {
    show('/org/projects');
    expect(kinds()).toEqual(['ORG']);
  });

  it('gains the project chip on a project page', () => {
    show('/project/users');
    expect(kinds()).toEqual(['ORG', 'PRJ']);
  });
});

describe('a project manager', () => {
  it('sees the project chip only', () => {
    show('/project');
    expect(kinds()).toEqual(['PRJ']);
  });
});

describe('the names on the chips', () => {
  beforeEach(() => { auth.isSuperAdmin = true; });

  it('uses the names the page has published', () => {
    show('/system/organisations/o1/projects/p1', { orgName: 'Acme', projectName: 'Portal' });

    expect(screen.getByText('Acme')).toBeInTheDocument();
    expect(screen.getByText('Portal')).toBeInTheDocument();
  });

  it('falls back to a shortened id while the page is still loading them', () => {
    show('/system/organisations/0123456789abcdefgh/projects/zyxwvutsrqponmlk');

    expect(screen.getByText('0123456789ab')).toBeInTheDocument();
    expect(screen.getByText('zyxwvutsrqpo')).toBeInTheDocument();
  });

  it('falls back to the generic word when there is no id either', () => {
    auth.isSuperAdmin = false;
    auth.isOrgAdmin = true;
    show('/project');

    expect(screen.getByText('Organisation')).toBeInTheDocument();
    expect(screen.getByText('Project')).toBeInTheDocument();
  });
});

describe('clicking a chip', () => {
  it('takes a super admin back through the system routes', async () => {
    auth.isSuperAdmin = true;
    const user = userEvent.setup();
    show('/system/organisations/o1/projects/p1');

    await user.click(screen.getByText('PRJ').closest('button')!);
    await arrivedAt('/system/organisations/o1/projects/p1');

    await user.click(screen.getByText('ORG').closest('button')!);
    await arrivedAt('/system/organisations/o1');

    await user.click(screen.getByText('SYS').closest('button')!);
    await arrivedAt('/system');
  });

  it('takes everyone else to their own scope home', async () => {
    auth.isOrgAdmin = true;
    const user = userEvent.setup();
    show('/project/users');

    await user.click(screen.getByText('PRJ').closest('button')!);
    await arrivedAt('/project');

    await user.click(screen.getByText('ORG').closest('button')!);
    await arrivedAt('/org');
  });
});

describe('the search button', () => {
  it('opens the command palette', async () => {
    const user = userEvent.setup();
    show('/project');

    await user.click(screen.getByRole('button', { name: /Search anywhere/ }));

    expect(onCmdK).toHaveBeenCalledOnce();
  });
});
