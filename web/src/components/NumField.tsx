import { useEffect, useState } from 'react';

interface NumFieldProps {
  value: string;
  onCommit: (text: string) => void;
  step?: number | string;
  min?: number | string;
  disabled?: boolean;
  title?: string;
  small?: boolean;
  // A relative field ("move by"): after committing, show `value` again so the next blur does not apply it twice.
  reset?: boolean;
}

// A number input that holds its own text while typing and commits on blur or Enter (like a native change event).
export function NumField({ value, onCommit, step, min, disabled, title, small, reset }: NumFieldProps) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return (
    <input
      className={`me-num${small ? ' me-num-sm' : ''}`}
      type="number"
      step={step}
      min={min}
      disabled={disabled}
      title={title}
      value={text}
      onChange={e => setText(e.target.value)}
      onBlur={e => {
        if (text === value) { return; }
        const validity = e.currentTarget.validity;
        // Step controls spinner increments; precise values between steps are still valid edits.
        if (!text.trim() || !Number.isFinite(Number(text)) || validity.badInput || validity.rangeUnderflow || validity.rangeOverflow) {
          setText(value);
          return;
        }
        onCommit(text);
        if (reset) { setText(value); }
      }}
      onKeyDown={e => {
        if (e.key === 'Escape') { setText(value); e.stopPropagation(); }
        if (e.key === 'Enter') { (e.target as HTMLInputElement).blur(); }
      }}
    />
  );
}
