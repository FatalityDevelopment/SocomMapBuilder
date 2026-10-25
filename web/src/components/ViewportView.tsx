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
}

// The 3D canvas and its overlay labels. The viewport class owns the canvas once it is ready.
export function ViewportView({ onReady, viewLabel, sceneNs, instanceCount, pointCount, hasSession, progress, busy }: ViewportViewProps) {
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
      {!hasSession && <div className="me-vp-center">File › Open package… (Ctrl+O)</div>}
      {progress && <div className="mp-progress">{progress}</div>}
      {busy && <div className="me-vp-busy">Applying…</div>}
    </div>
  );
}
