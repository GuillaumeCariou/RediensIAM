import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { IamChip } from '@/components/iam';
import { adminListAllProjects } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  org_id: string; org_name: string; hydra_client_id: string | null; created_at: string;
}

export default function SystemProjects() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  useEffect(() => {
    adminListAllProjects()
      .then(r => setProjects(r.projects ?? r ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  }, []);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.org_name.toLowerCase().includes(search.toLowerCase()) ||
    p.slug.toLowerCase().includes(search.toLowerCase())
  );

  return (
    <div>
      <PageHeader title="All Projects" description="Every project across all organisations" />
      <div className="iam-page">
        <div style={{ marginBottom: 14 }}>
          <div style={{ position: 'relative', maxWidth: 320 }}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
              style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', pointerEvents: 'none' }}>
              <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
            </svg>
            <input className="iam-input" style={{ paddingLeft: 30 }}
              placeholder="Search by name, org, or slug…"
              value={search} onChange={e => setSearch(e.target.value)}
            />
          </div>
        </div>

        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Project</th>
                <th>Organisation</th>
                <th>Status</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 6 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 4 }, (_, j) => (
                    <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                  ))}</tr>
                ));
                if (filtered.length === 0) return (
                  <tr><td colSpan={4}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">{search ? 'No projects match your search' : 'No projects yet'}</div>
                    </div>
                  </td></tr>
                );
                return filtered.map(p => (
                  <tr key={p.id}>
                    <td>
                      <Link to={`/system/organisations/${p.org_id}/projects/${p.id}`}
                        style={{ textDecoration: 'none', color: 'inherit' }}>
                        <div style={{ fontWeight: 500 }}>{p.name}</div>
                        <div className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>/{p.slug}</div>
                      </Link>
                    </td>
                    <td>
                      <Link to={`/system/organisations/${p.org_id}`}
                        style={{ fontSize: 13, color: 'var(--fg-muted)', textDecoration: 'none' }}>
                        {p.org_name}
                      </Link>
                    </td>
                    <td>
                      {p.active ? <IamChip tone="success">Active</IamChip> : <IamChip tone="default">Inactive</IamChip>}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDateShort(p.created_at)}</td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
