type DotTone = 'success' | 'warn' | 'danger' | 'muted';

interface IamDotProps {
  tone?: DotTone;
}

const CLS: Record<DotTone, string> = {
  success: 'iam-dot iam-dot-success',
  warn: 'iam-dot iam-dot-warn',
  danger: 'iam-dot iam-dot-danger',
  muted: 'iam-dot iam-dot-muted',
};

export default function IamDot({ tone = 'muted' }: Readonly<IamDotProps>) {
  return <span className={CLS[tone]} />;
}
