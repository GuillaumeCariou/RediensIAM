import { useEffect, useState } from 'react';
import Sidebar from './Sidebar';
import Topbar from './Topbar';
import CommandPalette from './CommandPalette';
import TweaksButton from './TweaksButton';

export default function Shell({ children }: Readonly<{ children: React.ReactNode }>) {
  const [cmdOpen, setCmdOpen] = useState(false);

  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault();
        setCmdOpen(o => !o);
      }
    };
    globalThis.addEventListener('keydown', h);
    return () => globalThis.removeEventListener('keydown', h);
  }, []);

  return (
    <div className="iam-screen">
      <Sidebar />
      <div className="iam-main">
        <Topbar onCmdK={() => setCmdOpen(true)} />
        <div className="iam-main-scroll">
          {children}
        </div>
      </div>
      {/* Mounted only while open, so the query and cursor start fresh every time. */}
      {cmdOpen && <CommandPalette onClose={() => setCmdOpen(false)} />}
      <TweaksButton />
    </div>
  );
}
