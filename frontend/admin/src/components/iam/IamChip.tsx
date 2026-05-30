import type { ReactNode } from 'react';

type ChipTone = 'default' | 'success' | 'warn' | 'danger' | 'accent';

interface IamChipProps {
  children: ReactNode;
  tone?: ChipTone;
  mono?: boolean;
  icon?: ReactNode;
  className?: string;
}

const TONE_CLASS: Record<ChipTone, string> = {
  default: 'iam-chip',
  success: 'iam-chip iam-chip-success',
  warn: 'iam-chip iam-chip-warn',
  danger: 'iam-chip iam-chip-danger',
  accent: 'iam-chip iam-chip-accent',
};

export default function IamChip({ children, tone = 'default', mono = false, icon, className = '' }: Readonly<IamChipProps>) {
  const cls = `${TONE_CLASS[tone]}${mono ? ' iam-chip-mono' : ''} ${className}`.trim();
  return (
    <span className={cls}>
      {icon}
      {children}
    </span>
  );
}
