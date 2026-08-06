import { useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate, Outlet, useNavigate, useParams } from 'react-router';
import { useAuth } from './context/AuthContext';
import { AuthProvider } from './context/AuthProvider';
import { consumeReturnTo } from './context/returnTo';
import { ThemeProvider } from './context/ThemeProvider';
import { ScopeProvider } from './context/ScopeProvider';
import Shell from './components/layout/Shell';
import ServiceAccounts from './pages/shared/ServiceAccounts';
import AuditLog from './pages/shared/AuditLog';
import Impersonation from './pages/system/Impersonation';
import DeploymentSettings from './pages/system/DeploymentSettings';
import { DESTINATIONS, ROUTE_BASES, type Level } from './scope';

import AccountPage from './pages/account/AccountPage';

import SystemDashboard from './pages/system/Dashboard';
import Organisations from './pages/system/Organisations';
import SystemUsers from './pages/system/Users';
import SystemMetrics from './pages/system/Metrics';
import SystemEmail from './pages/system/SystemEmail';
import SystemAdmins from './pages/system/SystemAdmins';
import OrgDetail from './pages/system/OrgDetail';
import SystemProjectDetail from './pages/system/SystemProjectDetail';
import SystemProjects from './pages/system/SystemProjects';
import SystemHealth from './pages/system/SystemHealth';
import ServiceAccountDetail from './pages/shared/ServiceAccountDetail';
import OrgAdmins from './pages/shared/OrgAdmins';
import UserListDetail from './pages/shared/UserListDetail';

import OrgDashboard from './pages/org/OrgDashboard';
import UserLists from './pages/org/UserLists';
import Projects from './pages/org/Projects';
import OrgEmail from './pages/org/OrgEmail';
import OrgWebhooks from './pages/org/OrgWebhooks';
import OrgSettings from './pages/org/OrgSettings';

import ProjectDashboard from './pages/project/ProjectDashboard';
import ProjectUsers from './pages/project/ProjectUsers';
import ProjectRoles from './pages/project/ProjectRoles';
import Authentication from './pages/project/Authentication';
import ProjectSettings from './pages/project/ProjectSettings';

function Loading() {
  return (
    <div className="flex h-screen items-center justify-center">
      <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
    </div>
  );
}

function defaultPath(isSuperAdmin: boolean, isOrgAdmin: boolean) {
  if (isSuperAdmin) return '/system';
  if (isOrgAdmin) return '/org';
  return '/project';
}

function NoRolesError({ onLogout }: Readonly<{ onLogout: () => void }>) {
  return (
    <div className="flex h-screen flex-col items-center justify-center gap-4 p-8 text-center">
      <h1 className="text-xl font-semibold">No access</h1>
      <p className="max-w-md text-sm text-muted-foreground">
        Your account has no roles assigned in this system. An administrator must grant you a role
        before you can use the admin console.
      </p>
      <button className="iam-btn iam-btn-secondary" onClick={onLogout}>Log out</button>
    </div>
  );
}

/**
 * The `account` route below sits outside every role guard on purpose: any authenticated user
 * must be able to reach their own profile, MFA and sessions, including one whose only role was
 * just revoked. Do not fold it into one of the guarded blocks.
 *
 * The `roles.length === 0` branch exists to break an infinite Navigate loop: defaultPath returns
 * /project for a token with no roles, and the project guard would Navigate straight back to home
 * (also /project). It renders an error page with a logout button instead.
 */
function AppRoutes() {
  const { ready, authenticated, isSuperAdmin, isOrgAdmin, isProjectManager, roles, logout } = useAuth();
  const navigate = useNavigate();

  /**
   * Back to the page the sign-in interrupted, exactly once.
   *
   * Imperative rather than `<Navigate to={returnTo ?? home}>` in the catch-all: the callback path
   * matches no route, so the catch-all is what renders, and feeding it the destination made the two
   * take turns — it navigates to a path the router cannot match, which renders the catch-all again.
   * An effect that runs once, after the value has been consumed, terminates: a destination the
   * application cannot render falls through to the catch-all and lands on the scope home.
   */
  useEffect(() => {
    if (!ready || !authenticated) return;
    const target = consumeReturnTo(import.meta.env.BASE_URL.replace(/\/$/, ''), globalThis.location.pathname);
    if (target) navigate(target, { replace: true });
  }, [ready, authenticated, navigate]);

  if (!ready) return <Loading />;
  if (!authenticated) return <Loading />;

  if (roles.length === 0 || (!isSuperAdmin && !isOrgAdmin && !isProjectManager)) {
    return <NoRolesError onLogout={logout} />;
  }

  const home = defaultPath(isSuperAdmin, isOrgAdmin);

  return (
    <Shell>
      <Routes>
        <Route index element={<Navigate to={home} replace />} />

        <Route path="account" element={<AccountPage />} />

        <Route element={isSuperAdmin ? <Outlet /> : <Navigate to={home} replace />}>
          {routesFor('deployment', '/system')}
          {systemShapes('org').flatMap(b => routesFor('org', b))}
          {systemShapes('project').flatMap(b => routesFor('project', b))}
          {/* Detail pages: reached from a destination, not destinations themselves. */}
          <Route path="system/service-accounts/:id" element={<ServiceAccountDetail />} />
          <Route path="system/userlists/:listId" element={<UserListDetail />} />
          <Route path="system/organisations/:id/userlists/:listId" element={<UserListDetail />} />
          <Route path="system/organisations/:id/service-accounts/:saId" element={<ServiceAccountDetail />} />
        </Route>

        <Route element={isOrgAdmin ? <Outlet /> : <Navigate to={home} replace />}>
          {routesFor('org', ownShape('org'))}
          <Route path="org/userlists/:listId" element={<UserListDetail />} />
          <Route path="org/service-accounts/:saId" element={<ServiceAccountDetail />} />
        </Route>

        <Route element={isProjectManager ? <Outlet /> : <Navigate to={home} replace />}>
          {routesFor('project', ownShape('project'))}
        </Route>

        <Route path="*" element={<Navigate to={home} replace />} />
      </Routes>
    </Shell>
  );
}

