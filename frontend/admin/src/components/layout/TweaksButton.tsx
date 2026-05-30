import { useState } from 'react';
import TweaksPanel from './TweaksPanel';

export default function TweaksButton() {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        onClick={() => setOpen(o => !o)}
        title="Appearance"
        style={{
          position: 'fixed', bottom: 24, right: 24, zIndex: 190,
          width: 44, height: 44, borderRadius: '50%',
          background: 'var(--ia-accent)', color: 'var(--accent-fg)',
          border: 'none', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
          boxShadow: 'var(--shadow-md)', transition: 'transform 150ms, box-shadow 150ms',
        }}
        onMouseEnter={e => { (e.currentTarget as HTMLButtonElement).style.transform = 'scale(1.08)'; }}
        onMouseLeave={e => { (e.currentTarget as HTMLButtonElement).style.transform = 'scale(1)'; }}
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
          <circle cx="12" cy="12" r="3"/>
          <path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/>
        </svg>
      </button>
      {open && <TweaksPanel onClose={() => setOpen(false)} />}
    </>
  );
}
