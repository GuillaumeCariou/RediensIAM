import { useEffect, useState } from 'react';
import { StatCard, ActivityChart, IamChip } from '@/components/iam';
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

export default function SystemDashboard() {
  const [metrics, setMetrics] = useState<Metrics | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    getMetrics().then(setMetrics).catch(console.error).finally(() => setLoading(false));
  }, []);

  return (
    <div>
      <PageHeader title="System Dashboard" description="Overview of the entire RediensIAM platform" />
      <div className="iam-page">
        <div className="iam-stats-grid" style={{ marginBottom: 24 }}>
          <StatCard
            label="Organisations"
            value={loading ? '—' : metrics?.organisations ?? 0}
            sub={loading ? undefined : `${metrics?.active_organisations ?? 0} active`}
          />
          <StatCard
            label="Total Users"
            value={loading ? '—' : metrics?.total_users ?? 0}
            sub={loading ? undefined : `${metrics?.active_users ?? 0} active`}
          />
          <StatCard
            label="Projects"
            value={loading ? '—' : metrics?.projects ?? 0}
          />
          <StatCard
            label="Service Accounts"
            value={loading ? '—' : metrics?.service_accounts ?? 0}
          />
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr auto', gap: 14, marginBottom: 24 }}>
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              {'Login activity · last 24h'}
              <span style={{ fontSize: 12, fontWeight: 400, color: 'var(--fg-muted)' }}>
                {loading ? '—' : metrics?.recent_logins ?? 0} logins
              </span>
            </div>
            <ActivityChart height={120} />
          </div>

          <div className="iam-card iam-card-pad" style={{ minWidth: 200 }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14 }}>Today</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
                <span style={{ fontSize: 13, color: 'var(--fg-muted)' }}>Logins</span>
                <span style={{ fontSize: 14, fontWeight: 600 }}>{loading ? '—' : metrics?.recent_logins ?? 0}</span>
              </div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
                <span style={{ fontSize: 13, color: 'var(--fg-muted)' }}>Audit events</span>
                <span style={{ fontSize: 14, fontWeight: 600 }}>{loading ? '—' : metrics?.audit_events_today ?? 0}</span>
              </div>
              {metrics?.uptime_since && (
                <div style={{ padding: '10px 0', display: 'flex', flexDirection: 'column', gap: 2 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <IamChip tone="success">Operational</IamChip>
                  </div>
                  <span style={{ fontSize: 11, color: 'var(--fg-subtle)' }}>Since {fmtDate(metrics.uptime_since)}</span>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
