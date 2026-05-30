import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { IamChip, IamDot } from '@/components/iam';
import { getEmailOverview } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface GlobalSmtp {
  configured: boolean;
  host?: string;
  port?: number;
  start_tls?: boolean;
  from_address?: string;
  from_name?: string;
}

interface ProjectOverride {
  id: string;
  name: string;
  email_from_name: string;
}

interface OrgRow {
  id: string;
  name: string;
  slug: string;
  smtp_configured: boolean;
  smtp_host?: string;
  smtp_port?: number;
  smtp_from_address?: string;
  smtp_from_name?: string;
  smtp_updated_at?: string;
  project_overrides: ProjectOverride[];
}

interface Overview {
  global_smtp: GlobalSmtp;
  orgs: OrgRow[];
}

export default function SystemEmail() {
  const navigate = useNavigate();
  const [data, setData] = useState<Overview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getEmailOverview()
      .then(setData)
      .catch(e => setError(e?.message ?? 'Failed to load email overview'))
      .finally(() => setLoading(false));
  }, []);

  const customCount = data?.orgs.filter(o => o.smtp_configured).length ?? 0;
  const totalOrgs = data?.orgs.length ?? 0;
  const overrideCount = data?.orgs.reduce((n, o) => n + o.project_overrides.length, 0) ?? 0;

  if (loading) return (
    <div>
      <PageHeader title="Email" />
      <div className="iam-page">
        <div className="iam-stats-grid" style={{ marginBottom: 24 }}>
          {Array.from({ length: 3 }, (_, i) => (
            <div key={i} className="iam-stat" style={{ height: 80 }} />
          ))}
        </div>
      </div>
    </div>
  );

  if (error || !data) return (
    <div>
      <PageHeader title="Email" />
      <div className="iam-page">
        <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>
          {error ?? 'No data returned'}
        </div>
      </div>
    </div>
  );

  const g = data.global_smtp;

  return (
    <div>
      <PageHeader title="Email" description="Global SMTP relay and per-organisation email configuration" />
      <div className="iam-page">
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 14, marginBottom: 24 }}>
          <div className="iam-stat">
            <div className="iam-stat-label">Global SMTP</div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 4 }}>
              <IamDot tone={g.configured ? 'success' : 'muted'} />
              <span style={{ fontSize: 14, fontWeight: 600 }}>{g.configured ? 'Configured' : 'Not configured'}</span>
            </div>
            {g.configured && <div className="iam-mono iam-stat-sub">{g.host}:{g.port}</div>}
          </div>
          <div className="iam-stat">
            <div className="iam-stat-label">Custom SMTP</div>
            <div className="iam-stat-value">{customCount}<span style={{ fontSize: 14, fontWeight: 400, color: 'var(--fg-muted)' }}> / {totalOrgs}</span></div>
            <div className="iam-stat-sub">organisations with own relay</div>
          </div>
          <div className="iam-stat">
            <div className="iam-stat-label">From-name overrides</div>
            <div className="iam-stat-value">{overrideCount}</div>
            <div className="iam-stat-sub">projects with custom sender name</div>
          </div>
        </div>

        <div className="iam-card iam-card-pad" style={{ marginBottom: 14 }}>
          <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', marginBottom: 14 }}>
            <div>
              <div style={{ fontSize: 14, fontWeight: 600, display: 'flex', alignItems: 'center', gap: 8 }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z"/><polyline points="22,6 12,13 2,6"/></svg>
                Global SMTP relay
              </div>
              <div style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 3 }}>
                Fallback relay used by organisations without their own SMTP. Configured via <span className="iam-mono">Smtp__*</span> environment variables.
              </div>
            </div>
            {g.configured
              ? <IamChip tone="success">Active</IamChip>
              : <IamChip tone="default">Not set</IamChip>}
          </div>
          {g.configured && (
            <div style={{ display: 'grid', gridTemplateColumns: '120px 1fr 120px 1fr', gap: '8px 16px', fontSize: 13 }}>
              <span style={{ color: 'var(--fg-muted)' }}>Host</span>
              <span className="iam-mono">{g.host}:{g.port}</span>
              <span style={{ color: 'var(--fg-muted)' }}>TLS</span>
              <span>{g.start_tls ? 'STARTTLS' : 'None / SSL'}</span>
              <span style={{ color: 'var(--fg-muted)' }}>From address</span>
              <span className="iam-mono">{g.from_address}</span>
              <span style={{ color: 'var(--fg-muted)' }}>From name</span>
              <span>{g.from_name}</span>
            </div>
          )}
        </div>

        <div className="iam-card">
          <div style={{ padding: '14px 16px', borderBottom: '1px solid var(--border)' }}>
            <div style={{ fontSize: 14, fontWeight: 600 }}>Organisation relay adoption</div>
            <div style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 2 }}>
              Each organisation can override the global relay with their own SMTP settings.
            </div>
          </div>
          {data.orgs.length === 0 ? (
            <div className="iam-empty"><div className="iam-empty-title">No organisations yet.</div></div>
          ) : (
            <table className="iam-tbl">
              <thead>
                <tr>
                  <th>Organisation</th>
                  <th>SMTP relay</th>
                  <th>From address</th>
                  <th>From name</th>
                  <th>Project overrides</th>
                  <th>Last updated</th>
                  <th style={{ width: 36 }}></th>
                </tr>
              </thead>
              <tbody>
                {data.orgs.map(org => (
                  <tr key={org.id}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{org.name}</div>
                      <div className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{org.slug}</div>
                    </td>
                    <td>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                        <IamDot tone={org.smtp_configured ? 'success' : 'muted'} />
                        <span style={{ fontSize: 12 }}>
                          {org.smtp_configured ? `${org.smtp_host}:${org.smtp_port}` : 'Global'}
                        </span>
                      </div>
                    </td>
                    <td className="iam-mono" style={{ fontSize: 12 }}>
                      {org.smtp_from_address ?? <span style={{ color: 'var(--fg-muted)' }}>{g.from_address ?? '—'}</span>}
                    </td>
                    <td style={{ fontSize: 13 }}>
                      {org.smtp_from_name ?? <span style={{ color: 'var(--fg-muted)' }}>{g.from_name ?? '—'}</span>}
                    </td>
                    <td>
                      {org.project_overrides.length === 0 ? (
                        <span style={{ color: 'var(--fg-muted)', fontSize: 13 }}>—</span>
                      ) : (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                          {org.project_overrides.map(p => (
                            <button key={p.id} className="iam-chip iam-chip-default"
                              title={`From name: "${p.email_from_name}"`}
                              onClick={() => navigate(`/system/organisations/${org.id}/projects`)}>
                              {p.name}
                            </button>
                          ))}
                        </div>
                      )}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>
                      {org.smtp_updated_at ? fmtDateShort(org.smtp_updated_at) : '—'}
                    </td>
                    <td>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
                        onClick={() => navigate(`/system/organisations/${org.id}/email`)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg>
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
}
