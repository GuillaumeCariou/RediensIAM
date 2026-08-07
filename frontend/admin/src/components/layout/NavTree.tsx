import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useLocation } from 'react-router';
import { listOrgs, listProjects } from '@/api';
import { useAuth } from '@/context/AuthContext';
import { DESTINATIONS, activeKey, hrefFor, scopeFromPath, type Level, type Scope } from '@/scope';
import { MUTATION_EVENT } from '@/auth';

/**
 * The console's navigation, as one tree.
 *
 * The three accordions it replaces asked the reader to hold the answer to "which scope am I in"
 * in their head: the same page appeared under System, under Organisation and under Project, and
 * which one you were looking at depended on a URL prefix the sidebar re-derived for itself. The
 * tree makes the level a *place* — you are on a node, and its children are what that node has.
 *
 * Every href comes from `scope.ts`. This component knows how to draw a tree and nothing about how
 * a console URL is spelled.
 */

interface OrgNode { id: string; name: string; active: boolean }
interface ProjectNode { id: string; name: string }

/** A row in the tree: a twisty, an icon slot, a label, and an optional trailing chip. */
function Row({ depth, expandable, open, onToggle, to, label, active, chip, dot }: Readonly<{
  depth: number;
  expandable?: boolean;
  open?: boolean;
  onToggle?: () => void;
  to: string;
  label: string;
  active: boolean;
  chip?: string;
  dot?: 'success' | 'muted';
}>) {
  return (
    <div className="iam-trow" style={{ paddingLeft: depth * 13 }}>
      {expandable ? (
        <button
          type="button"
          className={`iam-twist${open ? ' open' : ''}`}
          onClick={onToggle}
          aria-expanded={open}
          aria-label={open ? `Collapse ${label}` : `Expand ${label}`}
        >
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><path d="M9 18l6-6-6-6" /></svg>
        </button>
      ) : (
        <span className="iam-twist" style={{ visibility: 'hidden' }} />
      )}
      <Link to={to} className={`iam-treeitem${active ? ' active' : ''}`}>
        {/* Spelled out rather than interpolated: theme.test.ts checks every iam-* class a
            component names against index.css, and a computed name is a class it cannot check. */}
        {dot === 'success' && <span className="iam-dot iam-dot-success" />}
        {dot === 'muted' && <span className="iam-dot iam-dot-muted" />}
        {label}
        {chip && <span className="iam-chip" style={{ marginLeft: 'auto', fontSize: 10 }}>{chip}</span>}
      </Link>
    </div>
  );
}

/** The destinations of one level, as leaf rows. */
function Destinations({ scope, depth, pathname, isSuperAdmin, filter }: Readonly<{
  scope: Scope; depth: number; pathname: string; isSuperAdmin: boolean; filter: string;
}>) {
  const active = activeKey(scope, pathname);
  return (
    <>
      {DESTINATIONS[scope.level]
        .filter(d => d.key !== '')
        .filter(d => !d.superOnly || isSuperAdmin)
        .filter(d => matches(d.label, filter))
        .map(d => (
          <Row key={d.key} depth={depth} to={hrefFor(scope, d.key)} label={d.label} active={d.key === active} />
        ))}
    </>
  );
}

/** Case-insensitive substring, with an empty filter matching everything. */
function matches(label: string, filter: string): boolean {
  return filter.trim() === '' || label.toLowerCase().includes(filter.trim().toLowerCase());
}

