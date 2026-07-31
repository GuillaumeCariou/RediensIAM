import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip, IamDialog } from '@/components/iam';
import {
  listServiceAccounts, createServiceAccount,
  generatePat, listPats, revokePat, deleteServiceAccount,
  getProjectInfo,
} from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface SA { id: string; name: string; description: string | null; active: boolean; last_used_at: string | null; }
interface Pat { id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string; }

function CopyButton({ text }: Readonly<{ text: string }>) {
  const [copied, setCopied] = useState(false);
  const copy = () => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); };
  return (
    <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" onClick={copy}>
      {copied
        ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="var(--success)" strokeWidth="2"><polyline points="20 6 9 17 4 12"/></svg>
        : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>}
    </button>
  );
}

function SaMenu({ onViewPats, onGenPat, onDelete }: Readonly<{
  onViewPats: () => void; onGenPat: () => void; onDelete: () => void;
}>) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative' }}>
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} onKeyDown={(e) => { if (e.key === 'Escape') setOpen(false); }} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 160, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onViewPats(); }}>View PATs</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onGenPat(); }}>Generate PAT</button>
            <div style={{ height: 1, background: 'var(--border)', margin: '4px 0' }} />
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--danger)' }} onClick={() => { setOpen(false); onDelete(); }}>Delete</button>
          </div>
        </>
      )}
    </div>
  );
}

