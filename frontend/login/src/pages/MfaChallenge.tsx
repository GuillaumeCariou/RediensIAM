import { useState, useEffect, useRef } from 'react';
import { verifyTotp, verifyBackupCode, verifySmsOtp, sendSmsOtp, getWebAuthnOptions, verifyWebAuthn } from '../api';
import { safeNavigate } from '../safeNavigate';

type MfaMode = 'totp' | 'backup' | 'sms' | 'webauthn';

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
}

const METHODS: { id: MfaMode; name: string; desc: string; icon: React.ReactNode }[] = [
  {
    id: 'totp',
    name: 'Authenticator app',
    desc: 'Code from your authenticator',
    icon: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="M6 8h.01M10 8h.01M14 8h.01M18 8h.01M6 12h.01M10 12h.01M14 12h.01M18 12h.01M6 16h.01M10 16h.01M14 16h.01M18 16h.01"/></svg>,
  },
  {
    id: 'webauthn',
    name: 'Security key',
    desc: 'Touch your passkey or YubiKey',
    icon: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M12 11c0 3.517-1.009 6.799-2.753 9.571m-3.44-2.04.054-.09A13.916 13.916 0 0 0 8 11a4 4 0 1 1 8 0c0 1.017-.07 2.019-.203 3m-2.118 6.844A21.88 21.88 0 0 0 15.171 17m3.839 1.132c.645-2.266.99-4.659.99-7.132A8 8 0 0 0 8 4.07M3 15.364c.64-1.319 1-2.8 1-4.364 0-1.457.39-2.823 1.07-4"/></svg>,
  },
  {
    id: 'sms',
    name: 'Text message',
    desc: 'SMS to your registered phone',
    icon: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07A19.5 19.5 0 0 1 4.69 12a19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 3.56 1.35h3a2 2 0 0 1 2 1.72c.127.96.361 1.903.7 2.81a2 2 0 0 1-.45 2.11L7.91 8.9a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.907.339 1.85.573 2.81.7A2 2 0 0 1 22 16.92z"/></svg>,
  },
  {
    id: 'backup',
    name: 'Backup code',
    desc: 'One of your recovery codes',
    icon: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>,
  },
];

/** Six OTP inputs, fixed order — stable keys so React never keys on the array index. */
const OTP_CELL_IDS = ['otp-1', 'otp-2', 'otp-3', 'otp-4', 'otp-5', 'otp-6'];

function methodInstruction(mode: MfaMode, phoneHint: string): string {
  switch (mode) {
    case 'totp':     return 'Enter the 6-digit code from your authenticator app.';
    case 'sms':      return `Code sent to ${phoneHint || 'your phone'}.`;
    case 'webauthn': return 'Touch your passkey or security key to continue.';
    default:         return 'Enter one of your 8-character backup codes.';
  }
}

