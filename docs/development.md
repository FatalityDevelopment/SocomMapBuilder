# Development

How to run Map Builder from source with live reload, run the tests, and keep the project's documentation in step with the code.

## Run with live reload

Run the backend and the Vite dev server side by side. The dev server proxies `/api` to the backend and reloads the page on save.

```bash
cd server
dotnet watch
```

```bash
cd web
npm run dev
```

Open [http://localhost:5173](http://localhost:5173). `npm run typecheck` checks the client without building it.

## Tests

Tests use synthetic data, never game files. CI (`.github/workflows/ci.yml`) runs the server and web suites on every push and pull request.

```bash
dotnet test tests/MapBuilder.Tests
```

```bash
cd web
npm test
```

Some server tests also check real packages when you point them at your own files; without them, those tests pass without checking anything. For example, `MB_MAP_ZDB` names a map package such as `M51.ZDB`, and `MAPBUILDER_DISC=E:\ dotnet test --filter Every_disc` round-trips every `.rdr` and splices every ZED / ZAR on the disc.

### Browser checks

Browser regression checks exercise rendering, placement, layer visibility and locks, saved preferences, shortcuts and failed-load recovery with synthetic fixtures. With the backend and the Vite dev server running:

```bash
cd web
npm run test:browser
```

The checks use system Chromium at `/usr/bin/chromium` when available. Otherwise install a Playwright browser with `npx playwright install chromium`, or set `CHROMIUM_PATH` to your Chromium executable.

| Variable | Default | Selects |
|---|---|---|
| `MAP_BUILDER_URL` | `http://127.0.0.1:5173/` | The Vite development address |
| `MAP_BUILDER_MCP_URL` | `http://127.0.0.1:8770/mcp` | The MCP endpoint |

These checks need the Vite server because they also exercise viewport modules directly. They do not need game files and do not modify package files.

## Contributing

- Read the [Roadmap and work tracker](ROADMAP.md) first. **Update it in the same commit as the work**: tick the item, add a one-line note (what changed, which test covers it, anything left over) and rewrite *In progress / next up*.
- The [Handover](HANDOVER.md) records where each wave of work stands and the project's working conventions.
- When a change affects how a format is read or written, update its page in [docs/formats](formats/README.md); each page names the code that reads the format so the two stay in step.
- Never commit game files or test fixtures taken from them.

## Related

- [Architecture](architecture.md)
- [Command reference](command-reference.md)
