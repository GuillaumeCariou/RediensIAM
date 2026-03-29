import { useState, useEffect, useRef } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  LayoutDashboard, Building2, Users, List, FolderKanban,
  Shield, Bot, ScrollText, BarChart3, LogOut, ChevronRight,
  Sun, Moon, Monitor, KeyRound, Settings, UserCog, User, ChevronDown, Mail,
} from 'lucide-react';
import { useTheme, type Theme } from '@/context/ThemeContext';
import { cn } from '@/lib/utils';
import { useAuth } from '@/context/AuthContext';
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem,
  DropdownMenuSeparator, DropdownMenuTrigger, DropdownMenuLabel,
  DropdownMenuRadioGroup, DropdownMenuRadioItem,
} from '@/components/ui/dropdown-menu';

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
  { label: 'Projects',         to: '/system/projects',         icon: <FolderKanban className="h-4 w-4" />, superOnly: true },
  { label: 'User Lists',       to: '/system/userlists',        icon: <List className="h-4 w-4" />,        superOnly: true },
  { label: 'Service Accounts', to: '/system/service-accounts', icon: <Bot className="h-4 w-4" />,         superOnly: true },
  { label: 'Email',            to: '/system/email',            icon: <Mail className="h-4 w-4" />,        superOnly: true },
  { label: 'Audit Log',        to: '/system/audit-log',        icon: <ScrollText className="h-4 w-4" /> },
  { label: 'Metrics',          to: '/system/metrics',          icon: <BarChart3 className="h-4 w-4" /> },
];

