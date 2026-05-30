import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip } from '@/components/iam';
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
        <div className="iam-page" style={{ fontSize: 13, color: 'var(--fg-muted)' }}>No project selected. Navigate from an organisation.</div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (project?.name ?? 'Project')}
        description={project ? `/${project.slug} · ${project.hydra_client_id}` : undefined}
        actions={project ? [
          project.active
            ? <IamChip key="status" tone="success">Active</IamChip>
            : <IamChip key="status" tone="default">Inactive</IamChip>,
          ...(project.require_role_to_login ? [<IamChip key="role" tone="warn">Role Required</IamChip>] : []),
        ] : []}
      />
      <div className="iam-page">
        {loading ? (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 16 }}>
            {Array.from({ length: 3 }, (_, i) => (
              <div key={i} style={{ height: 128, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />
            ))}
          </div>
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
