interface IamAvatarProps {
  name: string;
  size?: 'sm' | 'default' | 'lg';
  className?: string;
}

const HUES = [265, 155, 70, 195, 310, 25, 340, 100];

function hueForName(name: string): number {
  const seed = name.split('').reduce((a, c) => a + (c.codePointAt(0) ?? 0), 0);
  return HUES[seed % HUES.length];
}

function initials(name: string): string {
  return (name || '?')
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0])
    .join('')
    .toUpperCase();
}

export default function IamAvatar({ name, size = 'default', className = '' }: Readonly<IamAvatarProps>) {
  const hue = hueForName(name);
  let cls = 'iam-avatar';
  if (size === 'lg') cls = 'iam-avatar iam-avatar-lg';
  else if (size === 'sm') cls = 'iam-avatar iam-avatar-sm';
  return (
    <div
      className={`${cls} ${className}`}
      style={{
        background: `oklch(0.9 0.06 ${hue})`,
        color: `oklch(0.35 0.14 ${hue})`,
      }}
    >
      {initials(name)}
    </div>
  );
}
