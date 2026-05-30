import { useEffect, type ReactNode } from 'react';

interface IamDialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  desc?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}

export default function IamDialog({ open, onClose, title, desc, children, footer, wide }: Readonly<IamDialogProps>) {
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div role="none" className="iam-dialog-scrim" onClick={onClose}>
      <dialog
        open
        className="iam-dialog"
        style={wide ? { width: 'min(720px, 92vw)' } : undefined}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
      >
        <div className="iam-dialog-head">
          <div className="iam-dialog-title">{title}</div>
          {desc && <div className="iam-dialog-desc">{desc}</div>}
        </div>
        <div className="iam-dialog-body">{children}</div>
        {footer && <div className="iam-dialog-foot">{footer}</div>}
      </dialog>
    </div>
  );
}
