import { useEffect, useState } from 'react';
import Sidebar from './Sidebar';
import Topbar from './Topbar';
import CommandPalette from './CommandPalette';
import MfaReminder from '@/components/MfaReminder';

/**
 * CommandPalette is mounted conditionally rather than kept mounted and hidden: unmounting is what
 * resets its query and keyboard cursor, so every open starts fresh. Hoisting it out of the
 * `cmdOpen &&` guard reintroduces a palette that reopens showing the last search.
 */
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
        <MfaReminder />
        <div className="iam-main-scroll">
          {children}
        </div>
      </div>
      {cmdOpen && <CommandPalette onClose={() => setCmdOpen(false)} />}
    </div>
  );
}
