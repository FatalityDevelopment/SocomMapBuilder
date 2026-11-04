import type { MapInstanceWire, MapPointWire, MapProblem, MapValidation, ModelSummary, SessionStatus } from '../webview/model-editor/asset-client';
import { fmt, movable, scaleOf, sortProblems, yawOf } from '../mapModel';
import { NumField } from './NumField';
import type { MapTool } from '../viewport/mapViewport';
import type { AlignTo, Axis } from '../viewport/arrange';

export interface BrushSettings { radius: number; spacing: number; density: number; slope: number; erase: boolean }

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
  // Align a multi-selection's boxes on an axis, or space them evenly.
  onArrange: (axis: Axis, how: AlignTo | 'distribute') => void;
  // What the Place tool puts down (picked in the Assets panel), and as what.
  placeModel: ModelSummary | null;
  placePrefab: string | null;
  placeAs: 'clutter' | 'prop';
  setPlaceAs: (v: 'clutter' | 'prop') => void;
  tool: MapTool;
  brush: BrushSettings;
  setBrush: (b: BrushSettings) => void;
  onPaintTool: () => void;
  placeYaw: number;
  setPlaceYaw: (v: number) => void;
  placeScale: number;
  setPlaceScale: (v: number) => void;
  randomYaw: boolean;
  setRandomYaw: (on: boolean) => void;
  randomScale: boolean;
  setRandomScale: (on: boolean) => void;
  minPlaceScale: number;
  setMinPlaceScale: (value: number) => void;
  maxPlaceScale: number;
  setMaxPlaceScale: (value: number) => void;
  alignSurface: boolean;
  setAlignSurface: (on: boolean) => void;
  // The map check (ASSET_VALIDATE): its last report, whether it is running, and selecting what a problem is about.
  check: MapValidation | null;
  checking: boolean;
  onCheck: () => void;
  onPickProblem: (problem: MapProblem) => void;
  // The edit history list (Map tab).
  history: React.ReactNode;
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
            <ProblemsCard {...p} />
            <div className="me-card">
              <div className="me-card-title">History</div>
              {p.history}
              <div className="lm-muted me-note">Click a step to go back to just before it (or forward to it). Autosave keeps a recovery copy of unsaved edits every few minutes{p.status?.autosave ? ` (last: ${new Date(p.status.autosave.at).toLocaleTimeString()})` : ''}.</div>
            </div>
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

