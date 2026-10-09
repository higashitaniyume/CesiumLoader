# SpectatorApi

Standalone .NET 8 localhost HTTP adapter for the official **PVE spectator** mod's file bridge. It reads only that bridge and never connects to game servers, downloads data, logs in, or accepts game/login credentials. Handcards must come from the client's official PVE spectator path. The API rejects PVP and snapshots that do not explicitly identify official spectating; it cannot prove how a producer obtained its data, so the mod remains responsible for that boundary.

## Build and run

Requires the .NET 8 SDK or later for building and the ASP.NET Core 8 runtime for running the framework-dependent output.

```powershell
dotnet build tools\SpectatorApi\SpectatorApi.csproj -c Release
dotnet tools\SpectatorApi\bin\Release\net8.0\SpectatorApi.dll --bridge "C:\path\AstralParty_ModLoader\spectator"
# Optional: --port 18742 (default)
```

The server binds **127.0.0.1 only**, never wildcard/IPv6 interfaces. `--bridge` is required; only `--bridge` and `--port` are accepted. A cryptographically random 256-bit token is generated for each process and printed locally as `Bearer token: ...`. All endpoints except `/health` require the exact header `Authorization: Bearer <token>`, including GET data. Tokens never go into bridge files. Request logging is disabled; responses have `Cache-Control: no-store`. No CORS permissions are enabled. Stop with Ctrl+C. Do not expose this port through a proxy or tunnel.

For a self-contained Windows executable, if desired:

```powershell
dotnet publish tools\SpectatorApi\SpectatorApi.csproj -c Release -r win-x64 --self-contained true -o tools\SpectatorApi\bin\publish
```

## HTTP contract

JSON properties are case-sensitive. Commands are asynchronous: **202 means accepted/pending, not successful execution**. A single command can be in flight; extra submissions return 409 rather than building a backlog.

| Method and path | Request | Response |
| --- | --- | --- |
| GET `/health` | No token needed | 200 `{"status":"ok"}`; process health only, not bridge readiness |
| GET `/state` | Bearer token | 200 current bridge snapshot, or 503 `{"error":"state_unavailable"}` |
| POST `/watch` | `{"watchCode":"code"}` | 202 `{"id":"<32 lowercase hex>","status":"pending"}`; bridge action `join` |
| POST `/focus` | `{"playerId":"9007199254740993"}` | 202 pending; bridge action `focus`; string preserved exactly |
| POST `/leave` | `{}` or empty body | 202 pending; bridge action `leave` |
| GET `/commands/{id}` | Bearer token; 32 lowercase hex ID | 202 pending; 200 terminal result; 404 unknown; 400 invalid ID |

401 means missing/incorrect bearer token. Invalid JSON or fields return 400; bodies over 4 KiB return 413; bridge I/O failure returns 503; an outstanding command returns 409 `{"error":"command_pending"}`. `/watch` and `/focus` accept exactly their single named string property, reject duplicate/unknown fields, and require 1–128 characters without control characters or leading/trailing whitespace. Numeric IDs are rejected to avoid loss of precision. `/leave` accepts no fields.

Example using a token copied from the local console:

```powershell
$headers = @{ Authorization = 'Bearer <printed-token>' }
Invoke-RestMethod http://127.0.0.1:18742/state -Headers $headers
$command = Invoke-RestMethod http://127.0.0.1:18742/watch -Method Post -Headers $headers -ContentType application/json -Body '{"watchCode":"your-watch-code"}'
Invoke-RestMethod "http://127.0.0.1:18742/commands/$($command.id)" -Headers $headers
```

## File bridge contract

The server creates missing bridge directories. Mod and API must use the same bridge path:

```text
<bridge>/
  state.json                 mod -> API: atomic snapshots
  commands/<id>.json         API -> mod: atomic inputs, consumed sequentially
  results/<id>.json          mod -> API: atomic pending/terminal results
  .api/server.lock           API-owned exclusive process lock
  .api/pending.json          API-owned durable pending-command ledger
```

