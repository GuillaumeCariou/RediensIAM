import { useEffect, useState } from 'react';
import { IamChip, IamDot } from '@/components/iam';
import { getSystemHealth } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

type HealthStatus = 'ok' | 'error' | 'not_configured';

interface ComponentHealth {
  name: string;
  category: string;
  status: HealthStatus;
  latency_ms: number | null;
  detail: string | null;
  stats: Record<string, string> | null;
}

interface HealthResponse {
  overall: 'ok' | 'error';
  checks: ComponentHealth[];
}

function dotStatus(s: HealthStatus): 'success' | 'danger' | 'muted' {
  if (s === 'ok') return 'success';
  if (s === 'error') return 'danger';
  return 'muted';
}

function StatusChip({ status }: Readonly<{ status: HealthStatus }>) {
  if (status === 'ok') return <IamChip tone="success">OK</IamChip>;
  if (status === 'error') return <IamChip tone="danger">Error</IamChip>;
  return <IamChip tone="default">Not configured</IamChip>;
}

function ComponentCard({ check }: Readonly<{ check: ComponentHealth }>) {
  return (
    <div style={{
      borderRadius: 8,
      border: `1px solid ${check.status === 'error' ? 'oklch(from var(--danger) l c h / 0.4)' : 'var(--border)'}`,
      padding: 14,
      background: check.status === 'error' ? 'var(--danger-soft)' : 'var(--surface)',
    }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, marginBottom: check.detail || check.stats ? 10 : 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
          <IamDot tone={dotStatus(check.status)} />
          <span style={{ fontWeight: 500, fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {check.name}
          </span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
          {check.latency_ms != null && (
            <span className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{check.latency_ms} ms</span>
          )}
          <StatusChip status={check.status} />
        </div>
      </div>

      {check.detail && (
        <div style={{ fontSize: 12, color: check.status === 'error' ? 'var(--danger)' : 'var(--fg-muted)', marginBottom: check.stats ? 8 : 0 }}>
          {check.detail}
        </div>
      )}

      {check.stats && Object.keys(check.stats).length > 0 && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '4px 16px', paddingTop: 8, borderTop: '1px solid var(--border)' }}>
          {Object.entries(check.stats).map(([k, v]) => (
            <div key={k} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
              <span style={{ fontSize: 11, color: 'var(--fg-muted)', textTransform: 'capitalize' }}>{k.replaceAll('_', ' ')}</span>
              <span className="iam-mono" style={{ fontSize: 11, fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '12rem', textAlign: 'right' }}>{v}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default function SystemHealth() {
  const [data, setData] = useState<HealthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [lastRun, setLastRun] = useState<Date | null>(null);

  /** The fetch alone. Sets no state synchronously, so an effect may call it directly. */
  const fetchHealth = () => {
    getSystemHealth()
      .then((d: HealthResponse) => { setData(d); setLastRun(new Date()); })
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  /** What the Re-run button calls: the spinner comes back, then the fetch. */
  const load = () => { setLoading(true); fetchHealth(); };

  useEffect(fetchHealth, []);

  const categories = data ? [...new Set(data.checks.map(c => c.category))] : [];

  return (
    <div>
      <PageHeader
        title="System Health"
        description="Connectivity and status of all backend components"
        actions={[
          lastRun
            ? <span key="time" style={{ fontSize: 12, color: 'var(--fg-muted)', alignSelf: 'center' }}>Checked {lastRun.toLocaleTimeString()}</span>
            : null,
          <button key="refresh" className="iam-btn iam-btn-secondary iam-btn-sm" onClick={load} disabled={loading}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
              style={{ animation: loading ? 'spin 1s linear infinite' : undefined }}>
              <polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/>
              <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/>
            </svg>
            Refresh
          </button>,
        ].filter(Boolean) as React.ReactNode[]}
      />
      <div className="iam-page">
        {!loading && data && (
          <div style={{
            display: 'flex', alignItems: 'center', gap: 10,
            borderRadius: 8, padding: '10px 14px', marginBottom: 20,
            border: `1px solid ${data.overall === 'ok' ? 'oklch(from var(--success) l c h / 0.3)' : 'oklch(from var(--danger) l c h / 0.3)'}`,
            background: data.overall === 'ok' ? 'var(--success-soft)' : 'var(--danger-soft)',
          }}>
            <IamDot tone={data.overall === 'ok' ? 'success' : 'danger'} />
            <span style={{ fontSize: 13, fontWeight: 500 }}>
              {data.overall === 'ok'
                ? 'All systems operational'
                : `${data.checks.filter(c => c.status === 'error').length} component(s) have errors`}
            </span>
          </div>
        )}

        {(() => {
          if (loading) return (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            {Array.from({ length: 6 }, (_, i) => (
              <div key={i} style={{ height: 80, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />
            ))}
          </div>
          );
          if (!data) return null;
          return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 24 }}>
            {categories.map(cat => (
              <div key={cat}>
                <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 10 }}>
                  {cat}
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  {data.checks.filter(c => c.category === cat).map(check => (
                    <ComponentCard key={check.name} check={check} />
                  ))}
                </div>
              </div>
            ))}
          </div>
          );
        })()}
      </div>

      <style>{`@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
