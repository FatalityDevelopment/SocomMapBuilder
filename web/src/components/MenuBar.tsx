import type { SessionStatus } from '../api/client';
import { fileName } from '../mapModel';

export interface MenuItem {
  label: string;
  shortcut?: string;
  action?: () => void;
  disabled?: () => boolean;
  checked?: () => boolean;
  separator?: boolean;
}

export interface Menu { id: string; label: string; items: MenuItem[] }

interface MenuBarProps {
  status: SessionStatus | null;
  dirtyCount: number;
  menus: Menu[];
  openMenu: string | null;
  setOpenMenu: (id: string | null) => void;
}

export function MenuBar({ status, dirtyCount, menus, openMenu, setOpenMenu }: MenuBarProps) {
  return (
    <div className="me-menubar">
      <span className="me-brand">
        <img src="/codedesigner-icon.png" alt="" width={14} height={14} /> Map Builder
      </span>
      {menus.map(menu => (
        <div key={menu.id} className={`me-menu${openMenu === menu.id ? ' open' : ''}`}>
          <button
            className="me-menu-btn"
            onClick={() => setOpenMenu(openMenu === menu.id ? null : menu.id)}
            onMouseEnter={() => { if (openMenu) { setOpenMenu(menu.id); } }}
          >
            {menu.label}
          </button>
          {openMenu === menu.id && (
            <div className="me-menu-pop">
              {menu.items.map((item, i) => item.separator ? (
                <div key={i} className="me-menu-sep" />
              ) : (
                <button
                  key={i}
                  className="me-menu-item"
                  disabled={item.disabled?.()}
                  onClick={() => { setOpenMenu(null); item.action?.(); }}
                >
                  <span className="me-menu-check">{item.checked?.() ? '✓' : ''}</span>
                  <span className="me-menu-label">{item.label}</span>
                  <span className="me-menu-key">{item.shortcut ?? ''}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      ))}
      <span className="me-spacer" />
      {status && (
        <>
          <span className="me-pkg" title={status.path}>{fileName(status.path)}</span>
          <span className={`me-badge${dirtyCount ? ' me-badge-dirty' : ''}`}>
            {dirtyCount ? `${dirtyCount} unsaved` : 'saved'} · rev {status.revision}
          </span>
          <span className="me-session" title="Asset session id">{status.session}</span>
        </>
      )}
    </div>
  );
}
