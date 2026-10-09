using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using Cysharp.Threading.Tasks;
using GameLogic;
using Core;
using Tools;
using UI;

[assembly: CesiumLoader.SDK.Manifests.ModManifest("PVE 观战 API 桥接", "0.1.1", "local prototype",
    Permissions = CesiumLoader.SDK.Manifests.ModPermission.ReadGameState | CesiumLoader.SDK.Manifests.ModPermission.GameActions | CesiumLoader.SDK.Manifests.ModPermission.FileSystem,
    SdkVersion = "2.3.2")]

namespace SpectatorBridgeMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new SpectatorBridge(), 30000); }
    }

    // Runs exclusively in ModHost's Unity main-thread callbacks.
    public sealed class SpectatorBridge : ModBase
    {
        private string _root;
        private readonly string _session = Guid.NewGuid().ToString("N");
        private DateTime _next;
        private string _pending;
        private DateTime _deadline;
        private bool _joining;
        private string _searchRoomId;
        private string _joinOwner;
        private string _pendingAction;
        private bool _joinRpcInFlight;
        private bool _refreshInFlight;
        private bool _leaveInFlight;
        private bool _stopped;

        public override void OnUnload()
        {
            _stopped = true;
            if (_root != null) WriteUnavailable("Mod unloaded");
        }
        public override string Version { get { return "0.1.1"; } }

        public override void OnInitialize()
        {
            var mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (string.IsNullOrEmpty(mods)) throw new InvalidOperationException("CESIUM_MODS_DIR missing");
            _root = Path.Combine(Path.GetDirectoryName(mods), "spectator");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "commands"));
            Directory.CreateDirectory(Path.Combine(_root, "results"));
            // A previous game's commands must never execute on startup.
            foreach (var file in Directory.GetFiles(Path.Combine(_root, "commands"), "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (ValidId(id)) Result(id, "failed", "Game session restarted; submit again after login.");
                File.Delete(file);
            }
            Snapshot();
            Log.Info("PVE official spectator bridge ready: " + _root);
        }

        public override void OnUpdate()
        {
            if (_stopped || _root == null || DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                if (_pending != null && DateTime.UtcNow > _deadline)
                {
                    Result(_pending, "timeout", "Official spectating did not become ready within 60 seconds.");
                    _pending = null;
                    _pendingAction = null;
                    if (!_joinRpcInFlight && !_refreshInFlight)
                    { ClearSearchRoom(); _joining = false; }
                    // In-flight RPCs cannot be cancelled; their callbacks settle the join lock.
                }
                ProcessCommand();
                Snapshot();
            }
            catch (Exception e)
            {
                Log.Warn("Spectator bridge: " + e.Message);
                try { WriteUnavailable(e.Message); } catch { }
            }
        }

        private void ProcessCommand()
        {
            if (_pending != null) return;
            var files = Directory.GetFiles(Path.Combine(_root, "commands"), "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0) return;
            var file = files[0];
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id)) { File.Delete(file); return; }
            var text = File.ReadAllText(file);
            File.Delete(file); // Claim before invoking game actions. No automatic replay.
            try
            {
                if (text.Length > 8192) throw new InvalidOperationException("Command too large");
                var cmd = CesiumJson.Deserialize(text) as Dictionary<string, object>;
                if (cmd == null || Str(cmd, "id") != id) throw new InvalidOperationException("Invalid command id");
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.watch == null || gm.account == null) throw new InvalidOperationException("Game not ready");
                var action = Str(cmd, "action");
                if (_leaveInFlight || _joining) throw new InvalidOperationException("Previous official operation is still in flight; wait or restart game");
                if (action == "join")
                {
                    var code = Str(cmd, "watchCode");
                    if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new InvalidOperationException("Invalid watchCode");
                    if (_joining || gm.watch.PlayerIsWatcher()) throw new InvalidOperationException("Already joining/spectating; leave first");
                    var roomState = gm.room?.roomController?.roomStateType.ToString();
                    if (roomState != "NONE")
                        throw new InvalidOperationException("Leave your current room before joining spectating");
                    if (!gm.account.StartGameLicense()) throw new InvalidOperationException("Login/license is not ready");
                    _joinOwner = id;
                    _pending = id; _pendingAction = "join"; _joining = true; _joinRpcInFlight = true;
                    _deadline = DateTime.UtcNow.AddSeconds(60);
                    Result(id, "pending", "Waiting for official join response");
                    gm.watch.RequestWatchJoinRoomC2S(code).OnFinished.AddOnce(result =>
                    {
                        if (_stopped || _joinOwner != id) return;
                        _joinRpcInFlight = false;
                        if (result.errId == 0)
                            _searchRoomId = gm.room?.curRoomInfo?.Id.ToString();
                        if (_pending != id) { ClearSearchRoom(); _joining = false; return; }
                        if (result.errId != 0) { FailJoin(id, "Official join error " + result.errId); return; }
                        EnterBattle(id).Forget();
                    });
                }
                else if (action == "focus")
                {
                    RequirePve(gm);
                    long playerId;
                    if (!long.TryParse(Str(cmd, "playerId"), out playerId) || gm.room.curRoomInfo.GetPlayerById(playerId) == null
                        || gm.battle.GetPlayerDataById(playerId) == null)
                        throw new InvalidOperationException("Unknown playerId");
                    gm.watch.UpdateSubscribePlayer(playerId);
                    gm.watch.SwitchFollow(false); // Same behavior as clicking a player in the official spectator UI.
                    Result(id, "done", "Official spectator focus changed");
                }
                else if (action == "leave")
                {
                    if (!gm.watch.PlayerIsWatcher()) throw new InvalidOperationException("Not spectating");
                    _pending = id; _pendingAction = "leave"; _leaveInFlight = true;
                    _deadline = DateTime.UtcNow.AddSeconds(60);
                    Result(id, "pending", "Waiting for official leave response");
                    gm.watch.RequestWatchExitRoomC2S().OnFinished.AddOnce(result =>
                    {
                        if (_stopped) return;
                        _leaveInFlight = false;
                        if (_pending != id) return;
                        Result(id, result.errId == 0 ? "done" : "failed", "Official leave result " + result.errId);
                        _pending = null; _pendingAction = null;
                    });
                }
                else throw new InvalidOperationException("Unsupported action");
            }
            catch (Exception e)
            {
                if (_pending == id) { _pending = null; _joining = false; }
                Result(id, "failed", e.Message);
            }
        }

        private async UniTaskVoid EnterBattle(string id)
        {
            try
            {
                if (_stopped || _pending != id) return;
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm.room.curRoomInfo;
                if (room == null || room.IsSingleGameModel() || !room.IsPVE())
                    throw new InvalidOperationException("Only official PVE spectating is supported");
                if (room.WatchCount >= StaticGlobalData.ROOM_AUDIENCE_NUMBLIMIT)
                    throw new InvalidOperationException("Spectator slots full");
                if (!gm.match.CheckOperateForMatch()) throw new InvalidOperationException("Official match operation rejected");
                await SimpleSingletonProvider<UIManager>.inst.OpenPanel(UIPanelType.RoomHero);
                if (_stopped || _joinOwner != id) return;
                if (_pending != id) { ClearSearchRoom(); _joining = false; return; }
                await SimpleSingletonProvider<InternalAssetManager>.inst.PreLoadBattleAsset();
                if (_stopped || _joinOwner != id) return;
                if (_pending != id) { ClearSearchRoom(); _joining = false; return; }
                // Same request as WatchLogic, with a tracked completion instead of async-void.
                _refreshInFlight = true;
                Tools.MonoSingletonProvider<Core.Net.NetManager>.inst.RPC.WatchRefreshRoomStateC2S.WatchRefreshRoomStateC2SCall(
                    new party.protocol.WatchRefreshRoomStateC2S { RoomId = room.Id, RoomServerId = room.info.RoomServerId })
                    .OnFinished.AddOnce(result =>
                    {
                        if (_stopped || _joinOwner != id) return;
                        _refreshInFlight = false;
                        if (result.errId != 0)
                        {
                            if (_pending == id) FailJoin(id, "Official refresh error " + result.errId);
                            else { ClearSearchRoom(); _joining = false; }
                        }
                        else if (_pending != id) { _joining = false; _searchRoomId = null; }
                    });
                // Completion is determined by the next ready spectator snapshot, not submission.
            }
            catch (Exception e) { FailJoin(id, e.Message); }
        }

        private void ClearSearchRoom()
        {
            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            // Official code search creates a local room before actually becoming a spectator.
            // Clear only this bridge's matching search room, never an active spectator/player room.
            if (_searchRoomId != null && gm?.room?.curRoomInfo != null
                && gm.room.curRoomInfo.Id.ToString() == _searchRoomId && !gm.watch.PlayerIsWatcher())
                gm.room.ClearRoomInfo();
            _searchRoomId = null;
        }

        private void FailJoin(string id, string error)
        {
            if (_stopped || _joinOwner != id) return;
            ClearSearchRoom();
            if (_pending == id)
            { Result(id, "failed", error); _pending = null; _pendingAction = null; }
            _joining = false;
        }

        private static void RequirePve(GameLogicManager gm)
        {
            if (gm.watch == null || !gm.watch.PlayerIsWatcher() || gm.room?.curRoomInfo == null
                || gm.room.curRoomInfo.IsSingleGameModel() || !gm.room.curRoomInfo.IsPVE()
                || gm.room.roomController.roomStateType.ToString() != "RUNNING")
                throw new InvalidOperationException("Not in official PVE spectating");
        }

        private void Snapshot()
        {
            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            try { RequirePve(gm); }
            catch { WriteUnavailable("Waiting for logged-in official PVE spectating"); return; }
            var pd = gm.battle.GetSelfPlayerData();
            if (pd?.player == null || pd.cardContainer?._HandCards == null)
            { WriteUnavailable("Spectated hand has not loaded"); return; }
            var cards = new List<object>();
            foreach (var card in pd.cardContainer._HandCards)
            {
                if (card == null) continue;
                cards.Add(new Dictionary<string, object> { { "cardId", card.CardId },
                    { "name", card.CardId > 0 ? CesiumLoader.SDK.Gameplay.Names.Card(card.CardId) : null },
                    { "known", card.CardId > 0 }, { "battleCost", card.BattleCost } });
            }
            var state = BaseState();
            state["mode"] = "pve";
            state["officialSpectating"] = true;
            state["roomId"] = gm.room.curRoomInfo.Id.ToString();
            state["playerId"] = pd.player.Id.ToString();
            state["cards"] = cards;
            state["status"] = "ready";
            var roster = new List<object>();
            foreach (var player in gm.room.curRoomInfo.Players)
                roster.Add(new Dictionary<string, object> { { "playerId", player.Id.ToString() }, { "slot", player.Slot } });
            state["players"] = roster;
            AtomicWrite(Path.Combine(_root, "state.json"), CesiumJson.Serialize(state));
            if (_pending != null && _pendingAction == "join" && _joining && !_refreshInFlight)
            { Result(_pending, "done", "Official PVE spectator hand ready"); _pending = null; _pendingAction = null; _joining = false; _searchRoomId = null; }
            else if (_pending == null && _joining && !_joinRpcInFlight && !_refreshInFlight)
            { _joining = false; _searchRoomId = null; }
        }

        private Dictionary<string, object> BaseState()
        {
            return new Dictionary<string, object> { { "sessionId", _session }, { "sampledUtc", DateTime.UtcNow.ToString("o") },
                { "mode", "unavailable" }, { "officialSpectating", false }, { "cards", new List<object>() } };
        }
        private void WriteUnavailable(string reason)
        {
            var state = BaseState(); state["status"] = "unavailable"; state["reason"] = reason;
            AtomicWrite(Path.Combine(_root, "state.json"), CesiumJson.Serialize(state));
        }
        private void Result(string id, string status, string message)
        {
            var result = new Dictionary<string, object> { { "id", id }, { "status", status }, { "message", message }, { "sessionId", _session } };
            AtomicWrite(Path.Combine(_root, "results", id + ".json"), CesiumJson.Serialize(result));
            if (status == "done" && _pending == id && !_joining) _pending = null;
        }
        private static string Str(Dictionary<string, object> obj, string key)
        { object value; return obj.TryGetValue(key, out value) ? value as string : null; }
        private static bool ValidId(string id)
        {
            if (id == null || id.Length != 32) return false;
            foreach (var c in id) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static void AtomicWrite(string path, string text)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            // HybridCLR's verified BCL has no File.Replace; delete/move creates a short read gap.
            // The API treats this gap as unavailable/retry, never as a partial snapshot.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
