import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Users, UserCheck, Shield, ArrowRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getProjectInfo, getProjectStats } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  assigned_user_list_id: string | null; assigned_user_list_name: string | null;
  require_role_to_login: boolean; hydra_client_id: string;
}

interface Stats {
  total_users: number;
  active_users: number;
  users_by_role: { role_id: string; role_name: string; count: number }[];
}

export default function ProjectDashboard() {
  const { projectId, projectBase } = useProjectContext();
  const [project, setProject] = useState<Project | null>(null);
  const [stats, setStats] = useState<Stats | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    Promise.all([
      getProjectInfo(projectId).then(setProject),
      getProjectStats(projectId).then(setStats).catch(() => null),
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
          <div className="grid grid-cols-3 gap-4">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-32 rounded-xl" />)}</div>
        ) : (
          <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Users className="h-4 w-4" />Total Users</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-3xl font-bold">{stats?.total_users ?? '—'}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`${projectBase}/users`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><UserCheck className="h-4 w-4" />Active Users</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-3xl font-bold">{stats?.active_users ?? '—'}</p>
                  {stats && stats.total_users > 0 && (
                    <p className="text-xs text-muted-foreground mt-1">
                      {Math.round((stats.active_users / stats.total_users) * 100)}% active
                    </p>
                  )}
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Shield className="h-4 w-4" />Roles</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-3xl font-bold">{stats?.users_by_role.length ?? '—'}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`${projectBase}/roles`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
            </div>

            {stats && stats.users_by_role.length > 0 && (
              <Card>
                <CardHeader><CardTitle className="text-base">Users by Role</CardTitle></CardHeader>
                <CardContent className="space-y-2">
                  {[...stats.users_by_role].sort((a, b) => b.count - a.count).map(r => (
                    <div key={r.role_id} className="flex items-center justify-between text-sm">
                      <span className="font-mono text-muted-foreground">{r.role_name}</span>
                      <div className="flex items-center gap-3">
                        <div className="w-32 h-1.5 rounded-full bg-muted overflow-hidden">
                          <div
                            className="h-full bg-primary rounded-full"
                            style={{ width: stats.total_users > 0 ? `${(r.count / stats.total_users) * 100}%` : '0%' }}
                          />
                        </div>
                        <span className="font-medium w-6 text-right">{r.count}</span>
                      </div>
                    </div>
                  ))}
                </CardContent>
              </Card>
            )}
          </>
        )}
      </div>
    </div>
  );
}
