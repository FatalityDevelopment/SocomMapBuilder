import { useState, type FormEvent } from 'react';

export interface PathDialogSpec {
  title: string;
  submitLabel: string;
  fields: Array<{ key: string; label: string; value: string }>;
  onSubmit: (values: Record<string, string>) => void;
}

interface PathDialogProps {
  spec: PathDialogSpec;
  onCancel: () => void;
}

// Browsers cannot hand over a full file path, so the paths are typed in (or pasted) here.
export function PathDialog({ spec, onCancel }: PathDialogProps) {
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(spec.fields.map(f => [f.key, f.value])));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    spec.onSubmit(values);
  };
  return (
    <div className="mb-backdrop" onPointerDown={e => { if (e.target === e.currentTarget) { onCancel(); } }}>
      <form className="mb-dialog" onSubmit={submit} onKeyDown={e => { if (e.key === 'Escape') { e.preventDefault(); onCancel(); } }}>
        <div className="me-panel-title">{spec.title}</div>
        {spec.fields.map(f => (
          <label key={f.key} className="me-field me-field-wide">
            {f.label}
            <input
              className="me-search"
              type="text"
              autoFocus={f === spec.fields[0]}
              value={values[f.key] ?? ''}
              onChange={e => setValues(v => ({ ...v, [f.key]: e.target.value }))}
            />
          </label>
        ))}
        <div className="me-actions">
          <button type="button" className="lm-tool-btn" onClick={onCancel}>Cancel</button>
          <button type="submit" className="lm-tool-btn">{spec.submitLabel}</button>
        </div>
      </form>
    </div>
  );
}
