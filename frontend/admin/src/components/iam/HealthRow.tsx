type HealthStatus = 'ok' | 'warn' | 'error';

interface HealthRowProps {
  name: string;
  status: HealthStatus;
  detail: string;
  tuple: string;
}

const DOT_CLASS: Record<HealthStatus, string> = {
  ok: 'iam-dot iam-dot-success',
  warn: 'iam-dot iam-dot-warn',
  error: 'iam-dot iam-dot-danger',
};

export default function HealthRow({ name, status, detail, tuple }: Readonly<HealthRowProps>) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '6px 0' }}>
      <span className={DOT_CLASS[status]} />
      <div style={{ flex: 1 }}>
        <div style={{ fontSize: 13, fontWeight: 500 }}>{name}</div>
        <div className="iam-mono" style={{ fontSize: 10.5, color: 'var(--fg-subtle)' }}>{tuple}</div>
      </div>
      <div className="iam-mono" style={{ fontSize: 11.5, color: 'var(--fg-muted)' }}>{detail}</div>
    </div>
  );
}