export default function NavTree() {
  const { pathname } = useLocation();
  const { isSuperAdmin, isOrgAdmin, isProjectManager } = useAuth();
  const here = scopeFromPath(pathname);

  const [filter, setFilter] = useState('');
  const [orgs, setOrgs] = useState<OrgNode[]>([]);
  const [projects, setProjects] = useState<Record<string, ProjectNode[]>>({});
  // The node you are on is open on arrival; everything else starts closed. Opening is the one
  // piece of state the tree owns — the rest is the URL.
  const [open, setOpen] = useState<Record<string, boolean>>(() => ({
    deployment: here.level === 'deployment' || !here.orgId,
    ...(here.orgId ? { [here.orgId]: true } : {}),
  }));

  const toggle = useCallback((key: string) => {
    setOpen(o => ({ ...o, [key]: !o[key] }));
  }, []);

  /**
   * Reloads when something is written that this tree draws.
   *
   * The tenant list was fetched once, at mount, so an operator who created a tenant saw it appear
   * on the page and not in the navigation until they reloaded — the console disagreeing with
   * itself about what exists. `apiFetch` announces every successful write; only the paths this
   * tree actually reads are worth a refetch, so editing a user does not redraw the sidebar.
   */
  const [writes, setWrites] = useState(0);
  useEffect(() => {
    const onWrite = (e: Event) => {
      const path = (e as CustomEvent<{ path?: string }>).detail?.path ?? '';
      if (/\/(organizations|projects)(\/|$)/.test(path)) setWrites(n => n + 1);
    };
    globalThis.addEventListener(MUTATION_EVENT, onWrite);
    return () => globalThis.removeEventListener(MUTATION_EVENT, onWrite);
  }, []);

  useEffect(() => {
    if (!isSuperAdmin) return;
    listOrgs()
      .then((r: OrgNode[]) => setOrgs(r ?? []))
      .catch(console.error);
    // Dropped rather than merged: a project created, renamed or deleted changes one organisation's
    // children, and clearing the cache lets the effect below refetch exactly the ones still open.
    setProjects({});
  }, [isSuperAdmin, writes]);

  // Projects are fetched when their organisation opens, not up front: a deployment with fifty
  // tenants would otherwise make fifty requests to draw a sidebar.
  useEffect(() => {
    const wanted = orgs.filter(o => open[o.id] && !projects[o.id]);
    if (wanted.length === 0) return;
    for (const org of wanted) {
      listProjects(org.id)
        .then((r: ProjectNode[]) => setProjects(p => ({ ...p, [org.id]: r ?? [] })))
        .catch(console.error);
    }
  }, [orgs, open, projects]);

  const visibleOrgs = useMemo(
    () => orgs.filter(o => matches(o.name, filter) || DESTINATIONS.org.some(d => matches(d.label, filter))),
    [orgs, filter],
  );

  return (
    <div className="iam-navtree">
      <div style={{ padding: '10px 10px 0' }}>
        <input
          className="iam-input"
          value={filter}
          onChange={e => setFilter(e.target.value)}
          placeholder="Filter the tree…"
          aria-label="Filter the tree"
        />
      </div>

      <div style={{ flex: 1, padding: '8px 10px', overflowY: 'auto' }} role="tree" aria-label="Console navigation">
        {isSuperAdmin && (
          <>
            <Row
              depth={0}
              expandable
              open={open.deployment}
              onToggle={() => toggle('deployment')}
              to={hrefFor({ level: 'deployment' }, '')}
              label="Deployment"
              active={here.level === 'deployment' && activeKey(here, pathname) === ''}
            />
            {open.deployment && (
              <Destinations scope={{ level: 'deployment' }} depth={2} pathname={pathname} isSuperAdmin filter={filter} />
            )}
            <div className="iam-tree-label">Tenants · {orgs.length}</div>
          </>
        )}

        {isSuperAdmin && visibleOrgs.map(org => {
          const orgScope: Scope = { level: 'org', orgId: org.id };
          return (
            <div key={org.id}>
              <Row
                depth={0}
                expandable
                open={open[org.id]}
                onToggle={() => toggle(org.id)}
                to={hrefFor(orgScope, '')}
                label={org.name}
                active={here.level === 'org' && here.orgId === org.id && activeKey(orgScope, pathname) === ''}
                dot={org.active ? 'success' : 'muted'}
              />
              {open[org.id] && (
                <>
                  <Destinations scope={orgScope} depth={2} pathname={pathname} isSuperAdmin={isSuperAdmin} filter={filter} />
                  {(projects[org.id] ?? []).filter(p => matches(p.name, filter)).map(project => {
                    const projectScope: Scope = { level: 'project', orgId: org.id, projectId: project.id };
                    return (
                      <div key={project.id}>
                        <Row
                          depth={2}
                          expandable
                          open={open[project.id]}
                          onToggle={() => toggle(project.id)}
                          to={hrefFor(projectScope, '')}
                          label={project.name}
                          active={here.projectId === project.id && activeKey(projectScope, pathname) === ''}
                        />
                        {open[project.id] && (
                          <Destinations scope={projectScope} depth={4} pathname={pathname} isSuperAdmin={isSuperAdmin} filter={filter} />
                        )}
                      </div>
                    );
                  })}
                </>
              )}
            </div>
          );
        })}

        {/*
          A tenant's own administrator gets their level as the root: they have exactly one
          organisation and cannot browse to another, so a "Tenants" list of one would be a
          navigation control that never navigates.
        */}
        {!isSuperAdmin && isOrgAdmin && (
          <OwnLevel level="org" label="Organisation" pathname={pathname} filter={filter} />
        )}
        {!isSuperAdmin && !isOrgAdmin && isProjectManager && (
          <OwnLevel level="project" label="Project" pathname={pathname} filter={filter} />
        )}
      </div>
    </div>
  );
}

/** The caller's own level, rooted — no ids in the scope, so the short URL shape is used. */
function OwnLevel({ level, label, pathname, filter }: Readonly<{
  level: Level; label: string; pathname: string; filter: string;
}>) {
  const scope: Scope = { level };
  return (
    <>
      <Row depth={0} to={hrefFor(scope, '')} label={label} active={activeKey(scope, pathname) === ''} />
      <Destinations scope={scope} depth={2} pathname={pathname} isSuperAdmin={false} filter={filter} />
    </>
  );
}
