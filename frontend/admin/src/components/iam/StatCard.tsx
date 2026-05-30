import type { ReactNode } from 'react';

type SubTone = 'muted' | 'up' | 'down';

interface StatCardProps {
  label: string;
  value: ReactNode;
  sub?: ReactNode;
  subTone?: SubTone;
  spark?: ReactNode;
}

export default function StatCard({ label, value, sub, subTone = 'muted', spark }: Readonly<StatCardProps>) {
  let subCls = 'iam-stat-sub';
  if (subTone === 'up') subCls = 'iam-stat-sub iam-stat-trend-up';
  else if (subTone === 'down') subCls = 'iam-stat-sub iam-stat-trend-down';

  return (
    <div className="iam-stat">
      <div className="iam-stat-label">{label}</div>
      <div className="iam-stat-value">{value}</div>
      {sub && <div className={subCls}>{sub}</div>}
      {spark && <div className="iam-stat-spark">{spark}</div>}
    </div>
  );
}
