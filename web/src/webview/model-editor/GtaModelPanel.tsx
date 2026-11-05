import { useRef, useState } from 'react';
import type { GtaModelInfo } from './asset-client';

function Coordinates({ name, values }: { name: string; values: number[] }) {
  return <div className="me-actions">{values.map((value, i) => {
    const axis = 'XYZ'[i];
    return <label key={axis}>{axis} <input className="me-num" type="number" step="any" required
      aria-label={name + ' ' + axis} name={name + i} defaultValue={value} /></label>;
  })}</div>;
}

const vector = (data: FormData, key: string) => [0, 1, 2].map(i => Number(data.get(key + i)));

function TextureField({ texture, capacity, choices, preview }: {
  texture: string; capacity: number; choices: GtaModelInfo['textureChoices']; preview: (name: string) => Promise<string>;
}) {
  const [draft, setDraft] = useState(texture);
  const [image, setImage] = useState('');
  const request = useRef(0);
  const choose = async (name: string) => {
    const current = ++request.current;
    setDraft(name);
    setImage('');
    if (!name) return;
    try {
      const url = await preview(name);
      if (request.current === current) setImage(url);
    } catch { /* A replacement can refer to a texture supplied separately. */ }
  };
  return <>
    {choices.length > 0 && <label className="me-field me-field-wide">Available textures
      <select value={choices.some(c => c.name === draft) ? draft : ''} onChange={e => void choose(e.target.value)}>
        <option value="" disabled>Choose a texture…</option>
        {choices.map(c => <option key={c.name} value={c.name}>{c.name} ({c.dictionary})</option>)}
      </select>
    </label>}
    <label className="me-field me-field-wide">Texture name <input className="me-input" name="texture"
      value={draft} onChange={e => { ++request.current; setDraft(e.target.value); setImage(''); }} maxLength={capacity} required={!!texture} /></label>
    {image && <img className="me-thumb" src={image} alt={draft} />}
  </>;
}

export function GtaModelPanel({ info, missing, busy, edit, preview }: {
  info: GtaModelInfo; missing: string[]; busy: boolean; edit: (op: Record<string, unknown>, label: string) => void;
  preview: (name: string) => Promise<string>;
}) {
  const [frameId, setFrame] = useState(0), [geometry, setGeometry] = useState(0), [matId, setMaterial] = useState(0);
  const frame = info.frames[frameId], materials = info.materials.filter(x => x.geometry === geometry);
  const mat = materials[matId];
  const missingNames = new Set(missing.map(t => t.slice(t.lastIndexOf('/') + 1).toLowerCase()));
  const unresolved = info.materials.filter(m => m.texture && missingNames.has(m.texture.toLowerCase()));
  return <>
    {unresolved.length > 0 && <label className="me-field me-field-wide">Missing textures
      <select value="" onChange={e => {
        const picked = unresolved[Number(e.target.value)];
        setGeometry(picked.geometry);
        setMaterial(info.materials.filter(m => m.geometry === picked.geometry).indexOf(picked));
      }}>
        <option value="" disabled>Choose a material to repair…</option>
        {unresolved.map((m, i) => <option key={i} value={i}>{m.texture} · geometry {m.geometry}, material {m.index}</option>)}
      </select>
    </label>}
    {frame && <div className="me-card">
      <label className="me-field me-field-wide">Frame <select value={frameId} onChange={e => setFrame(Number(e.target.value))}>
        {info.frames.map(f => <option value={f.index} key={f.index}>{f.index} {f.name}</option>)}
      </select></label>
      <form key={frameId + ':' + frame.position.join(',')} onSubmit={e => {
        e.preventDefault(); edit({ op: 'gtaFrame', frame: frameId, position: vector(new FormData(e.currentTarget), 'Position') }, 'Move DFF frame');
      }}>
        <label>Position relative to parent (GTA axes)</label>
        <Coordinates name="Position" values={frame.position} />
        <button className="lm-tool-btn" disabled={busy}>Apply frame position</button>
      </form>
    </div>}
    {info.geometryCount > 0 && <div className="me-card">
      <label className="me-field me-field-wide">Geometry <select value={geometry} onChange={e => { setGeometry(Number(e.target.value)); setMaterial(0); }}>
        {Array.from({ length: info.geometryCount }, (_, i) => <option key={i} value={i}>{i}</option>)}
      </select></label>
      <form key={geometry} onSubmit={e => {
        e.preventDefault();
        const form = e.currentTarget, data = new FormData(form);
        edit({ op: 'gtaGeometry', geometry, factors: vector(data, 'Scale'), offset: vector(data, 'Offset') }, 'Transform DFF geometry');
      }}>
        <label>Scale about the geometry origin</label><Coordinates name="Scale" values={[1, 1, 1]} />
        <label>Offset in GTA units (Z up)</label><Coordinates name="Offset" values={[0, 0, 0]} />
        <button className="lm-tool-btn" disabled={busy}>Apply geometry transform</button>
      </form>
    </div>}
    {mat && <div className="me-card">
      <label className="me-field me-field-wide">Material <select value={matId} onChange={e => setMaterial(Number(e.target.value))}>
        {materials.map((m, i) => <option key={m.index} value={i}>{m.index} {m.texture}</option>)}
      </select></label>
      <form key={[geometry, matId, mat.color, mat.texture].join(':')} onSubmit={e => {
        e.preventDefault();
        const data = new FormData(e.currentTarget);
        edit({ op: 'gtaMaterial', geometry, material: mat.index,
          color: Array.from('RGBA', k => Number(data.get(k))), ...(data.get('texture') ? { texture: String(data.get('texture')) } : {}) }, 'Edit DFF material');
      }}>
        {mat.texture !== null && <TextureField texture={mat.texture} capacity={mat.textureCapacity} choices={info.textureChoices} preview={preview} />}
        <div className="me-actions">{Array.from('RGBA', (k, i) => <label key={k}>{k} <input className="me-num" type="number" required
          name={k} min="0" max="255" step="1" defaultValue={mat.color[i]} /></label>)}</div>
        <button className="lm-tool-btn" disabled={busy}>Apply material</button>
      </form>
    </div>}
  </>;
}
