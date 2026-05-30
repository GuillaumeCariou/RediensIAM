import { useMemo } from 'react';

interface Bucket { k: string; s: number; f: number; }

interface ActivityChartProps {
  buckets?: number;
  height?: number;
}

export default function ActivityChart({ buckets = 48, height = 130 }: Readonly<ActivityChartProps>) {
  const data = useMemo<Bucket[]>(() => {
    return Array.from({ length: buckets }, (_, i) => ({
      k: `b${i}`,
      s: 60 + Math.sin(i / 4) * 30 + ((i * 17 + 3) % 20),
      f: Math.max(0, ((i * 13 + 7) % 10) + Math.sin(i / 6) * 3),
    }));
  }, [buckets]);

  const max = Math.max(...data.map((d) => d.s + d.f));
  const barMax = height - 10;

  return (
    <div style={{ height, display: 'flex', alignItems: 'flex-end', gap: 2, padding: '0 2px', overflow: 'hidden' }}>
      {data.map((d) => (
        <div
          key={d.k}
          style={{ flex: '1 1 0', minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', gap: 1 }}
        >
          {d.f > 0 && (
            <div
              style={{
                backgroundColor: 'var(--danger)',
                height: `${Math.max(2, (d.f / max) * barMax)}px`,
                borderRadius: '2px 2px 0 0',
              }}
            />
          )}
          <div
            style={{
              backgroundColor: 'var(--ia-accent)',
              height: `${Math.max(2, (d.s / max) * barMax)}px`,
              borderRadius: '2px 2px 0 0',
            }}
          />
        </div>
      ))}
    </div>
  );
}
