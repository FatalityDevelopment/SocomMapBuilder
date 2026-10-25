import type { ReactNode } from 'react';
import { Icon } from './shared/Icon';
import { HostDialog } from './shared/HostDialog';
import { FileBrowser } from './shared/FileBrowser';
import { ImportDialog } from './shared/ImportDialog';
import { activePage, useSignals, type PageId } from './shared';
import { MapPage } from './MapPage';
import { ModelEditorPage } from './webview/model-editor/ModelEditorPage';
import { AnimationEditorPage } from './webview/animation-editor/AnimationEditorPage';
import { ViewerPage } from './webview/zipper-workbench/ViewerPage';

const PAGES: Array<{ id: PageId; label: string; icon: string; render: () => ReactNode }> = [
  { id: 'map', label: 'Map Builder', icon: 'globe', render: () => <MapPage /> },
  { id: 'models', label: 'Model Editor', icon: 'library', render: () => <ModelEditorPage /> },
  { id: 'animations', label: 'Animation Editor', icon: 'repeat', render: () => <AnimationEditorPage /> },
  { id: 'viewer', label: 'Asset Viewer', icon: 'database', render: () => <ViewerPage /> },
];

// The activity bar switches pages; every page stays mounted so its session, camera and selection survive.
export function AppShell() {
  useSignals();
  const current = activePage();
  return (
    <div className="mb-shell">
      <nav className="mb-activity">
        <div className="mb-activity-logo"><img src="/codedesigner-icon.png" alt="Map Builder" /></div>
        {PAGES.map(p => (
          <button key={p.id} className={`mb-activity-btn${current === p.id ? ' on' : ''}`} title={p.label} onClick={() => activePage.set(p.id)}>
            <Icon name={p.icon} size={22} strokeWidth={1.5} />
          </button>
        ))}
      </nav>
      <main className="mb-pages">
        {PAGES.map(p => (
          <div key={p.id} className={`mb-page${current === p.id ? ' on' : ''}`}>{p.render()}</div>
        ))}
      </main>
      <HostDialog />
      {/* The import dialog opens the file browser, so the browser renders on top. */}
      <ImportDialog />
      <FileBrowser />
    </div>
  );
}
