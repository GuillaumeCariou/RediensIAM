import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { IamChip, StatCard } from '@/components/iam';
import { getOrgInfo, listProjects, listUserLists } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Org { id: string; name: string; slug: string; active: boolean; suspended_at: string | null; created_at: string; metadata: Record<string, string>; }
interface Project { id: string; name: string; slug: string; active: boolean; }
interface UserList { id: string; name: string; immovable: boolean; user_count: number; }

export default function OrgDashboard() {
  const { orgId, orgBase, projectUrl } = useOrgContext();
  const [org, setOrg] = useState<Org | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [lists, setLists] = useState<UserList[]>([]);
  // Starts false when there is no org: the effect below has nothing to fetch, and flipping the
  // flag from inside it is a synchronous setState in an effect (react-hooks/set-state-in-effect).
  const [loading, setLoading] = useState(Boolean(orgId));

  useEffect(() => {
    if (!orgId) return;
    Promise.all([
      getOrgInfo().then(setOrg),
      listProjects(orgId).then(r => setProjects(r.projects ?? r ?? [])),
      listUserLists(orgId).then(r => setLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [orgId]);

  if (!orgId) return (
    <div>
      <PageHeader title="Organisation" />
      <div className="iam-page" style={{ fontSize: 13, color: 'var(--fg-muted)' }}>
        No organisation selected. Navigate here from <Link to="/system/organisations" style={{ color: 'var(--ia-accent)' }}>Organisations</Link>.
      </div>
    </div>
  );

  let orgStatusChip = null;
  if (org) {
    if (org.suspended_at) orgStatusChip = <IamChip tone="danger">Suspended</IamChip>;
    else if (org.active) orgStatusChip = <IamChip tone="success">Active</IamChip>;
    else orgStatusChip = <IamChip tone="default">Inactive</IamChip>;
  }

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (org?.name ?? 'Organisation')}
        description={org ? `/${org.slug}` : undefined}
        actions={orgStatusChip ? [orgStatusChip] : []}
      />
      <div className="iam-page">
        <div className="iam-stats-grid" style={{ marginBottom: 24 }}>
          <StatCard label="Projects" value={loading ? '—' : projects.length} />
          <StatCard label="User Lists" value={loading ? '—' : lists.length} />
          <StatCard label="Total Users" value={loading ? '—' : lists.reduce((n, l) => n + (l.user_count ?? 0), 0)} />
          <StatCard label="Member since" value={loading ? '—' : fmtDateShort(org?.created_at)} />
        </div>

        {!loading && projects.length > 0 && (
          <div className="iam-card" style={{ marginBottom: 14 }}>
            <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div style={{ fontSize: 13, fontWeight: 600 }}>Projects</div>
              <Link to={`${orgBase}/projects`}
                style={{ fontSize: 12, color: 'var(--ia-accent)', textDecoration: 'none' }}>
                Manage →
              </Link>
            </div>
            <table className="iam-tbl">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Slug</th>
                  <th>Status</th>
                  <th style={{ width: 80 }}></th>
                </tr>
              </thead>
              <tbody>
                {projects.map(p => (
                  <tr key={p.id}>
                    <td style={{ fontWeight: 500 }}>{p.name}</td>
                    <td><span className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{p.slug}</span></td>
                    <td>{p.active ? <IamChip tone="success">Active</IamChip> : <IamChip tone="default">Inactive</IamChip>}</td>
                    <td>
                      <Link to={projectUrl(p.id)} style={{ fontSize: 12, color: 'var(--ia-accent)', textDecoration: 'none' }}>Open →</Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {!loading && lists.length > 0 && (
          <div className="iam-card">
            <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div style={{ fontSize: 13, fontWeight: 600 }}>User Lists</div>
              <Link to={`${orgBase}/userlists`} style={{ fontSize: 12, color: 'var(--ia-accent)', textDecoration: 'none' }}>Manage →</Link>
            </div>
            <table className="iam-tbl">
              <thead>
                <tr><th>Name</th><th>Users</th></tr>
              </thead>
              <tbody>
                {lists.map(l => (
                  <tr key={l.id}>
                    <td style={{ fontWeight: 500 }}>{l.name}</td>
                    <td style={{ fontSize: 13 }}>{l.user_count ?? 0}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
