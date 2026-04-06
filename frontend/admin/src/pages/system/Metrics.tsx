import { useEffect, useState } from 'react';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, LineChart, Line, CartesianGrid } from 'recharts';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { getMetrics } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Metrics {
  organisations: number;
  active_organisations: number;
  total_users: number;
  active_users: number;
  projects: number;
  service_accounts: number;
  recent_logins: number;
  audit_events_today: number;
  logins_by_hour?: { hour: string; count: number }[];
  users_by_org?: { org: string; count: number }[];
}

export default function SystemMetrics() {
  const [metrics, setMetrics] = useState<Metrics | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    getMetrics().then(setMetrics).catch(console.error).finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div>
        <PageHeader title="Metrics" description="System-wide usage statistics" />
        <div className="p-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
          {Array.from({ length: 4 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-64 rounded-xl" />)}
        </div>
      </div>
    );
  }

  const summaryData = [
    { name: 'Total Orgs', value: metrics?.organisations ?? 0 },
    { name: 'Active Orgs', value: metrics?.active_organisations ?? 0 },
    { name: 'Total Users', value: metrics?.total_users ?? 0 },
    { name: 'Active Users', value: metrics?.active_users ?? 0 },
    { name: 'Projects', value: metrics?.projects ?? 0 },
    { name: 'Service Accts', value: metrics?.service_accounts ?? 0 },
  ];

  return (
    <div>
      <PageHeader title="Metrics" description="System-wide usage statistics" />
      <div className="p-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader><CardTitle className="text-base">System Summary</CardTitle></CardHeader>
          <CardContent>
            <ResponsiveContainer width="100%" height={240}>
              <BarChart data={summaryData} margin={{ top: 5, right: 5, bottom: 40, left: 5 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="name" tick={{ fontSize: 11 }} angle={-35} textAnchor="end" />
                <YAxis tick={{ fontSize: 11 }} />
                <Tooltip contentStyle={{ background: 'hsl(var(--background))', border: '1px solid hsl(var(--border))', borderRadius: 8 }} />
                <Bar dataKey="value" fill="hsl(var(--primary))" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>

        {metrics?.logins_by_hour && (
          <Card>
            <CardHeader><CardTitle className="text-base">Logins by Hour (last 24h)</CardTitle></CardHeader>
            <CardContent>
              <ResponsiveContainer width="100%" height={240}>
                <LineChart data={metrics.logins_by_hour} margin={{ top: 5, right: 5, bottom: 20, left: 5 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                  <XAxis dataKey="hour" tick={{ fontSize: 11 }} />
                  <YAxis tick={{ fontSize: 11 }} />
                  <Tooltip contentStyle={{ background: 'hsl(var(--background))', border: '1px solid hsl(var(--border))', borderRadius: 8 }} />
                  <Line type="monotone" dataKey="count" stroke="hsl(var(--primary))" strokeWidth={2} dot={false} />
                </LineChart>
              </ResponsiveContainer>
            </CardContent>
          </Card>
        )}

        {metrics?.users_by_org && (
          <Card>
            <CardHeader><CardTitle className="text-base">Users per Organisation</CardTitle></CardHeader>
            <CardContent>
              <ResponsiveContainer width="100%" height={240}>
                <BarChart data={metrics.users_by_org} layout="vertical" margin={{ top: 5, right: 20, bottom: 5, left: 60 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                  <XAxis type="number" tick={{ fontSize: 11 }} />
                  <YAxis type="category" dataKey="org" tick={{ fontSize: 11 }} width={55} />
                  <Tooltip contentStyle={{ background: 'hsl(var(--background))', border: '1px solid hsl(var(--border))', borderRadius: 8 }} />
                  <Bar dataKey="count" fill="hsl(var(--primary))" radius={[0, 4, 4, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader><CardTitle className="text-base">Today</CardTitle></CardHeader>
          <CardContent>
            <div className="space-y-3">
              <div className="flex justify-between items-center py-2 border-b">
                <span className="text-sm">Logins</span>
                <span className="font-semibold">{metrics?.recent_logins ?? 0}</span>
              </div>
              <div className="flex justify-between items-center py-2">
                <span className="text-sm">Audit Events</span>
                <span className="font-semibold">{metrics?.audit_events_today ?? 0}</span>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
