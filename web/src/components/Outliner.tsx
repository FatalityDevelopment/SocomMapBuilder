import { useMemo, useState } from 'react';
import type { MapInstanceWire, MapPointWire } from '../webview/model-editor/asset-client';
import type { MapLayer } from '../viewport/mapViewport';
import type { ModelGroup } from '../mapModel';

interface OutlinerProps {
  query: string;
  setQuery: (q: string) => void;
  layers: Array<{ id: MapLayer; label: string }>;
  points: MapPointWire[];
  filteredPoints: MapPointWire[];
  selectedPoint: string | null;
  groups: Map<MapLayer, Map<string, ModelGroup>>;
  instances: MapInstanceWire[];
  selection: number[];
  openNodes: Set<string>;
  toggleOpen: (key: string) => void;
  isLayerHidden: (layer: MapLayer) => boolean;
  toggleLayer: (layer: MapLayer) => void;
  isModelHidden: (source: string) => boolean;
  toggleModel: (source: string) => void;
  layerCount: (layer: MapLayer) => number;
  pickGroup: (g: ModelGroup, shift: boolean) => void;
  pickInstance: (i: number, shift: boolean) => void;
  pickPoint: (name: string) => void;
}

const PAGE_ROWS = 300;

function groupsOf(groups: OutlinerProps['groups'], layer: MapLayer): ModelGroup[] {
  return [...(groups.get(layer)?.values() ?? [])].sort((a, b) => a.model.localeCompare(b.model));
}

function instanceLabel(inst: MapInstanceWire): string {
  const p = inst.matrix;
  const id = inst.clutterIndex !== null ? `#${inst.clutterIndex}` : inst.instance;
  return `${id} · ${p[12].toFixed(0)}, ${p[13].toFixed(0)}, ${p[14].toFixed(0)}`;
}

export function Outliner(props: OutlinerProps) {
  const { query, setQuery, layers, points, filteredPoints, selectedPoint, groups, instances, selection, openNodes, toggleOpen } = props;
  const isOpen = (key: string) => openNodes.has(key);
  // Clutter groups run to thousands of rows; look selection up in a set.
  const selected = useMemo(() => new Set(selection), [selection]);
  // Rows shown per open model group, a page at a time.
  const [limits, setLimits] = useState<Map<string, number>>(() => new Map());
  const limitOf = (key: string) => limits.get(key) ?? PAGE_ROWS;
  return (
    <div className="me-outliner">
      <div className="me-panel-title">Outliner</div>
      <input className="me-search" type="text" placeholder="Filter…" value={query} onChange={e => setQuery(e.target.value)} />
      <div className="me-tree">
        {layers.map(layer => (
          <div key={layer.id}>
            <div className="mp-group">
              <button className="mp-twist" onClick={() => toggleOpen('L:' + layer.id)}>{isOpen('L:' + layer.id) ? '▾' : '▸'}</button>
              <button className="me-eye" onClick={() => props.toggleLayer(layer.id)} title={props.isLayerHidden(layer.id) ? 'Show' : 'Hide'}>
                {props.isLayerHidden(layer.id) ? '◌' : '●'}
              </button>
              <span>{layer.label}</span>
              <span className="lm-muted">{layer.id === 'points' ? points.length : props.layerCount(layer.id)}</span>
            </div>
            {isOpen('L:' + layer.id) && (layer.id === 'points' ? (
              filteredPoints.map(p => (
                <div key={p.name} className={`me-tree-row mp-model${selectedPoint === p.name ? ' sel' : ''}`}>
                  <span onClick={() => props.pickPoint(p.name)}>{p.name}</span>
                  <span className="lm-muted">{p.kind}{p.team ? ` · t${p.team}` : ''}</span>
                </div>
              ))
            ) : (
              groupsOf(groups, layer.id).map(g => {
                const key = 'M:' + layer.id + g.source;
                const groupSelected = g.indices.length > 0 && g.indices.every(i => selected.has(i));
                return (
                  <div key={g.source}>
                    <div className={`me-tree-row mp-model${groupSelected ? ' sel' : ''}`}>
                      <button className="mp-twist" onClick={() => toggleOpen(key)}>
                        {g.indices.length > 1 ? (isOpen(key) ? '▾' : '▸') : ''}
                      </button>
                      <button className="me-eye" onClick={() => props.toggleModel(g.source)} title={props.isModelHidden(g.source) ? 'Show' : 'Hide'}>
                        {props.isModelHidden(g.source) ? '◌' : '●'}
                      </button>
                      <span onClick={e => props.pickGroup(g, e.shiftKey)} title={g.source}>{g.model}</span>
                      <span className="lm-muted">{g.indices.length}</span>
                    </div>
                    {g.indices.length > 1 && isOpen(key) && g.indices.slice(0, limitOf(key)).map(i => (
                      <div key={i} className={`me-tree-row mp-inst${selected.has(i) ? ' sel' : ''}`}>
                        <span onClick={e => props.pickInstance(i, e.shiftKey)}>{instanceLabel(instances[i])}</span>
                      </div>
                    ))}
                    {g.indices.length > 1 && isOpen(key) && g.indices.length > limitOf(key) && (
                      <div className="me-tree-row mp-inst">
                        <span className="lm-muted" onClick={() => setLimits(l => new Map(l).set(key, limitOf(key) + PAGE_ROWS))}>
                          Show {Math.min(PAGE_ROWS, g.indices.length - limitOf(key))} more of {g.indices.length - limitOf(key)}…
                        </span>
                      </div>
                    )}
                  </div>
                );
              })
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}
