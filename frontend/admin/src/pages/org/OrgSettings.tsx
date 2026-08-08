import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import {
  getOrg, getOrgInfo, updateOrg, updateOrgInfo,
  getOrgSmtp, adminGetOrgSmtp,
  suspendOrg, unsuspendOrg, deleteOrg,
} from '@/api';
import { ApiError } from '@/auth';
import { useOrgContext } from '@/hooks/useOrgContext';
import { IamChip, IamDialog } from '@/components/iam';
import PageHeader from '@/components/layout/PageHeader';

/**
 * Everything about one tenant that is not a list of something else.
 *
 * <p>This page is routed at both <code>/org/settings</code> and
 * <code>/system/organisations/:id/settings</code>. It used to call the token-scoped
 * <code>/org</code> routes in both cases, so a super admin opening another organisation's settings
 * read and wrote their OWN org's retention while the URL said otherwise.</p>
 *
 * <p>The two audiences do not get the same page, and not for cosmetic reasons: the rename, the
 * suspension and the deletion are <code>/admin/organizations/*</code>, super-admin only.
 * <code>PATCH /org/settings</code> binds nothing but the retention. A tenant admin is shown the
 * name read-only and no danger zone rather than buttons that answer 403.</p>
 */

interface Org {
  id?: string;
  name?: string;
  slug?: string;
  active?: boolean;
  suspended_at?: string | null;
  audit_retention_days?: number | null;
}

interface Smtp { configured: boolean; host?: string; port?: number }

/**
 * What a write can refuse, said in the operator's words. Motif `apiErrorMessage`: a rejected
 * promise with no catch leaves the page unchanged and the refusal in the browser console, which is
 * the defect this whole screen was rebuilt over.
 */
const SAVE_ERRORS: Record<string, string> = {
  audit_retention_too_short: 'Retention must be at least 90 days. Nothing was changed.',
  name_too_long: 'That name is too long. Nothing was changed.',
};

function apiErrorMessage(e: unknown, table: Record<string, string>, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return (body?.error && table[body.error]) ?? body?.detail ?? body?.error ?? fallback;
}

/** The floor `OrgController` refuses below, and the ceiling the deployment clamps its own value to. */
const MIN_RETENTION = 90;
const MAX_RETENTION = 3650;

