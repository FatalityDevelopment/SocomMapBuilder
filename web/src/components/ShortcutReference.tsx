import { useEffect, useRef, useState } from 'react';

export const MAP_SHORTCUTS = [
  ['Tools', 'V / G / R / T / P', 'Select / Move / Rotate / Scale / Place clutter'],
  ['Navigation', 'W A S D · Q / E', 'Fly; Q / E move down / up (viewport focused)'],
  ['Navigation', 'Shift while flying', 'Fly faster'],
  ['Navigation', 'Middle drag / Alt + left drag', 'Orbit'],
  ['Navigation', 'Right drag / Wheel', 'Pan / Zoom'],
  ['Selection', 'Click / Shift + click / Ctrl + click', 'Select / Add / Remove'],
  ['Selection', 'Drag / Shift + A', 'Box select / Select same model'],
  ['Selection', 'Double-click', 'Open the selected prop in Model Editor'],
  ['Editing', 'Shift + D', 'Duplicate selection'],
  ['Editing', 'X / Delete', 'Delete selection'],
  ['Editing', 'End', 'Drop selection to ground'],
  ['Editing', 'Ctrl / Cmd + C / V', 'Copy / Paste clutter'],
  ['Editing', 'Ctrl / Cmd + Z', 'Undo'],
  ['Editing', 'Ctrl / Cmd + Shift + Z · Ctrl / Cmd + Y', 'Redo'],
  ['Files', 'Ctrl / Cmd + O', 'Open package'],
  ['Files', 'Ctrl / Cmd + S', 'Save copy'],
  ['View', 'F / Home', 'Frame selection / Frame map'],
  ['View', 'Numpad 1 / 3 / 7', 'Front / Right / Top view'],
  ['View', 'Numpad 5 / Z', 'Toggle orthographic / Cycle shading'],
  ['General', 'Escape', 'Cancel placement or tool; clear selection when using Select'],
  ['General', '?', 'Open keyboard shortcuts'],
] as const;

export function filterShortcuts(query: string) {
  const terms = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
  return MAP_SHORTCUTS.filter(row => terms.every(term => row.join(' ').toLowerCase().includes(term)));
}

export function ShortcutReference({ onClose }: { onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [query, setQuery] = useState('');
  useEffect(() => {
    const element = dialog.current!;
    element.showModal();
    return () => { if (element.open) { element.close(); } };
  }, []);
  const rows = filterShortcuts(query);
  return (
    <dialog ref={dialog} className="mp-shortcuts" aria-labelledby="mp-shortcut-title" onCancel={onClose} onClose={onClose}>
      <div className="mp-shortcut-heading">
        <h2 id="mp-shortcut-title">Keyboard shortcuts</h2>
        <button type="button" className="lm-tool-btn" onClick={onClose}>Close</button>
      </div>
      <input className="me-search" aria-label="Search shortcuts" placeholder="Search tools, actions or keys…" value={query} onChange={e => setQuery(e.target.value)} autoFocus />
      <p className="lm-muted">Shortcuts apply to Map Builder. Typing in fields leaves editor shortcuts inactive.</p>
      <div className="mp-shortcut-list">
        <table><thead><tr><th>Group</th><th>Keys</th><th>Action</th></tr></thead>
          <tbody>{rows.map(([group, keys, action]) => <tr key={keys}><td>{group}</td><td><kbd>{keys}</kbd></td><td>{action}</td></tr>)}</tbody>
        </table>
        {!rows.length && <p role="status">No shortcuts match “{query}”.</p>}
      </div>
    </dialog>
  );
}
