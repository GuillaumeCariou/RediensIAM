import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: ReactNode;
  description?: ReactNode;
  desc?: ReactNode;
  action?: ReactNode;
  actions?: ReactNode[];
}

export default function PageHeader({ title, description, desc, action, actions }: Readonly<PageHeaderProps>) {
  const descContent = description ?? desc;
  const actionContent = action ?? (actions && actions.length > 0 ? (
    <div style={{ display: 'flex', gap: 8 }}>{actions}</div>
  ) : null);

  return (
    <div className="iam-page-header">
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 16 }}>
        <div style={{ flex: 1 }}>
          <div className="iam-page-title">{title}</div>
          {descContent && <div className="iam-page-desc">{descContent}</div>}
        </div>
        {actionContent && <div style={{ flexShrink: 0 }}>{actionContent}</div>}
      </div>
    </div>
  );
}
