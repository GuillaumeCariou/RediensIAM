import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { useAuth } from '@/context/AuthContext';

// ── Inline SVG icons (16×16 Lucide-compatible paths) ─────────────────────────

function Icon({ path, size = 15 }: Readonly<{ path: string; size?: number }>) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d={path} />
    </svg>
  );
}

const ICONS = {
  shield:    'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
  dashboard: 'M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  building:  'M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM9 22V12h6v10',
  users:     'M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75M9 7a4 4 0 1 1 0-8 4 4 0 0 1 0 8z',
  list:      'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  folder:    'M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z',
  bot:       'M12 8V4H8M8 8H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V10a2 2 0 0 0-2-2h-4M8 8h8M12 12v4M10 14h4',
  mail:      'M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2zM22 6l-10 7L2 6',
  log:       'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M16 13H8M16 17H8M10 9H8',
  chart:     'M18 20V10M12 20V4M6 20v-6',
  heart:     'M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z',
  key:       'M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4',
  settings:  'M12 20h9M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z',
  zap:       'M13 2L3 14h9l-1 8 10-12h-9l1-8z',
  user:      'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z',
  logout:    'M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9',
  chevRight: 'M9 18l6-6-6-6',
  chevDown:  'M6 9l6 6 6-6',
} as const;

// ── Nav definitions ────────────────────────────────────────────────────────────

interface NavItem { label: string; to: string; icon: keyof typeof ICONS; superOnly?: boolean; exact?: boolean; }

const systemNav: NavItem[] = [
  { label: 'Dashboard',        to: '/system',                  icon: 'dashboard', exact: true },
  { label: 'Organisations',    to: '/system/organisations',    icon: 'building' },
  { label: 'Admins',           to: '/system/admins',           icon: 'shield',   superOnly: true },
  { label: 'Users',            to: '/system/users',            icon: 'users',    superOnly: true },
  { label: 'Projects',         to: '/system/projects',         icon: 'folder',   superOnly: true },
  { label: 'User Lists',       to: '/system/userlists',        icon: 'list',     superOnly: true },
  { label: 'Service Accounts', to: '/system/service-accounts', icon: 'bot',      superOnly: true },
  { label: 'Email',            to: '/system/email',            icon: 'mail',     superOnly: true },
  { label: 'Audit Log',        to: '/system/audit-log',        icon: 'log' },
  { label: 'Metrics',          to: '/system/metrics',          icon: 'chart' },
  { label: 'Health',           to: '/system/health',           icon: 'heart',    superOnly: true },
];

const orgNav: NavItem[] = [
  { label: 'Overview',         to: '/org',                   icon: 'dashboard', exact: true },
  { label: 'Projects',         to: '/org/projects',          icon: 'folder' },
  { label: 'User Lists',       to: '/org/userlists',         icon: 'list' },
  { label: 'Admins',           to: '/org/admins',            icon: 'shield' },
  { label: 'Service Accounts', to: '/org/service-accounts',  icon: 'bot' },
  { label: 'Email',            to: '/org/email',             icon: 'mail' },
  { label: 'Audit Log',        to: '/org/audit-log',         icon: 'log' },
  { label: 'Webhooks',         to: '/org/webhooks',          icon: 'zap' },
  { label: 'Settings',         to: '/org/settings',          icon: 'settings' },
];

const projectNav: NavItem[] = [
  { label: 'Overview',         to: '/project',                   icon: 'dashboard', exact: true },
  { label: 'Users',            to: '/project/users',             icon: 'users' },
  { label: 'Roles',            to: '/project/roles',             icon: 'shield' },
  { label: 'Service Accounts', to: '/project/service-accounts',  icon: 'bot' },
  { label: 'Authentication',   to: '/project/authentication',    icon: 'key' },
  { label: 'Settings',         to: '/project/settings',         icon: 'settings' },
];

// ── Helpers ────────────────────────────────────────────────────────────────────

function roleLabel(isSuperAdmin: boolean, isOrgAdmin: boolean): string {
  if (isSuperAdmin) return 'super_admin';
  if (isOrgAdmin) return 'org_admin';
  return 'project_admin';
}

function isActive(item: NavItem, pathname: string): boolean {
  return item.exact ? pathname === item.to : pathname.startsWith(item.to);
}

function buildSysOrgNav(base: string): NavItem[] {
  return [
    { label: 'Overview',         to: base,                         icon: 'dashboard', exact: true },
    { label: 'Projects',         to: `${base}/projects`,           icon: 'folder' },
    { label: 'User Lists',       to: `${base}/userlists`,          icon: 'list' },
    { label: 'Admins',           to: `${base}/admins`,             icon: 'shield' },
    { label: 'Service Accounts', to: `${base}/service-accounts`,   icon: 'bot' },
    { label: 'Email',            to: `${base}/email`,              icon: 'mail' },
    { label: 'Audit Log',        to: `${base}/audit-log`,          icon: 'log' },
    { label: 'Webhooks',         to: `${base}/webhooks`,           icon: 'zap' },
    { label: 'Settings',         to: `${base}/settings`,           icon: 'settings' },
  ];
}

