import { useTheme, PRESETS, isDarkPreset, isLightPreset } from '@/context/ThemeContext';

function PresetCard({ label, bg, accent, sidebar, active, onClick }: Readonly<{
  label: string; bg: string; accent: string; sidebar: string;
  active: boolean; onClick: () => void;
}>) {
  return (
    <button onClick={onClick} title={label} style={{
      width: '100%', aspectRatio: '4/3', borderRadius: 8,
      border: active ? `2px solid ${accent}` : '2px solid var(--border)',
      overflow: 'hidden', cursor: 'pointer', background: bg,
      boxShadow: active ? `0 0 0 2px ${accent}40` : 'none',
      transition: 'border-color 120ms, box-shadow 120ms',
      display: 'flex', flexDirection: 'column', padding: 0,
    }}>
      {/* Mini shell: sidebar + content */}
      <div style={{ flex: 1, display: 'flex', overflow: 'hidden' }}>
        {/* Sidebar strip */}
        <div style={{ width: '28%', background: sidebar, display: 'flex', flexDirection: 'column', padding: '5px 4px', gap: 3 }}>
          {[0.7, 0.5, 0.5, 0.4].map((w, i) => (
            <div key={i} style={{ height: 3, borderRadius: 2, width: `${w * 100}%`, background: accent, opacity: i === 0 ? 0.9 : 0.4 }} />
          ))}
        </div>
        {/* Content area */}
        <div style={{ flex: 1, padding: '5px 5px', display: 'flex', flexDirection: 'column', gap: 3 }}>
          {/* Stat row */}
          <div style={{ display: 'flex', gap: 2, marginBottom: 2 }}>
            {[1, 1, 1].map((_, i) => (
              <div key={i} style={{ flex: 1, height: 8, borderRadius: 3, background: accent, opacity: 0.15 }} />
            ))}
          </div>
          {/* Table rows */}
          {[0.9, 0.7, 0.8, 0.6].map((w, i) => (
            <div key={i} style={{ height: 3, borderRadius: 2, width: `${w * 100}%`, background: accent, opacity: 0.2 }} />
          ))}
        </div>
      </div>
      {/* Label strip */}
      <div style={{ fontSize: 9, fontWeight: 600, textAlign: 'center', padding: '3px 0',
        color: bg.includes('0.08') || bg.includes('0.085') || bg.includes('0.11') || bg.includes('0.125') || bg.includes('0.108') || bg.includes('0.115') || bg.includes('0.112') ? '#fff' : accent,
        background: bg, letterSpacing: '0.04em', textTransform: 'uppercase' }}>
        {label}
      </div>
    </button>
  );
}

export default function TweaksPanel({ onClose }: Readonly<{ onClose: () => void }>) {
  const { preset, dark, setPreset, toggleDark } = useTheme();
  const canToggleDark = !isDarkPreset(preset) && !isLightPreset(preset);

  return (
    <>
      {/* Scrim */}
      <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 199 }} onClick={onClose} onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }} />

      {/* Panel */}
      <div style={{
        position: 'fixed', right: 0, top: 0, bottom: 0, zIndex: 200,
        width: 280, background: 'var(--surface)', borderLeft: '1px solid var(--border)',
        boxShadow: 'var(--shadow-lg)', display: 'flex', flexDirection: 'column',
        animation: 'tweaks-slide-in 200ms ease',
      }}>
        <style>{`@keyframes tweaks-slide-in { from { transform: translateX(100%); opacity: 0; } to { transform: translateX(0); opacity: 1; } }`}</style>

        {/* Header */}
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '16px 20px', borderBottom: '1px solid var(--border)' }}>
          <div style={{ fontSize: 13, fontWeight: 600 }}>Appearance</div>
          <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" onClick={onClose}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
          </button>
        </div>

        {/* Body */}
        <div style={{ flex: 1, overflowY: 'auto', padding: '16px 20px', display: 'flex', flexDirection: 'column', gap: 20 }}>

          {/* Dark mode toggle */}
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 10 }}>Mode</div>
            <div style={{ display: 'flex', border: '1px solid var(--border)', borderRadius: 8, overflow: 'hidden' }}>
              {[
                { label: 'Light', icon: <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>, value: false },
                { label: 'Dark',  icon: <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>, value: true },
              ].map(({ label, icon, value }) => {
                const isActive = dark === value;
                const disabled = value ? isLightPreset(preset) : isDarkPreset(preset);
                return (
                  <button key={label} onClick={() => { if (!disabled && dark !== value) toggleDark(); }}
                    disabled={disabled}
                    style={{
                      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
                      padding: '8px 0', fontSize: 12, fontWeight: 500, border: 'none', cursor: disabled ? 'not-allowed' : 'pointer',
                      background: isActive ? 'var(--ia-accent)' : 'var(--surface)',
                      color: isActive ? 'var(--accent-fg)' : disabled ? 'var(--fg-subtle)' : 'var(--fg-muted)',
                      transition: 'background 150ms, color 150ms',
                    }}>
                    {icon}{label}
                  </button>
                );
              })}
            </div>
            {!canToggleDark && (
              <p style={{ fontSize: 11, color: 'var(--fg-subtle)', marginTop: 6 }}>
                {isDarkPreset(preset) ? 'Dark-only preset.' : 'Light-only preset.'}
              </p>
            )}
          </div>

          {/* Preset grid */}
          <div>
            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 10 }}>Colour Preset</div>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 8 }}>
              {PRESETS.map(p => (
                  <PresetCard
                    key={p.id}
                    label={p.label}
                    bg={p.bg}
                    accent={p.accent}
                    sidebar={p.sidebar}
                    active={preset === p.id}
                    onClick={() => setPreset(p.id)}
                  />
              ))}
            </div>
          </div>
        </div>

        {/* Footer */}
        <div style={{ padding: '12px 20px', borderTop: '1px solid var(--border)', fontSize: 11, color: 'var(--fg-subtle)', textAlign: 'center' }}>
          Preferences saved locally
        </div>
      </div>
    </>
  );
}
