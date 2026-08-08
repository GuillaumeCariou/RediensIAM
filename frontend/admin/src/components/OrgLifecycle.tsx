import { IamDialog } from '@/components/iam';

/**
 * Les deux confirmations du cycle de vie d'une organisation, écrites une fois pour toute la console.
 *
 * <p>Trois pages les offraient avec trois textes indépendants, et les écarts n'étaient pas
 * cosmétiques :</p>
 *
 * <ul>
 *   <li>la liste des organisations disait « toutes les données associées » — donc rien ;</li>
 *   <li>la vue d'ensemble d'un locataire nommait les listes, les projets et les comptes de service,
 *       en <b>omettant</b> les comptes <i>dans</i> ces listes, les grants Keto et la chaîne
 *       d'audit ;</li>
 *   <li>la même vue d'ensemble suspendait <b>sans aucune confirmation</b>, alors que la suspension
 *       révoque immédiatement toutes les sessions vivantes du locataire, celles de ses propres
 *       administrateurs comprises — ils ne peuvent plus se reconnecter.</li>
 * </ul>
 *
 * <p>Trois copies d'une phrase qui décrit une destruction en cascade divergent : c'est arrivé ici, et
 * la plus consultée était la moins complète. Elles vivent donc à un seul endroit, et une page qui
 * offre l'action hérite du texte juste sans avoir à le recopier.</p>
 */

/** Ce que la suppression emporte. Une seule source, parce qu'une seconde finirait par mentir. */
export const ORG_DELETE_CASCADE =
  'This destroys the organisation and everything under it: every project and its OAuth2 client, '
  + 'every user list, every account in those lists, every admin grant in Keto, and this tenant’s '
  + 'whole audit chain. Sessions are revoked as it goes. It cannot be undone.';

/** Ce que la suspension fait, et ce qu'elle ne fait pas — la seconde moitié compte autant. */
export const ORG_SUSPEND_EFFECT =
  'Every session in this organisation is revoked immediately — its own administrators included — '
  + 'and every sign-in is refused until you unsuspend. Nothing is deleted.';

export const ORG_UNSUSPEND_EFFECT =
  'Sign-in is allowed again from now on. The sessions the suspension revoked stay revoked.';

interface SuspendProps {
  open: boolean;
  name?: string;
  /** L'organisation est-elle déjà suspendue — la boîte devient alors celle du retour en arrière. */
  suspended: boolean;
  busy?: boolean;
  onClose: () => void;
  onConfirm: () => void;
}

export function OrgSuspendDialog({ open, name, suspended, busy, onClose, onConfirm }: Readonly<SuspendProps>) {
  return (
    <IamDialog
      open={open}
      onClose={onClose}
      title={suspended ? <>Unsuspend “{name}”?</> : <>Suspend “{name}”?</>}
      desc={suspended ? ORG_UNSUSPEND_EFFECT : ORG_SUSPEND_EFFECT}
      footer={
        <>
          <button type="button" className="iam-btn iam-btn-secondary" onClick={onClose}>Cancel</button>
          <button type="button" className="iam-btn iam-btn-danger" onClick={onConfirm} disabled={busy}>
            {suspended ? 'Unsuspend' : 'Suspend'}
          </button>
        </>
      }
    />
  );
}

interface DeleteProps {
  open: boolean;
  name?: string;
  busy?: boolean;
  onClose: () => void;
  onConfirm: () => void;
}

export function OrgDeleteDialog({ open, name, busy, onClose, onConfirm }: Readonly<DeleteProps>) {
  return (
    <IamDialog
      open={open}
      onClose={onClose}
      title={<>Delete “{name}”?</>}
      desc={ORG_DELETE_CASCADE}
      footer={
        <>
          <button type="button" className="iam-btn iam-btn-secondary" onClick={onClose}>Cancel</button>
          <button type="button" className="iam-btn iam-btn-danger" onClick={onConfirm} disabled={busy}>
            {busy ? 'Deleting…' : 'Delete for good'}
          </button>
        </>
      }
    />
  );
}