function buildSysProjNav(base: string): NavItem[] {
  return [
    { label: 'Overview',         to: base,                           icon: 'dashboard', exact: true },
    { label: 'Users',            to: `${base}/users`,                icon: 'users' },
    { label: 'Roles',            to: `${base}/roles`,                icon: 'shield' },
    { label: 'Service Accounts', to: `${base}/service-accounts`,     icon: 'bot' },
    { label: 'Authentication',   to: `${base}/authentication`,       icon: 'key' },
    { label: 'Settings',         to: `${base}/settings`,             icon: 'settings' },
  ];
}

// ── NavLink ────────────────────────────────────────────────────────────────────

function NavLink({ item, active, superAdmin }: Readonly<{ item: NavItem; active: boolean; superAdmin: boolean }>) {
  if (item.superOnly && !superAdmin) return null;
  return (
    <Link to={item.to} className={`iam-nav-item${active ? ' active' : ''}`}>
      <span className="iam-nav-icon"><Icon path={ICONS[item.icon]} size={15} /></span>
      {item.label}
    </Link>
  );
}

// ── AccordionSection ──────────────────────────────────────────────────────────

interface AccordionProps {
  label: string;
  iconKey: keyof typeof ICONS;
  open: boolean;
  onToggle: () => void;
  highlight?: boolean;
  children: React.ReactNode;
}

function AccordionSection({ label, iconKey, open, onToggle, highlight, children }: Readonly<AccordionProps>) {
  return (
    <div className={`iam-nav-section${highlight ? ' iam-nav-section-highlight' : ''}`}>
      <button className={`iam-nav-section-header${open ? ' open' : ''}`} onClick={onToggle}>
        <Icon path={ICONS[iconKey]} size={11} />
        <span>{label}</span>
        <span className="iam-chev"><Icon path={ICONS.chevRight} size={11} /></span>
      </button>
      {open && <div className="iam-nav-items">{children}</div>}
    </div>
  );
}

// ── User popover ───────────────────────────────────────────────────────────────

function UserPopover({ onClose }: Readonly<{ onClose: () => void }>) {
  const navigate = useNavigate();
  const { logout } = useAuth();
  return (
    <div style={{
      position: 'absolute', bottom: '100%', left: 0, marginBottom: 6, zIndex: 50,
      background: 'var(--surface)', border: '1px solid var(--border)',
      borderRadius: 'var(--iam-radius)', boxShadow: 'var(--shadow-lg)',
      minWidth: 180, padding: '4px',
    }}>
      <button className="iam-nav-item" style={{ width: '100%' }}
        onClick={() => { navigate('/account'); onClose(); }}>
        <span className="iam-nav-icon"><Icon path={ICONS.user} size={14} /></span>{' '}My Account
      </button>
      <div style={{ height: 1, background: 'var(--border)', margin: '4px 8px' }} />
      <button className="iam-nav-item" style={{ width: '100%', color: 'var(--danger)' }}
        onClick={() => { logout(); onClose(); }}>
        <span className="iam-nav-icon"><Icon path={ICONS.logout} size={14} /></span>{' '}Sign out
      </button>
    </div>
  );
}

// ── Sidebar ────────────────────────────────────────────────────────────────────

interface ScopeDerived {
  urlOrgId: string;
  urlProjectId: string;
  sysOrgBase: string;
  sysProjBase: string;
}

function deriveScope(pathname: string): ScopeDerived {
  const sysProjMatch = /^\/system\/organisations\/([^/]+)\/projects\/([^/]+)/.exec(pathname);
  const sysOrgMatch  = /^\/system\/organisations\/([^/]+)/.exec(pathname);
  const urlOrgId      = sysOrgMatch?.[1]  ?? '';
  const urlProjectId  = sysProjMatch?.[2] ?? '';
  const urlOrgForProj = sysProjMatch?.[1] ?? '';
  return {
    urlOrgId,
    urlProjectId,
    sysOrgBase:  urlOrgId     ? `/system/organisations/${urlOrgId}`                               : '',
    sysProjBase: urlProjectId ? `/system/organisations/${urlOrgForProj}/projects/${urlProjectId}` : '',
  };
}

function pickActiveSection(pathname: string, scope: ScopeDerived, isSuperAdmin: boolean): 'system' | 'org' | 'project' | null {
  const projectActive = isSuperAdmin ? scope.urlProjectId !== '' : pathname.startsWith('/project');
  if (projectActive) return 'project';
  const orgActive = isSuperAdmin ? scope.urlOrgId !== '' : pathname.startsWith('/org');
  if (orgActive) return 'org';
  if (pathname.startsWith('/system')) return 'system';
  return null;
}

function pickProjectVisibility(pathname: string, scope: ScopeDerived, roles: { isSuperAdmin: boolean; isOrgAdmin: boolean; isProjectManager: boolean }): boolean {
  if (roles.isSuperAdmin) return scope.urlProjectId !== '';
  if (roles.isOrgAdmin)   return pathname.startsWith('/project');
  return roles.isProjectManager;
}

