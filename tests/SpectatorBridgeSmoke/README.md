# Spectator bridge lifecycle smoke harness

Run from the loader repository:

```powershell
dotnet run --project tests/SpectatorBridgeSmoke/SpectatorBridgeSmoke.csproj
```

This console project links the current `mods/SpectatorBridgeMod/ModEntry.cs` directly. It supplies minimal game, SDK, RPC-signal and async-task stubs, with no game DLL or game-directory dependency. Each fixture writes bridge commands, results and snapshots beneath a unique `%TEMP%\SpectatorBridgeSmoke\<guid>` directory. Fixture directories remain available for inspection.

Timeouts and polling are advanced through reflection on the bridge's private `_deadline` and `_next` fields, avoiding real 60-second waits. RPC completion and panel/asset completion are controlled explicitly. The runner returns exit code 1 when any scenario fails.

Coverage includes rejected-search recovery, room-state admission, late query replies, delayed refresh followed by leave, exit timeout command blocking, unload guards, panel timeout cleanup and protection of unrelated rooms. A cross-command regression also checks that an older panel continuation cannot clear a newer search's room.

These are managed lifecycle checks. They do not establish HybridCLR BCL compatibility, Unity main-thread dispatch, server behavior, actual UI loading, hand completeness or networking. Query response stubs install local search room state before firing the callback, matching the inspected official `WatchLogic` ordering. Refresh readiness is installed explicitly before firing refresh completion. Unload checks measure bridge-owned mutations; official game handlers can still act independently of the unloaded mod.
