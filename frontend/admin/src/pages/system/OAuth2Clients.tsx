import { useEffect, useState } from 'react';
import { IamChip, IamDialog } from '@/components/iam';
import PageHeader from '@/components/layout/PageHeader';
import { listHydraClients, createHydraClient, getHydraClient, deleteHydraClient } from '@/api';
import { ApiError } from '@/auth';

/**
 * The OAuth2 clients Hydra holds, which is more than the ones created here.
 *
 * The list is Hydra's own registry, so it also contains the client the console mints for every
 * project (`client_`) and for every service account (`sa_`). Those two prefixes carry
 * authorisation meaning elsewhere in the backend — which is why creating one is refused here, and
 * why deleting one takes down a tenant's sign-in rather than an integration nobody uses.
 */

interface HydraClient {
  client_id: string;
  client_name?: string;
  grant_types?: string[];
  redirect_uris?: string[];
  post_logout_redirect_uris?: string[];
  scope?: string;
  token_endpoint_auth_method?: string;
  created_at?: string;
}

const GRANT_TYPES = ['authorization_code', 'refresh_token', 'client_credentials'];

const CREATE_ERRORS: Record<string, string> = {
  invalid_client_id: 'A client id may only contain letters, digits, "-", "_" and "." (64 characters at most), and may not start with "sa_" or "client_" — both are reserved by the backend.',
  client_id_taken: 'A client already uses that id.',
};

function apiErrorMessage(e: unknown, table: Record<string, string>, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return (body?.error && table[body.error]) ?? body?.detail ?? body?.error ?? fallback;
}

/** What the console itself created this client for, read off the reserved prefixes. */
function clientKind(id: string): string | null {
  if (id.startsWith('sa_')) return 'service account';
  if (id.startsWith('client_')) return 'project';
  return null;
}

/** One URI per line, blanks dropped — the form takes a textarea, the API takes an array. */
function lines(value: string): string[] {
  return value.split('\n').map(l => l.trim()).filter(Boolean);
}

const EMPTY_FORM = {
  client_id: '', client_name: '', scope: '',
  redirect_uris: '', post_logout_redirect_uris: '',
  grant_types: ['authorization_code', 'refresh_token'],
};