function ProblemsCard(p: PropsPanelProps) {
  const c = p.check;
  const summary = !c ? 'Not checked yet.'
    : c.problems.length === 0 ? 'No problems found.'
      : (['error', 'warning', 'info'] as const).filter(k => c.counts[k]).map(k => `${c.counts[k]} ${k}${c.counts[k] === 1 ? '' : 's'}`).join(', ');
  return (
    <div className="me-card">
      <div className="me-card-title">Problems</div>
      <div className="mb-problems-head">
        <span className={`me-note${c && !c.ok ? ' mb-problem-error' : ''}`}>{p.checking ? 'Checking…' : summary}</span>
        <button className="lm-tool-btn" disabled={p.checking || !p.status} onClick={p.onCheck}>Check map</button>
      </div>
      {c && c.problems.length > 0 && (
        <ul className="mb-problems">
          {sortProblems(c.problems).map((pr, i) => {
            const target = pr.point || typeof pr.geoIndex === 'number' || typeof pr.clutterIndex === 'number';
            return (
              <li key={i} className={`mb-problem mb-problem-${pr.severity}${target ? ' mb-problem-link' : ''}`} title={pr.code}
                onClick={target ? () => p.onPickProblem(pr) : undefined}>
                <span className="mb-problem-badge">{pr.severity}</span>
                <span>{pr.message}</span>
              </li>
            );
          })}
        </ul>
      )}
      {c && <div className="lm-muted me-note">{c.instances} instances, {c.textures} textures ({(c.textureBytes / 1024).toFixed(0)} KB).</div>}
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
        {p.movableCount > 1 && <ArrangeCard {...p} />}
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

function ArrangeCard(p: PropsPanelProps) {
  const axes: Array<{ axis: Axis; label: string }> = [{ axis: 0, label: 'X' }, { axis: 1, label: 'Y' }, { axis: 2, label: 'Z' }];
  return (
    <div className="me-card">
      <div className="me-card-title">Align and distribute</div>
      <div className="mb-arrange">
        {axes.map(({ axis, label }) => (
          <div key={axis} className="mb-arrange-row">
            <span>{label}</span>
            <button className="lm-tool-btn" disabled={p.busy} onClick={() => p.onArrange(axis, 'min')} title={`Line up the lowest ${label} of every box`}>Min</button>
            <button className="lm-tool-btn" disabled={p.busy} onClick={() => p.onArrange(axis, 'center')} title={`Line up the centres on ${label}`}>Centre</button>
            <button className="lm-tool-btn" disabled={p.busy} onClick={() => p.onArrange(axis, 'max')} title={`Line up the highest ${label} of every box`}>Max</button>
            <button className="lm-tool-btn" disabled={p.busy || p.movableCount < 3} onClick={() => p.onArrange(axis, 'distribute')} title={`Space the centres evenly along ${label} (the outer two stay)`}>Spread</button>
          </div>
        ))}
      </div>
    </div>
  );
}

function PlaceTab(p: PropsPanelProps) {
  const b = p.brush;
  const what = p.placePrefab ? `prefab ${p.placePrefab}` : p.placeModel ? p.placeModel.name : null;
  return (
    <>
      <div className="me-card mp-place">
        <div className="me-card-title">Place</div>
        <div className="me-note">{what ? <>Placing <b className="lm-mono">{what}</b>. Click a surface with the Place tool (P), or drag a tile from Assets onto the map.</>
          : 'Pick a model or prefab in the Assets panel on the left.'}</div>
        {!p.placePrefab && (
          <div className="me-seg" title="A clutter placement (CLUTTER.ZAR: light, many) or a GEO prop (a scene node, like the map's buildings)">
            <button className={p.placeAs === 'clutter' ? 'on' : ''} onClick={() => p.setPlaceAs('clutter')}>As clutter</button>
            <button className={p.placeAs === 'prop' ? 'on' : ''} onClick={() => p.setPlaceAs('prop')}>As prop</button>
          </div>
        )}
        <label className="me-field me-field-wide">Yaw ° <NumField value={String(p.placeYaw)} step={15} onCommit={v => p.setPlaceYaw(+v || 0)} /></label>
        <label className="me-field me-field-wide">Scale <NumField value={String(p.placeScale)} step={0.1} min={0.01} onCommit={v => p.setPlaceScale(+v || 1)} /></label>
        <label className="me-toggle"><input type="checkbox" checked={p.randomYaw} onChange={e => p.setRandomYaw(e.target.checked)} /> Random yaw</label>
        <label className="me-toggle"><input type="checkbox" checked={p.randomScale} onChange={e => p.setRandomScale(e.target.checked)} /> Random scale</label>
        {p.randomScale && <>
          <label className="me-field me-field-wide">Scale from <NumField value={String(p.minPlaceScale)} min={0.01} step={0.01} onCommit={v => p.setMinPlaceScale(Number(v))} /></label>
          <label className="me-field me-field-wide">Scale to <NumField value={String(p.maxPlaceScale)} min={0.01} step={0.01} onCommit={v => p.setMaxPlaceScale(Number(v))} /></label>
        </>}
        <label className="me-toggle"><input type="checkbox" checked={p.alignSurface} onChange={e => p.setAlignSurface(e.target.checked)} /> Align to surface</label>
        <div className="lm-muted me-note">The preview uses the next placement's rotation and scale. Escape cancels. Unlock and show the Clutter layer to place clutter.</div>
      </div>
      <div className="me-card mp-place">
        <div className="me-card-title">Clutter brush</div>
        {p.tool !== 'paint'
          ? <button className="lm-tool-btn" onClick={p.onPaintTool}>Paint clutter (B)</button>
          : <div className="me-note">Drag over the ground to paint {p.placeModel ? <b className="lm-mono">{p.placeModel.name}</b> : 'the model picked in Assets'}; hold Ctrl (or tick Erase) to remove {p.placeModel ? 'it' : 'clutter'}. One stroke is one undo.</div>}
        <label className="me-field me-field-wide">Radius <NumField value={String(b.radius)} min={0.5} step={1} onCommit={v => p.setBrush({ ...b, radius: Math.max(0.5, +v || b.radius) })} /></label>
        <label className="me-field me-field-wide" title="The gap kept between placements, and from clutter of the same model already there">Spacing <NumField value={String(b.spacing)} min={0} step={0.5} onCommit={v => p.setBrush({ ...b, spacing: Math.max(0, +v) })} /></label>
        <label className="me-field me-field-wide" title="Placements tried per dab (spacing and slope then thin them)">Density <NumField value={String(b.density)} min={1} step={1} onCommit={v => p.setBrush({ ...b, density: Math.min(100, Math.max(1, Math.round(+v) || 1)) })} /></label>
        <label className="me-field me-field-wide" title="Steepest ground the brush paints on (degrees)">Max slope ° <NumField value={String(b.slope)} min={0} step={5} onCommit={v => p.setBrush({ ...b, slope: Math.min(90, Math.max(0, +v)) })} /></label>
        <label className="me-toggle"><input type="checkbox" checked={b.erase} onChange={e => p.setBrush({ ...b, erase: e.target.checked })} /> Erase</label>
        <div className="lm-muted me-note">Rotation and scale come from the settings above (random yaw and scale make it look natural); Align to surface tilts each one with the ground.</div>
      </div>
    </>
  );
}