The API writes a unique adjacent `.tmp` file and publishes using `File.Move` without overwrite. The mod should also publish snapshots and results atomically with an adjacent temporary file and rename/replace. Ignore `.tmp` files; read only `commands/*.json`. The API never removes command inputs or mod results. The mod must consume/remove an input and publish a terminal result when finished; otherwise the API deliberately remains busy.

Command JSON always contains these four properties (irrelevant values are `null`):

```json
{"id":"0d301dd95de04a6a82bff47cd2611256","action":"join","watchCode":"example","playerId":null}
{"id":"741e0514f1e244efbb19174e89c4bddd","action":"focus","watchCode":null,"playerId":"9007199254740993"}
{"id":"c3dcd83f63134b74897c29c8329c50e3","action":"leave","watchCode":null,"playerId":null}
```

A result is a JSON object up to **64 KiB** with the exact corresponding `id`, nonempty string `sessionId`, and `status` equal to `pending`, `done`, `failed`, or `timeout`. Other result fields (for example `error`, `message`) pass through. The terminal statuses are `done`, `failed`, and `timeout`. Interim `pending`, malformed/oversized results, and a mismatched ID **never release the command gate**. There is no API-side deadline that silently permits overlapping execution. Result polling does not itself delete files or acknowledge execution.

```json
{"id":"0d301dd95de04a6a82bff47cd2611256","sessionId":"game-session-123","status":"done"}
```

Snapshot JSON is an object up to **1 MiB**, parsed using `System.Text.Json` with maximum depth 32. Its required freshness/scope fields are:

```json
{
  "sampledUtc":"2026-10-13T08:00:00.1234567Z",
  "sessionId":"game-session-123",
  "mode":"pve",
  "officialSpectating":true,
  "players":[{"playerId":"9007199254740993","cards":[123,456]}]
}
```

`sampledUtc` must be ISO UTC (`Z` or `+00:00`, optional fractional seconds), `sessionId` a nonempty string, `mode` exactly `pve`, and `officialSpectating` exactly `true`. Remaining snapshot fields pass through; the example `players/cards` schema is illustrative and owned by the mod. A snapshot **older than 5 seconds**, more than 1 second in the future, missing, malformed, too large, missing required fields, PVP, or nonobserving is unavailable. The mod samples every half second. In waiting/nonobserving states it publishes `mode:"unavailable"`, `officialSpectating:false`, and empty cards; the API returns 503 without any snapshot/card payload. It never caches and serves old cards when a sample fails. Unavailability and a pending command are distinct states: 503 on `/state` vs 202 on command polling.

The gate is synchronized for simultaneous HTTP requests, locked to one server per bridge, and journaled **before** command publication. The ledger survives API restart even after the mod consumes the input; a matching terminal result is required before a new command. Any remaining `commands/*.json` also blocks submissions. A crash between journal and publication conservatively leaves a blocked ledger; inspect the bridge with the server and mod stopped and resolve the pending operation before removing `.api/pending.json`. Do not delete it to bypass an active command. Server shutdown/restart does not cancel or replay commands. Result files are retained by the mod's policy; clients should poll IDs returned by this API rather than treat it as a historical database.

## Game-free smoke checks

```powershell
pwsh -File tools\SpectatorApi\smoke.ps1
```

The script builds Release, chooses a free localhost port, launches a temporary API, and exercises the real HTTP endpoints using a fake bridge. It covers authentication, fresh/stale/future state, PVP and nonofficial rejection, bounded state/request sizes, malformed/duplicate/unknown inputs, exact large string player IDs, atomic command publication, input consumption, interim and mismatched results, terminal completion, token rotation and pending recovery after restart, and simultaneous submissions against the single-slot gate. It stops every API process it starts in `finally`, including on failure. Fake bridge and local test logs are retained under ignored `.smoke/<unique-id>` for diagnosis; test tokens there are invalid after the process exits. No game, network access beyond localhost, or persistent server is required.