export default function OAuth2Clients() {
  const [clients, setClients] = useState<HydraClient[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [createOpen, setCreateOpen] = useState(false);
  const [form, setForm] = useState(EMPTY_FORM);
  const [saving, setSaving] = useState(false);
  const [createError, setCreateError] = useState('');

  const [detail, setDetail] = useState<HydraClient | null>(null);
  const [detailError, setDetailError] = useState('');

  const [deleteTarget, setDeleteTarget] = useState<HydraClient | null>(null);
  const [deleteError, setDeleteError] = useState('');

  // A write asks for a reload by bumping this, rather than by calling a fetch function the effect
  // also calls: `react-hooks/set-state-in-effect` refuses any setState the effect reaches
  // synchronously, and the whole list lives in setState. Inside `.then` it is fine.
  const [reloads, setReloads] = useState(0);
  const reload = () => setReloads(n => n + 1);

  useEffect(() => {
    listHydraClients()
      .then((r: HydraClient[]) => setClients(r ?? []))
      .catch(() => setError('Could not read the OAuth2 clients. Hydra may be unreachable.'))
      .finally(() => setLoading(false));
  }, [reloads]);

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    setCreateError('');
    try {
      await createHydraClient({
        client_name: form.client_name,
        grant_types: form.grant_types,
        redirect_uris: lines(form.redirect_uris),
        post_logout_redirect_uris: lines(form.post_logout_redirect_uris),
        scope: form.scope.trim() || undefined,
        client_id: form.client_id.trim() || undefined,
      });
      setCreateOpen(false);
      setForm(EMPTY_FORM);
      reload();
    } catch (e) {
      setCreateError(apiErrorMessage(e, CREATE_ERRORS, 'Could not create the client.'));
    } finally { setSaving(false); }
  };

  const handleDetail = async (id: string) => {
    setDetailError('');
    try {
      setDetail(await getHydraClient(id));
    } catch {
      setDetailError(`Could not read "${id}". It may have just been deleted.`);
    }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleteError('');
    try {
      await deleteHydraClient(deleteTarget.client_id);
      setDeleteTarget(null);
      reload();
    } catch (e) {
      setDeleteError(apiErrorMessage(e, {}, 'Could not delete the client.'));
    }
  };

  const toggleGrant = (grant: string) => setForm(f => ({
    ...f,
    grant_types: f.grant_types.includes(grant)
      ? f.grant_types.filter(g => g !== grant)
      : [...f.grant_types, grant],
  }));

  return (
    <div>
      <PageHeader
        title="OAuth2 Clients"
        description="Every client registered in Hydra, including the ones this console creates for projects and service accounts"
        actions={[
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            New Client
          </button>,
        ]}
      />

      {error && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{error}</div>}
      {detailError && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{detailError}</div>}

      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Client ID</th><th>Name</th><th>Grant types</th><th>Redirect URIs</th>
                <th style={{ width: 130 }}></th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <tr>{Array.from({ length: 5 }, (_, j) => (
                  <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                ))}</tr>
              )}

              {!loading && clients.length === 0 && (
                <tr><td colSpan={5}>
                  <div className="iam-empty">
                    <div className="iam-empty-title">No OAuth2 clients</div>
                    <div className="iam-empty-desc">
                      Register one for an application that signs users in through this deployment.
                    </div>
                  </div>
                </td></tr>
              )}

              {!loading && clients.map(c => (
                <tr key={c.client_id}>
                  <td>
                    <span className="iam-mono" style={{ fontSize: 12 }}>{c.client_id}</span>
                    {clientKind(c.client_id) && (
                      <span style={{ marginLeft: 8 }}><IamChip tone="warn">{clientKind(c.client_id)}</IamChip></span>
                    )}
                  </td>
                  <td style={{ fontSize: 12 }}>{c.client_name || '—'}</td>
                  <td style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                    {(c.grant_types ?? []).map(g => <IamChip key={g}>{g}</IamChip>)}
                  </td>
                  <td style={{ fontSize: 11, color: 'var(--fg-muted)' }}>
                    {(c.redirect_uris ?? []).map(u => <div key={u} className="iam-mono">{u}</div>)}
                  </td>
                  <td>
                    <button className="iam-btn iam-btn-ghost iam-btn-sm" onClick={() => handleDetail(c.client_id)}>
                      Details
                    </button>
                    <button className="iam-btn iam-btn-ghost iam-btn-sm" style={{ color: 'var(--danger)' }}
                      onClick={() => { setDeleteError(''); setDeleteTarget(c); }}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={!!detail}
        onClose={() => setDetail(null)}
        title={detail?.client_id ?? ''}
        desc={detail?.client_name}
        footer={<button className="iam-btn iam-btn-ghost" onClick={() => setDetail(null)}>Close</button>}
        wide
      >
        <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '8px 16px', fontSize: 12.5, margin: 0 }}>
          <dt style={{ color: 'var(--fg-muted)' }}>Grant types</dt>
          <dd style={{ margin: 0 }}>{(detail?.grant_types ?? []).join(', ') || '—'}</dd>
          <dt style={{ color: 'var(--fg-muted)' }}>Scope</dt>
          <dd className="iam-mono" style={{ margin: 0 }}>{detail?.scope || '—'}</dd>
          <dt style={{ color: 'var(--fg-muted)' }}>Auth method</dt>
          <dd className="iam-mono" style={{ margin: 0 }}>{detail?.token_endpoint_auth_method || '—'}</dd>
          <dt style={{ color: 'var(--fg-muted)' }}>Redirect URIs</dt>
          <dd style={{ margin: 0 }}>
            {(detail?.redirect_uris ?? []).map(u => <div key={u} className="iam-mono">{u}</div>)}
            {(detail?.redirect_uris ?? []).length === 0 && '—'}
          </dd>
          <dt style={{ color: 'var(--fg-muted)' }}>Post-logout URIs</dt>
          <dd style={{ margin: 0 }}>
            {(detail?.post_logout_redirect_uris ?? []).map(u => <div key={u} className="iam-mono">{u}</div>)}
            {(detail?.post_logout_redirect_uris ?? []).length === 0 && '—'}
          </dd>
        </dl>
      </IamDialog>

      <IamDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        title="Register an OAuth2 client"
        desc="For an application that signs users in through this deployment."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-client-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create client'}
            </button>
          </>
        }
      >
        <form id="create-client-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="client-name">Name</label>
            <input id="client-name" className="iam-input" value={form.client_name}
              onChange={e => setForm(f => ({ ...f, client_name: e.target.value }))}
              required placeholder="Billing portal" />
          </div>
          <div>
            <label className="iam-label" htmlFor="client-id">Client ID (optional)</label>
            <input id="client-id" className="iam-input iam-mono" value={form.client_id}
              onChange={e => setForm(f => ({ ...f, client_id: e.target.value }))}
              placeholder="billing-portal" />
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>
              Left empty, Hydra mints a random one. Pinning it keeps the integration's configuration
              the same across environments.
            </p>
          </div>
          <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
            <legend className="iam-label">Grant types</legend>
            {GRANT_TYPES.map(g => (
              <label key={g} style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 12.5 }}>
                <input type="checkbox" checked={form.grant_types.includes(g)} onChange={() => toggleGrant(g)} />
                <span className="iam-mono">{g}</span>
              </label>
            ))}
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>
              A client asking for client_credentials is registered with private_key_jwt and needs a
              key; any other combination is registered as a public client.
            </p>
          </fieldset>
          <div>
            <label className="iam-label" htmlFor="client-redirects">Redirect URIs — one per line</label>
            <textarea id="client-redirects" className="iam-input iam-mono" rows={3} value={form.redirect_uris}
              onChange={e => setForm(f => ({ ...f, redirect_uris: e.target.value }))}
              placeholder="https://billing.example.com/callback" />
          </div>
          <div>
            <label className="iam-label" htmlFor="client-logouts">Post-logout redirect URIs — one per line</label>
            <textarea id="client-logouts" className="iam-input iam-mono" rows={2} value={form.post_logout_redirect_uris}
              onChange={e => setForm(f => ({ ...f, post_logout_redirect_uris: e.target.value }))}
              placeholder="https://billing.example.com/" />
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>
              Hydra refuses a post-logout URI the client has not registered, so without one the
              application can be signed into and not out of.
            </p>
          </div>
          <div>
            <label className="iam-label" htmlFor="client-scope">Scope (optional)</label>
            <input id="client-scope" className="iam-input iam-mono" value={form.scope}
              onChange={e => setForm(f => ({ ...f, scope: e.target.value }))}
              placeholder="openid profile offline_access" />
          </div>
          {createError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{createError}</p>}
        </form>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete client "${deleteTarget?.client_id}"?`}
        desc="The application using this client stops being able to sign anyone in, immediately and without warning. Its tokens are refused at the next refresh. This cannot be undone — a new client gets a new secret and a new registration."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setDeleteTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleDelete}>Delete client</button>
          </>
        }
      >
        <div style={{ fontSize: 13 }}>
          <div><b>{deleteTarget?.client_name || deleteTarget?.client_id}</b></div>
          {(deleteTarget?.redirect_uris ?? []).map(u => (
            <div key={u} className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{u}</div>
          ))}
          {deleteTarget && clientKind(deleteTarget.client_id) && (
            <p style={{ color: 'var(--danger)', marginTop: 8 }}>
              This client belongs to a {clientKind(deleteTarget.client_id)} managed by this console.
              Deleting it here leaves that {clientKind(deleteTarget.client_id)} registered with no
              client, and nobody will be able to sign in to it.
            </p>
          )}
          {deleteError && <p style={{ fontSize: 12, color: 'var(--danger)', marginTop: 8 }}>{deleteError}</p>}
        </div>
      </IamDialog>
    </div>
  );
}
