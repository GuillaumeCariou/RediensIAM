interface SparkProps {
  points?: number[];
  color?: string;
  width?: number;
  height?: number;
}

const DEFAULT_POINTS = [3, 6, 4, 8, 7, 12, 10, 14, 11, 16];

export default function Spark({
  points = DEFAULT_POINTS,
  color = 'currentColor',
  width = 80,
  height = 26,
}: Readonly<SparkProps>) {
  const max = Math.max(...points);
  const min = Math.min(...points);
  const pts = points
    .map((p, i) => {
      const x = (i / (points.length - 1)) * width;
      const y = height - ((p - min) / (max - min || 1)) * height;
      return `${x},${y}`;
    })
    .join(' ');

  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
      <polyline
        points={pts}
        fill="none"
        stroke={color}
        strokeWidth="1.5"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