const orgNav: NavItem[] = [
  { label: 'Overview',         to: '/org',                   icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
  { label: 'Projects',         to: '/org/projects',          icon: <FolderKanban className="h-4 w-4" /> },
  { label: 'User Lists',       to: '/org/userlists',         icon: <List className="h-4 w-4" /> },
  { label: 'Admins',           to: '/org/admins',            icon: <Shield className="h-4 w-4" /> },
  { label: 'Service Accounts', to: '/org/service-accounts',  icon: <Bot className="h-4 w-4" /> },
  { label: 'Email',            to: '/org/email',             icon: <Mail className="h-4 w-4" /> },
  { label: 'Audit Log',        to: '/org/audit-log',         icon: <ScrollText className="h-4 w-4" /> },
];

const projectNav: NavItem[] = [
  { label: 'Overview',         to: '/project',                   icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
  { label: 'Users',            to: '/project/users',             icon: <Users className="h-4 w-4" /> },
  { label: 'Roles',            to: '/project/roles',             icon: <Shield className="h-4 w-4" /> },
  { label: 'Service Accounts', to: '/project/service-accounts',  icon: <Bot className="h-4 w-4" /> },
  { label: 'Authentication',   to: '/project/authentication',    icon: <KeyRound className="h-4 w-4" /> },
  { label: 'Settings',         to: '/project/settings',          icon: <Settings className="h-4 w-4" /> },
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
  const sysProjMatch  = pathname.match(/^\/system\/organisations\/([^/]+)\/projects\/([^/]+)/);
  const sysOrgMatch   = pathname.match(/^\/system\/organisations\/([^/]+)/);
  const urlOrgId      = sysOrgMatch?.[1]  ?? '';
  const urlProjectId  = sysProjMatch?.[2] ?? '';
  const urlOrgForProj = sysProjMatch?.[1] ?? '';
  const sysOrgBase    = urlOrgId     ? `/system/organisations/${urlOrgId}`                               : '';
  const sysProjBase   = urlProjectId ? `/system/organisations/${urlOrgForProj}/projects/${urlProjectId}` : '';

  const onProjectPath = pathname.startsWith('/project');

  // ── Visibility rules ──────────────────────────────────────────
  const showSystem  = isSuperAdmin;
  const showOrg     = isSuperAdmin ? urlOrgId !== ''    : isOrgAdmin;
  const showProject = isSuperAdmin
    ? urlProjectId !== ''
    : isOrgAdmin ? onProjectPath : isProjectManager;

  // ── Accordion open state ──────────────────────────────────────
  const [systemOpen,  setSystemOpen]  = useState(pathname.startsWith('/system'));
  const [orgOpen,     setOrgOpen]     = useState(
    isSuperAdmin ? urlOrgId !== '' : pathname.startsWith('/org')
  );
  const [projectOpen, setProjectOpen] = useState(
    isSuperAdmin ? urlProjectId !== '' : pathname.startsWith('/project')
  );

  // Determine which top-level section the current path belongs to
  const activeSection =
    (isSuperAdmin ? urlProjectId !== '' : pathname.startsWith('/project')) ? 'project' :
    (isSuperAdmin ? urlOrgId !== ''     : pathname.startsWith('/org'))     ? 'org'     :
    pathname.startsWith('/system')                                          ? 'system'  : null;

  // On section transition: collapse the previous section, expand the new one.
  // Manual toggling in between is preserved (effect only fires when section changes).
  const prevSectionRef = useRef(activeSection);
  useEffect(() => {
    const prev = prevSectionRef.current;
    if (prev === activeSection) return;
    prevSectionRef.current = activeSection;

    if (prev === 'system')  setSystemOpen(false);
    if (prev === 'org')     setOrgOpen(false);
    if (prev === 'project') setProjectOpen(false);

    if (activeSection === 'system')  setSystemOpen(true);
    if (activeSection === 'org')     setOrgOpen(true);
    if (activeSection === 'project') setProjectOpen(true);
  }, [activeSection]);

  // ── Contextual nav for super_admin ────────────────────────────
  const sysOrgNav: NavItem[] = sysOrgBase ? [
    { label: 'Overview',         to: sysOrgBase,                         icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
    { label: 'Projects',         to: `${sysOrgBase}/projects`,           icon: <FolderKanban  className="h-4 w-4" /> },
    { label: 'User Lists',       to: `${sysOrgBase}/userlists`,          icon: <List          className="h-4 w-4" /> },
    { label: 'Admins',           to: `${sysOrgBase}/admins`,             icon: <Shield        className="h-4 w-4" /> },
    { label: 'Service Accounts', to: `${sysOrgBase}/service-accounts`,   icon: <Bot           className="h-4 w-4" /> },
    { label: 'Email',            to: `${sysOrgBase}/email`,              icon: <Mail          className="h-4 w-4" /> },
    { label: 'Audit Log',        to: `${sysOrgBase}/audit-log`,          icon: <ScrollText    className="h-4 w-4" /> },
  ] : [];

  const sysProjNav: NavItem[] = sysProjBase ? [
    { label: 'Overview',         to: sysProjBase,                          icon: <LayoutDashboard className="h-4 w-4" />, exact: true },
    { label: 'Users',            to: `${sysProjBase}/users`,               icon: <Users           className="h-4 w-4" /> },
    { label: 'Roles',            to: `${sysProjBase}/roles`,               icon: <Shield          className="h-4 w-4" /> },
    { label: 'Service Accounts', to: `${sysProjBase}/service-accounts`,    icon: <Bot             className="h-4 w-4" /> },
    { label: 'Authentication',   to: `${sysProjBase}/authentication`,      icon: <KeyRound        className="h-4 w-4" /> },
    { label: 'Settings',         to: `${sysProjBase}/settings`,            icon: <Settings        className="h-4 w-4" /> },
  ] : [];

  const activeOrgNav     = isSuperAdmin ? sysOrgNav  : orgNav;
  const activeProjectNav = isSuperAdmin ? sysProjNav : projectNav;

  // ── Helpers ───────────────────────────────────────────────────
  const isActive = (item: NavItem) =>
    item.exact ? pathname === item.to : pathname.startsWith(item.to);

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

  const AccordionSection = ({
    label, icon, open, onToggle, children, highlight,
  }: {
    label: string;
    icon: React.ReactNode;
    open: boolean;
    onToggle: () => void;
    children: React.ReactNode;
    highlight?: boolean;
  }) => (
    <div className={cn(
      'rounded-lg overflow-hidden',
      highlight && 'border border-primary/20 bg-primary/5',
    )}>
      <button
        onClick={onToggle}
        className={cn(
          'flex w-full items-center gap-2 px-3 py-2 text-xs font-semibold uppercase tracking-wider transition-colors',
          highlight
            ? 'text-primary/80 hover:text-primary'
            : 'text-sidebar-foreground/40 hover:text-sidebar-foreground/70',
        )}
      >
        {icon}
        <span className="flex-1 text-left">{label}</span>
        <ChevronDown className={cn('h-3 w-3 transition-transform duration-200', open && 'rotate-180')} />
      </button>
      {open && (
        <nav className="space-y-0.5 px-1 pb-1">
          {children}
        </nav>
      )}
    </div>
  );

  const currentTheme = themeOptions.find(o => o.value === theme) ?? themeOptions[0];

  return (
    <aside className="flex h-screen w-60 flex-col bg-sidebar text-sidebar-foreground border-r border-sidebar-border">
      {/* Logo + User menu */}
      <div className="flex h-14 items-center gap-2 border-b border-sidebar-border px-3">
        <Shield className="h-5 w-5 text-primary shrink-0" />
        <span className="font-bold text-base tracking-tight flex-1 truncate">RediensIAM</span>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button
              className="flex items-center gap-1 rounded-md p-1.5 text-sidebar-foreground/60 hover:bg-sidebar-accent hover:text-sidebar-foreground transition-colors"
              title="Account & settings"
            >
              <User className="h-4 w-4" />
              <ChevronDown className="h-3 w-3" />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-52">
            <DropdownMenuItem onClick={() => navigate('/account')}>
              <User className="h-4 w-4" />
              My Account
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs text-muted-foreground font-normal">Theme</DropdownMenuLabel>
            <DropdownMenuRadioGroup value={theme} onValueChange={v => setTheme(v as Theme)}>
              {themeOptions.map(opt => (
                <DropdownMenuRadioItem key={opt.value} value={opt.value} className="gap-2">
                  {opt.icon}{opt.label}
                </DropdownMenuRadioItem>
              ))}
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuItem onClick={logout} className="text-destructive focus:text-destructive">
              <LogOut className="h-4 w-4" />
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>

      {/* Navigation */}
      <div className="flex flex-1 flex-col overflow-y-auto p-3 gap-2">

        {showSystem && (
          <AccordionSection
            label="System"
            icon={<Shield className="h-3 w-3" />}
            open={systemOpen}
            onToggle={() => setSystemOpen(o => !o)}
            highlight
          >
            {systemNav.map(item => <NavLink key={item.to} item={item} />)}
          </AccordionSection>
        )}

        {showOrg && activeOrgNav.length > 0 && (
          <AccordionSection
            label="Organisation"
            icon={<Building2 className="h-3 w-3" />}
            open={orgOpen}
            onToggle={() => setOrgOpen(o => !o)}
          >
            {activeOrgNav.map(item => <NavLink key={item.to} item={item} />)}
          </AccordionSection>
        )}

        {showProject && activeProjectNav.length > 0 && (
          <AccordionSection
            label="Project"
            icon={<FolderKanban className="h-3 w-3" />}
            open={projectOpen}
            onToggle={() => setProjectOpen(o => !o)}
          >
            {activeProjectNav.map(item => <NavLink key={item.to} item={item} />)}
          </AccordionSection>
        )}

      </div>

      {/* Compact footer */}
      <div className="border-t border-sidebar-border px-4 py-2 flex items-center gap-2 text-xs text-sidebar-foreground/40">
        {currentTheme.icon}
        <span>{currentTheme.label} theme</span>
      </div>
    </aside>
  );
}
