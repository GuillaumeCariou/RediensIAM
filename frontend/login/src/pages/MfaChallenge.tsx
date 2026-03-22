import { useState } from 'react';
import { verifyTotp, verifyBackupCode, verifySmsOtp, sendSmsOtp } from '../api';

type MfaMode = 'totp' | 'backup' | 'sms';

export default function MfaChallenge() {
  const initialMfaType = (sessionStorage.getItem('mfa_type') ?? 'totp') as MfaMode;
  const phoneHint = sessionStorage.getItem('mfa_phone_hint') ?? '';

  const [mode, setMode]       = useState<MfaMode>(initialMfaType);
  const [code, setCode]       = useState('');
  const [error, setError]     = useState('');
  const [loading, setLoading] = useState(false);
  const [resent, setResent]   = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      let res;
      if (mode === 'totp')   res = await verifyTotp(code);
      else if (mode === 'sms') res = await verifySmsOtp(code);
      else                   res = await verifyBackupCode(code);

      if (res.error) {
        setError(mode === 'backup'
          ? 'Invalid backup code. Check the code and try again.'
          : 'Invalid or expired code. Try again.');
        return;
      }
      if (res.redirect_to) {
        sessionStorage.removeItem('mfa_type');
        sessionStorage.removeItem('mfa_phone_hint');
        window.location.href = res.redirect_to;
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handleResend() {
    setResent(false);
    await sendSmsOtp();
    setResent(true);
  }

  function switchMode(next: MfaMode) {
    setMode(next);
    setCode('');
    setError('');
    setResent(false);
  }

  return (
    <div className="card">
      {mode === 'totp' && (
        <>
          <h1 className="card-title">Two-factor auth</h1>
          <p className="card-subtitle">Enter the 6-digit code from your authenticator app</p>
        </>
      )}
      {mode === 'sms' && (
        <>
          <h1 className="card-title">SMS verification</h1>
          <p className="card-subtitle">
            Code sent to {phoneHint || 'your phone'}
          </p>
        </>
      )}
      {mode === 'backup' && (
        <>
          <h1 className="card-title">Use a backup code</h1>
          <p className="card-subtitle">Enter one of your 8-character backup codes</p>
        </>
      )}

      {error && <div className="alert alert-error">{error}</div>}
      {resent && <div className="alert alert-success">Code resent!</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          {mode === 'backup' ? (
            <input
              className="input" type="text" autoFocus required
              placeholder="XXXXXXXX" maxLength={8} autoComplete="off"
              value={code} onChange={e => setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, ''))}
            />
          ) : (
            <input
              className="otp-input" type="text" inputMode="numeric"
              pattern="\d{6}" maxLength={6} autoFocus required
              placeholder="000000"
              value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
            />
          )}
        </div>
        <button
          className="btn" type="submit"
          disabled={loading || (mode === 'backup' ? code.length !== 8 : code.length !== 6)}
        >
          {loading ? 'Verifying…' : 'Verify'}
        </button>
      </form>

      <div className="mt-4 text-center" style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        {mode === 'sms' && (
          <button className="link" type="button" onClick={handleResend}>Resend code</button>
        )}
        {mode !== 'backup' && (
          <button className="link" type="button" onClick={() => switchMode('backup')}>
            Use a backup code instead
          </button>
        )}
        {mode === 'backup' && initialMfaType !== 'backup' && (
          <button className="link" type="button" onClick={() => switchMode(initialMfaType)}>
            Back to {initialMfaType === 'sms' ? 'SMS code' : 'authenticator app'}
          </button>
        )}
      </div>
    </div>
  );
}
