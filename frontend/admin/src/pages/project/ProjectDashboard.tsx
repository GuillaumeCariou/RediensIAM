import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getProjectInfo, getProjectStats } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import ProjectStatsCards from '@/components/ProjectStatsCards';
import type { ProjectStats } from '@/components/ProjectStatsCards';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  assigned_user_list_id: string | null; assigned_user_list_name: string | null;
  require_role_to_login: boolean; hydra_client_id: string;
}

export default function ProjectDashboard() {
  const { projectId, projectBase } = useProjectContext();
  const [project, setProject] = useState<Project | null>(null);
  const [stats, setStats] = useState<ProjectStats | null>(null);
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
          <ProjectStatsCards
            stats={stats}
            loading={false}
            usersLink={`${projectBase}/users`}
            rolesLink={`${projectBase}/roles`}
          />
        )}
      </div>
    </div>
  );
}
