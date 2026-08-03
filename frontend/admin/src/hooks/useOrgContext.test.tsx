import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { useOrgContext, useProjectContext } from './useOrgContext';

/**
 * Both hooks answer one question: which tenant is this page about? Three sources disagree — the
 * URL path, the query string and the token — and the precedence between them is the whole logic.
 * Get it wrong and a super admin managing someone else's organisation is silently pinned to their
 * own, or an admin following a project link lands on a different project than the one they clicked.
 */

const auth = vi.hoisted(() => ({ orgId: '', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

function OrgProbe() {
  const { orgId, isSystemCtx, orgBase, userListBase, projectUrl } = useOrgContext();
  return (
    <dl>
      <dd data-testid="orgId">{orgId}</dd>
      <dd data-testid="isSystemCtx">{String(isSystemCtx)}</dd>
      <dd data-testid="orgBase">{orgBase}</dd>
      <dd data-testid="userListBase">{userListBase}</dd>
      <dd data-testid="projectUrl">{projectUrl('p9')}</dd>
    </dl>
  );
}

function ProjectProbe() {
  const { projectId, isSystemCtx, projectBase } = useProjectContext();
  return (
    <dl>
      <dd data-testid="projectId">{projectId}</dd>
      <dd data-testid="isSystemCtx">{String(isSystemCtx)}</dd>
      <dd data-testid="projectBase">{projectBase}</dd>
    </dl>
  );
}

/** Renders `probe` at `path`, with `pattern` deciding which segments become params. */
function at(path: string, pattern: string, probe: React.ReactNode) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={probe} /></Routes>
    </MemoryRouter>,
  );
}

const value = (id: string) => screen.getByTestId(id).textContent;

describe('useOrgContext', () => {
  it('falls back to the token claim when the URL names no organisation', () => {
    auth.orgId = 'tok-org';
    at('/org/projects', '/org/projects', <OrgProbe />);

    expect(value('orgId')).toBe('tok-org');
    expect(value('isSystemCtx')).toBe('false');
    expect(value('orgBase')).toBe('/org');
    expect(value('userListBase')).toBe('/org/userlists');
  });

  it('lets a :id in the URL beat the token claim', () => {
    // Otherwise a super admin can only ever manage the organisation their own token names.
    auth.orgId = 'tok-org';
    at('/system/organisations/o1', '/system/organisations/:id', <OrgProbe />);

    expect(value('orgId')).toBe('o1');
    expect(value('orgBase')).toBe('/system/organisations/o1');
    expect(value('userListBase')).toBe('/system/organisations/o1/userlists');
  });

  it('accepts :oid as the same thing, for the routes nested under a project', () => {
    auth.orgId = 'tok-org';
    at('/system/organisations/o2/projects/p1', '/system/organisations/:oid/projects/:pid', <OrgProbe />);
    expect(value('orgId')).toBe('o2');
  });

  it('decides the system context from the path, not from the params it happens to have', () => {
    // `org/userlists/:id` once bound :id to the USER LIST id, so an org admin opening one of their
    // own lists was routed to the super-admin-only endpoints and got a 403 on every request.
    auth.orgId = 'tok-org';
    at('/org/userlists/l1', '/org/userlists/:id', <OrgProbe />);

    expect(value('isSystemCtx')).toBe('false');
    expect(value('orgBase')).toBe('/org');
  });

  it('links to a project through the system route when it is in the system context', () => {
    at('/system/organisations/o1', '/system/organisations/:id', <OrgProbe />);
    expect(value('projectUrl')).toBe('/system/organisations/o1/projects/p9');
  });

  it('links to it through the query-string route otherwise', () => {
    auth.orgId = 'tok-org';
    at('/org', '/org', <OrgProbe />);
    expect(value('projectUrl')).toBe('/project?project_id=p9');
  });
});

describe('useProjectContext', () => {
  it('falls back to the token claim, which is what a project manager has', () => {
    auth.projectId = 'tok-proj';
    at('/project', '/project', <ProjectProbe />);

    expect(value('projectId')).toBe('tok-proj');
    expect(value('isSystemCtx')).toBe('false');
    expect(value('projectBase')).toBe('/project');
  });

  it('lets ?project_id beat the token claim, which is the link an org admin follows', () => {
    auth.projectId = 'tok-proj';
    at('/project?project_id=p1', '/project', <ProjectProbe />);
    expect(value('projectId')).toBe('p1');
  });

  it('lets the path param beat both', () => {
    auth.projectId = 'tok-proj';
    at('/system/organisations/o1/projects/p2?project_id=p1',
      '/system/organisations/:oid/projects/:pid', <ProjectProbe />);

    expect(value('projectId')).toBe('p2');
    expect(value('isSystemCtx')).toBe('true');
    expect(value('projectBase')).toBe('/system/organisations/o1/projects/p2');
  });
});
