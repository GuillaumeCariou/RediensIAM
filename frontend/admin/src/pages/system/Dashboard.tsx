import { useEffect, useState } from 'react';
import { Building2, Users, FolderKanban, Bot, Activity, TrendingUp } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { getMetrics } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface Metrics {
  organisations: number;
  active_organisations: number;
  total_users: number;
  active_users: number;
  projects: number;
  service_accounts: number;
  recent_logins: number;
  audit_events_today: number;
  uptime_since?: string;
}

function StatCard({ label, value, sub, icon, trend }: Readonly<{
  label: string; value: number | string; sub?: string; icon: React.ReactNode; trend?: number;
}>) {
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between pb-2">
        <CardTitle className="text-sm font-medium text-muted-foreground">{label}</CardTitle>
        <div className="text-muted-foreground">{icon}</div>
      </CardHeader>
      <CardContent>
        <p className="text-2xl font-bold">{value}</p>
        {sub && <p className="text-xs text-muted-foreground mt-1">{sub}</p>}
        {trend !== undefined && (
          <div className="flex items-center gap-1 mt-1">
            <TrendingUp className="h-3 w-3 text-green-600" />
            <span className="text-xs text-green-600">{trend} today</span>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

export default function SystemDashboard() {
  const [metrics, setMetrics] = useState<Metrics | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    getMetrics().then(setMetrics).catch(console.error).finally(() => setLoading(false));
  }, []);

  return (
    <div>
      <PageHeader title="System Dashboard" description="Overview of the entire RediensIAM platform" />
      <div className="p-6 space-y-6">
        {loading ? (
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            {Array.from({ length: 8 }).map((_, i) => <Skeleton key={i} className="h-28 rounded-xl" />)}
          </div>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
              <StatCard label="Organisations" value={metrics?.organisations ?? 0} sub={`${metrics?.active_organisations ?? 0} active`} icon={<Building2 className="h-4 w-4" />} />
              <StatCard label="Total Users" value={metrics?.total_users ?? 0} sub={`${metrics?.active_users ?? 0} active`} icon={<Users className="h-4 w-4" />} />
              <StatCard label="Projects" value={metrics?.projects ?? 0} icon={<FolderKanban className="h-4 w-4" />} />
              <StatCard label="Service Accounts" value={metrics?.service_accounts ?? 0} icon={<Bot className="h-4 w-4" />} />
              <StatCard label="Recent Logins" value={metrics?.recent_logins ?? 0} sub="last 24h" icon={<Activity className="h-4 w-4" />} trend={metrics?.recent_logins} />
              <StatCard label="Audit Events" value={metrics?.audit_events_today ?? 0} sub="today" icon={<Activity className="h-4 w-4" />} />
            </div>

            {metrics?.uptime_since && (
              <Card>
                <CardHeader>
                  <CardTitle className="text-sm">System Status</CardTitle>
                </CardHeader>
                <CardContent className="flex items-center gap-3">
                  <Badge variant="success">Operational</Badge>
                  <span className="text-sm text-muted-foreground">Running since {fmtDate(metrics.uptime_since)}</span>
                </CardContent>
              </Card>
            )}
          </>
        )}
      </div>
    </div>
  );
}
