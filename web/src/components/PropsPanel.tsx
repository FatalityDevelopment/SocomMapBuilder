import type { MapInstanceWire, MapPointWire, ModelSummary, SessionStatus } from '../webview/model-editor/asset-client';
import { fmt, movable, scaleOf, yawOf } from '../mapModel';
import { NumField } from './NumField';

export type PropTab = 'item' | 'place' | 'points' | 'map';

const TABS: Array<{ id: PropTab; label: string }> = [
  { id: 'item', label: 'Item' },
  { id: 'place', label: 'Place' },
  { id: 'points', label: 'Points' },
  { id: 'map', label: 'Map' },
];

interface PropsPanelProps {
  tab: PropTab;
  setTab: (tab: PropTab) => void;
  status: SessionStatus | null;
  sceneNs: string;
  countList: Array<{ kind: string; count: number }>;
  pointKinds: Array<{ kind: string; count: number }>;
  pointInfo: MapPointWire | null;
  selectedPoint: string | null;
  selection: number[];
  selectionSummary: string;
  single: MapInstanceWire | null;
  movableCount: number;
  busy: boolean;
  onMovePoint: (name: string, axis: 'x' | 'z', value: string) => void;
  onSetField: (field: 'position' | 'yaw' | 'scale', value: string, axis?: number) => void;
  // A multi-selection: move by an offset, turn by an angle, or set one scale for all.
  onEditSelection: (field: 'offset' | 'turn' | 'scale', value: string, axis?: number) => void;
  onDuplicate: () => void;
  onDelete: () => void;
  onDrop: () => void;
  onOpenModel: () => void;
  library: ModelSummary[];
  libQuery: string;
  setLibQuery: (q: string) => void;
  placeModel: ModelSummary | null;
  setPlaceModel: (m: ModelSummary) => void;
  placeYaw: number;
  setPlaceYaw: (v: number) => void;
  placeScale: number;
  setPlaceScale: (v: number) => void;
  randomYaw: boolean;
  setRandomYaw: (on: boolean) => void;
  // Import from package… (a model from another package into this one).
  onImport: () => void;
}

