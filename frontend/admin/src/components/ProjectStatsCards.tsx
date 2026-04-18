import { Link } from 'react-router-dom';
import { Users, UserCheck, Shield, ArrowRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

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
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <Users className="h-4 w-4" />Total Users
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.total_users ?? '—'}</p>
                {usersLink && (
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={usersLink}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                )}
              </>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <UserCheck className="h-4 w-4" />Active Users
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.active_users ?? '—'}</p>
                {stats && stats.total_users > 0 && (
                  <p className="text-xs text-muted-foreground mt-1">
                    {Math.round((stats.active_users / stats.total_users) * 100)}% active
                  </p>
                )}
              </>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <Shield className="h-4 w-4" />Roles
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.users_by_role.length ?? '—'}</p>
                {rolesLink && (
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={rolesLink}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                )}
              </>
            )}
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
  );
}
