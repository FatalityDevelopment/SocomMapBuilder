import { Component, Suspense, lazy, useState, type ErrorInfo, type ReactNode } from 'react';
import { Icon } from './shared/Icon';
import { HostDialog } from './shared/HostDialog';
import { FileBrowser } from './shared/FileBrowser';
import { ImportDialog } from './shared/ImportDialog';
import { activePage, useSignals, type PageId } from './shared';
import { MapPage } from './MapPage';

const ModelEditorPage = lazy(() => import('./webview/model-editor/ModelEditorPage').then(module => ({ default: module.ModelEditorPage })));
const AnimationEditorPage = lazy(() => import('./webview/animation-editor/AnimationEditorPage').then(module => ({ default: module.AnimationEditorPage })));
const ViewerPage = lazy(() => import('./webview/zipper-workbench/ViewerPage').then(module => ({ default: module.ViewerPage })));

const PAGES: Array<{ id: PageId; label: string; icon: string; render: () => ReactNode }> = [
  { id: 'map', label: 'Map Builder', icon: 'globe', render: () => <MapPage /> },
  { id: 'models', label: 'Model Editor', icon: 'library', render: () => <ModelEditorPage /> },
  { id: 'animations', label: 'Animation Editor', icon: 'repeat', render: () => <AnimationEditorPage /> },
  { id: 'viewer', label: 'Asset Viewer', icon: 'database', render: () => <ViewerPage /> },
];

// A page starts on its first visit and stays mounted afterwards, preserving its session, camera and selection.
export function AppShell() {
  useSignals();
  const current = activePage();
  const [visited, setVisited] = useState(() => new Set<PageId>([current]));
  if (!visited.has(current)) { setVisited(new Set([...visited, current])); }
  return (
    <div className="mb-shell">
      <nav className="mb-activity" aria-label="Editors">
        <div className="mb-activity-logo"><img src="/codedesigner-icon.png" alt="Map Builder" /></div>
        {PAGES.map(p => (
          <button key={p.id} className={`mb-activity-btn${current === p.id ? ' on' : ''}`} title={p.label}
            aria-label={p.label} aria-pressed={current === p.id} onClick={() => activePage.set(p.id)}>
            <Icon name={p.icon} size={22} strokeWidth={1.5} />
          </button>
        ))}
      </nav>
      <main className="mb-pages">
        {PAGES.filter(p => visited.has(p.id)).map(p => (
          <div key={p.id} className={`mb-page${current === p.id ? ' on' : ''}`} data-editor={p.id}>
            <EditorBoundary label={p.label}>
              <Suspense fallback={<div className="mb-page-loading" role="status">Loading {p.label}…</div>}>
                {p.render()}
              </Suspense>
            </EditorBoundary>
          </div>
        ))}
      </main>
      <HostDialog />
      {/* The import dialog opens the file browser, so the browser renders on top. */}
      <ImportDialog />
      <FileBrowser />
    </div>
  );
}

// A failed download or editor startup should leave the other editors available.
class EditorBoundary extends Component<{ label: string; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error: Error, info: ErrorInfo) { console.error(`${this.props.label} could not start`, error, info); }
  render() {
    if (this.state.failed) {
      return <div className="mb-page-loading" role="alert">
        <h2>{this.props.label} could not start</h2>
        <p>You can continue using the other editors. To retry, save any open work before reloading the app.</p>
      </div>;
    }
    return this.props.children;
  }
}
