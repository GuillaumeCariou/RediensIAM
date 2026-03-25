import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  LayoutDashboard, Building2, Users, List, FolderKanban,
  Shield, Bot, ScrollText, BarChart3, LogOut, ChevronRight,
  Sun, Moon, Monitor, Palette, UserCog, User,
} from 'lucide-react';
import { useTheme, type Theme } from '@/context/ThemeContext';
import { cn } from '@/lib/utils';
import { useAuth } from '@/context/AuthContext';

interface NavItem {
  label: string;
  to: string;
  icon: React.ReactNode;
  superOnly?: boolean;
  exact?: boolean;
}

// ── Static nav definitions ─────────────────────────────────────────

const systemNav: NavItem[] = [
  { label: 'Dashboard',        to: '/system',                  icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
  { label: 'Organisations',    to: '/system/organisations',    icon: <Building2 className="h-4 w-4" /> },
  { label: 'Admins',           to: '/system/admins',           icon: <UserCog className="h-4 w-4" />,     superOnly: true },
  { label: 'Users',            to: '/system/users',            icon: <Users className="h-4 w-4" />,       superOnly: true },
  { label: 'User Lists',       to: '/system/userlists',        icon: <List className="h-4 w-4" />,        superOnly: true },
  { label: 'Service Accounts', to: '/system/service-accounts', icon: <Bot className="h-4 w-4" />,         superOnly: true },
  { label: 'Audit Log',        to: '/system/audit-log',        icon: <ScrollText className="h-4 w-4" /> },
  { label: 'Metrics',          to: '/system/metrics',          icon: <BarChart3 className="h-4 w-4" /> },
];

const orgNav: NavItem[] = [
  { label: 'Overview',         to: '/org',                   icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
  { label: 'User Lists',       to: '/org/userlists',         icon: <List className="h-4 w-4" /> },
  { label: 'Projects',         to: '/org/projects',          icon: <FolderKanban className="h-4 w-4" /> },
  { label: 'Admins',           to: '/org/admins',            icon: <Shield className="h-4 w-4" /> },
  { label: 'Service Accounts', to: '/org/service-accounts',  icon: <Bot className="h-4 w-4" /> },
  { label: 'Audit Log',        to: '/org/audit-log',         icon: <ScrollText className="h-4 w-4" /> },
];

const projectNav: NavItem[] = [
  { label: 'Overview',         to: '/project',                   icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
  { label: 'Users',            to: '/project/users',             icon: <Users className="h-4 w-4" /> },
  { label: 'Roles',            to: '/project/roles',             icon: <Shield className="h-4 w-4" /> },
  { label: 'Service Accounts', to: '/project/service-accounts',  icon: <Bot className="h-4 w-4" /> },
  { label: 'Login Theme',      to: '/project/theme',             icon: <Palette className="h-4 w-4" /> },
];

const themeOptions: { value: Theme; icon: React.ReactNode; label: string }[] = [
  { value: 'system', icon: <Monitor className="h-4 w-4" />, label: 'System' },
  { value: 'light',  icon: <Sun     className="h-4 w-4" />, label: 'Light'  },
  { value: 'dark',   icon: <Moon    className="h-4 w-4" />, label: 'Dark'   },
];

// ── Sidebar ────────────────────────────────────────────────────────

export default function Sidebar() {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const { isSuperAdmin, isOrgAdmin, isProjectManager, logout } = useAuth();
  const { theme, setTheme } = useTheme();

  // ── Parse URL context for super_admin ─────────────────────────
  // Matches /system/organisations/:orgId[/projects/:projectId[/...]]
  const sysProjMatch  = pathname.match(/^\/system\/organisations\/([^/]+)\/projects\/([^/]+)/);
  const sysOrgMatch   = pathname.match(/^\/system\/organisations\/([^/]+)/);
  const urlOrgId      = sysOrgMatch?.[1]  ?? '';
  const urlProjectId  = sysProjMatch?.[2] ?? '';
  const urlOrgForProj = sysProjMatch?.[1] ?? '';
  const sysOrgBase    = urlOrgId      ? `/system/organisations/${urlOrgId}`                              : '';
  const sysProjBase   = urlProjectId  ? `/system/organisations/${urlOrgForProj}/projects/${urlProjectId}` : '';

  const onProjectPath = pathname.startsWith('/project');

  // ── Visibility rules ──────────────────────────────────────────
  const showSystem = isSuperAdmin;

  // Org section: always for pure org_admin; for super_admin only when inside an org URL
  const showOrg = isSuperAdmin
    ? urlOrgId !== ''
    : isOrgAdmin;

  // Project section: pure project_manager always; org_admin only when on /project/*;
  // super_admin only when inside a project URL
  const showProject = isSuperAdmin
    ? urlProjectId !== ''
    : isOrgAdmin
      ? onProjectPath
      : isProjectManager;

  // ── Contextual nav for super_admin ────────────────────────────
  // Super_admin can't use /org/* or /project/* (no orgId/projectId in token).
  // We build system-level links for the selected org / project instead.
  const sysOrgNav: NavItem[] = sysOrgBase ? [
    { label: 'Overview',         to: sysOrgBase,                              icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
    { label: 'User Lists',       to: `${sysOrgBase}/userlists`,               icon: <List          className="h-4 w-4" /> },
    { label: 'Projects',         to: `${sysOrgBase}/projects`,                icon: <FolderKanban  className="h-4 w-4" /> },
    { label: 'Admins',           to: `${sysOrgBase}/admins`,                  icon: <Shield        className="h-4 w-4" /> },
    { label: 'Service Accounts', to: `${sysOrgBase}/service-accounts`,        icon: <Bot           className="h-4 w-4" /> },
    { label: 'Audit Log',        to: `${sysOrgBase}/audit-log`,               icon: <ScrollText    className="h-4 w-4" /> },
  ] : [];

  const sysProjNav: NavItem[] = sysProjBase ? [
    { label: 'Overview',         to: sysProjBase,                            icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
    { label: 'Users',            to: `${sysProjBase}/users`,                 icon: <Users           className="h-4 w-4" /> },
    { label: 'Roles',            to: `${sysProjBase}/roles`,                 icon: <Shield          className="h-4 w-4" /> },
    { label: 'Service Accounts', to: `${sysProjBase}/service-accounts`,      icon: <Bot             className="h-4 w-4" /> },
    { label: 'Login Theme',      to: `${sysProjBase}/theme`,                 icon: <Palette         className="h-4 w-4" /> },
  ] : [];

  // ── Which nav lists to render ─────────────────────────────────
  const activeOrgNav     = isSuperAdmin ? sysOrgNav     : orgNav;
  const activeProjectNav = isSuperAdmin ? sysProjNav    : projectNav;

  // ── Helpers ───────────────────────────────────────────────────
  const isActive = (item: NavItem) =>
    item.exact ? pathname === item.to : pathname.startsWith(item.to);

  const nextTheme = () => {
    const idx = themeOptions.findIndex(o => o.value === theme);
    setTheme(themeOptions[(idx + 1) % themeOptions.length].value);
  };
  const current = themeOptions.find(o => o.value === theme) ?? themeOptions[0];

  const NavLink = ({ item }: { item: NavItem }) => {
    if (item.superOnly && !isSuperAdmin) return null;
    const active = isActive(item);
    return (
      <Link
        to={item.to}
        className={cn(
          'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
          active
            ? 'bg-sidebar-accent text-sidebar-accent-foreground'
            : 'text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground'
        )}
      >
        {item.icon}
        {item.label}
        {active && <ChevronRight className="ml-auto h-3 w-3" />}
      </Link>
    );
  };

  const SectionLabel = ({ label }: { label: string }) => (
    <p className="px-3 mb-1 text-xs font-semibold text-sidebar-foreground/40 uppercase tracking-wider">
      {label}
    </p>
  );

  return (
    <aside className="flex h-screen w-60 flex-col bg-sidebar text-sidebar-foreground border-r border-sidebar-border">
      {/* Logo */}
      <div className="flex h-14 items-center gap-2 border-b border-sidebar-border px-4">
        <Shield className="h-6 w-6 text-primary" />
        <span className="font-bold text-lg tracking-tight">RediensIAM</span>
      </div>

      {/* Navigation */}
      <div className="flex flex-1 flex-col overflow-y-auto p-3 gap-4">

        {/* System section — super_admin only, always visible */}
        {showSystem && (
          <div className="rounded-lg border border-primary/20 bg-primary/5 p-2">
            <div className="flex items-center gap-1.5 px-1 mb-1.5">
              <Shield className="h-3 w-3 text-primary" />
              <p className="text-xs font-semibold text-primary/80 uppercase tracking-wider">System</p>
            </div>
            <nav className="space-y-0.5">
              {systemNav.map(item => <NavLink key={item.to} item={item} />)}
            </nav>
          </div>
        )}

        {/* Org section — org_admin always; super_admin when inside an org URL */}
        {showOrg && activeOrgNav.length > 0 && (
          <div>
            <SectionLabel label="Organisation" />
            <nav className="space-y-0.5">
              {activeOrgNav.map(item => <NavLink key={item.to} item={item} />)}
            </nav>
          </div>
        )}

        {/* Project section — project_manager always; org_admin when on /project/*;
            super_admin when inside a project URL */}
        {showProject && activeProjectNav.length > 0 && (
          <div>
            <SectionLabel label="Project" />
            <nav className="space-y-0.5">
              {activeProjectNav.map(item => <NavLink key={item.to} item={item} />)}
            </nav>
          </div>
        )}

      </div>

      {/* Footer */}
      <div className="border-t border-sidebar-border p-3 space-y-0.5">
        <button
          onClick={() => navigate('/account')}
          className={cn(
            'flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
            pathname === '/account'
              ? 'bg-sidebar-accent text-sidebar-accent-foreground'
              : 'text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground'
          )}
        >
          <User className="h-4 w-4" />
          My Account
        </button>
        <button
          onClick={nextTheme}
          title={`Theme: ${current.label} (click to change)`}
          className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground transition-colors"
        >
          {current.icon}
          {current.label}
        </button>
        <button
          onClick={logout}
          className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground transition-colors"
        >
          <LogOut className="h-4 w-4" />
          Sign out
        </button>
      </div>
    </aside>
  );
}