export default function ProjectServiceAccounts() {
  const { projectId } = useProjectContext();
  const [accounts, setAccounts] = useState<SA[]>([]);
  const [assignedListId, setAssignedListId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<SA | null>(null);
  const [patSa, setPatSa] = useState<SA | null>(null);
  const [pats, setPats] = useState<Pat[]>([]);
  const [newPat, setNewPat] = useState<string | null>(null);
  const [genPatOpen, setGenPatOpen] = useState<SA | null>(null);
  const [form, setForm] = useState({ name: '', description: '' });
  const [patForm, setPatForm] = useState({ name: '', expires_at: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listServiceAccounts().then(r => setAccounts(r ?? [])),
      getProjectInfo(projectId).then(r => setAssignedListId(r.assigned_user_list_id ?? null)),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [projectId]);

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!assignedListId) return;
    setSaving(true);
    try {
      await createServiceAccount({ name: form.name, description: form.description || undefined, user_list_id: assignedListId });
      setCreateOpen(false);
      setForm({ name: '', description: '' });
      load();
    } finally { setSaving(false); }
  };

  const openPats = async (sa: SA) => {
    setPatSa(sa);
    const res = await listPats(sa.id);
    setPats(res.pats ?? res ?? []);
  };

  const handleGenPat = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!genPatOpen) return;
    setSaving(true);
    try {
      const res = await generatePat(genPatOpen.id, { name: patForm.name, expires_at: patForm.expires_at || undefined });
      setNewPat(res.token);
      setPatForm({ name: '', expires_at: '' });
      setGenPatOpen(null);
      if (patSa?.id === genPatOpen.id) openPats(genPatOpen);
    } finally { setSaving(false); }
  };

  const handleRevokePat = async (patId: string) => {
    if (!patSa) return;
    await revokePat(patSa.id, patId);
    setPats(p => p.filter(x => x.id !== patId));
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteServiceAccount(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Service Accounts"
        description="Non-human identities for this project"
        actions={projectId ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" disabled={!assignedListId} onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Service Account
          </button>
        ] : []}
      />
      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr><th>Name</th><th>Status</th><th>Last Used</th><th style={{ width: 36 }}></th></tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 3 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 4 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (accounts.length === 0) return (
                  <tr><td colSpan={4}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">No service accounts</div>
                      <div className="iam-empty-desc">{assignedListId ? 'Create one for automation.' : 'Assign a user list to this project first.'}</div>
                    </div>
                  </td></tr>
                );
                return accounts.map(sa => (
                  <tr key={sa.id}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{sa.name}</div>
                      {sa.description && <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{sa.description}</div>}
                    </td>
                    <td><IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip></td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(sa.last_used_at)}</td>
                    <td>
                      <SaMenu
                        onViewPats={() => openPats(sa)}
                        onGenPat={() => setGenPatOpen(sa)}
                        onDelete={() => setDeleteTarget(sa)}
                      />
                    </td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        title="Create Service Account"
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-proj-sa-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-proj-sa-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="sa-name">Name</label>
            <input id="sa-name" className="iam-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="ci-deploy-bot" />
          </div>
          <div>
            <label className="iam-label" htmlFor="sa-description">Description (optional)</label>
            <input id="sa-description" className="iam-input" value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} />
          </div>
        </form>
      </IamDialog>

      <IamDialog
        open={!!patSa}
        onClose={() => setPatSa(null)}
        title={`PATs — ${patSa?.name}`}
        desc="Raw tokens are shown once at generation."
        wide
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setPatSa(null)}>Close</button>
            <button className="iam-btn iam-btn-secondary" onClick={() => { const s = patSa; setPatSa(null); setGenPatOpen(s); }}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
              Generate PAT
            </button>
          </>
        }
      >
        <div style={{ maxHeight: 320, overflowY: 'auto' }}>
          {pats.length === 0
            ? <p style={{ fontSize: 13, color: 'var(--fg-muted)', textAlign: 'center', padding: '16px 0' }}>No PATs yet.</p>
            : pats.map((p, i) => (
                <div key={p.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '10px 0', borderBottom: i < pats.length - 1 ? '1px solid var(--border)' : 'none' }}>
                  <div>
                    <p style={{ fontSize: 13, fontWeight: 500 }}>{p.name}</p>
                    <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Expires: {fmtDate(p.expires_at)} · Last used: {fmtDate(p.last_used_at)}</p>
                  </div>
                  <button className="iam-btn iam-btn-ghost iam-btn-sm" style={{ color: 'var(--danger)' }} onClick={() => handleRevokePat(p.id)}>Revoke</button>
                </div>
              ))
          }
        </div>
      </IamDialog>

      <IamDialog
        open={!!genPatOpen}
        onClose={() => setGenPatOpen(null)}
        title="Generate PAT"
        desc="The token will only be shown once."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setGenPatOpen(null)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="gen-pat-form" type="submit" disabled={saving}>
              {saving ? 'Generating…' : 'Generate'}
            </button>
          </>
        }
      >
        <form id="gen-pat-form" onSubmit={handleGenPat} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="pat-name">Token Name</label>
            <input id="pat-name" className="iam-input" value={patForm.name} onChange={e => setPatForm(f => ({ ...f, name: e.target.value }))} required placeholder="ci-pipeline-token" />
          </div>
          <div>
            <label className="iam-label" htmlFor="pat-expires">Expires At (optional)</label>
            <input id="pat-expires" className="iam-input" type="datetime-local" value={patForm.expires_at} onChange={e => setPatForm(f => ({ ...f, expires_at: e.target.value }))} />
          </div>
        </form>
      </IamDialog>

      <IamDialog
        open={!!newPat}
        onClose={() => setNewPat(null)}
        title="Your New PAT"
        desc="Copy this token now — it will not be shown again."
        footer={<button className="iam-btn iam-btn-primary" onClick={() => setNewPat(null)}>Done</button>}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 12px', background: 'var(--surface-2)', borderRadius: 6, fontFamily: 'var(--font-mono)', fontSize: 12, wordBreak: 'break-all' }}>
          <span style={{ flex: 1 }}>{newPat}</span>
          {newPat && <CopyButton text={newPat} />}
        </div>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete ${deleteTarget?.name}?`}
        desc="All PATs for this service account will also be revoked."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setDeleteTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleDelete}>Delete</button>
          </>
        }
      >
        <div />
      </IamDialog>
    </div>
  );
}
