import { useEffect, useRef, useState, type ReactNode } from 'react';

interface IamMenuProps {
  /** Rendered inside the trigger button. */
  trigger: ReactNode;
  triggerClassName?: string;
  triggerLabel?: string;
  children: ReactNode;
}

/**
 * Dropdown menu. Closes on Escape and on any pointer press outside the anchor — `pointerdown`
 * rather than `click`, so a press that lands on another control closes this menu before that
 * control's own handler runs, instead of leaving two menus open.
 *
 * Items are plain `<button className="iam-menu-item">` at the call site; nothing here wraps them.
 */
export default function IamMenu({ trigger, triggerClassName = 'iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm', triggerLabel, children }: Readonly<IamMenuProps>) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!anchor.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    globalThis.addEventListener('pointerdown', onDown);
    globalThis.addEventListener('keydown', onKey);
    return () => {
      globalThis.removeEventListener('pointerdown', onDown);
      globalThis.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div className="iam-menu-anchor" ref={anchor}>
      <button
        type="button"
        className={triggerClassName}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={triggerLabel}
        onClick={() => setOpen(o => !o)}
      >
        {trigger}
      </button>
      {open && (
        // Click anywhere in the menu closes it: every item here runs one action and dismisses.
        <div className="iam-menu" role="menu" onClick={() => setOpen(false)}>
          {children}
        </div>
      )}
    </div>
  );
}
