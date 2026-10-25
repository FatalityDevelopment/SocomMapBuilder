import { Icon } from './Icon';
import './page-header.css';

// The full-width page header CodeDesigner's standalone pages use (icon tile, title, subtitle).
export function PageHeader({ icon, title, subtitle = '' }: { icon: string; title: string; subtitle?: string }) {
  return (
    <header className="cd-page-header">
      <div className="cd-page-brand">
        <div className="cd-page-mark"><Icon name={icon} size={22} /></div>
        <div className="cd-page-copy">
          <h1 className="cd-page-title">{title}</h1>
          <p className="cd-page-subtitle">{subtitle}</p>
        </div>
      </div>
      <div className="cd-page-actions" />
    </header>
  );
}
