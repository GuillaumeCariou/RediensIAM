import { useEffect, useState } from 'react';
import { StatCard, ActivityChart } from '@/components/iam';
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

  return (
    <div>
      <PageHeader title="Metrics" description="System-wide usage statistics" />
      <div className="iam-page">
        <div className="iam-stats-grid" style={{ marginBottom: 24 }}>
          <StatCard label="Organisations" value={loading ? '—' : metrics?.organisations ?? 0} sub={loading ? undefined : `${metrics?.active_organisations ?? 0} active`} />
          <StatCard label="Total Users" value={loading ? '—' : metrics?.total_users ?? 0} sub={loading ? undefined : `${metrics?.active_users ?? 0} active`} />
          <StatCard label="Projects" value={loading ? '—' : metrics?.projects ?? 0} />
          <StatCard label="Service Accounts" value={loading ? '—' : metrics?.service_accounts ?? 0} />
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14, marginBottom: 24 }}>
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              {'Login activity · last 24h'}
              <span style={{ display: 'flex', gap: 14, fontSize: 12, color: 'var(--fg-muted)' }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  <span style={{ width: 8, height: 8, borderRadius: 2, background: 'var(--ia-accent)', display: 'inline-block' }} />
                  {'Success'}
                </span>
                <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  <span style={{ width: 8, height: 8, borderRadius: 2, background: 'var(--danger)', display: 'inline-block' }} />
                  {'Failed'}
                </span>
              </span>
            </div>
            <ActivityChart height={120} />
          </div>

          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14 }}>Today's activity</div>
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
                <span style={{ fontSize: 13, color: 'var(--fg-muted)' }}>Logins</span>
                <span style={{ fontSize: 16, fontWeight: 600 }}>{loading ? '—' : metrics?.recent_logins ?? 0}</span>
              </div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '10px 0' }}>
                <span style={{ fontSize: 13, color: 'var(--fg-muted)' }}>Audit events</span>
                <span style={{ fontSize: 16, fontWeight: 600 }}>{loading ? '—' : metrics?.audit_events_today ?? 0}</span>
              </div>
            </div>
          </div>
        </div>

        {metrics?.users_by_org && metrics.users_by_org.length > 0 && (
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14 }}>Users per organisation</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
              {metrics.users_by_org.map(row => {
                const maxCount = Math.max(...metrics.users_by_org!.map(r => r.count));
                const pct = maxCount > 0 ? (row.count / maxCount) * 100 : 0;
                return (
                  <div key={row.org} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
                    <div style={{ width: 120, fontSize: 12, color: 'var(--fg-muted)', flexShrink: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {row.org}
                    </div>
                    <div style={{ flex: 1, height: 6, background: 'var(--surface-2)', borderRadius: 3, overflow: 'hidden' }}>
                      <div style={{ height: '100%', width: `${pct}%`, background: 'var(--ia-accent)', borderRadius: 3 }} />
                    </div>
                    <div style={{ fontSize: 13, fontWeight: 600, width: 40, textAlign: 'right', flexShrink: 0 }}>{row.count}</div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
