import { useId, useState } from 'react';

interface Bucket { hour: string; succeeded: number; failed: number }

interface ActivityChartProps {
  /**
   * One entry per hour, oldest first. Supplied by the caller — this component used to generate its
   * own bars from a sine wave under a heading reading "Login activity · last 24h", with a
   * Success/Failed legend and a real login count beside it: the same invented shape on every
   * deployment and every reload.
   */
  data?: Bucket[];
  height?: number;
}

/** L'heure locale du seau, à partir de l'horodatage UTC que le serveur aligne sur l'heure pleine. */
function hourLabel(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : `${String(d.getHours()).padStart(2, '0')}:00`;
}

/**
 * Vingt-quatre seaux d'une heure, succès et échecs empilés.
 *
 * <p>Le survol n'était qu'un attribut `title` : il rendait l'horodatage ISO brut, après la seconde
 * de latence que le navigateur impose, et n'existait pas au clavier. L'axe et l'échelle manquaient
 * tout autant — on voyait des barres sans savoir ni quand, ni combien.</p>
 *
 * <p>Les couleurs restent celles des jetons de la console : l'accent pour ce qui a réussi, le rouge
 * de danger pour ce qui a échoué. L'identité ne repose pas sur elles seules — la légende les nomme
 * au-dessus, et l'infobulle les nomme encore, ce qui est ce qui compte pour qui ne les distingue
 * pas.</p>
 */
export default function ActivityChart({ data, height = 130 }: Readonly<ActivityChartProps>) {
  const [hovered, setHovered] = useState<number | null>(null);
  const tipId = useId();

  if (!data || data.length === 0) {
    return (
      <div style={{ height, display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--fg-subtle)', fontSize: 12 }}>
        No sign-ins recorded in this window
      </div>
    );
  }

  const max = Math.max(1, ...data.map(d => d.succeeded + d.failed));
  const barMax = height - 10;
  const active = hovered != null ? data[hovered] : null;

  return (
    <div style={{ position: 'relative' }}>
      {/* L'échelle. Sans elle, deux graphiques côte à côte se lisent comme comparables alors que
          leurs maxima diffèrent. */}
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10.5, color: 'var(--fg-subtle)', marginBottom: 4 }}>
        <span>{max} max / h</span>
        <span>{data.reduce((n, d) => n + d.succeeded + d.failed, 0)} total</span>
      </div>

      <div
        style={{ height, display: 'flex', alignItems: 'flex-end', gap: 2, padding: '0 2px' }}
        onMouseLeave={() => setHovered(null)}
      >
        {data.map((d, i) => {
          const total = d.succeeded + d.failed;
          return (
            <button
              key={d.hour}
              type="button"
              aria-describedby={hovered === i ? tipId : undefined}
              aria-label={`${hourLabel(d.hour)} — ${d.succeeded} succeeded, ${d.failed} failed`}
              onMouseEnter={() => setHovered(i)}
              onFocus={() => setHovered(i)}
              onBlur={() => setHovered(null)}
              style={{
                flex: '1 1 0', minWidth: 0, height: '100%', padding: 0, border: 'none', cursor: 'default',
                // La colonne entière est la cible, pas la barre : un seau vide reste survolable, et
                // une heure sans connexion est une information comme une autre.
                background: hovered === i ? 'var(--surface-2)' : 'transparent',
                borderRadius: 3,
                display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', gap: 2,
              }}
            >
              {d.failed > 0 && (
                <div style={{
                  backgroundColor: 'var(--danger)',
                  height: `${Math.max(2, (d.failed / max) * barMax)}px`,
                  borderRadius: '3px 3px 0 0',
                }} />
              )}
              {d.succeeded > 0 && (
                <div style={{
                  backgroundColor: 'var(--ia-accent)',
                  height: `${Math.max(2, (d.succeeded / max) * barMax)}px`,
                  borderRadius: total === d.succeeded ? '3px 3px 0 0' : 0,
                }} />
              )}
            </button>
          );
        })}
      </div>

      {/* Quatre repères plutôt que vingt-quatre : les étiquettes se chevauchaient sur une carte de
          demi-largeur, et un axe illisible ne vaut pas mieux qu'un axe absent. Les indices sont
          calculés sur la longueur reçue — les coder en dur faisait sortir du tableau, et le rendu
          entier tombait, dès qu'une série comptait moins de dix-sept seaux. */}
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10.5, color: 'var(--fg-subtle)', marginTop: 4 }}>
        {[...new Set([0, Math.floor(data.length / 3), Math.floor((data.length * 2) / 3), data.length - 1])]
          .map(i => <span key={data[i].hour}>{hourLabel(data[i].hour)}</span>)}
      </div>

      {active && (
        <div
          id={tipId}
          role="tooltip"
          style={{
            position: 'absolute', top: 0, left: 0, right: 0, pointerEvents: 'none',
            display: 'flex', justifyContent: 'center',
          }}
        >
          <div style={{
            background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 6,
            boxShadow: 'var(--shadow-md)', padding: '6px 10px', fontSize: 12, whiteSpace: 'nowrap',
          }}>
            <div style={{ fontWeight: 600, marginBottom: 2 }}>{hourLabel(active.hour)}</div>
            <div style={{ display: 'flex', gap: 10, color: 'var(--fg-muted)' }}>
              <span><span style={{ color: 'var(--ia-accent)' }}>●</span> {active.succeeded} succeeded</span>
              <span><span style={{ color: 'var(--danger)' }}>●</span> {active.failed} failed</span>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
