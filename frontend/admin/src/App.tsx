import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ThemeProvider } from './context/ThemeContext';
import Shell from './components/layout/Shell';

// System pages
import SystemDashboard from './pages/system/Dashboard';
import Organisations from './pages/system/Organisations';
import SystemUsers from './pages/system/Users';
import AuditLog from './pages/system/AuditLog';
import SystemMetrics from './pages/system/Metrics';
import HydraClients from './pages/system/HydraClients';
import SystemUserLists from './pages/system/SystemUserLists';
import SystemServiceAccounts from './pages/system/SystemServiceAccounts';
import SystemUserListDetail from './pages/system/UserListDetail';

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
import LoginTheme from './pages/project/LoginTheme';

function Loading() {
  return (
    <div className="flex h-screen items-center justify-center">
      <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
    </div>
  );
}

function AppRoutes() {
  const { ready, authenticated, isSuperAdmin } = useAuth();

  if (!ready) return <Loading />;
  if (!authenticated) return <Loading />;

  return (
    <Shell>
      <Routes>
        {/* Default redirect */}
        <Route index element={<Navigate to={isSuperAdmin ? '/system' : '/org'} replace />} />

        {/* System (super_admin) */}
        <Route path="system" element={<SystemDashboard />} />
        <Route path="system/organisations" element={<Organisations />} />
        <Route path="system/users" element={<SystemUsers />} />
        <Route path="system/userlists" element={<SystemUserLists />} />
        <Route path="system/userlists/:id" element={<SystemUserListDetail />} />
        <Route path="system/service-accounts" element={<SystemServiceAccounts />} />
        <Route path="system/hydra-clients" element={<HydraClients />} />
        <Route path="system/audit-log" element={<AuditLog />} />
        <Route path="system/metrics" element={<SystemMetrics />} />

        {/* Org (org_admin) */}
        <Route path="org" element={<OrgDashboard />} />
        <Route path="org/userlists" element={<UserLists />} />
        <Route path="org/userlists/:id" element={<OrgUserListDetail />} />
        <Route path="org/projects" element={<Projects />} />
        <Route path="org/admins" element={<OrgAdmins />} />
        <Route path="org/service-accounts" element={<OrgServiceAccounts />} />
        <Route path="org/audit-log" element={<OrgAuditLog />} />

        {/* Project (project_manager) */}
        <Route path="project" element={<ProjectDashboard />} />
        <Route path="project/users" element={<ProjectUsers />} />
        <Route path="project/roles" element={<ProjectRoles />} />
        <Route path="project/theme" element={<LoginTheme />} />

        {/* Fallback */}
        <Route path="*" element={<Navigate to="/" replace />} />
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
