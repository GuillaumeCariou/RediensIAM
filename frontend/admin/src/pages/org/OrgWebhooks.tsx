import { useEffect, useState } from 'react';
import { IamChip, IamDialog } from '@/components/iam';
import { listWebhooks, createWebhook, getWebhook, updateWebhook, deleteWebhook, testWebhook, rotateWebhookSecret, listWebhookDeliveries } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { useOrgContext } from '@/hooks/useOrgContext';
import { fmtDate } from '@/lib/utils';

interface Webhook {
  id: string; url: string; events: string[]; active: boolean;
  last_delivery_status?: number | null; created_at: string;
}

interface Delivery {
  id: string; event: string; status_code: number | null;
  attempt_count: number; delivered_at: string | null; payload?: string | null;
  error_message?: string | null;
}

/** Ce que `GET /org/webhooks/{id}` ajoute à la ligne du tableau : l'URL et les événements entiers. */
interface WebhookDetail extends Webhook {
  recent_deliveries?: Delivery[];
}

const EVENT_GROUPS: { label: string; events: string[] }[] = [
  { label: 'User events', events: ['user.created', 'user.updated', 'user.deleted', 'user.locked', 'user.login.success', 'user.login.failure'] },
  { label: 'Role events', events: ['role.assigned', 'role.revoked'] },
  { label: 'Session events', events: ['session.revoked'] },
  { label: 'Project events', events: ['project.updated'] },
];

function Toggle({ checked, onChange }: Readonly<{ checked: boolean; onChange: () => void }>) {
  return (
    <input type="checkbox" className="iam-switch" checked={checked} onChange={onChange} />
  );
}