/**
 * Which component answers each destination, by level.
 *
 * The routes below are generated from this and `ROUTE_BASES`, so a page is registered once and
 * reached by both URL shapes. Written by hand, `UserLists` appeared three times and both service
 * account pages twice — and a route added to one shape and forgotten on the other is a page that
 * exists for a tenant admin and 404s for the super-admin looking at the same tenant.
 *
 * Destination keys come from `scope.ts`; a key here that is not a destination there, or the
 * reverse, is caught by App.test.
 */
export const PAGES: Record<Level, Record<string, React.ReactElement>> = {
  deployment: {
    '':                 <SystemDashboard />,
    'organisations':    <Organisations />,
    'admins':           <SystemAdmins />,
    'users':            <SystemUsers />,
    'projects':         <SystemProjects />,
    'userlists':        <UserLists />,
    'service-accounts': <ServiceAccounts level="deployment" />,
    'email':            <SystemEmail />,
    'impersonation':    <Impersonation />,
    'settings':         <DeploymentSettings />,
    'audit-log':        <AuditLog level="deployment" />,
    'metrics':          <SystemMetrics />,
    'health':           <SystemHealth />,
  },
  org: {
    '':                 <OrgDashboardOrDetail />,
    'projects':         <Projects />,
    'userlists':        <UserLists />,
    'admins':           <OrgAdmins />,
    'service-accounts': <ServiceAccounts level="org" />,
    'email':            <OrgEmail />,
    'audit-log':        <AuditLog level="org" />,
    'webhooks':         <OrgWebhooks />,
    'settings':         <OrgSettings />,
  },
  project: {
    '':                 <ProjectDashboardOrDetail />,
    'users':            <ProjectUsers />,
    'roles':            <ProjectRoles />,
    'service-accounts': <ServiceAccounts level="project" />,
    'authentication':   <Authentication />,
    'settings':         <ProjectSettings />,
  },
};

/**
 * The level's own page differs by audience: a tenant admin lands on their dashboard, a super-admin
 * browsing in lands on the tenant's detail page. That is the one place the two shapes genuinely
 * mean different things, so it is decided here rather than duplicated across the route table.
 */
function OrgDashboardOrDetail() {
  return useParams().id ? <OrgDetail /> : <OrgDashboard />;
}

function ProjectDashboardOrDetail() {
  return useParams().pid ? <SystemProjectDetail /> : <ProjectDashboard />;
}

/**
 * Every route of a level, on one URL shape.
 *
 * One shape at a time, not all of them, because the shapes are guarded differently: the
 * `/system/...` forms belong to a super-admin browsing into a tenant, the short forms to that
 * tenant's own administrator. Mounting both under one guard would hand a super-admin `/org`, a page
 * that resolves its organisation from a token that names none.
 */
function routesFor(level: Level, base: string) {
  return DESTINATIONS[level].map(d => (
    <Route key={`${base}/${d.key}`} path={d.key ? `${base}/${d.key}` : base} element={PAGES[level][d.key]} />
  ));
}

/** The shapes a super-admin reaches a level by, and the one its own administrator does. */
const systemShapes = (level: Level) => ROUTE_BASES[level].filter(b => b.startsWith('/system'));
const ownShape     = (level: Level) => ROUTE_BASES[level].find(b => !b.startsWith('/system'))!;

export default function App() {
  // basename comes from Vite, so the router, the bundle's asset paths and the server's fallback
  // cannot drift apart — all three read the same `base`. Writing it here as a literal is how
  // "/admin" survived the move to /console and left the router refusing to match any URL at all,
  // with one console warning and a blank page as the only symptom.
  return (
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <ThemeProvider>
        <ScopeProvider>
          <AuthProvider>
            <AppRoutes />
          </AuthProvider>
        </ScopeProvider>
      </ThemeProvider>
    </BrowserRouter>
  );
}
