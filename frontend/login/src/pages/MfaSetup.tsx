import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { setupTotp, confirmTotp } from '../api';

type Step = 'loading' | 'setup' | 'backup';

export default function MfaSetup() {
  const navigate = useNavigate();

  const [step,       setStep]       = useState<Step>('loading');
  const [setupData,  setSetupData]  = useState<{ otpauth_url: string; secret: string } | null>(null);
  const [backupCodes,setBackupCodes]= useState<string[]>([]);
  const [redirectTo, setRedirectTo] = useState('');
  const [code,       setCode]       = useState('');
  const [error,      setError]      = useState('');
  const [loading,    setLoading]    = useState(false);
  const [copied,     setCopied]     = useState(false);

  useEffect(() => {
    if (!sessionStorage.getItem('mfa_setup_challenge')) {
      navigate('/login');
      return;
    }
    setupTotp()
      .then(res => {
        if (res.error || !res.secret) { navigate('/login'); return; }
        setSetupData({ otpauth_url: res.otpauth_url, secret: res.secret });
        setStep('setup');
      })
      .catch(() => navigate('/login'));
  }, [navigate]);

  async function handleConfirm(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await confirmTotp(code);
      if (res.error) {
        setError('Incorrect code. Check your authenticator app and try again.');
        return;
      }
      sessionStorage.removeItem('mfa_setup_challenge');
      sessionStorage.removeItem('mfa_setup_user');
      if (res.backup_codes?.length) {
        setBackupCodes(res.backup_codes);
        setRedirectTo(res.redirect_to ?? '');
        setStep('backup');
        return;
      }
      if (res.redirect_to) globalThis.location.href = res.redirect_to;
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  function copySecret() {
    navigator.clipboard.writeText(setupData?.secret ?? '');
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  function copyBackupCodes() {
    navigator.clipboard.writeText(backupCodes.join('\n'));
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  // ── Loading ──────────────────────────────────────────────────────────────────
  if (step === 'loading') return (
    <div className="card">
      <p className="card-subtitle">Setting up…</p>
    </div>
  );

  // ── Backup codes ─────────────────────────────────────────────────────────────
  if (step === 'backup') return (
    <div className="card">
      <h1 className="card-title">Save your backup codes</h1>
      <p className="card-subtitle">Each code can be used once if you lose access to your authenticator. Store them somewhere safe.</p>

      <div style={{ background: 'var(--background)', borderRadius: 'var(--radius)', padding: '1rem', marginBottom: '1rem', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.5rem' }}>
        {backupCodes.map(c => (
          <code key={c} style={{ fontFamily: 'monospace', fontSize: '0.8rem', padding: '0.3rem 0.5rem', background: 'var(--surface)', borderRadius: '4px', textAlign: 'center', border: '1px solid var(--border)' }}>
            {c}
          </code>
        ))}
      </div>

      <button type="button" onClick={copyBackupCodes}
        style={{ width: '100%', padding: '0.5rem', border: '1px solid var(--border)', borderRadius: 'var(--radius)', background: 'var(--surface)', color: 'var(--text)', fontSize: '0.875rem', cursor: 'pointer', marginBottom: '1rem' }}>
        {copied ? '✓ Copied' : 'Copy all codes'}
      </button>

      <div className="alert alert-error" style={{ fontSize: '0.8rem', marginBottom: '1rem' }}>
        ⚠ You will not see these again. Copy them before continuing.
      </div>

      <button className="btn" type="button" onClick={() => {
        if (redirectTo) globalThis.location.href = redirectTo;
        else navigate('/login');
      }}>
        I've saved my codes — continue
      </button>
    </div>
  );

  // ── TOTP setup ───────────────────────────────────────────────────────────────
  return (
    <div className="card">
      <h1 className="card-title">Set up two-factor auth</h1>
      <p className="card-subtitle">Your organization requires MFA. Add an authenticator app to continue.</p>

      {error && <div className="alert alert-error">{error}</div>}

      <div style={{ background: 'var(--background)', borderRadius: 'var(--radius)', padding: '1rem', marginBottom: '1.25rem' }}>
        <p style={{ fontSize: '0.875rem', fontWeight: 500, marginBottom: '0.75rem' }}>
          1. Open your authenticator app and add a new account.
        </p>

        <div style={{ marginBottom: '0.625rem' }}>
          <span style={{ display: 'block', fontSize: '0.75rem', color: 'var(--text-muted)', marginBottom: '0.3rem' }}>
            Secret key
          </span>
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
            <code style={{ fontFamily: 'monospace', fontSize: '0.8rem', background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius)', padding: '0.375rem 0.625rem', flex: 1, wordBreak: 'break-all' }}>
              {setupData?.secret}
            </code>
            <button type="button" onClick={copySecret}
              style={{ padding: '0.375rem 0.75rem', border: '1px solid var(--border)', borderRadius: 'var(--radius)', background: 'var(--surface)', color: 'var(--text)', fontSize: '0.8rem', cursor: 'pointer', whiteSpace: 'nowrap' }}>
              {copied ? '✓ Copied' : 'Copy'}
            </button>
          </div>
        </div>

        <a href={setupData?.otpauth_url} style={{ fontSize: '0.8rem', color: 'var(--primary)' }}>
          Or open in authenticator app →
        </a>
      </div>

      <form onSubmit={handleConfirm}>
        <p style={{ fontSize: '0.875rem', fontWeight: 500, marginBottom: '0.75rem' }}>
          2. Enter the 6-digit code from your app.
        </p>
        <div className="form-group">
          <input
            className="otp-input"
            type="text"
            inputMode="numeric"
            pattern="\d{6}"
            maxLength={6}
            autoFocus
            required
            placeholder="000000"
            value={code}
            onChange={e => setCode(e.target.value.replaceAll(/\D/g, ''))}
          />
        </div>
        <button className="btn" type="submit" disabled={loading || code.length !== 6}>
          {loading ? 'Verifying…' : 'Confirm and continue'}
        </button>
      </form>
    </div>
  );
}
