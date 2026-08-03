interface ActivityChartProps {
  /**
   * One entry per hour, oldest first. Supplied by the caller — this component used to generate its
   * own bars from a sine wave under a heading reading "Login activity · last 24h", with a
   * Success/Failed legend and a real login count beside it: the same invented shape on every
   * deployment and every reload.
   */
  data?: { hour: string; succeeded: number; failed: number }[];
  height?: number;
}

export default function ActivityChart({ data, height = 130 }: Readonly<ActivityChartProps>) {
  if (!data || data.length === 0) {
    return (
      <div
        style={{
          height,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          color: 'var(--fg-subtle)',
          fontSize: 12,
        }}
      >
        No sign-ins recorded in this window
      </div>
    );
  }

  const max = Math.max(1, ...data.map(d => d.succeeded + d.failed));
  const barMax = height - 10;

  return (
    <div style={{ height, display: 'flex', alignItems: 'flex-end', gap: 2, padding: '0 2px', overflow: 'hidden' }}>
      {data.map(d => (
        <div
          key={d.hour}
          title={`${d.hour}: ${d.succeeded} succeeded, ${d.failed} failed`}
          style={{ flex: '1 1 0', minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', gap: 1 }}
        >
          {d.failed > 0 && (
            <div
              style={{
                backgroundColor: 'var(--danger)',
                height: `${Math.max(2, (d.failed / max) * barMax)}px`,
                borderRadius: '2px 2px 0 0',
              }}
            />
          )}
          {d.succeeded > 0 && (
            <div
              style={{
                backgroundColor: 'var(--ia-accent)',
                height: `${Math.max(2, (d.succeeded / max) * barMax)}px`,
                borderRadius: '2px 2px 0 0',
              }}
            />
          )}
        </div>
      ))}
    </div>
  );
}