export function PropsPanel(p: PropsPanelProps) {
  return (
    <div className="me-props">
      <div className="me-tabs">
        {TABS.map(t => (
          <button key={t.id} className={`me-tab${p.tab === t.id ? ' active' : ''}`} onClick={() => p.setTab(t.id)}>{t.label}</button>
        ))}
      </div>
      <div className="me-tab-body">
        {p.tab === 'item' && <ItemTab {...p} />}
        {p.tab === 'place' && <PlaceTab {...p} />}
        {p.tab === 'points' && (
          <div className="me-card">
            <div className="me-card-title">AIMAPS points</div>
            <dl className="me-kv">
              {p.pointKinds.map(k => <div key={k.kind} style={{ display: 'contents' }}><dt>{k.kind}</dt><dd>{k.count}</dd></div>)}
            </dl>
            <div className="lm-muted me-note">Starts are green, waypoints blue. Select one in the outliner or the viewport and move it (movePoint).</div>
          </div>
        )}
        {p.tab === 'map' && (
          <>
            {p.status && (
              <div className="me-card">
                <div className="me-card-title">Map</div>
                <dl className="me-kv">
                  <dt>Package</dt><dd className="lm-mono">{p.status.path}</dd>
                  <dt>Scene</dt><dd className="lm-mono">{p.sceneNs}_GEO.ZED</dd>
                  {p.countList.map(c => <div key={c.kind} style={{ display: 'contents' }}><dt>{c.kind}</dt><dd>{c.count}</dd></div>)}
                  <dt>Changed</dt><dd className="lm-mono">{p.status.dirty.join(', ') || '—'}</dd>
                </dl>
              </div>
            )}
            <div className="me-card">
              <div className="me-card-title">Session</div>
              <div className="me-note">Edits stay in the session until you save. File › Save copy… writes a .ZDB; File › Export memory-card pack… writes a CDB.</div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function ItemTab(p: PropsPanelProps) {
  const { single, selectedPoint, pointInfo, selection } = p;
  if (selectedPoint && pointInfo) {
    return (
      <>
        <div className="me-card">
          <div className="me-card-title">Point {pointInfo.name}</div>
          <dl className="me-kv">
            <dt>Kind</dt><dd>{pointInfo.kind}</dd>
            <dt>Team</dt><dd>{pointInfo.team}</dd>
            <dt>Cell</dt><dd>{pointInfo.cellX}, {pointInfo.cellZ}</dd>
          </dl>
          <div className="me-xform">
            <span>X / Z</span>
            <NumField value={fmt(pointInfo.x)} step={1} onCommit={v => p.onMovePoint(pointInfo.name, 'x', v)} />
            <input className="me-num" type="number" disabled value="—" title="Points have no height (on the ground)" readOnly />
            <NumField value={fmt(pointInfo.z)} step={1} onCommit={v => p.onMovePoint(pointInfo.name, 'z', v)} />
          </div>
          <div className="lm-muted me-note">AIMAPS cells snap to the grid; drag with the Move tool (G) or type a position.</div>
        </div>
        <Actions {...p} />
      </>
    );
  }
  if (selection.length === 1 && single) {
    const inst = single;
    const canMove = movable(inst);
    return (
      <>
        <div className="me-card">
          <div className="me-card-title">{inst.kind}</div>
          <dl className="me-kv">
            <dt>Model</dt><dd className="lm-mono">{inst.model}</dd>
            <dt>Instance</dt><dd className="lm-mono">{inst.instance}</dd>
            {inst.geoIndex !== null && <><dt>GEO</dt><dd className="lm-mono">#{inst.geoIndex} {inst.path}</dd></>}
            {inst.clutterIndex !== null && <><dt>Clutter</dt><dd className="lm-mono">#{inst.clutterIndex}</dd></>}
            <dt>Source</dt><dd className="lm-mono">{inst.source ?? 'unresolved'}</dd>
          </dl>
        </div>
        <div className="me-card">
          <div className="me-card-title">Transform (game units)</div>
          <div className="me-xform">
            <span>Position</span>
            {[0, 1, 2].map(k => (
              <NumField key={k} value={fmt(inst.matrix[12 + k])} step={1} disabled={!canMove} onCommit={v => p.onSetField('position', v, k)} />
            ))}
            <span>Yaw °</span>
            <NumField value={fmt(yawOf(inst.matrix))} step={5} disabled={!canMove} onCommit={v => p.onSetField('yaw', v)} />
            <span>Scale</span>
            <NumField value={fmt(scaleOf(inst.matrix))} step={0.1} min={0.01} disabled={!canMove} onCommit={v => p.onSetField('scale', v)} />
          </div>
        </div>
        <Actions {...p} />
      </>
    );
  }
  if (selection.length > 1) {
    return (
      <>
        <div className="me-card">
          <div className="me-card-title">{selection.length} instances</div>
          <div className="me-note">{p.selectionSummary}</div>
        </div>
        {p.movableCount > 0 && (
          <div className="me-card">
            <div className="me-card-title">Transform all ({p.movableCount} movable)</div>
            <div className="me-xform">
              {/* Each commit applies once, then the field resets. */}
              <span>Move by</span>
              {[0, 1, 2].map(k => (
                <NumField key={k} reset value="0" step={1} disabled={p.busy} title={`Offset along ${'XYZ'[k]}`} onCommit={v => p.onEditSelection('offset', v, k)} />
              ))}
              <span>Turn by °</span>
              <NumField reset value="0" step={15} disabled={p.busy} onCommit={v => p.onEditSelection('turn', v)} />
              <span>Scale</span>
              <NumField reset value="" step={0.1} min={0.01} disabled={p.busy} title="Set every selected instance to this scale" onCommit={v => p.onEditSelection('scale', v)} />
            </div>
          </div>
        )}
        <Actions {...p} />
      </>
    );
  }
  return (
    <>
      <div className="me-empty">Click a prop, clutter or point to select it (Shift adds, drag for a box). Double-click opens its model in the Model Editor.</div>
    </>
  );
}

function Actions(p: PropsPanelProps) {
  if (!p.selection.length) { return null; }
  return (
    <div className="me-card">
      <div className="me-card-title">Edit</div>
      <div className="me-actions">
        <button className="lm-tool-btn" disabled={p.busy || !p.movableCount} onClick={p.onDuplicate}>Duplicate (Shift+D)</button>
        <button className="lm-tool-btn lm-danger-btn" disabled={p.busy || !p.movableCount} onClick={p.onDelete}>Delete (X)</button>
        <button className="lm-tool-btn" disabled={p.busy || !p.movableCount} onClick={p.onDrop}>Drop to ground</button>
        <button className="lm-tool-btn" disabled={p.selection.length !== 1 || !p.single?.source} onClick={p.onOpenModel}>Open model…</button>
      </div>
    </div>
  );
}

function PlaceTab(p: PropsPanelProps) {
  return (
    <div className="me-card mp-place">
      <div className="me-card-title">Place clutter</div>
      <div className="me-actions">
        <button className="lm-tool-btn" disabled={p.busy || !p.status || !!p.status.readOnly} onClick={p.onImport} title="Copy a prop, vehicle, weapon or character from another package into this one">Import from package…</button>
      </div>
      <input className="me-search" type="text" placeholder="Filter models…" value={p.libQuery} onChange={e => p.setLibQuery(e.target.value)} />
      <div className="mp-lib">
        {p.library.map(m => (
          <button key={m.archive + ':' + m.name} type="button" className={`me-row${p.placeModel?.name === m.name ? ' active' : ''}`}
            onClick={() => p.setPlaceModel(m)} title={m.archive}>
            <span className="me-row-name">{m.name}</span><span className="me-row-meta">{m.kind}</span>
          </button>
        ))}
      </div>
      <label className="me-field me-field-wide">Yaw ° <NumField value={String(p.placeYaw)} step={15} onCommit={v => p.setPlaceYaw(+v || 0)} /></label>
      <label className="me-field me-field-wide">Scale <NumField value={String(p.placeScale)} step={0.1} min={0.01} onCommit={v => p.setPlaceScale(+v || 1)} /></label>
      <label className="me-toggle"><input type="checkbox" checked={p.randomYaw} onChange={e => p.setRandomYaw(e.target.checked)} /> Random yaw</label>
      <div className="lm-muted me-note">With the Place tool (P), click the ground to add a clutter placement of the chosen model (addClutter).</div>
    </div>
  );
}