export default function OrgSettings() {
  const { orgId, isSystemCtx, orgBase } = useOrgContext();
  const navigate = useNavigate();

  const [org, setOrg] = useState<Org | null>(null);
  const [smtp, setSmtp] = useState<Smtp | null>(null);
  const [name, setName] = useState('');
  const [retention, setRetention] = useState('');
  const [useDefault, setUseDefault] = useState(false);

  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState('');
  const [saveError, setSaveError] = useState('');
  const [mailError, setMailError] = useState('');

  const [suspendOpen, setSuspendOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [dangerBusy, setDangerBusy] = useState(false);
  const [dangerError, setDangerError] = useState('');

  // Written as promise callbacks rather than await/try, and there is no `setLoading(true)`: both
  // are called straight from the effect below, and a setState reached synchronously from an effect
  // body is what `react-hooks/set-state-in-effect` refuses. The skeleton is only wanted on the
  // first read anyway — a re-read after a save must not blank the card being looked at.
  const load = useCallback(() => {
    const read: Promise<Org> = isSystemCtx && orgId ? getOrg(orgId) : getOrgInfo();
    return read
      .then(d => {
        setOrg(d);
        setName(d.name ?? '');
        // Without this the page rendered the deployment default — which null also means — so a
        // failed read was indistinguishable from a real setting, and saving wiped the real one.
        setUseDefault(d.audit_retention_days == null);
        setRetention(d.audit_retention_days == null ? '' : String(d.audit_retention_days));
        setError('');
      })
      .catch(() => setError('Could not load these settings. Reload before changing anything.'))
      .finally(() => setLoading(false));
  }, [orgId, isSystemCtx]);

  // Not folded into the load above: not knowing which relay this tenant sends through must not
  // hide the retention and the danger zone, and "unknown" must not read as "the default one".
  const loadSmtp = useCallback(() => {
    const read: Promise<Smtp> = isSystemCtx && orgId ? adminGetOrgSmtp(orgId) : getOrgSmtp();
    return read
      .then(s => { setSmtp(s); setMailError(''); })
      .catch((e: unknown) => setMailError(apiErrorMessage(e, {}, 'Could not read this organisation’s mail relay.')));
  }, [orgId, isSystemCtx]);

  useEffect(() => { void load(); void loadSmtp(); }, [load, loadSmtp]);

  const save = async () => {
    setSaving(true);
    setSaveError('');
    try {
      // -1 is the sentinel `OrgController` reads as "back to the deployment default"; null is
      // dropped by `HasValue` and wrote nothing at all, which is what "Forever" used to send.
      const days = useDefault ? -1 : Number(retention);
      if (isSystemCtx && orgId) await updateOrg(orgId, { name, audit_retention_days: days });
      else await updateOrgInfo({ audit_retention_days: days });
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
      await load();
    } catch (e) {
      setSaveError(apiErrorMessage(e, SAVE_ERRORS, 'Could not save. Nothing was changed.'));
    } finally { setSaving(false); }
  };

  const suspended = !!org?.suspended_at;

  const toggleSuspend = async () => {
    if (!orgId) return;
    setDangerBusy(true);
    setDangerError('');
    try {
      if (suspended) await unsuspendOrg(orgId);
      else await suspendOrg(orgId);
      setSuspendOpen(false);
      await load();
    } catch (e) {
      setDangerError(apiErrorMessage(e, {}, 'Could not change the suspension. Nothing was changed.'));
    } finally { setDangerBusy(false); }
  };

  const destroy = async () => {
    if (!orgId) return;
    setDangerBusy(true);
    setDangerError('');
    try {
      await deleteOrg(orgId);
      navigate('/system/organisations');
    } catch (e) {
      setDangerError(apiErrorMessage(e, {}, 'Could not delete this organisation. Nothing was destroyed.'));
    } finally { setDangerBusy(false); }
  };

  const saveButton = (
    <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={save} disabled={saving || loading}>
      {saving ? 'Saving…' : 'Save changes'}
    </button>
  );

  return (
    <div>
      <PageHeader title="Settings" description={org?.name ?? 'Organisation-level configuration'} action={saveButton} />
      <div className="iam-page" style={{ maxWidth: 680, display: 'flex', flexDirection: 'column', gap: 16 }}>
        {error && <div className="iam-alert iam-alert-danger">{error}</div>}
        {saveError && <div className="iam-alert iam-alert-danger">{saveError}</div>}
        {saved && <div className="iam-alert iam-alert-success">Saved.</div>}

        {loading ? (
          <div style={{ height: 140, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />
        ) : (
          <>
            <div className="iam-card iam-card-pad">
              <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 16 }}>General</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div>
                  <label className="iam-label" htmlFor="org-name">Name</label>
                  <input
                    id="org-name"
                    className="iam-input"
                    value={name}
                    disabled={!isSystemCtx}
                    onChange={e => setName(e.target.value)}
                  />
                  {!isSystemCtx && (
                    <p className="iam-help">Only a deployment administrator can rename an organisation.</p>
                  )}
                </div>
                <div>
                  <label className="iam-label" htmlFor="org-slug">Slug</label>
                  <input id="org-slug" className="iam-input iam-mono" value={org?.slug ?? ''} disabled readOnly />
                  <p className="iam-help">
                    Fixed after creation — it is part of every audit row and Keto object below this tenant.
                  </p>
                </div>
                <div>
                  <span className="iam-label">Status</span>
                  <div>
                    <IamChip tone={suspended ? 'danger' : 'success'}>{suspended ? 'Suspended' : 'Active'}</IamChip>
                  </div>
                </div>
              </div>
            </div>

            <div className="iam-card iam-card-pad">
              <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Mail</div>
              {mailError && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 14 }}>{mailError}</div>}
              <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>
                {smtp?.configured
                  ? <>This organisation sends through its own relay, <span className="iam-mono">{smtp.host}:{smtp.port}</span>.</>
                  : 'This organisation currently sends through the deployment relay. Set your own to send from your domain.'}
              </div>
              <Link className="iam-btn iam-btn-secondary iam-btn-sm" to={`${orgBase}/email`}>
                {smtp?.configured ? 'Edit the relay' : 'Configure own SMTP'}
              </Link>
            </div>

            <div className="iam-card iam-card-pad">
              <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Audit retention</div>
              <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>
                Rows older than this are removed by the nightly sweep. At least {MIN_RETENTION} days —
                anything shorter is refused, not clamped, because silently keeping less history than
                an administrator asked for is its own surprise.
              </div>
              <label className="iam-label" htmlFor="org-retention">Retention (days)</label>
              <input
                id="org-retention"
                className="iam-input"
                type="number"
                min={MIN_RETENTION}
                max={MAX_RETENTION}
                style={{ maxWidth: 150 }}
                disabled={useDefault}
                value={retention}
                onChange={e => setRetention(e.target.value)}
              />
              <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 12.5 }}>
                <input
                  type="checkbox"
                  checked={useDefault}
                  onChange={e => setUseDefault(e.target.checked)}
                />
                Follow the deployment default
              </label>
            </div>

            {isSystemCtx && (
              <div
                className="iam-card iam-card-pad"
                style={{ borderColor: 'color-mix(in oklch, var(--danger) 30%, transparent)' }}
              >
                <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--danger)', marginBottom: 14 }}>
                  Danger zone
                </div>
                {dangerError && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 14 }}>{dangerError}</div>}
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16, paddingBottom: 14, borderBottom: '1px solid var(--border)' }}>
                  <div>
                    <p style={{ fontSize: 13, fontWeight: 500, margin: 0 }}>{suspended ? 'Unsuspend' : 'Suspend'}</p>
                    <p style={{ fontSize: 11.5, color: 'var(--fg-muted)', margin: '2px 0 0', lineHeight: 1.5 }}>
                      {suspended
                        ? 'Lets this tenant sign in again. Sessions revoked by the suspension are not restored.'
                        : 'Revokes every session in this tenant immediately and refuses every sign-in until you unsuspend. Reversible.'}
                    </p>
                  </div>
                  <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => setSuspendOpen(true)}>
                    {suspended ? 'Unsuspend' : 'Suspend'}
                  </button>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16, paddingTop: 14 }}>
                  <div>
                    <p style={{ fontSize: 13, fontWeight: 500, margin: 0 }}>Delete</p>
                    <p style={{ fontSize: 11.5, color: 'var(--fg-muted)', margin: '2px 0 0', lineHeight: 1.5 }}>
                      Destroys every project, user list, account and grant below this tenant, and its
                      whole audit chain. Not reversible.
                    </p>
                  </div>
                  <button className="iam-btn iam-btn-danger iam-btn-sm" onClick={() => setDeleteOpen(true)}>Delete</button>
                </div>
              </div>
            )}
          </>
        )}
      </div>

      <IamDialog
        open={suspendOpen}
        onClose={() => setSuspendOpen(false)}
        title={suspended ? <>Unsuspend “{org?.name}”?</> : <>Suspend “{org?.name}”?</>}
        desc={suspended
          ? 'Sign-in is allowed again from now on. The sessions the suspension revoked stay revoked.'
          : 'Every session in this organisation is revoked immediately — its own administrators included — and every sign-in is refused until you unsuspend. Nothing is deleted.'}
        footer={
          <>
            <button type="button" className="iam-btn iam-btn-secondary" onClick={() => setSuspendOpen(false)}>Cancel</button>
            <button type="button" className="iam-btn iam-btn-danger" onClick={toggleSuspend} disabled={dangerBusy}>
              {suspended ? 'Unsuspend' : 'Suspend'}
            </button>
          </>
        }
      />

      <IamDialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        title={<>Delete “{org?.name}”?</>}
        desc={<>
          This destroys the organisation and everything under it: every project and its OAuth2
          client, every user list, every account in those lists, every admin grant in Keto, and this
          tenant’s whole audit chain. Sessions are revoked as it goes. It cannot be undone.
        </>}
        footer={
          <>
            <button type="button" className="iam-btn iam-btn-secondary" onClick={() => setDeleteOpen(false)}>Cancel</button>
            <button type="button" className="iam-btn iam-btn-danger" onClick={destroy} disabled={dangerBusy}>
              {dangerBusy ? 'Deleting…' : 'Delete for good'}
            </button>
          </>
        }
      />
    </div>
  );
}
