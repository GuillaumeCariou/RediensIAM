import { useEffect, useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { Users, Shield, ArrowRight, Palette } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getProject, listProjectUsers, listRoles } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  assigned_user_list_id: string | null; assigned_user_list_name: string | null;
  require_role_to_login: boolean; hydra_client_id: string;
  login_theme: Record<string, string> | null;
}

export default function ProjectDashboard() {
  const [params] = useSearchParams();
  const projectId = params.get('project_id') ?? '';
  const orgId = params.get('org_id') ?? '';
  const [project, setProject] = useState<Project | null>(null);
  const [userCount, setUserCount] = useState<number | null>(null);
  const [roleCount, setRoleCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    Promise.all([
      getProject(projectId).then(setProject),
      listProjectUsers(projectId).then(r => setUserCount((r.users ?? r ?? []).length)),
      listRoles(projectId).then(r => setRoleCount((r.roles ?? r ?? []).length)),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [projectId]);

  if (!projectId) {
    return (
      <div>
        <PageHeader title="Project" />
        <div className="p-6 text-sm text-muted-foreground">No project selected. Navigate from an organisation.</div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (project?.name ?? 'Project')}
        description={project ? `/${project.slug} · ${project.hydra_client_id}` : undefined}
        action={
          project && (
            <div className="flex gap-2">
              {project.active ? <Badge variant="success">Active</Badge> : <Badge variant="secondary">Inactive</Badge>}
              {project.require_role_to_login && <Badge variant="warning">Role Required</Badge>}
            </div>
          )
        }
      />
      <div className="p-6 space-y-6">
        {loading ? (
          <div className="grid grid-cols-3 gap-4">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-32 rounded-xl" />)}</div>
        ) : (
          <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Users className="h-4 w-4" />Users</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-2xl font-bold">{userCount ?? '—'}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`/project/users?project_id=${projectId}&org_id=${orgId}`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Shield className="h-4 w-4" />Roles</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-2xl font-bold">{roleCount ?? '—'}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`/project/roles?project_id=${projectId}&org_id=${orgId}`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Palette className="h-4 w-4" />Login Theme</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-sm text-muted-foreground">{project?.login_theme ? 'Customized' : 'Default'}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`/project/theme?project_id=${projectId}&org_id=${orgId}`}>Edit Theme <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
            </div>

            {project?.assigned_user_list_name && (
              <Card>
                <CardHeader><CardTitle className="text-base">Configuration</CardTitle></CardHeader>
                <CardContent className="space-y-3">
                  <div className="flex justify-between text-sm border-b pb-2">
                    <span className="text-muted-foreground">User List</span>
                    <Badge>{project.assigned_user_list_name}</Badge>
                  </div>
                  <div className="flex justify-between text-sm border-b pb-2">
                    <span className="text-muted-foreground">Hydra Client</span>
                    <span className="font-mono text-xs">{project.hydra_client_id}</span>
                  </div>
                  <div className="flex justify-between text-sm">
                    <span className="text-muted-foreground">Role Required to Login</span>
                    <span>{project.require_role_to_login ? 'Yes' : 'No'}</span>
                  </div>
                </CardContent>
              </Card>
            )}
          </>
        )}
      </div>
    </div>
  );
}
