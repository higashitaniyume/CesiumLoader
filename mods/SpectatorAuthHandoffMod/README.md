# SpectatorAuthHandoffMod (optional)

Explicit own-account CN game authentication handoff to a local standalone client. This mod is optional; it is not added to the builtin package, deployment scripts, or solution. It never automatically exports, logs token material, reads BnSdk credentials/cache/logs, attempts login, or makes network requests.

## Build and manual installation

From the CesiumLoader repository root:

```powershell
dotnet build mods/SpectatorAuthHandoffMod/SpectatorAuthHandoffMod.csproj -c Release
dotnet run --project tests/SpectatorAuthHandoffSmoke/SpectatorAuthHandoffSmoke.csproj -c Release
```

Build artifact: `mods/SpectatorAuthHandoffMod/bin/Release/netstandard2.0/SpectatorAuthHandoffMod.dll` (optional matching PDB). To install, copy only this DLL and the source `SpectatorAuthHandoffMod.json` manifest into the existing game's `AstralParty_ModLoader/mods/SpectatorAuthHandoffMod/`. Do not copy or deploy an SDK DLL from this project. An existing compatible CesiumLoader SDK 2.3.2 installation is required. Building does not deploy or start anything.

The mod uses the same netstandard2.0 game assembly references as SpectatorBridgeMod. Its SDK project reference has `Private=false`, as do its game references. Assembly and sidecar permissions are `ReadGameState | FileSystem` = **257** (1 + 256); SDK version is **2.3.2**. It does not need `GameActions` or networking permissions.

## Marker and response protocol

`ModBase.Run(..., 30000)` delays startup by 30 seconds. Initialization and polling use the loader's Unity main-thread callbacks. Polling is every 500 ms using `DateTime.UtcNow`, independent of frame-rate and scaled game time.

The separate exchange directory is the parent of `CESIUM_MODS_DIR` plus `auth-handoff`: normally `AstralParty_ModLoader/auth-handoff/`. The standalone client must explicitly create `request.json` here after the mod has initialized. Submit only on an explicit own-account import action. The request contains exactly one literal key and a **32-character lowercase hexadecimal nonce**:

```json
{"nonce":"0123456789abcdef0123456789abcdef"}
```

The shown nonce is an illustrative synthetic value. The client should generate a fresh unpredictable nonce for every request. JSON whitespace is accepted; additional keys, duplicate keys, escaped keys/values, trailing data, uppercase/nonhex/incorrect-length nonces and non-object requests are rejected. The strict shape check supplements CesiumJson, whose parser alone accepts trailing data.

The mod reads and deletes the marker before parsing or accessing the game token. If deletion fails, it captures nothing. Invalid requests, unavailable tokens, invalid endpoint settings, reused nonces, and requests arriving while a response is live are consumed without a response or credential log. The client should treat a missing response as a timeout and require a fresh explicit request. Nonces are remembered for the current mod lifetime; this is not persistent replay protection across restarts.

A successful response is `response.json` with exactly these fields:

| Field | JSON type | Meaning |
| --- | --- | --- |
| `nonce` | string | Exact accepted request nonce |
| `capturedUtc` | string | UTC capture time in round-trip ISO 8601 format |
| `host` | string | `Core.GameSettings.IP` |
| `port` | number | `Core.GameSettings.Port`, integer 1–65535 |
| `clientVersion` | string | `Core.GameSettings.APP_VERSION` |
| `connectRequestBase64` | string | Base64 of the complete protobuf `ConnectC2S` serialized using `Google.Protobuf.MessageExtensions.ToByteArray` |
| `protocolVersion1` | number | `Core.GameSettings.VER1`, resource-version major used for incoming frame compatibility |
| `protocolVersion2` | number | `Core.GameSettings.VER2`, resource-version minor |
| `protocolVersion3` | number | `Core.GameSettings.VER3`, resource-version patch |

Version 0.1.1 adds these actual resource-version fields. The outgoing frame's fixed 1/0/0 bytes are not the compatibility limit for incoming server frames.

**The response is credential-bearing. Base64 is encoding, not encryption.** The whole connect message is copied as serialized protobuf; it can contain other authentication-related fields as well as China authentication. No response example containing authentication bytes is printed here, in the harness output, or in logs.

Only one response can be live. Writing uses `response.json.tmp`, then `File.Delete(response.json)` and `File.Move(tmp, response.json)`. Delete/Move is not a transactional replace guarantee. The client should match the nonce, enforce the capture age, consume immediately, and delete `response.json`. If it chooses persistence, it may immediately convert into a protected local user credential store and then delete the exchange response; persistence and its protection are the client's responsibility.

The mod tracks expiry in a `DateTime` field and deletes the response after 30 seconds on the first available poll. It does not use `FileInfo`, file modification times, FileStream/Flush, or unverified timestamp BCL calls. It deletes response/temp remnants on initialization and attempts deletion on unload. No continuous refresh, request retry, response copy, or token cache is created by the mod.

## Sensitive-file limits

The exchange directory **should be accessible only by the current user**. The mod does not set or audit OS ACLs. Check inherited permissions before importing. Network-share access permissions matter: avoid an exchange directory on a share accessible to others, and consider both share and filesystem permissions.

Response and temp files are temporary but contain raw usable authentication material. The 30-second cleanup is best effort: game suspension, a blocked main thread, crash, forced termination, failed filesystem deletion, or a clock adjustment can extend the exposure. A restart deletes remnants at initialization, which itself happens after the 30-second startup delay. The standalone client should delete immediately; normal deletion is **not secure erasure**. Backups, indexing, synchronization, snapshots, or other processes may retain copies. No encryption claim is made for these exchange files.

## Verified API and caveats

The local compiled `AstralParty.Runtime.dll` confirms public static field `GameLogic.LoginServiceHelper.connectToken` has type `party.protocol.ConnectC2S`. The mod requires `Auth == party.protocol.AuthType.China` and nonnull `China`; null, Dev, and other Auth values are refused. `Core.GameSettings.IP` and `APP_VERSION` are public static strings and `Port` is a public static integer. The real protobuf extension method and all these references are verified by the Release netstandard2.0 build.

The game can retain `connectToken` after a failed login or logout. Presence of a China token does not prove a currently authenticated session or future server validity. Request only after the user has successfully logged in their own account. This mod neither reads SDK state to confirm it nor tests remote authentication. A separate client login may affect the game session; orchestrating login and closing the game are the client's responsibility.

The net8 smoke harness links the actual mod source and actual CesiumJson source against synthetic game/SDK/protobuf stubs. It checks startup delay/cleanup, absence of automatic capture, marker parsing, request consumption, half-second polling, response schema, nonce replay rejection, one live response, tracked expiry, and null/non-China/missing-China rejection. It does not establish HybridCLR runtime behavior or server acceptance. No real credentials are read by the harness and no live game, deployment, or remote login was attempted.
