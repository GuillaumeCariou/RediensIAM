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
      // Lightness comes from the theme, hue from the name: a fixed pair renders as a bright
      // pastel disc on a dark table, which is the one thing an avatar should never be.
      style={{
        background: `oklch(var(--iam-avatar-bg-l) var(--iam-avatar-bg-c) ${hue})`,
        color: `oklch(var(--iam-avatar-fg-l) var(--iam-avatar-fg-c) ${hue})`,
      }}
    >
      {initials(name)}
    </div>
  );
}
