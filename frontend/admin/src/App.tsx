import { BrowserRouter, Routes, Route, Navigate, Outlet } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ThemeProvider } from './context/ThemeContext';
import Shell from './components/layout/Shell';

// Account page
import AccountPage from './pages/account/AccountPage';

// System pages
import SystemDashboard from './pages/system/Dashboard';
import Organisations from './pages/system/Organisations';
import SystemUsers from './pages/system/Users';
import AuditLog from './pages/system/AuditLog';
import SystemMetrics from './pages/system/Metrics';
import SystemUserLists from './pages/system/SystemUserLists';
import SystemServiceAccounts from './pages/system/SystemServiceAccounts';
import SystemUserListDetail from './pages/system/UserListDetail';
import OrgDetail from './pages/system/OrgDetail';
import SystemProjectDetail from './pages/system/SystemProjectDetail';
import SystemProjects from './pages/system/SystemProjects';
import SystemAdmins from './pages/system/SystemAdmins';
import ServiceAccountDetail from './pages/ServiceAccountDetail';

// Org pages
import OrgDashboard from './pages/org/OrgDashboard';
import UserLists from './pages/org/UserLists';
import Projects from './pages/org/Projects';
import OrgAdmins from './pages/org/OrgAdmins';
import OrgServiceAccounts from './pages/org/OrgServiceAccounts';
import OrgAuditLog from './pages/org/OrgAuditLog';
import OrgUserListDetail from './pages/org/UserListDetail';

// Project pages
import ProjectDashboard from './pages/project/ProjectDashboard';
import ProjectUsers from './pages/project/ProjectUsers';
import ProjectRoles from './pages/project/ProjectRoles';
import ProjectServiceAccounts from './pages/project/ProjectServiceAccounts';
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

function AppRoutes() {
  const { ready, authenticated, isSuperAdmin, isOrgAdmin, isProjectManager } = useAuth();

  if (!ready) return <Loading />;
  if (!authenticated) return <Loading />;

  const home = defaultPath(isSuperAdmin, isOrgAdmin);

  return (
    <Shell>
      <Routes>
        <Route index element={<Navigate to={home} replace />} />

        {/* Account — all authenticated users */}
        <Route path="account" element={<AccountPage />} />

        {/* System — super_admin only */}
        <Route element={isSuperAdmin ? <Outlet /> : <Navigate to={home} replace />}>
          <Route path="system" element={<SystemDashboard />} />
          <Route path="system/admins" element={<SystemAdmins />} />
          <Route path="system/projects" element={<SystemProjects />} />
          <Route path="system/organisations" element={<Organisations />} />
          <Route path="system/organisations/:id" element={<OrgDetail />} />
          <Route path="system/organisations/:id/userlists" element={<UserLists />} />
          <Route path="system/organisations/:id/projects" element={<Projects />} />
          <Route path="system/organisations/:id/admins" element={<OrgAdmins />} />
          <Route path="system/organisations/:id/service-accounts" element={<OrgServiceAccounts />} />
          <Route path="system/organisations/:id/service-accounts/:saId" element={<ServiceAccountDetail />} />
          <Route path="system/organisations/:id/audit-log" element={<OrgAuditLog />} />
          <Route path="system/organisations/:oid/projects/:pid" element={<SystemProjectDetail />} />
          <Route path="system/organisations/:oid/projects/:pid/users" element={<ProjectUsers />} />
          <Route path="system/organisations/:oid/projects/:pid/roles" element={<ProjectRoles />} />
          <Route path="system/organisations/:oid/projects/:pid/service-accounts" element={<ProjectServiceAccounts />} />
          <Route path="system/organisations/:oid/projects/:pid/authentication" element={<Authentication />} />
          <Route path="system/organisations/:oid/projects/:pid/settings" element={<ProjectSettings />} />
          <Route path="system/users" element={<SystemUsers />} />
          <Route path="system/userlists" element={<SystemUserLists />} />
          <Route path="system/userlists/:id" element={<SystemUserListDetail />} />
          <Route path="system/organisations/:id/userlists/:listId" element={<SystemUserListDetail />} />
          <Route path="system/service-accounts" element={<SystemServiceAccounts />} />
          <Route path="system/service-accounts/:id" element={<ServiceAccountDetail />} />
          <Route path="system/audit-log" element={<AuditLog />} />
          <Route path="system/metrics" element={<SystemMetrics />} />
        </Route>

        {/* Org — org_admin (and super_admin) */}
        <Route element={isOrgAdmin ? <Outlet /> : <Navigate to={home} replace />}>
          <Route path="org" element={<OrgDashboard />} />
          <Route path="org/userlists" element={<UserLists />} />
          <Route path="org/userlists/:id" element={<OrgUserListDetail />} />
          <Route path="org/projects" element={<Projects />} />
          <Route path="org/admins" element={<OrgAdmins />} />
          <Route path="org/service-accounts" element={<OrgServiceAccounts />} />
          <Route path="org/service-accounts/:saId" element={<ServiceAccountDetail />} />
          <Route path="org/audit-log" element={<OrgAuditLog />} />
        </Route>

        {/* Project — project_manager (and above) */}
        <Route element={isProjectManager ? <Outlet /> : <Navigate to={home} replace />}>
          <Route path="project" element={<ProjectDashboard />} />
          <Route path="project/users" element={<ProjectUsers />} />
          <Route path="project/roles" element={<ProjectRoles />} />
          <Route path="project/service-accounts" element={<ProjectServiceAccounts />} />
          <Route path="project/authentication" element={<Authentication />} />
          <Route path="project/settings" element={<ProjectSettings />} />
        </Route>

        <Route path="*" element={<Navigate to={home} replace />} />
      </Routes>
    </Shell>
  );
}

export default function App() {
  return (
    <BrowserRouter basename="/admin">
      <ThemeProvider>
        <AuthProvider>
          <AppRoutes />
        </AuthProvider>
      </ThemeProvider>
    </BrowserRouter>
  );
}
