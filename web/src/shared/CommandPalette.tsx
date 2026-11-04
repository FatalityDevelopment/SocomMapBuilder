// The command search (F3, or Ctrl+Shift+P): type to find any menu command of the page in front, Enter runs it.

import { useEffect, useRef, useState } from 'react';
import { searchCommands, type Command } from './commands';

export function CommandPalette({ commands, onClose }: { commands: Command[]; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const found = searchCommands(commands, query);
  useEffect(() => {
    const element = dialog.current!;
    element.showModal();
    return () => { if (element.open) { element.close(); } };
  }, []);
  useEffect(() => setActive(0), [query]);
  const run = (c: Command | undefined) => {
    if (!c || c.disabled) { return; }
    onClose();
    // After the dialog closes, so a command that opens its own prompt gets the focus.
    setTimeout(c.run, 0);
  };
  return (
    <dialog ref={dialog} className="mp-shortcuts cmd-palette" aria-label="Search commands" onCancel={onClose} onClose={onClose}
      onClick={e => { if (e.target === dialog.current) { onClose(); } }}>
      <input className="me-search" aria-label="Search commands" placeholder="Search commands…" value={query} autoFocus
        onChange={e => setQuery(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setActive(i => Math.min(found.length - 1, i + 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(i => Math.max(0, i - 1)); }
          else if (e.key === 'Enter') { e.preventDefault(); run(found[active]); }
        }} />
      <ul className="cmd-list" role="listbox">
        {found.map((c, i) => (
          <li key={`${c.group}:${c.label}`} role="option" aria-selected={i === active} aria-disabled={c.disabled}
            className={`cmd-item${i === active ? ' active' : ''}${c.disabled ? ' disabled' : ''}`}
            onMouseEnter={() => setActive(i)} onClick={() => run(c)}>
            <span className="cmd-label">{c.label}</span>
            <span className="cmd-group">{c.group}</span>
            {c.shortcut && <kbd className="cmd-key">{c.shortcut}</kbd>}
          </li>
        ))}
        {!found.length && <li className="lm-muted cmd-none">No command matches “{query}”.</li>}
      </ul>
    </dialog>
  );
}
