import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { useAuth } from '@/context/AuthContext';
import NavTree from './NavTree';
import { useTheme } from '@/context/ThemeContext';
import { getServerVersion } from '@/auth';

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



// ── Helpers ────────────────────────────────────────────────────────────────────

function roleLabel(isSuperAdmin: boolean, isOrgAdmin: boolean): string {
  if (isSuperAdmin) return 'super_admin';
  if (isOrgAdmin) return 'org_admin';
  return 'project_admin';
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
      <button type="button" className="iam-menu-item" style={{ width: '100%' }}
        onClick={() => { navigate('/account'); onClose(); }}>
        <span className="iam-nav-icon"><Icon path={ICONS.user} size={14} /></span>{' '}My Account
      </button>
      <div className="iam-menu-sep" />
      <button type="button" className="iam-menu-item iam-menu-item-danger" style={{ width: '100%' }}
        onClick={() => { logout(); onClose(); }}>
        <span className="iam-nav-icon"><Icon path={ICONS.logout} size={14} /></span>{' '}Sign out
      </button>
    </div>
  );
}

// ── Sidebar ────────────────────────────────────────────────────────────────────



export default function Sidebar() {
  const { pathname } = useLocation();
  const { isSuperAdmin, isOrgAdmin, isProjectManager } = useAuth();
  const [userOpen, setUserOpen] = useState(false);
  const { dark, toggleDark } = useTheme();
  // Read once at mount: /console/config has already been fetched by the time a session exists.
  const version = getServerVersion();
  const userRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!userOpen) return;
    const h = (e: MouseEvent) => { if (!userRef.current?.contains(e.target as Node)) setUserOpen(false); };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, [userOpen]);

  return (
    <aside className="iam-sidebar">
      <div className="iam-sidebar-brand">
        <div className="iam-brand-mark">R</div>
        <span>RediensIAM</span>
        {version && (
          <span className="iam-mono" style={{ fontSize: 10, color: 'var(--iam-sidebar-muted)' }}>v{version}</span>
        )}
        <button
          className="iam-sidebar-icon-btn"
          style={{ marginLeft: 'auto' }}
          onClick={toggleDark}
          aria-pressed={dark}
          title={dark ? 'Switch to light theme' : 'Switch to dark theme'}
        >
          {dark ? (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <circle cx="12" cy="12" r="5"/>
              <path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/>
            </svg>
          ) : (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/>
            </svg>
          )}
        </button>
      </div>

      <NavTree />
      <div className="iam-sidebar-footer" ref={userRef} style={{ position: 'relative' }}>
        {userOpen && <UserPopover onClose={() => setUserOpen(false)} />}
        <div style={{ width: 24, height: 24, borderRadius: '50%', background: 'var(--iam-sidebar-accent, var(--surface-2))', display: 'grid', placeItems: 'center', flexShrink: 0 }}>
          <Icon path={ICONS.user} size={13} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="iam-mono" style={{ fontSize: 10, color: 'var(--iam-sidebar-muted, var(--fg-subtle))' }}>
            {roleLabel(isSuperAdmin, isOrgAdmin)}
          </div>
        </div>
        <button type="button" className="iam-sidebar-icon-btn"
          title="Account & sign out"
          onClick={() => setUserOpen(o => !o)}>
          <Icon path={ICONS.chevDown} size={13} />
        </button>
      </div>
    </aside>
  );
}
