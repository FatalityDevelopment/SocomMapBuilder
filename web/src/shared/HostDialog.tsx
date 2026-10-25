import { useEffect, useState, type FormEvent } from 'react';
import { activeDialog, useSignals, type DialogRequest } from './index';

// The prompt the host is waiting on (paths, pack names, clip names). Escape or a click outside cancels.
export function HostDialog() {
  useSignals();
  const request = activeDialog();
  return request ? <DialogForm key={request.title + request.fields.map(f => f.key).join()} request={request} /> : null;
}

function DialogForm({ request }: { request: DialogRequest }) {
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(request.fields.map(f => [f.key, f.value])));
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setError(null), [values]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    for (const f of request.fields) {
      const v = (values[f.key] ?? '').trim();
      if (!v && !f.optional) { setError(`${f.label} is required`); return; }
      if (v && f.pattern && !f.pattern.test(v)) { setError(f.hint ?? `${f.label} is not valid`); return; }
    }
    request.resolve(values);
  };

  return (
    <div className="mb-backdrop" onPointerDown={e => { if (e.target === e.currentTarget) { request.resolve(null); } }}>
      <form className="mb-dialog" onSubmit={submit} onKeyDown={e => { if (e.key === 'Escape') { e.preventDefault(); request.resolve(null); } }}>
        <div className="me-panel-title">{request.title}</div>
        {request.fields.map((f, i) => (
          <label key={f.key} className="me-field me-field-wide">
            {f.label}
            <input
              className="me-search"
              type="text"
              autoFocus={i === 0}
              value={values[f.key] ?? ''}
              onChange={e => setValues(v => ({ ...v, [f.key]: e.target.value }))}
            />
          </label>
        ))}
        {error && <div className="mb-dialog-error">{error}</div>}
        <div className="me-actions">
          <button type="button" className="lm-tool-btn" onClick={() => request.resolve(null)}>Cancel</button>
          <button type="submit" className="lm-tool-btn">{request.submitLabel}</button>
        </div>
      </form>
    </div>
  );
}
