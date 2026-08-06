import { describe, expect, it } from 'vitest';
import { DESTINATIONS, ROUTE_BASES, activeKey, basePath, hrefFor, scopeFromPath, type Scope } from './scope';

/**
 * The navigation's one description of itself.
 *
 * These assert the property the module exists for: a level's destinations are written once, and
 * the two URL shapes that reach them — a super-admin browsing into a tenant, and that tenant's own
 * administrator — are produced from the same list rather than from two hand-kept copies.
 */

describe('basePath', () => {
  it.each([
    [{ level: 'deployment' } as Scope,                              '/system'],
    [{ level: 'org' } as Scope,                                     '/org'],
    [{ level: 'org', orgId: 'o1' } as Scope,                        '/system/organisations/o1'],
    [{ level: 'project' } as Scope,                                 '/project'],
    [{ level: 'project', orgId: 'o1', projectId: 'p1' } as Scope,   '/system/organisations/o1/projects/p1'],
  ])('%o → %s', (scope, expected) => {
    expect(basePath(scope)).toBe(expected);
  });

  /**
   * A project scope carrying only half its ids cannot address the long shape, and guessing would
   * produce `/system/organisations/undefined/...`. It falls back to the caller's own project.
   */
  it('falls back to the short shape when a project scope is missing its organisation', () => {
    expect(basePath({ level: 'project', projectId: 'p1' })).toBe('/project');
  });
});

describe('hrefFor', () => {
  it('addresses the level itself with the empty key', () => {
    expect(hrefFor({ level: 'org', orgId: 'o1' }, '')).toBe('/system/organisations/o1');
  });

  it('produces both shapes of the same destination from one entry', () => {
    expect(hrefFor({ level: 'org' }, 'service-accounts')).toBe('/org/service-accounts');
    expect(hrefFor({ level: 'org', orgId: 'o1' }, 'service-accounts'))
      .toBe('/system/organisations/o1/service-accounts');
  });
});

describe('scopeFromPath', () => {
  /**
   * The project shape is also a prefix match for the organisation shape. Read in the wrong order,
   * a project page puts the tree on its organisation — which is why this is the first case.
   */
  it('reads a project inside an organisation as a project, not as its organisation', () => {
    expect(scopeFromPath('/system/organisations/o1/projects/p1/users'))
      .toEqual({ level: 'project', orgId: 'o1', projectId: 'p1' });
  });

  it.each([
    ['/system',                                  { level: 'deployment' }],
    ['/system/service-accounts/sa1',             { level: 'deployment' }],
    ['/system/organisations/o1/userlists',       { level: 'org', orgId: 'o1' }],
    ['/org/webhooks',                            { level: 'org' }],
    ['/project/roles',                           { level: 'project' }],
    ['/account',                                 { level: 'deployment' }],
  ])('%s → %o', (path, expected) => {
    expect(scopeFromPath(path)).toEqual(expected);
  });

  it('round-trips every destination of every level', () => {
    const scopes: Scope[] = [
      { level: 'deployment' },
      { level: 'org' }, { level: 'org', orgId: 'o1' },
      { level: 'project' }, { level: 'project', orgId: 'o1', projectId: 'p1' },
    ];
    for (const scope of scopes) {
      for (const destination of DESTINATIONS[scope.level]) {
        expect(scopeFromPath(hrefFor(scope, destination.key))).toEqual(scope);
      }
    }
  });
});

describe('activeKey', () => {
  /**
   * Overview's key is the empty string and its path is a prefix of every sibling, so a
   * prefix-first match would light it up on every page of the level. Exact match wins first.
   */
  it('does not light up Overview on a sibling page', () => {
    expect(activeKey({ level: 'org' }, '/org')).toBe('');
    expect(activeKey({ level: 'org' }, '/org/webhooks')).toBe('webhooks');
  });

  it('keeps the destination lit on its own detail pages', () => {
    expect(activeKey({ level: 'deployment' }, '/system/service-accounts/sa1')).toBe('service-accounts');
    expect(activeKey({ level: 'org', orgId: 'o1' }, '/system/organisations/o1/userlists/l1')).toBe('userlists');
  });

  /**
   * `/system/organisations/o1` is a destination of the deployment level *and* the base of the
   * organisation level. Asked at organisation level it is that level's Overview, not the
   * deployment's Organisations list.
   */
  it('answers for the level it was asked about', () => {
    expect(activeKey({ level: 'deployment' }, '/system/organisations')).toBe('organisations');
    expect(activeKey({ level: 'org', orgId: 'o1' }, '/system/organisations/o1')).toBe('');
  });

  it('is null where no destination owns the path', () => {
    expect(activeKey({ level: 'deployment' }, '/account')).toBeNull();
  });
});

describe('the destination lists', () => {
  it('name each key once per level', () => {
    for (const [level, destinations] of Object.entries(DESTINATIONS)) {
      const keys = destinations.map(d => d.key);
      expect(new Set(keys).size, `${level} repeats a key`).toBe(keys.length);
    }
  });

  it('give every level its own page first', () => {
    for (const destinations of Object.values(DESTINATIONS)) {
      expect(destinations[0].key).toBe('');
    }
  });

  /**
   * `superOnly` hides a deployment entry from an administrator who is not one. It is meaningless
   * below the deployment — an organisation's own pages are reached by its own admin by
   * definition — and a stray flag there would hide a page from the person it belongs to.
   */
  it('restricts nothing below the deployment', () => {
    expect(DESTINATIONS.org.some(d => d.superOnly)).toBe(false);
    expect(DESTINATIONS.project.some(d => d.superOnly)).toBe(false);
  });
});

describe('the route patterns', () => {
  /**
   * The router mounts `ROUTE_BASES`; the links are built by `basePath`. If the two ever describe
   * different shapes, every link of that level leads to the catch-all — which looks like a routing
   * bug and is really a second description of the same fact.
   */
  it.each([
    ['deployment' as const, { level: 'deployment' } as Scope,                            0],
    ['org' as const,        { level: 'org' } as Scope,                                   0],
    ['org' as const,        { level: 'org', orgId: ':id' } as Scope,                      1],
    ['project' as const,    { level: 'project' } as Scope,                               0],
    ['project' as const,    { level: 'project', orgId: ':oid', projectId: ':pid' } as Scope, 1],
  ])('%s pattern %i is what basePath builds', (level, scope, index) => {
    expect(basePath(scope)).toBe(ROUTE_BASES[level][index]);
  });

  it('gives every level at least one pattern', () => {
    for (const [level, bases] of Object.entries(ROUTE_BASES)) {
      expect(bases.length, `${level} has no route`).toBeGreaterThan(0);
    }
  });
});
