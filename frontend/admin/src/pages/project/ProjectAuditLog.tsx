import { useEffect, useState } from 'react';
import { getProjectAuditLog } from '@/api';
import { useProjectContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import AuditLogTable, { type AuditEntry } from '@/components/AuditLogTable';

/**
 * Le journal du projet.
 *
 * `/project/audit-log` est la seule des trois routes de journal qu'un project_admin peut lire :
 * `/admin/audit-log` est réservé au super admin et `/org/audit-log` à l'organisation. Elle n'a pas
 * d'export CSV côté serveur, d'où le tableau sans bouton d'export — un bouton qui rendrait 404
 * serait pire que son absence.
 */

const ACTION_COLORS: Record<string, 'default' | 'destructive' | 'success' | 'warning' | 'secondary'> = {
  login: 'success',
  login_failed: 'destructive',
  logout: 'secondary',
  'user.created': 'default',
  'role.assigned': 'default',
  'role.revoked': 'secondary',
  'role.created': 'default',
  'role.deleted': 'destructive',
  'session.revoked': 'warning',
  'project.updated': 'warning',
};

const PAGE_SIZE = 50;

export default function ProjectAuditLog() {
  const { projectId } = useProjectContext();
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(Boolean(projectId));
  const [offset, setOffset] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [error, setError] = useState('');

  // La page lue est `offset`, donc l'effet en dépend et les boutons ne font que la déplacer : rien
  // n'est posé de façon synchrone dans le corps de l'effet (react-hooks/set-state-in-effect).
  useEffect(() => {
    if (!projectId) return;
    getProjectAuditLog(projectId, { limit: PAGE_SIZE, offset })
      .then(res => {
        const rows: AuditEntry[] = Array.isArray(res) ? res : (res?.entries ?? []);
        setEntries(rows);
        setHasMore(rows.length === PAGE_SIZE);
        setError('');
      })
      // Sans ce catch la page restait sur son squelette et le refus n'existait que dans les
      // devtools — le défaut d'origine de ce chantier.
      .catch(() => { setEntries([]); setHasMore(false); setError('Could not read this project’s audit log.'); })
      .finally(() => setLoading(false));
  }, [projectId, offset]);

  const goTo = (o: number) => { setLoading(true); setOffset(o); };
  const prev = () => goTo(Math.max(0, offset - PAGE_SIZE));
  const next = () => goTo(offset + PAGE_SIZE);

  return (
    <div>
      <PageHeader title="Audit log" description="Administrative actions in this project" />
      {error && (
        <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{error}</div>
      )}
      <AuditLogTable
        entries={entries}
        loading={loading}
        offset={offset}
        hasMore={hasMore}
        onPrev={prev}
        onNext={next}
        actionColors={ACTION_COLORS}
      />
    </div>
  );
}