export default function MfaChallenge() {
  const initialMfaType = (sessionStorage.getItem('mfa_type') ?? 'totp') as MfaMode;
  const phoneHint = sessionStorage.getItem('mfa_phone_hint') ?? '';

  const [mode, setMode]       = useState<MfaMode>(initialMfaType);
  const [cells, setCells]     = useState<string[]>(new Array(6).fill(''));
  const [backupCode, setBackupCode] = useState('');
  const [error, setError]     = useState('');
  const [loading, setLoading] = useState(false);
  const [resent, setResent]   = useState(false);
  const cellRefs = useRef<(HTMLInputElement | null)[]>([]);

  const otp = cells.join('');

  const webauthnFired = useRef(false);
  useEffect(() => {
    // StrictMode runs effects twice in dev — guard so we don't prompt the user twice
    // for their authenticator. Reset when leaving webauthn mode.
    if (mode === 'webauthn' && !webauthnFired.current) {
      webauthnFired.current = true;
      handleWebAuthn();
    }
    if (mode !== 'webauthn') webauthnFired.current = false;
  }, [mode]);

  async function handleWebAuthn() {
    setLoading(true);
    setError('');
    try {
      const options = await getWebAuthnOptions();
      if (options.error) { setError('Failed to get passkey options.'); return; }

      // Whitelist server-supplied fields and force userVerification=required so a
      // hostile/mis-configured backend cannot downgrade the assertion strength.
      const safeOptions = {
        challenge: base64urlToBuffer(options.challenge),
        timeout: typeof options.timeout === 'number' ? options.timeout : 60000,
        rpId: typeof options.rpId === 'string' ? options.rpId : undefined,
        userVerification: 'required' as const,
        allowCredentials: Array.isArray(options.allowCredentials)
          ? options.allowCredentials.map((c: { id: string; type?: string; transports?: string[] }) => ({
              id: base64urlToBuffer(c.id),
              type: 'public-key' as const,
              transports: c.transports,
            }))
          : undefined,
      };

      const assertion = await navigator.credentials.get({ publicKey: safeOptions }) as PublicKeyCredential;
      if (!assertion) { setError('No credential returned.'); return; }

      const response = assertion.response as AuthenticatorAssertionResponse;
      const body = {
        id:    assertion.id,
        rawId: bufferToBase64url(assertion.rawId),
        type:  assertion.type,
        response: {
          authenticatorData: bufferToBase64url(response.authenticatorData),
          clientDataJSON:    bufferToBase64url(response.clientDataJSON),
          signature:         bufferToBase64url(response.signature),
          userHandle:        response.userHandle ? bufferToBase64url(response.userHandle) : null,
        }
      };

      const res = await verifyWebAuthn(body);
      if (res.error) { setError('Passkey verification failed. Try again.'); return; }
      if (res.redirect_to) {
        sessionStorage.removeItem('mfa_type');
        sessionStorage.removeItem('mfa_phone_hint');
        if (!safeNavigate(res.redirect_to)) { setError('Sign-in could not complete. Please try again.'); }
      }
    } catch (e: unknown) {
      if (e instanceof Error && e.name === 'NotAllowedError') {
        setError('Passkey prompt was cancelled or timed out.');
      } else {
        setError('Something went wrong. Try a different method.');
      }
    } finally {
      setLoading(false);
    }
  }

  async function submitVerify(code: string) {
    setLoading(true);
    setError('');
    try {
      let res;
      if (mode === 'totp')        res = await verifyTotp(code);
      else if (mode === 'sms')    res = await verifySmsOtp(code);
      else                        res = await verifyBackupCode(code);

      if (res.error) {
        setError(mode === 'backup'
          ? 'Invalid backup code. Check the code and try again.'
          : 'Invalid or expired code. Try again.');
        setCells(new Array(6).fill(''));
        cellRefs.current[0]?.focus();
        return;
      }
      if (res.redirect_to) {
        sessionStorage.removeItem('mfa_type');
        sessionStorage.removeItem('mfa_phone_hint');
        if (!safeNavigate(res.redirect_to)) { setError('Sign-in could not complete. Please try again.'); }
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    await submitVerify(mode === 'backup' ? backupCode : otp);
  }

  async function handleResend() {
    setResent(false);
    await sendSmsOtp();
    setResent(true);
  }

  function switchMode(next: MfaMode) {
    setMode(next);
    setCells(new Array(6).fill(''));
    setBackupCode('');
    setError('');
    setResent(false);
  }

  function handleCellChange(i: number, v: string) {
    if (!/^\d?$/.test(v)) return;
    const next = [...cells]; next[i] = v; setCells(next);
    if (v && i < 5) cellRefs.current[i + 1]?.focus();
    if (v && i === 5 && next.every(c => c !== '')) {
      submitVerify(next.join(''));
    }
  }

  function handleCellPaste(e: React.ClipboardEvent) {
    const digits = e.clipboardData.getData('text').replaceAll(/\D/g, '').slice(0, 6);
    if (digits.length === 0) return;
    e.preventDefault();
    const next = ['', '', '', '', '', ''];
    for (let i = 0; i < digits.length; i++) next[i] = digits[i];
    setCells(next);
    if (digits.length === 6) submitVerify(digits);
    else cellRefs.current[digits.length]?.focus();
  }

  function handleCellKeyDown(e: React.KeyboardEvent, i: number) {
    if (e.key === 'Backspace' && !cells[i] && i > 0) {
      cellRefs.current[i - 1]?.focus();
    }
  }

  const methodDesc = methodInstruction(mode, phoneHint);

  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Verify it's you.</h1>
        <p className="login-subtitle">Two-factor authentication required.</p>

        <div className="mfa-methods">
          {METHODS.map(m => (
            <button key={m.id} className={`mfa-method${mode === m.id ? ' selected' : ''}`}
              onClick={() => switchMode(m.id)}>
              <div className="mfa-method-icon">{m.icon}</div>
              <div style={{ flex: 1 }}>
                <div className="mfa-method-name">{m.name}</div>
                <div className="mfa-method-desc">{m.id === 'sms' && phoneHint ? `SMS to ${phoneHint}` : m.desc}</div>
              </div>
              {mode === m.id && (
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" strokeWidth="2.5" style={{ flexShrink: 0 }}>
                  <polyline points="20,6 9,17 4,12"/>
                </svg>
              )}
            </button>
          ))}
        </div>

        {error && (
          <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0, marginTop: 1 }}>
              <circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/>
            </svg>
            {error}
          </div>
        )}
        {resent && <div className="alert alert-success" style={{ marginTop: 12 }}>Code resent!</div>}

        {(() => {
          if (mode === 'webauthn') return (
          <div style={{ marginTop: 24, padding: 32, background: 'var(--surface-2)', borderRadius: 12, textAlign: 'center' }}>
            <div style={{ display: 'inline-grid', placeItems: 'center', width: 56, height: 56, borderRadius: 16, background: 'var(--accent-soft)', color: 'var(--accent)', marginBottom: 14 }}>
              <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 11c0 3.517-1.009 6.799-2.753 9.571m-3.44-2.04.054-.09A13.916 13.916 0 0 0 8 11a4 4 0 1 1 8 0c0 1.017-.07 2.019-.203 3m-2.118 6.844A21.88 21.88 0 0 0 15.171 17m3.839 1.132c.645-2.266.99-4.659.99-7.132A8 8 0 0 0 8 4.07M3 15.364c.64-1.319 1-2.8 1-4.364 0-1.457.39-2.823 1.07-4"/>
              </svg>
            </div>
            <div style={{ fontWeight: 500, color: 'var(--fg)' }}>
              {loading ? 'Waiting for your security key…' : 'Ready for passkey'}
            </div>
            <div style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 4 }}>Touch your passkey or YubiKey to continue.</div>
            {!loading && (
              <button className="btn btn-primary" style={{ marginTop: 16 }} onClick={handleWebAuthn}>
                Use passkey
              </button>
            )}
          </div>
          );
          if (mode === 'backup') return (
          <form onSubmit={handleSubmit} style={{ marginTop: 20 }}>
            <label className="label">{methodDesc}</label>
            <input
              className="input mono"
              type="text" autoFocus required
              placeholder="XXXXXXXXXXXXXXXX" maxLength={20} autoComplete="off"
              value={backupCode}
              onChange={e => setBackupCode(e.target.value.toUpperCase().replaceAll(/[^A-Z0-9]/g, '').slice(0, 16))}
              style={{ fontSize: 15, letterSpacing: '0.05em', textAlign: 'center' }}
            />
            <button className="btn btn-primary btn-lg" type="submit" disabled={loading || backupCode.length !== 16} style={{ marginTop: 12 }}>
              {loading ? 'Verifying…' : 'Verify'}
            </button>
          </form>
          );
          return (
          <form onSubmit={handleSubmit}>
            {mode === 'sms' && (
              <div className="deny-banner" style={{ marginTop: 16, marginBottom: 0 }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>
                Code sent to {phoneHint || 'your phone'}. Expires in 5 minutes.
              </div>
            )}
            <div>
              <div className="label" style={{ textAlign: 'center', marginTop: 16 }}>Enter 6-digit code</div>
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
            </div>
            {mode === 'sms' && (
              <div style={{ marginTop: 10, textAlign: 'center' }}>
                <button className="link" type="button" onClick={handleResend} style={{ fontSize: 12 }}>Resend code</button>
              </div>
            )}
          </form>
          );
        })()}

        <button className="btn btn-ghost btn-sm" style={{ marginTop: 20 }} onClick={() => globalThis.history.back()}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="15,18 9,12 15,6"/></svg>
          Back
        </button>
      </div>
    </div>
  );
}

function base64urlToBuffer(b64: string): ArrayBuffer {
  const bin = atob(b64.replaceAll('-', '+').replaceAll('_', '/'));
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.codePointAt(i)!;
  return buf.buffer;
}

function bufferToBase64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let str = '';
  for (const b of bytes) str += String.fromCodePoint(b);
  return btoa(str).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}
