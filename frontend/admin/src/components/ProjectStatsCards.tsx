import { Link } from 'react-router';
import { Users, UserCheck, Shield, ArrowRight } from 'lucide-react';

export interface ProjectStats {
  total_users: number;
  active_users: number;
  users_by_role: { role_id: string; role_name: string; count: number }[];
}

interface Props {
  stats: ProjectStats | null;
  loading: boolean;
  usersLink?: string;
  rolesLink?: string;
}

export default function ProjectStatsCards({ stats, loading, usersLink, rolesLink }: Readonly<Props>) {
  return (
    <>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div className="iam-card">
          <div className="iam-card-pad pb-0 pb-2">
            <h3 className="text-sm font-semibold text-sm text-muted-foreground flex items-center gap-2">
              <Users className="h-4 w-4" />Total Users
            </h3>
          </div>
          <div className="iam-card-pad">
            {loading ? <div className="iam-skeleton h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.total_users ?? '—'}</p>
                {usersLink && (
                  <Link className="iam-btn iam-btn-ghost iam-btn-sm mt-2 -ml-2 text-xs" to={usersLink}>
                    Manage <ArrowRight className="h-3 w-3" />
                  </Link>
                )}
              </>
            )}
          </div>
        </div>
        <div className="iam-card">
          <div className="iam-card-pad pb-0 pb-2">
            <h3 className="text-sm font-semibold text-sm text-muted-foreground flex items-center gap-2">
              <UserCheck className="h-4 w-4" />Active Users
            </h3>
          </div>
          <div className="iam-card-pad">
            {loading ? <div className="iam-skeleton h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.active_users ?? '—'}</p>
                {stats && stats.total_users > 0 && (
                  <p className="text-xs text-muted-foreground mt-1">
                    {Math.round((stats.active_users / stats.total_users) * 100)}% active
                  </p>
                )}
              </>
            )}
          </div>
        </div>
        <div className="iam-card">
          <div className="iam-card-pad pb-0 pb-2">
            <h3 className="text-sm font-semibold text-sm text-muted-foreground flex items-center gap-2">
              <Shield className="h-4 w-4" />Roles
            </h3>
          </div>
          <div className="iam-card-pad">
            {loading ? <div className="iam-skeleton h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.users_by_role.length ?? '—'}</p>
                {rolesLink && (
                  <Link className="iam-btn iam-btn-ghost iam-btn-sm mt-2 -ml-2 text-xs" to={rolesLink}>
                    Manage <ArrowRight className="h-3 w-3" />
                  </Link>
                )}
              </>
            )}
          </div>
        </div>
      </div>

      {stats && stats.users_by_role.length > 0 && (
        <div className="iam-card">
          <div className="iam-card-pad pb-0"><h3 className="text-sm font-semibold text-base">Users by Role</h3></div>
          <div className="iam-card-pad space-y-2">
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
          </div>
        </div>
      )}
    </>
  );
}
