import { rowActivation } from '../../components/iam/rowActivation';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { IamChip, IamAvatar, IamDialog } from '@/components/iam';
import { listOrgs, createOrg, suspendOrg, unsuspendOrg, deleteOrg } from '@/api';
import { OrgSuspendDialog, OrgDeleteDialog } from '@/components/OrgLifecycle';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Org {
  id: string;
  name: string;
  slug: string;
  active: boolean;
  suspended_at: string | null;
  created_at: string;
  metadata: Record<string, string>;
}

function MoreMenu({ org, onSuspend, onDelete }: Readonly<{
  org: Org; onSuspend: () => void; onDelete: () => void;
}>) {
  const [open, setOpen] = useState(false);

  // On the document, not on the scrim below. A div with no tabindex never receives a key event,
  // so the handler that used to sit there could not fire: the menu opened over the page and the
  // only way out was the mouse.
  useEffect(() => {
    if (!open) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', h);
    return () => document.removeEventListener('keydown', h);
  }, [open]);

  return (
    <div style={{ position: 'relative' }}>
      {/* Named: the button's whole content is an SVG, so without this a screen reader announces
          "button" and nothing else, and there is one per row. */}
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
        aria-label={`Actions for ${org.name}`}
        aria-expanded={open}
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 140, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }}
              onClick={() => { setOpen(false); onSuspend(); }}>
              {org.suspended_at ? 'Unsuspend' : 'Suspend'}
            </button>
            <div style={{ height: 1, background: 'var(--border)', margin: '4px 0' }} />
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--danger)' }}
              onClick={() => { setOpen(false); onDelete(); }}>
              Delete
            </button>
          </div>
        </>
      )}
    </div>
  );
}

export default function Organisations() {
  const navigate = useNavigate();
  const [orgs, setOrgs] = useState<Org[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Org | null>(null);
  const [suspendTarget, setSuspendTarget] = useState<Org | null>(null);
  const [form, setForm] = useState({ name: '', slug: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = () => {
    setLoading(true);
    listOrgs().then(setOrgs).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const filtered = orgs.filter(o =>
    o.name.toLowerCase().includes(search.toLowerCase()) ||
    o.slug.toLowerCase().includes(search.toLowerCase())
  );

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true); setError('');
    try {
      await createOrg(form);
      setCreateOpen(false);
      setForm({ name: '', slug: '' });
      load();
    } catch { setError('Failed to create organisation.'); }
    finally { setSaving(false); }
  };

  /**
   * Suspension is confirmed, unsuspension is not.
   *
   * Suspending revokes every live session of the tenant — its own administrator is signed out
   * mid-task and cannot sign back in. That is a destructive act on other people's work, and it
   * used to happen on one click of a menu item, while Delete two rows below asked. Unsuspending
   * takes nothing away, so it stays immediate.
   */
  const handleSuspend = async (org: Org) => {
    if (org.suspended_at) {
      await unsuspendOrg(org.id);
      load();
      return;
    }
    setSuspendTarget(org);
  };

  const confirmSuspend = async () => {
    if (!suspendTarget) return;
    await suspendOrg(suspendTarget.id);
    setSuspendTarget(null);
    load();
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteOrg(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Organisations"
        description="Manage all tenant organisations in the system"
        actions={[
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Organisation
          </button>
        ]}
      />
      <div className="iam-page">
        <div style={{ marginBottom: 14 }}>
          <div style={{ position: 'relative', maxWidth: 320 }}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
              style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', pointerEvents: 'none' }}>
              <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
            </svg>
            <input className="iam-input" style={{ paddingLeft: 30 }}
              placeholder="Search by name or slug…"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>
        </div>

        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Organisation</th>
                <th>Slug</th>
                <th>Status</th>
                <th>Created</th>
                <th style={{ width: 36 }}></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 5 }, (_, i) => (
                  <tr key={i}>
                    {Array.from({ length: 5 }, (_, j) => (
                      <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                    ))}
                  </tr>
                ));
                if (filtered.length === 0) return (
                  <tr>
                    <td colSpan={5}>
                      <div className="iam-empty">
                        <div className="iam-empty-icon">
                          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5"><path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><polyline points="9 22 9 12 15 12 15 22"/></svg>
                        </div>
                        <div className="iam-empty-title">No organisations found</div>
                        <div className="iam-empty-desc">Try a different search or create one.</div>
                      </div>
                    </td>
                  </tr>
                );
                return filtered.map(org => {
                  let statusChip = <IamChip tone="default">Inactive</IamChip>;
                  if (org.suspended_at) statusChip = <IamChip tone="danger">Suspended</IamChip>;
                  else if (org.active) statusChip = <IamChip tone="success">Active</IamChip>;
                  return (
                    <tr key={org.id} {...rowActivation(() => navigate(`/system/organisations/${org.id}`))}>
                      <td>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          <IamAvatar name={org.name} size="sm" />
                          <span style={{ fontWeight: 500 }}>{org.name}</span>
                        </div>
                      </td>
                      <td><span className="iam-mono" style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{org.slug}</span></td>
                      <td>{statusChip}</td>
                      <td style={{ color: 'var(--fg-muted)', fontSize: 12 }}>{fmtDateShort(org.created_at)}</td>
                      <td onClick={e => e.stopPropagation()}>
                        <MoreMenu org={org} onSuspend={() => handleSuspend(org)} onDelete={() => setDeleteTarget(org)} />
                      </td>
                    </tr>
                  );
                });
              })()}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={createOpen}
        onClose={() => { setCreateOpen(false); setError(''); }}
        title="Create Organisation"
        desc="A new tenant with its own projects and user lists."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-org-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-org-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          {error && <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>{error}</div>}
          <div>
            <label className="iam-label" htmlFor="org-create-name">Name</label>
            <input id="org-create-name" className="iam-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="Acme Corp" />
          </div>
          <div>
            <label className="iam-label" htmlFor="org-create-slug">Slug</label>
            <input id="org-create-slug" className="iam-input iam-mono" value={form.slug} onChange={e => setForm(f => ({ ...f, slug: e.target.value.toLowerCase().replaceAll(/\s+/g, '-') }))} required placeholder="acme-corp" pattern="[a-z0-9]+(-[a-z0-9]+)*" />
            <div style={{ fontSize: 11, color: 'var(--fg-subtle)', marginTop: 4 }}>Lowercase letters, numbers and hyphens only.</div>
          </div>
        </form>
      </IamDialog>

      <OrgSuspendDialog
        open={!!suspendTarget} name={suspendTarget?.name} suspended={false}
        onClose={() => setSuspendTarget(null)} onConfirm={confirmSuspend} />

      <OrgDeleteDialog
        open={!!deleteTarget} name={deleteTarget?.name}
        onClose={() => setDeleteTarget(null)} onConfirm={handleDelete} />
    </div>
  );
}
