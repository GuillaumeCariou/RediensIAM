import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '@/context/AuthContext';

function Kbd({ children }: Readonly<{ children: React.ReactNode }>) {
  return <kbd className="iam-kbd">{children}</kbd>;
}

interface CmdItem {
  group: string;
  label: string;
  sub?: string;
  kind: 'nav' | 'org' | 'proj' | 'action';
  to?: string;
}

const ICONS: Record<CmdItem['kind'], React.ReactNode> = {
  org: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM9 22V12h6v10"/></svg>,
  proj: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>,
  nav: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg>,
  action: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z"/></svg>,
};

interface CommandPaletteProps {
  onClose: () => void;
}

export default function CommandPalette({ onClose }: Readonly<CommandPaletteProps>) {
  const navigate = useNavigate();
  const { isSuperAdmin, isOrgAdmin, isProjectManager } = useAuth();
  const [q, setQ] = useState('');
  const [sel, setSel] = useState(0);
  const ref = useRef<HTMLDialogElement>(null);
  const listId = useId();

  const items = useMemo<CmdItem[]>(() => {
    const list: CmdItem[] = [];
    if (isSuperAdmin) {
      list.push(
        { group: 'System', label: 'Dashboard',            kind: 'nav', to: '/system' },
        { group: 'System', label: 'Organisations',         kind: 'nav', to: '/system/organisations' },
        { group: 'System', label: 'System Users',          kind: 'nav', to: '/system/users' },
        { group: 'System', label: 'System Projects',       kind: 'nav', to: '/system/projects' },
        { group: 'System', label: 'Service Accounts',      kind: 'nav', to: '/system/service-accounts' },
        { group: 'System', label: 'Audit Log',             kind: 'nav', to: '/system/audit-log' },
        { group: 'System', label: 'Metrics',               kind: 'nav', to: '/system/metrics' },
        { group: 'System', label: 'Health',                kind: 'nav', to: '/system/health' },
        { group: 'System', label: 'Email Config',          kind: 'nav', to: '/system/email' },
      );
    }
    if (isOrgAdmin) {
      list.push(
        { group: 'Organisation', label: 'Overview',         kind: 'nav', to: '/org' },
        { group: 'Organisation', label: 'Projects',         kind: 'nav', to: '/org/projects' },
        { group: 'Organisation', label: 'User Lists',       kind: 'nav', to: '/org/userlists' },
        { group: 'Organisation', label: 'Admins',           kind: 'nav', to: '/org/admins' },
        { group: 'Organisation', label: 'Service Accounts', kind: 'nav', to: '/org/service-accounts' },
        { group: 'Organisation', label: 'Audit Log',        kind: 'nav', to: '/org/audit-log' },
        { group: 'Organisation', label: 'Webhooks',         kind: 'nav', to: '/org/webhooks' },
        { group: 'Organisation', label: 'Settings',         kind: 'nav', to: '/org/settings' },
      );
    }
    if (isProjectManager) {
      list.push(
        { group: 'Project', label: 'Overview',          kind: 'nav', to: '/project' },
        { group: 'Project', label: 'Users',             kind: 'nav', to: '/project/users' },
        { group: 'Project', label: 'Roles',             kind: 'nav', to: '/project/roles' },
        { group: 'Project', label: 'Service Accounts',  kind: 'nav', to: '/project/service-accounts' },
        { group: 'Project', label: 'Authentication',    kind: 'nav', to: '/project/authentication' },
        { group: 'Project', label: 'Settings',          kind: 'nav', to: '/project/settings' },
      );
    }
    list.push({ group: 'Account', label: 'My Account', kind: 'nav', to: '/account' });
    return list;
  }, [isSuperAdmin, isOrgAdmin, isProjectManager]);

  const filtered = useMemo(() => {
    if (!q.trim()) return items;
    const lq = q.toLowerCase();
    return items.filter(c => c.label.toLowerCase().includes(lq) || c.sub?.toLowerCase().includes(lq));
  }, [q, items]);

  // Flat index travels with each item so the keyboard cursor and the rendered rows agree.
  const groups = useMemo(() => {
    const map = new Map<string, { item: CmdItem; index: number }[]>();
    filtered.forEach((item, index) => {
      const bucket = map.get(item.group) ?? [];
      bucket.push({ item, index });
      map.set(item.group, bucket);
    });
    return Array.from(map.entries());
  }, [filtered]);

  function run(item: CmdItem) {
    if (item.to) navigate(item.to);
    onClose();
  }

  // showModal() gives focus containment, an inert background and Escape-to-close for free;
  // closedby="any" restores the click-outside-to-close the old scrim div provided. Set here
  // rather than in JSX because the linters do not know the attribute yet.
  useEffect(() => {
    ref.current?.setAttribute('closedby', 'any');
    ref.current?.showModal();
  }, []);

  const optionId = (index: number) => `${listId}-opt-${index}`;

  function onInputKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setSel(s => Math.min(filtered.length - 1, s + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setSel(s => Math.max(0, s - 1)); }
    else if (e.key === 'Enter') { e.preventDefault(); const c = filtered[sel]; if (c) run(c); }
  }

  return (
    <dialog ref={ref} className="iam-cmdk" aria-label="Command palette" onClose={onClose}>
      <input
        className="iam-cmdk-input"
        autoFocus
        placeholder="Search pages, actions…"
        aria-label="Search pages, actions"
        role="combobox"
        aria-expanded
        aria-controls={listId}
        aria-activedescendant={filtered[sel] ? optionId(sel) : undefined}
        value={q}
        onChange={e => { setQ(e.target.value); setSel(0); }}
        onKeyDown={onInputKeyDown}
      />
      <div className="iam-cmdk-list" id={listId} role="listbox" aria-label="Results">
        {groups.length === 0 && (
          <div style={{ padding: 24, textAlign: 'center', color: 'var(--fg-muted)', fontSize: 13 }}>
            No matches
          </div>
        )}
        {groups.map(([group, groupItems]) => (
          <div key={group} role="group" aria-label={group}>
            <div className="iam-cmdk-group-label">{group}</div>
            {groupItems.map(({ item: c, index }) => (
              <button
                type="button"
                key={c.label + c.to}
                id={optionId(index)}
                role="option"
                aria-selected={index === sel}
                tabIndex={-1}
                className={`iam-cmdk-item${index === sel ? ' selected' : ''}`}
                onClick={() => run(c)}
                onMouseEnter={() => setSel(index)}
              >
                {ICONS[c.kind]}
                <span>{c.label}</span>
                {c.sub && <span className="mono" style={{ color: 'var(--fg-subtle)', fontSize: 11 }}>{c.sub}</span>}
                <span className="iam-cmdk-kind">{c.kind}</span>
              </button>
            ))}
          </div>
        ))}
      </div>
      <div className="iam-cmdk-foot">
        <span><Kbd>↑</Kbd><Kbd>↓</Kbd> navigate</span>
        <span><Kbd>↵</Kbd> go</span>
        <span><Kbd>esc</Kbd> close</span>
      </div>
    </dialog>
  );
}
