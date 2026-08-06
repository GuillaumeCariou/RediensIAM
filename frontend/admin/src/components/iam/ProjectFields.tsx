import {
  slugify,
  type ProjectFormState,
} from '@/lib/projectForm';

/**
 * The project form's fields. The rules they obey live in lib/projectForm.ts, so that the two pages
 * rendering this cannot disagree about what they collected.
 */
interface Props {
  /** Prefixes every id, so two of these can coexist on one page without colliding labels. */
  idPrefix: string;
  form: ProjectFormState;
  onChange: (next: ProjectFormState) => void;
  /** Name and slug are fixed once a project exists; only the URIs stay editable. */
  identityReadOnly?: boolean;
}

export default function ProjectFields({ idPrefix, form, onChange, identityReadOnly = false }: Readonly<Props>) {
  const set = <K extends keyof ProjectFormState>(key: K, value: ProjectFormState[K]) =>
    onChange({ ...form, [key]: value });

  return (
    <>
      {!identityReadOnly && (
        <>
          <div>
            <label className="iam-label" htmlFor={`${idPrefix}-name`}>Name</label>
            <input id={`${idPrefix}-name`} className="iam-input" value={form.name}
              onChange={e => set('name', e.target.value)} required placeholder="My Dashboard" />
          </div>
          <div>
            <label className="iam-label" htmlFor={`${idPrefix}-slug`}>Slug</label>
            <input id={`${idPrefix}-slug`} className="iam-input iam-mono" value={form.slug}
              onChange={e => set('slug', slugify(e.target.value))} required
              placeholder="my-dashboard" pattern="[a-z0-9]+(-[a-z0-9]+)*" />
            <p className="iam-help">Lowercase letters, numbers and hyphens only.</p>
          </div>
        </>
      )}
      <div>
        <label className="iam-label" htmlFor={`${idPrefix}-uris`}>Redirect URIs (one per line)</label>
        <textarea id={`${idPrefix}-uris`} className="iam-input" style={{ minHeight: 80, resize: 'vertical' }}
          value={form.redirect_uris} onChange={e => set('redirect_uris', e.target.value)}
          placeholder="https://dashboard.example.com/callback" />
        <p className="iam-help">
          Where sign-in returns the user. Each one also becomes an allowed origin for this project —
          nothing else has to be configured for a new front to work.
        </p>
      </div>
      <div>
        <label className="iam-label" htmlFor={`${idPrefix}-logout-uris`}>Post-logout redirect URIs (one per line)</label>
        <textarea id={`${idPrefix}-logout-uris`} className="iam-input" style={{ minHeight: 60, resize: 'vertical' }}
          value={form.post_logout_redirect_uris}
          onChange={e => set('post_logout_redirect_uris', e.target.value)}
          placeholder="https://dashboard.example.com/" />
        <p className="iam-help">Where sign-out may return the user. A target not listed here is refused, and the sign-out fails.</p>
      </div>
    </>
  );
}
