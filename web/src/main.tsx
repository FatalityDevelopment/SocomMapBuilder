import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles/theme.css';
import './styles/shared.css';
import './styles/model-editor-page.css';
import './styles/map-builder.css';
import './styles/dialog.css';

createRoot(document.getElementById('root')!).render(<App />);
