import type { ViewportStats } from '../webview/zipper-core/viewport-core';
import { useEffect, useRef } from 'react';

interface ViewportViewProps {
  onReady: (canvas: HTMLCanvasElement) => void;
  viewLabel: string;
  sceneNs: string;
  instanceCount: number;
  pointCount: number;
  hasSession: boolean;
  progress: string;
  busy: boolean;
  stats?: ViewportStats | null;
  loading?: boolean;
  warnings?: string[];
  connectionIssue?: string | null;
  onRetry?: () => void;
}

// The 3D canvas and its overlay labels. The viewport class owns the canvas once it is ready.
export function ViewportView({ onReady, viewLabel, sceneNs, instanceCount, pointCount, hasSession, progress, busy, stats, loading, warnings = [], connectionIssue, onRetry }: ViewportViewProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  useEffect(() => { if (canvasRef.current) { onReady(canvasRef.current); } }, [onReady]);
  return (
    <div className="me-viewport">
      <canvas ref={canvasRef} tabIndex={0} />
      <div className="me-vp-label">
        <div>{viewLabel}</div>
        {sceneNs && <div className="lm-muted">{sceneNs} — {instanceCount} instances · {pointCount} points</div>}
        <div className="lm-muted">WASD fly · Q/E down/up · Shift faster</div>
      </div>
      {stats && <div className="mp-vp-stats" aria-label="Viewport statistics">
        {stats.fps} FPS · {stats.calls.toLocaleString()} draw calls · {stats.triangles.toLocaleString()} triangles<br />
        {stats.geometries.toLocaleString()} geometries · {stats.textures.toLocaleString()} textures
      </div>}
      {!hasSession && <div className="me-vp-center">File › Open package… (Ctrl+O)</div>}
      {(warnings.length > 0 || connectionIssue) && <div className="mp-load-notice" role="status">
        {connectionIssue && <p>{connectionIssue}</p>}
        {warnings.length > 0 && <>
          <details><summary>Map loading needs attention ({warnings.length})</summary>
            <ul>{warnings.map((warning, i) => <li key={i}>{warning}</li>)}</ul>
          </details>
          <button type="button" className="lm-tool-btn" disabled={busy || loading} onClick={onRetry}>Retry map loading</button>
        </>}
      </div>}
      {progress && <div className="mp-progress" role="status">{progress}</div>}
      {busy && !loading && <div className="me-vp-busy" role="status">Working…</div>}
    </div>
  );
}
