import { createRoot } from 'react-dom/client';
import { AppShell } from './AppShell';
import './styles/theme.css';
import './webview/shared/styles/shared.css';
import './webview/model-editor/model-editor-page.css';
import './webview/animation-editor/animation-editor-page.css';
import './webview/zipper-workbench/zipper-workbench-page.css';
import './styles/map-builder.css';
import './styles/dialog.css';
import './styles/shell.css';

createRoot(document.getElementById('root')!).render(<AppShell />);