export default function OrgWebhooks() {
  const { isSystemCtx } = useOrgContext();
  const [webhooks, setWebhooks] = useState<Webhook[]>([]);
  const [loading, setLoading] = useState(true);
  const [addOpen, setAddOpen] = useState(false);
  const [newUrl, setNewUrl] = useState('');
  const [newEvents, setNewEvents] = useState<string[]>([]);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState('');
  const [secretOpen, setSecretOpen] = useState(false);
  const [newSecret, setNewSecret] = useState('');
  const [deliveriesOpen, setDeliveriesOpen] = useState(false);
  const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [deliveriesLoading, setDeliveriesLoading] = useState(false);
  const [expandedDelivery, setExpandedDelivery] = useState<string | null>(null);
  const [testMsg, setTestMsg] = useState<{ id: string; ok: boolean; text: string } | null>(null);
  const [rotateTarget, setRotateTarget] = useState<Webhook | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);
  const [detail, setDetail] = useState<WebhookDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState('');

  const load = () => {
    // /org/webhooks is scoped by the caller's own token, and there is no admin-scope equivalent —
    // so in the system context this page used to list and edit the SIGNED-IN admin's webhooks
    // while the URL named someone else's organisation. Rather than write to the wrong tenant, it
    // says what it cannot do.
    if (isSystemCtx) { setWebhooks([]); setLoading(false); return; }
    setLoading(true);
    listWebhooks()
      .then((d: Webhook[]) => setWebhooks(Array.isArray(d) ? d : []))
      .catch(console.error)
      .finally(() => setLoading(false));
  };
  useEffect(load, [isSystemCtx]);

  /**
   * The `else` branch below must not close the dialog quietly. A webhook created without a
   * returned signing secret leaves the user with no way to verify signatures and no second
   * chance to read the secret, so the failure is surfaced and they are told to rotate it.
   */
  const handleCreate = async () => {
    setCreateError('');
    if (!newUrl.startsWith('https://')) { setCreateError('URL must use HTTPS.'); return; }
    if (newEvents.length === 0) { setCreateError('Select at least one event.'); return; }
    setCreating(true);
    try {
      const res = await createWebhook({ url: newUrl, events: newEvents });
      if (res.error) { setCreateError(res.error_description ?? 'Failed to create webhook.'); return; }
      if (res.secret) {
        setAddOpen(false); setNewUrl(''); setNewEvents([]);
        setNewSecret(res.secret);
        setSecretOpen(true);
        load();
      } else {
        setCreateError('Webhook created, but the server did not return a signing secret. Please rotate the secret manually before relying on signature verification.');
        load();
      }
    } finally { setCreating(false); }
  };

  const handleToggleActive = async (wh: Webhook) => {
    setWebhooks(ws => ws.map(w => w.id === wh.id ? { ...w, active: !w.active } : w));
    await updateWebhook(wh.id, { active: !wh.active });
  };

  const handleDelete = async (id: string) => {
    await deleteWebhook(id);
    setWebhooks(ws => ws.filter(w => w.id !== id));
  };

  /**
   * Refrappe le secret et le montre. La boîte est celle de la création : le serveur renvoie le
   * secret en clair une seule fois dans les deux cas, et rien ne peut le relire ensuite.
   *
   * Le message d'échec de création disait déjà « rotate the secret manually » — la route existait,
   * la console n'avait aucun bouton pour l'appeler.
   */
  const handleRotateSecret = async (id: string) => {
    setRotateTarget(null);
    try {
      const res = await rotateWebhookSecret(id);
      if (!res.secret) {
        setTestMsg({ id, ok: false, text: 'The server rotated the secret but did not return it. Rotate again.' });
        setTimeout(() => setTestMsg(null), 4000);
        return;
      }
      setNewSecret(res.secret);
      setSecretOpen(true);
    } catch {
      setTestMsg({ id, ok: false, text: 'Failed to rotate the signing secret.' });
      setTimeout(() => setTestMsg(null), 4000);
    }
  };

  const handleTest = async (id: string) => {
    setTestMsg(null);
    const res = await testWebhook(id);
    if (res.error) setTestMsg({ id, ok: false, text: `Test failed: ${res.error}` });
    else setTestMsg({ id, ok: true, text: 'Test payload sent.' });
    setTimeout(() => setTestMsg(null), 4000);
  };

  /**
   * Le détail complet. Le tableau tronque l'URL et n'affiche que trois événements sur N ; cette
   * route rend les deux entiers, plus les dix dernières livraisons. Elle n'avait aucun appelant.
   */
  const openDetail = (id: string) => {
    setDetailOpen(true); setDetail(null); setDetailError(''); setDetailLoading(true);
    getWebhook(id)
      .then(setDetail)
      .catch(() => setDetailError('Could not read this webhook.'))
      .finally(() => setDetailLoading(false));
  };

  const openDeliveries = (id: string) => {
    setDeliveriesOpen(true); setExpandedDelivery(null); setDeliveriesLoading(true);
    listWebhookDeliveries(id)
      .then((d: Delivery[]) => setDeliveries(Array.isArray(d) ? d : []))
      .catch(console.error)
      .finally(() => setDeliveriesLoading(false));
  };

  const toggleEventSelection = (ev: string) => setNewEvents(evs => evs.includes(ev) ? evs.filter(e => e !== ev) : [...evs, ev]);
  const toggleGroup = (events: string[]) => {
    const allSelected = events.every(e => newEvents.includes(e));
    if (allSelected) setNewEvents(evs => evs.filter(e => !events.includes(e)));
    else setNewEvents(evs => [...new Set([...evs, ...events])]);
  };

  if (isSystemCtx) {
    return (
      <div>
        <PageHeader title="Webhooks" description="Receive HTTP notifications when events occur" />
        <div className="iam-page">
          <div className="iam-empty">
            <div className="iam-empty-title">Not available from the system console</div>
            <div className="iam-empty-desc">
              Webhooks are scoped to the organisation whose credentials made the request, and there
              is no deployment-wide route for them. Manage them from that organisation&apos;s own
              console. This page used to edit your own organisation&apos;s webhooks here.
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title="Webhooks"
        description="Receive HTTP notifications when events occur"
        actions={[
          <button key="add" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => { setCreateError(''); setAddOpen(true); }}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            Add Webhook
          </button>
        ]}
      />
      <div className="iam-page">
        <div className="iam-card">
          {(() => {
            if (loading) return (
              <div style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 8 }}>
                {Array.from({ length: 3 }, (_, i) => <div key={i} style={{ height: 40, background: 'var(--surface-2)', borderRadius: 6 }} />)}
              </div>
            );
            if (webhooks.length === 0) return (
              <div className="iam-empty">
                <div className="iam-empty-title">No webhooks configured</div>
                <div className="iam-empty-desc">Add one to receive event notifications.</div>
              </div>
            );
            return (
            <table className="iam-tbl">
              <thead>
                <tr><th>URL</th><th>Events</th><th>Active</th><th>Last status</th><th>Created</th><th style={{ width: 36 }}></th></tr>
              </thead>
              <tbody>
                {webhooks.map(wh => (
                  <tr key={wh.id}>
                    <td><span className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)', maxWidth: 240, overflow: 'hidden', textOverflow: 'ellipsis', display: 'block', whiteSpace: 'nowrap' }}>{wh.url}</span></td>
                    <td>
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 3, maxWidth: 220 }}>
                        {wh.events.slice(0, 3).map(e => (
                          <span key={e} className="iam-chip iam-chip-mono" style={{ fontSize: 10 }}>{e}</span>
                        ))}
                        {wh.events.length > 3 && <span className="iam-chip" style={{ fontSize: 10 }}>+{wh.events.length - 3}</span>}
                      </div>
                    </td>
                    <td><Toggle checked={wh.active} onChange={() => handleToggleActive(wh)} /></td>
                    <td>
                      {wh.last_delivery_status == null
                        ? <span style={{ fontSize: 12, color: 'var(--fg-muted)' }}>—</span>
                        : <IamChip tone={wh.last_delivery_status >= 200 && wh.last_delivery_status < 300 ? 'success' : 'danger'}>
                            {wh.last_delivery_status}
                          </IamChip>}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(wh.created_at)}</td>
                    <td>
                      {testMsg?.id === wh.id && (
                        <span style={{ fontSize: 11, marginRight: 8, color: testMsg.ok ? 'var(--success)' : 'var(--danger)' }}>{testMsg.text}</span>
                      )}
                      <WebhookMenu
                        onDetails={() => openDetail(wh.id)}
                        onTest={() => handleTest(wh.id)}
                        onRotate={() => setRotateTarget(wh)}
                        onDeliveries={() => openDeliveries(wh.id)}
                        onDelete={() => handleDelete(wh.id)}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            );
          })()}
        </div>
      </div>

      <IamDialog
        open={addOpen}
        onClose={() => setAddOpen(false)}
        title="Add Webhook"
        desc="Receive HTTP POST notifications when events occur in your organisation."
        wide
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setAddOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" onClick={handleCreate} disabled={creating}>
              {creating ? 'Creating…' : 'Create Webhook'}
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          {createError && (
            <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>{createError}</div>
          )}
          <div>
            <label className="iam-label" htmlFor="webhook-url">URL</label>
            <input id="webhook-url" className="iam-input" type="url" placeholder="https://example.com/webhook" value={newUrl} onChange={e => setNewUrl(e.target.value)} />
          </div>
          <div>
            <span className="iam-label" style={{ marginBottom: 10, display: 'block' }}>Events</span>
            {EVENT_GROUPS.map(group => {
              const allChecked = group.events.every(e => newEvents.includes(e));
              const someChecked = group.events.some(e => newEvents.includes(e));
              return (
                <div key={group.label} style={{ marginBottom: 12 }}>
                  <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 11, fontWeight: 600, color: 'var(--fg-subtle)', textTransform: 'uppercase', letterSpacing: '0.06em', cursor: 'pointer', marginBottom: 6 }}>
                    <input type="checkbox" checked={allChecked}
                      ref={el => { if (el) el.indeterminate = someChecked && !allChecked; }}
                      onChange={() => toggleGroup(group.events)} />
                    {group.label}
                  </label>
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 4, paddingLeft: 16 }}>
                    {group.events.map(ev => (
                      <label key={ev} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12, cursor: 'pointer' }}>
                        <input type="checkbox" checked={newEvents.includes(ev)} onChange={() => toggleEventSelection(ev)} />
                        <span className="iam-mono">{ev}</span>
                      </label>
                    ))}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </IamDialog>

      <IamDialog
        open={!!rotateTarget}
        onClose={() => setRotateTarget(null)}
        title="Rotate this webhook's signing secret?"
        desc="The current secret stops being valid immediately. Every receiver verifying signatures with it will reject deliveries until you install the new one."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setRotateTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={() => rotateTarget && handleRotateSecret(rotateTarget.id)}>Rotate</button>
          </>
        }
      >
        <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', wordBreak: 'break-all' }}>{rotateTarget?.url}</div>
      </IamDialog>

      <IamDialog
        open={secretOpen}
        onClose={() => setSecretOpen(false)}
        title="Webhook Secret"
        desc="Copy this now — it won't be shown again. Use it to verify webhook signatures."
        footer={
          <>
            <button className="iam-btn iam-btn-secondary" onClick={() => navigator.clipboard.writeText(newSecret)}>Copy</button>
            <button className="iam-btn iam-btn-primary" onClick={() => { setSecretOpen(false); setNewSecret(''); }}>I've saved it</button>
          </>
        }
      >
        <div style={{ padding: 14, background: 'var(--bg-sunken)', border: '1px solid var(--border)', borderRadius: 8, fontFamily: 'var(--font-mono)', fontSize: 12, wordBreak: 'break-all' }}>
          {newSecret}
        </div>
      </IamDialog>

      <IamDialog
        open={detailOpen}
        onClose={() => setDetailOpen(false)}
        title="Webhook Details"
        desc="The full endpoint and every event it is subscribed to."
        wide
        footer={<button className="iam-btn iam-btn-ghost" onClick={() => setDetailOpen(false)}>Close</button>}
      >
        {(() => {
          if (detailError) return <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>{detailError}</div>;
          if (detailLoading || !detail) return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {Array.from({ length: 3 }, (_, i) => <div key={i} style={{ height: 32, background: 'var(--surface-2)', borderRadius: 6 }} />)}
            </div>
          );
          return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14, fontSize: 13 }}>
              <div>
                <span className="iam-label" style={{ display: 'block', marginBottom: 4 }}>URL</span>
                <span className="iam-mono" style={{ wordBreak: 'break-all', fontSize: 12 }}>{detail.url}</span>
              </div>
              <div>
                <span className="iam-label" style={{ display: 'block', marginBottom: 6 }}>Events ({detail.events.length})</span>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {detail.events.map(e => <span key={e} className="iam-chip iam-chip-mono" style={{ fontSize: 10 }}>{e}</span>)}
                </div>
              </div>
              <div style={{ display: 'flex', gap: 20 }}>
                <div>
                  <span className="iam-label" style={{ display: 'block', marginBottom: 4 }}>Status</span>
                  <IamChip tone={detail.active ? 'success' : 'default'}>{detail.active ? 'Active' : 'Paused'}</IamChip>
                </div>
                <div>
                  <span className="iam-label" style={{ display: 'block', marginBottom: 4 }}>Created</span>
                  <span style={{ color: 'var(--fg-muted)', fontSize: 12 }}>{fmtDate(detail.created_at)}</span>
                </div>
              </div>
              <div>
                <span className="iam-label" style={{ display: 'block', marginBottom: 6 }}>Recent deliveries</span>
                {(detail.recent_deliveries ?? []).length === 0
                  ? <span style={{ color: 'var(--fg-muted)', fontSize: 12 }}>No deliveries yet.</span>
                  : (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      {(detail.recent_deliveries ?? []).map(d => (
                        <div key={d.id} style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: 12 }}>
                          <span className="iam-mono" style={{ flex: 1 }}>{d.event}</span>
                          {d.status_code == null
                            ? <IamChip tone="default">pending</IamChip>
                            : <IamChip tone={d.status_code >= 200 && d.status_code < 300 ? 'success' : 'danger'}>{d.status_code}</IamChip>}
                          <span style={{ color: 'var(--fg-muted)', fontSize: 11 }}>{d.delivered_at ? fmtDate(d.delivered_at) : '—'}</span>
                        </div>
                      ))}
                    </div>
                  )}
              </div>
            </div>
          );
        })()}
      </IamDialog>

      <IamDialog
        open={deliveriesOpen}
        onClose={() => setDeliveriesOpen(false)}
        title="Delivery Log"
        desc="Last 25 deliveries for this webhook."
        wide
        footer={<button className="iam-btn iam-btn-ghost" onClick={() => setDeliveriesOpen(false)}>Close</button>}
      >
        {(() => {
          if (deliveriesLoading) return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {Array.from({ length: 5 }, (_, i) => <div key={i} style={{ height: 40, background: 'var(--surface-2)', borderRadius: 6 }} />)}
            </div>
          );
          if (deliveries.length === 0) return (
            <div className="iam-empty"><div className="iam-empty-title">No deliveries yet.</div></div>
          );
          return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 380, overflowY: 'auto' }}>
              {deliveries.map(d => {
                const statusChip = d.status_code == null
                  ? <IamChip tone="default">pending</IamChip>
                  : <IamChip tone={d.status_code >= 200 && d.status_code < 300 ? 'success' : 'danger'}>{d.status_code}</IamChip>;
                return (
                  <div key={d.id} style={{ border: '1px solid var(--border)', borderRadius: 6, overflow: 'hidden' }}>
                    <button
                      style={{ display: 'flex', width: '100%', alignItems: 'center', gap: 10, padding: '8px 12px', fontSize: 12, background: 'none', cursor: 'pointer', textAlign: 'left' }}
                      onClick={() => setExpandedDelivery(expandedDelivery === d.id ? null : d.id)}
                    >
                      <span className="iam-mono" style={{ flex: 1 }}>{d.event}</span>
                      {statusChip}
                      <span style={{ color: 'var(--fg-muted)', fontSize: 11 }}>{d.attempt_count} attempt{d.attempt_count === 1 ? '' : 's'}</span>
                      <span style={{ color: 'var(--fg-muted)', fontSize: 11 }}>{d.delivered_at ? fmtDate(d.delivered_at) : '—'}</span>
                    </button>
                    {expandedDelivery === d.id && d.payload && (
                      <pre style={{ fontSize: 11, background: 'var(--bg-sunken)', padding: 12, margin: 0, overflow: 'auto', borderTop: '1px solid var(--border)', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
                        {(() => { try { return JSON.stringify(JSON.parse(d.payload), null, 2); } catch { return d.payload; } })()}
                      </pre>
                    )}
                  </div>
                );
              })}
            </div>
          );
        })()}
      </IamDialog>
    </div>
  );
}

function WebhookMenu({ onDetails, onTest, onDeliveries, onRotate, onDelete }: Readonly<{ onDetails: () => void; onTest: () => void; onDeliveries: () => void; onRotate: () => void; onDelete: () => void; }>) {
  const [open, setOpen] = useState(false);

  // On the document, not on the scrim below — see the same note in system/Organisations.tsx.
  useEffect(() => {
    if (!open) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', h);
    return () => document.removeEventListener('keydown', h);
  }, [open]);

  return (
    <div style={{ position: 'relative' }}>
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" onClick={() => setOpen(o => !o)}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 140, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onDetails(); }}>View details</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onTest(); }}>Test</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onDeliveries(); }}>View deliveries</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onRotate(); }}>Rotate secret</button>
            <div style={{ height: 1, background: 'var(--border)', margin: '4px 0' }} />
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--danger)' }} onClick={() => { setOpen(false); onDelete(); }}>Delete</button>
          </div>
        </>
      )}
    </div>
  );
}
