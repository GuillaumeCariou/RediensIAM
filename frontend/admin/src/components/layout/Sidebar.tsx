import { Link, useLocation } from 'react-router-dom';
import {
  LayoutDashboard, Building2, Users, List, FolderKanban,
  Shield, Bot, ScrollText, BarChart3, Key, LogOut, ChevronRight,
  Sun, Moon, Monitor,
} from 'lucide-react';
import { useTheme, type Theme } from '@/context/ThemeContext';
import { cn } from '@/lib/utils';
import { useAuth } from '@/context/AuthContext';

interface NavItem {
  label: string;
  to: string;
  icon: React.ReactNode;
  superOnly?: boolean;
}

const systemNav: NavItem[] = [
  { label: 'Dashboard', to: '/system', icon: <LayoutDashboard className="h-4 w-4" /> },
  { label: 'Organisations', to: '/system/organisations', icon: <Building2 className="h-4 w-4" /> },
  { label: 'Users', to: '/system/users', icon: <Users className="h-4 w-4" />, superOnly: true },
  { label: 'User Lists', to: '/system/userlists', icon: <List className="h-4 w-4" />, superOnly: true },
  { label: 'Service Accounts', to: '/system/service-accounts', icon: <Bot className="h-4 w-4" />, superOnly: true },
  { label: 'Hydra Clients', to: '/system/hydra-clients', icon: <Key className="h-4 w-4" />, superOnly: true },
  { label: 'Audit Log', to: '/system/audit-log', icon: <ScrollText className="h-4 w-4" /> },
  { label: 'Metrics', to: '/system/metrics', icon: <BarChart3 className="h-4 w-4" /> },
];

const orgNav: NavItem[] = [
  { label: 'Overview', to: '/org', icon: <LayoutDashboard className="h-4 w-4" /> },
  { label: 'User Lists', to: '/org/userlists', icon: <List className="h-4 w-4" /> },
  { label: 'Projects', to: '/org/projects', icon: <FolderKanban className="h-4 w-4" /> },
  { label: 'Admins', to: '/org/admins', icon: <Shield className="h-4 w-4" /> },
  { label: 'Service Accounts', to: '/org/service-accounts', icon: <Bot className="h-4 w-4" /> },
  { label: 'Audit Log', to: '/org/audit-log', icon: <ScrollText className="h-4 w-4" /> },
];

const themeOptions: { value: Theme; icon: React.ReactNode; label: string }[] = [
  { value: 'system', icon: <Monitor className="h-4 w-4" />, label: 'System' },
  { value: 'light',  icon: <Sun    className="h-4 w-4" />, label: 'Light'  },
  { value: 'dark',   icon: <Moon   className="h-4 w-4" />, label: 'Dark'   },
];

export default function Sidebar() {
  const { pathname } = useLocation();
  const { isSuperAdmin, isOrgAdmin, logout } = useAuth();
  const { theme, setTheme } = useTheme();

  const nextTheme = () => {
    const idx = themeOptions.findIndex(o => o.value === theme);
    setTheme(themeOptions[(idx + 1) % themeOptions.length].value);
  };
  const current = themeOptions.find(o => o.value === theme) ?? themeOptions[0];

  const isActive = (to: string) => pathname === to || (to !== '/system' && to !== '/org' && pathname.startsWith(to));

  const NavLink = ({ item }: { item: NavItem }) => {
    if (item.superOnly && !isSuperAdmin) return null;
    return (
      <Link
        to={item.to}
        className={cn(
          'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
          isActive(item.to)
            ? 'bg-sidebar-accent text-sidebar-accent-foreground'
            : 'text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground'
        )}
      >
        {item.icon}
        {item.label}
        {isActive(item.to) && <ChevronRight className="ml-auto h-3 w-3" />}
      </Link>
    );
  };

  return (
    <aside className="flex h-screen w-60 flex-col bg-sidebar text-sidebar-foreground border-r border-sidebar-border">
      {/* Logo */}
      <div className="flex h-14 items-center gap-2 border-b border-sidebar-border px-4">
        <Shield className="h-6 w-6 text-primary" />
        <span className="font-bold text-lg tracking-tight">RediensIAM</span>
      </div>

      {/* Navigation */}
      <div className="flex flex-1 flex-col overflow-y-auto p-3 gap-6">
        {isSuperAdmin && (
          <div>
            <p className="px-3 mb-1 text-xs font-semibold text-sidebar-foreground/40 uppercase tracking-wider">System</p>
            <nav className="space-y-0.5">
              {systemNav.map(item => <NavLink key={item.to} item={item} />)}
            </nav>
          </div>
        )}

        {isOrgAdmin && (
          <div>
            <p className="px-3 mb-1 text-xs font-semibold text-sidebar-foreground/40 uppercase tracking-wider">Organisation</p>
            <nav className="space-y-0.5">
              {orgNav.map(item => <NavLink key={item.to} item={item} />)}
            </nav>
          </div>
        )}
      </div>

      {/* Footer */}
      <div className="border-t border-sidebar-border p-3 space-y-0.5">
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
