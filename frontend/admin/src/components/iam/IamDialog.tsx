import { useEffect, useId, useRef, type ReactNode } from 'react';

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
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descId = useId();

  // showModal() gives focus containment, an inert background and Escape-to-close for free;
  // closedby="any" restores the click-outside-to-close the old scrim div provided. Set here
  // rather than in JSX because the linters do not know the attribute yet.
  useEffect(() => {
    const dialog = ref.current;
    if (!open || !dialog || dialog.open) return;
    dialog.setAttribute('closedby', 'any');
    dialog.showModal();
  }, [open]);

  if (!open) return null;

  return (
    <dialog
      ref={ref}
      className="iam-dialog"
      style={wide ? { width: 'min(720px, 92vw)' } : undefined}
      aria-labelledby={titleId}
      aria-describedby={desc ? descId : undefined}
      onClose={onClose}
    >
      <div className="iam-dialog-head">
        <div className="iam-dialog-title" id={titleId}>{title}</div>
        {desc && <div className="iam-dialog-desc" id={descId}>{desc}</div>}
      </div>
      <div className="iam-dialog-body">{children}</div>
      {footer && <div className="iam-dialog-foot">{footer}</div>}
    </dialog>
  );
}
