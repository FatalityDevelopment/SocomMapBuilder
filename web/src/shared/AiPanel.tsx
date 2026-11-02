import { Icon } from './Icon';
import { aiConnected, aiFollow, aiLog, aiPanelOpen, setAiFollow } from './aiBridge';

// What the AI (an MCP client on this server) is doing: each tool call with its reason, live, newest first.
export function AiPanel() {
  if (!aiPanelOpen()) { return null; }
  const log = [...aiLog()].reverse().slice(0, 12);
  return (
    <div className="mb-ai-panel">
      <div className="mb-ai-head">
        <span className={`mb-ai-dot${aiConnected() ? ' on' : ''}`} title={aiConnected() ? 'Connected to the server' : 'Not connected'} />
        <Icon name="bot" size={14} />
        <span className="mb-ai-title">AI activity</span>
        <label className="mb-ai-follow" title="Switch pages, open packages and frame what the AI edits">
          <input type="checkbox" checked={aiFollow()} onChange={e => setAiFollow(e.target.checked)} /> Follow AI
        </label>
        <button className="mb-ai-close" onClick={() => aiPanelOpen.set(false)} title="Hide (the activity bar's AI button shows it again)">
          <Icon name="x" size={14} />
        </button>
      </div>
      <div className="mb-ai-list">
        {log.length === 0 && (
          <div className="mb-ai-empty">
            No AI activity yet. Connect an MCP client to <code>{location.origin.replace(':5173', ':8770')}/mcp</code>, e.g.
            <code>claude mcp add --transport http mapbuilder http://127.0.0.1:8770/mcp</code>
          </div>
        )}
        {log.map(a => (
          <div key={a.id} className={`mb-ai-row ${a.phase}${a.readOnly ? ' read' : ''}`}>
            <span className="mb-ai-state">{a.phase === 'start' ? <span className="mb-ai-spin" /> : a.phase === 'done' ? '✓' : '✗'}</span>
            <div className="mb-ai-text">
              <div><strong>{a.label}</strong>{a.summary && <span className="mb-ai-summary"> · {a.summary}</span>}{a.ms !== undefined && <span className="mb-ai-ms"> {a.ms} ms</span>}</div>
              {a.reason && <div className="mb-ai-reason">{a.reason}</div>}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