export default function Sidebar() {
  const { pathname } = useLocation();
  const { isSuperAdmin, isOrgAdmin, isProjectManager } = useAuth();
  const [userOpen, setUserOpen] = useState(false);
  const userRef = useRef<HTMLDivElement>(null);

  const scope = deriveScope(pathname);
  const { urlOrgId, urlProjectId, sysOrgBase, sysProjBase } = scope;

  const showSystem  = isSuperAdmin;
  const showOrg     = isSuperAdmin ? urlOrgId !== '' : isOrgAdmin;
  const showProject = pickProjectVisibility(pathname, scope, { isSuperAdmin, isOrgAdmin, isProjectManager });
  const activeSection = pickActiveSection(pathname, scope, isSuperAdmin);

  const [systemOpen,  setSystemOpen]  = useState(pathname.startsWith('/system'));
  const [orgOpen,     setOrgOpen]     = useState(isSuperAdmin ? urlOrgId !== '' : pathname.startsWith('/org'));
  const [projectOpen, setProjectOpen] = useState(isSuperAdmin ? urlProjectId !== '' : pathname.startsWith('/project'));

  const prevSection = useRef(activeSection);
  useEffect(() => {
    const prev = prevSection.current;
    if (prev === activeSection) return;
    prevSection.current = activeSection;
    if (prev === 'system') setSystemOpen(false);
    if (prev === 'org')    setOrgOpen(false);
    if (prev === 'project') setProjectOpen(false);
    if (activeSection === 'system') setSystemOpen(true);
    if (activeSection === 'org')    setOrgOpen(true);
    if (activeSection === 'project') setProjectOpen(true);
  }, [activeSection]);

  useEffect(() => {
    if (!userOpen) return;
    const h = (e: MouseEvent) => { if (!userRef.current?.contains(e.target as Node)) setUserOpen(false); };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, [userOpen]);

  const activeOrgNav     = isSuperAdmin && sysOrgBase  ? buildSysOrgNav(sysOrgBase)   : orgNav;
  const activeProjectNav = isSuperAdmin && sysProjBase ? buildSysProjNav(sysProjBase) : projectNav;

  const orgLabel     = urlOrgId     ? `Org · ${urlOrgId.slice(0, 8)}…`     : 'Organisation';
  const projectLabel = urlProjectId ? `Proj · ${urlProjectId.slice(0, 8)}…` : 'Project';

  return (
    <aside className="iam-sidebar">
      <div className="iam-sidebar-brand">
        <div className="iam-brand-mark">R</div>
        <span>RediensIAM</span>
        <span className="mono" style={{ marginLeft: 'auto', fontSize: 10, color: 'var(--fg-subtle)' }}>v0.1</span>
      </div>

      <div className="iam-sidebar-nav">
        {showSystem && (
          <AccordionSection label="System" iconKey="shield"
            open={systemOpen} onToggle={() => setSystemOpen(o => !o)}
            highlight={activeSection === 'system'}>
            {systemNav.map(item => (
              <NavLink key={item.to} item={item} active={isActive(item, pathname)} superAdmin={isSuperAdmin} />
            ))}
          </AccordionSection>
        )}

        {showOrg && activeOrgNav.length > 0 && (
          <AccordionSection label={orgLabel} iconKey="building"
            open={orgOpen} onToggle={() => setOrgOpen(o => !o)}
            highlight={activeSection === 'org'}>
            {activeOrgNav.map(item => (
              <NavLink key={item.to} item={item} active={isActive(item, pathname)} superAdmin={isSuperAdmin} />
            ))}
          </AccordionSection>
        )}

        {showProject && activeProjectNav.length > 0 && (
          <AccordionSection label={projectLabel} iconKey="folder"
            open={projectOpen} onToggle={() => setProjectOpen(o => !o)}
            highlight={activeSection === 'project'}>
            {activeProjectNav.map(item => (
              <NavLink key={item.to} item={item} active={isActive(item, pathname)} superAdmin={isSuperAdmin} />
            ))}
          </AccordionSection>
        )}
      </div>

      <div className="iam-sidebar-footer" ref={userRef} style={{ position: 'relative' }}>
        {userOpen && <UserPopover onClose={() => setUserOpen(false)} />}
        <div style={{ width: 24, height: 24, borderRadius: '50%', background: 'var(--iam-sidebar-accent, var(--surface-2))', display: 'grid', placeItems: 'center', flexShrink: 0 }}>
          <Icon path={ICONS.user} size={13} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="mono" style={{ fontSize: 10, color: 'var(--iam-sidebar-muted, var(--fg-subtle))' }}>
            {roleLabel(isSuperAdmin, isOrgAdmin)}
          </div>
        </div>
        <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
          title="Account & sign out"
          onClick={() => setUserOpen(o => !o)}>
          <Icon path={ICONS.chevDown} size={13} />
        </button>
      </div>
    </aside>
  );
}
