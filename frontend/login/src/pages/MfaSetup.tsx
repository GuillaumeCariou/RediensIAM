import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { setupTotp, confirmTotp } from '../api';
import { safeNavigate } from '../safeNavigate';

type Step = 'loading' | 'qr' | 'verify' | 'backup';

const STEP_NUMBERS: Record<Step, number> = { loading: 0, qr: 1, verify: 2, backup: 3 };

/** Six OTP inputs, fixed order — stable keys so React never keys on the array index. */
const OTP_CELL_IDS = ['otp-1', 'otp-2', 'otp-3', 'otp-4', 'otp-5', 'otp-6'];

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
}

interface StepperProps { step: number }
function Stepper({ step }: Readonly<StepperProps>) {
  return (
    <div className="setup-stepper">
      {[1, 2, 3].map((n, i) => (
        <>
          <div key={n} className={`setup-step ${step >= n ? 'on' : 'off'}`}>{n}</div>
          {i < 2 && <div key={`line-${n}`} className={`setup-step-line ${step > n ? 'on' : 'off'}`} />}
        </>
      ))}
    </div>
  );
}

export default function MfaSetup() {
  const navigate = useNavigate();

  const [step,        setStep]        = useState<Step>('loading');
  const [setupData,   setSetupData]   = useState<{ otpauth_url: string; secret: string } | null>(null);
  const [backupCodes, setBackupCodes] = useState<string[]>([]);
  const [redirectTo,  setRedirectTo]  = useState('');
  const [cells,       setCells]       = useState<string[]>(new Array(6).fill(''));
  const [error,       setError]       = useState('');
  const [loading,     setLoading]     = useState(false);
  const [copied,      setCopied]      = useState(false);
  const cellRefs = useRef<(HTMLInputElement | null)[]>([]);

  const code = cells.join('');

  useEffect(() => {
    if (!sessionStorage.getItem('mfa_setup_challenge')) {
      navigate('/login');
      return;
    }
    setupTotp()
      .then(res => {
        if (res.error || !res.secret) { navigate('/login'); return; }
        setSetupData({ otpauth_url: res.otpauth_url, secret: res.secret });
        setStep('qr');
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
        setCells(new Array(6).fill(''));
        cellRefs.current[0]?.focus();
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
      if (res.redirect_to && !safeNavigate(res.redirect_to)) {
        setError('Sign-in could not complete. Please try again.');
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  function handleCellChange(i: number, v: string) {
    if (!/^\d?$/.test(v)) return;
    const next = [...cells]; next[i] = v; setCells(next);
    if (v && i < 5) cellRefs.current[i + 1]?.focus();
  }

  function handleCellPaste(e: React.ClipboardEvent) {
    const digits = e.clipboardData.getData('text').replaceAll(/\D/g, '').slice(0, 6);
    if (digits.length === 0) return;
    e.preventDefault();
    const next = ['', '', '', '', '', ''];
    for (let i = 0; i < digits.length; i++) next[i] = digits[i];
    setCells(next);
    cellRefs.current[Math.min(digits.length, 5)]?.focus();
  }

  function handleCellKeyDown(e: React.KeyboardEvent, i: number) {
    if (e.key === 'Backspace' && !cells[i] && i > 0) {
      cellRefs.current[i - 1]?.focus();
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

  const stepNum = STEP_NUMBERS[step];

  if (step === 'loading') return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <p className="login-subtitle">Setting up two-factor authentication…</p>
      </div>
    </div>
  );

  if (step === 'backup') return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Save your backup codes.</h1>
        <p className="login-subtitle">Each code can be used once if you lose your authenticator. Store them safely.</p>
        <Stepper step={stepNum} />

        <div className="deny-banner" style={{ background: 'var(--success-soft)', color: 'var(--success)', borderColor: 'oklch(from var(--success) l c h / 0.4)', marginTop: 20 }}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" style={{ flexShrink: 0 }}>
            <polyline points="20,6 9,17 4,12"/>
          </svg>
          Two-factor authentication enabled.
        </div>

        <div className="label" style={{ marginTop: 16 }}>Save these backup codes</div>
        <div className="backup-codes">
          {backupCodes.map(c => (
            <div key={c} className="backup-code">{c}</div>
          ))}
        </div>

        <div className="deny-banner deny-banner-error" style={{ marginTop: 10 }}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}>
            <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
          </svg>
          You will not see these again. Copy them before continuing.
        </div>

        <button type="button" className="btn btn-secondary" style={{ width: '100%', justifyContent: 'center', marginTop: 12 }} onClick={copyBackupCodes}>
          {copied ? '✓ Copied' : 'Copy all codes'}
        </button>

        <button className="btn btn-primary btn-lg" style={{ marginTop: 8 }} type="button"
          onClick={() => { if (!redirectTo || !safeNavigate(redirectTo)) navigate('/login'); }}>
          I've saved my codes — continue
        </button>
      </div>
    </div>
  );

  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Set up two-factor.</h1>
        <p className="login-subtitle">Required to access this project. Add your authenticator app.</p>
        <Stepper step={stepNum} />

        {step === 'qr' && (
          <div style={{ marginTop: 20 }}>
            <div className="label">Scan with your authenticator app or enter the key manually</div>
            <div style={{ padding: 16, background: 'var(--surface-2)', border: '1px solid var(--border)', borderRadius: 'var(--radius)', marginBottom: 8 }}>
              <div style={{ marginBottom: 8, fontSize: 12, color: 'var(--fg-muted)' }}>Secret key</div>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <code style={{ fontFamily: 'var(--font-mono)', fontSize: 13, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)', padding: '6px 10px', flex: 1, wordBreak: 'break-all', color: 'var(--fg)' }}>
                  {setupData?.secret}
                </code>
                <button type="button" onClick={copySecret} className="btn btn-secondary btn-sm" style={{ flexShrink: 0 }}>
                  {copied ? '✓' : 'Copy'}
                </button>
              </div>
            </div>
            {setupData?.otpauth_url && (
              <a href={setupData.otpauth_url} style={{ fontSize: 12, color: 'var(--accent)', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                Open in authenticator app
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg>
              </a>
            )}
            <button className="btn btn-primary btn-lg" style={{ marginTop: 20 }} onClick={() => setStep('verify')}>
              Continue
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg>
            </button>
          </div>
        )}

        {step === 'verify' && (
          <form style={{ marginTop: 20 }} onSubmit={handleConfirm}>
            {error && (
              <div className="deny-banner deny-banner-error" style={{ marginBottom: 14 }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}>
                  <circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/>
                </svg>
                {error}
              </div>
            )}
            <div className="label" style={{ textAlign: 'center' }}>Enter the 6-digit code from your app</div>
            <div className="otp-grid">
              {OTP_CELL_IDS.map((cellId, i) => (
                <input
                  key={cellId}
                  ref={el => { cellRefs.current[i] = el; }}
                  className="otp-cell"
                  type="text" inputMode="numeric"
                  maxLength={1} value={cells[i]}
                  autoFocus={i === 0}
                  aria-label={`Digit ${i + 1} of 6`}
                  onChange={e => handleCellChange(i, e.target.value)}
                  onKeyDown={e => handleCellKeyDown(e, i)}
                  onPaste={handleCellPaste}
                />
              ))}
            </div>
            <button className="btn btn-primary btn-lg" type="submit" disabled={loading || code.length !== 6} style={{ marginTop: 20 }}>
              {loading ? 'Verifying…' : <>Verify <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"><polyline points="20,6 9,17 4,12"/></svg></>}
            </button>
            <button type="button" className="btn btn-ghost btn-sm" style={{ marginTop: 10 }} onClick={() => setStep('qr')}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="15,18 9,12 15,6"/></svg>
              Back
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
