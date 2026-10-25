// The one transport to the Map Builder server: every page's commands (ASSET_*, ZIPPER_*) are POSTed to
// /api/command. The server answers with CommandResponse; Data holds the result as JSON text.

interface CommandResponse {
  Success: boolean;
  Command?: string;
  Message?: string;
  Error?: string;
  Data?: string;
}

export type CommandFields = Record<string, string | number | boolean | undefined | null>;

export async function command<T>(name: string, fields: CommandFields = {}, session: string | null = null): Promise<T> {
  const res = await fetch('/api/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ Command: name, AssetSession: session ?? undefined, ...fields }),
  });
  if (!(res.headers.get('content-type') ?? '').includes('json')) {
    throw new Error(`${name}: backend answered HTTP ${res.status} (is MapBuilder.Server running?)`);
  }
  const reply = (await res.json()) as CommandResponse;
  if (!reply.Success) { throw new Error(reply.Error ?? `${name} failed`); }
  return (reply.Data ? JSON.parse(reply.Data) : reply.Message) as T;
}

// Some ZIPPER_* commands answer raw text in Data (a base64 node, not JSON).
export async function commandRaw(name: string, fields: CommandFields = {}): Promise<string> {
  const res = await fetch('/api/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ Command: name, ...fields }),
  });
  const reply = (await res.json()) as CommandResponse;
  if (!reply.Success) { throw new Error(reply.Error ?? `${name} failed`); }
  return reply.Data ?? '';
}
