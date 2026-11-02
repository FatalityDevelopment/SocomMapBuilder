// View settings shared by every page, remembered in the browser: the viewport backdrop (dark or blueprint).

import { effect, signal } from './signals';
import type { Backdrop } from '../webview/zipper-core/viewport-core';

const BACKDROP = 'mapbuilder.backdrop';

function stored(): Backdrop {
  try { return localStorage.getItem(BACKDROP) === 'blueprint' ? 'blueprint' : 'dark'; } catch { return 'dark'; }
}

export const viewBackdrop = signal<Backdrop>(stored());

// The Asset Viewer's previews draw on CSS backgrounds; the body class switches them too.
effect(() => {
  const mode = viewBackdrop();
  if (typeof document !== 'undefined') { document.body.classList.toggle('bg-blueprint', mode === 'blueprint'); }
  try { localStorage.setItem(BACKDROP, mode); } catch { /* storage unavailable */ }
});
