import { useEffect, useState } from 'react';

interface NumFieldProps {
  value: string;
  onCommit: (text: string) => void;
  step?: number | string;
  min?: number | string;
  disabled?: boolean;
  title?: string;
  small?: boolean;
}

// A number input that holds its own text while typing and commits on blur or Enter (like a native change event).
export function NumField({ value, onCommit, step, min, disabled, title, small }: NumFieldProps) {
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
      onBlur={() => { if (text !== value) { onCommit(text); } }}
      onKeyDown={e => { if (e.key === 'Enter') { (e.target as HTMLInputElement).blur(); } }}
    />
  );
}
