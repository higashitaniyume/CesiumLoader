using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Core;
using Core.Net;
using Core.Scene;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.protocol;
using Tools;
using UnityEngine;

namespace HubBridgeMod
{
    /// <summary>
    /// 主线程状态机：负责发现 Toys 本地桥接端点、上报登录/房间状态、
    /// 执行自动进房命令，并在 GameFinishS2C 到达时抓取即时战报与回放 ID。
    /// </summary>
    internal sealed class HubBridgeSession : IDisposable
    {
        private const int MaxEventQueueSize = 32;
        private const int MaxEventRetries = 8;

        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly string _modsDir;
        private readonly Queue<BridgeEvent> _eventQueue = new Queue<BridgeEvent>();
        private readonly HashSet<string> _handledCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedFinishKeys = new HashSet<string>(StringComparer.Ordinal);

        private BridgeEndpointInfo _endpoint;
        private DateTime _lastEndpointMtimeUtc;
        private float _nextEndpointCheckAt;
        private float _nextHookCheckAt;
        private float _nextProbeAt;
        private float _nextHeartbeatAt;

        private HttpRequestHandle _inflightRequest;
        private BridgeEvent _inflightEvent;
        private bool _inflightIsHeartbeat;

        private bool _lastLoggedIn;
        private long _lastUid;
        private string _lastRegion = "";
        private string _lastRoomSignature = "";
        private long _lastRoomId;
        private bool _joinInFlight;
        private bool _stopped;
        private bool _warnedHttpMissing;

        // GameFinishS2C 回调包装（与 SDK HookSlot 同款的幂等保护，确保不断游戏链）
        private object _hookedRpcOwner;
        private GameFinishS2CRPC.OnGameFinishS2CServerDelegate _wrappedGameFinish;
        private GameFinishS2CRPC.OnGameFinishS2CServerDelegate _originalGameFinish;

        public HubBridgeSession(string modsDir)
        {
            _modsDir = modsDir ?? "";
        }

        public void Tick(float now, HubBridgeConfig cfg, ModLogger log)
        {
            if (_stopped) return;

            if (cfg == null || !cfg.Enabled)
            {
                AbortInflight();
                return;
            }

            if (now >= _nextHookCheckAt)
            {
                _nextHookCheckAt = now + 1.0f;
                EnsureGameFinishHook(log);
            }

            if (now >= _nextEndpointCheckAt)
            {
                _nextEndpointCheckAt = now + 1.0f;
                RefreshEndpoint(cfg);
            }

            if (now >= _nextProbeAt)
            {
                _nextProbeAt = now + (cfg.PollIntervalMs / 1000f);
                ProbeRuntimeState(log);
            }

            PollInflight(now, cfg, log);

            if (_inflightRequest == null && _endpoint != null)
            {
                if (_eventQueue.Count > 0)
                {
                    SendNextEvent(cfg, log);
                }
                else if (now >= _nextHeartbeatAt)
                {
                    _nextHeartbeatAt = now + (cfg.PollIntervalMs / 1000f);
                    SendHeartbeat(cfg, log);
                }
            }
        }

        public void OnSettingsChanged(HubBridgeConfig cfg)
        {
            _nextEndpointCheckAt = 0f;
            _nextHeartbeatAt = 0f;
            if (cfg == null || !cfg.Enabled)
            {
                AbortInflight();
            }
        }

        public void Dispose()
        {
            _stopped = true;
            AbortInflight();
            RestoreGameFinishHook();
        }

        private void RefreshEndpoint(HubBridgeConfig cfg)
        {
            string path = HubBridgeProtocol.ResolveEndpointPath(cfg.EndpointPath, _modsDir);
            if (string.IsNullOrEmpty(path))
            {
                _endpoint = null;
                _lastEndpointMtimeUtc = DateTime.MinValue;
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    _endpoint = null;
                    _lastEndpointMtimeUtc = DateTime.MinValue;
                    return;
                }

                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (_endpoint != null && mtime == _lastEndpointMtimeUtc)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                BridgeEndpointInfo parsed;
                string err;
                if (BridgeEndpointInfo.TryParse(json, out parsed, out err))
                {
                    _endpoint = parsed;
                    _lastEndpointMtimeUtc = mtime;
                }
                else
                {
                    _endpoint = null;
                }
            }
            catch
            {
                _endpoint = null;
            }
        }

        private void ProbeRuntimeState(ModLogger log)
        {
            bool isLoggedIn;
            long uid;
            string region;
            string sceneName;
            bool isInRoom;
            bool inMatchTeam;
            BridgeRoomSnapshot room;

            ReadRuntimeSnapshot(out isLoggedIn, out uid, out region, out sceneName, out isInRoom, out inMatchTeam, out room);

            if (isLoggedIn != _lastLoggedIn || uid != _lastUid || !string.Equals(region, _lastRegion, StringComparison.Ordinal))
            {
                _lastLoggedIn = isLoggedIn;
                _lastUid = uid;
                _lastRegion = region;

                if (isLoggedIn && uid > 0)
                {
                    EnqueueEvent("login_ready", new Dictionary<string, object>
                    {
                        { "uid", uid },
                        { "region", region },
                        { "scene", sceneName },
                    });
                }
            }

            string signature = HubBridgeProtocol.BuildRoomSignature(room);
            if (!string.Equals(signature, _lastRoomSignature, StringComparison.Ordinal))
            {
                long prevRoomId = _lastRoomId;
                _lastRoomSignature = signature;
                _lastRoomId = room != null ? room.RoomId : 0;

                if (room != null && room.RoomId > 0)
                {
                    if (room.IsMaster)
                    {
                        string eventType = prevRoomId != room.RoomId ? "room_created" : "room_updated";
                        EnqueueEvent(eventType, room.ToDictionary());
                        if (eventType == "room_created")
                        {
                            log?.Info("检测到房主已创建游戏房间: roomId=" + room.RoomId);
                        }
                    }
                    else if (prevRoomId != room.RoomId)
                    {
                        EnqueueEvent("room_entered", room.ToDictionary());
                    }
                }
                else if (prevRoomId > 0)
                {
                    EnqueueEvent("room_left", new Dictionary<string, object>
                    {
                        { "gameRoomId", prevRoomId },
                    });
                }
            }
        }

        private static void ReadRuntimeSnapshot(
            out bool isLoggedIn,
            out long uid,
            out string region,
            out string sceneName,
            out bool isInRoom,
            out bool inMatchTeam,
            out BridgeRoomSnapshot room)
        {
            isLoggedIn = false;
            uid = 0;
            region = "";
            sceneName = "None";
            isInRoom = false;
            inMatchTeam = false;
            room = null;

            try
            {
                isLoggedIn = SimpleSingletonProvider<GameManager>.hasInstance
                    && SimpleSingletonProvider<GameManager>.inst != null
                    && SimpleSingletonProvider<GameManager>.inst.IsLogined;
            }
            catch { isLoggedIn = false; }

            try
            {
                if (SimpleSingletonProvider<SceneManager>.hasInstance && SimpleSingletonProvider<SceneManager>.inst != null)
                {
                    sceneName = SimpleSingletonProvider<SceneManager>.inst.currentType.Value.ToString();
                }
            }
            catch { sceneName = "None"; }

            string authName = "";
            try
            {
                var token = LoginServiceHelper.connectToken;
                if (token != null)
                {
                    authName = token.Auth.ToString();
                }
            }
            catch { authName = ""; }

            string rawZone = "";
            try { rawZone = Convert.ToString(StaticGlobalData.GAME_ZONE_TYPE); }
            catch { rawZone = ""; }

            region = HubBridgeProtocol.MapRegion(authName, rawZone);

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;
            GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
            if (gm == null) return;

            try
            {
                if (gm.account != null)
                {
                    uid = gm.account.GetPlayerID();
                }
            }
            catch { uid = 0; }

            try
            {
                inMatchTeam = gm.match != null && gm.match.matchData != null && gm.match.matchData.InTeam;
            }
            catch { inMatchTeam = false; }

            try
            {
                if (gm.room != null && gm.room.IsInRoom && gm.room.curRoomInfo != null)
                {
                    isInRoom = true;
                    RoomInfo info = gm.room.curRoomInfo;
                    if (info.Id > 0)
                    {
                        BridgeRoomSnapshot snap = new BridgeRoomSnapshot();
                        snap.RoomId = info.Id;
                        snap.Pwd = info.Pwd ?? "";
                        snap.RoomServerId = info.info != null ? info.info.RoomServerId : 0;
                        snap.MapId = info.MapId;
                        snap.MapType = info.MapType;
                        snap.MasterId = info.MasterId;
                        snap.IsMaster = uid > 0 && info.MasterId == uid;
                        snap.State = gm.room.roomController != null ? gm.room.roomController.roomStateType.ToString() : "";
                        snap.PlayerCount = info.Players != null ? info.Players.Count : 0;
                        room = snap;
                    }
                }
            }
            catch { }
        }

        private void EnsureGameFinishHook(ModLogger log)
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;
                NetManager netInst = MonoSingletonProvider<NetManager>.inst;
                RPCMsgManager rpcMgr = netInst != null ? netInst.RPC : null;
                GameFinishS2CRPC rpc = rpcMgr != null ? rpcMgr.GameFinishS2C : null;
                if (rpc == null) return;

                var current = rpc.OnGameFinishS2CServerCallBackAsync;
                if (current == null) return;
                if (_wrappedGameFinish != null && ReferenceEquals(current, _wrappedGameFinish)) return;
                if (_wrappedGameFinish != null && ReferenceEquals(_hookedRpcOwner, rpc) && !ReferenceEquals(current, _originalGameFinish))
                {
                    // 其它订阅者在我们的包装之上又套了一层，不再重复包裹。
                    return;
                }

                GameFinishS2CRPC.OnGameFinishS2CServerDelegate original = current;
                GameFinishS2CRPC.OnGameFinishS2CServerDelegate wrapped = (model, errId, isDispatch) =>
                {
                    ModCallbackGuard.Run("HubBridge/GameFinish", () => CaptureGameFinish(model, errId, log));
                    if (original != null) return original(model, errId, isDispatch);
                    return UniTask.CompletedTask;
                };

                _originalGameFinish = original;
                _wrappedGameFinish = wrapped;
                _hookedRpcOwner = rpc;
                rpc.OnGameFinishS2CServerCallBackAsync = wrapped;
            }
            catch (Exception ex)
            {
                log?.Warn("挂载 GameFinishS2C 回调失败: " + ex.Message);
            }
        }

        private void RestoreGameFinishHook()
        {
            try
            {
                GameFinishS2CRPC rpc = _hookedRpcOwner as GameFinishS2CRPC;
                if (rpc != null && _wrappedGameFinish != null && ReferenceEquals(rpc.OnGameFinishS2CServerCallBackAsync, _wrappedGameFinish))
                {
                    rpc.OnGameFinishS2CServerCallBackAsync = _originalGameFinish;
                }
            }
            catch { }

            _wrappedGameFinish = null;
            _originalGameFinish = null;
            _hookedRpcOwner = null;
        }

        private void CaptureGameFinish(GameFinishS2C model, int errId, ModLogger log)
        {
            if (errId != 0 || model == null) return;
            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;
            GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
            if (gm == null) return;
            if (gm.replay != null && gm.replay.Session != null && gm.replay.Session.IsReplay) return;

            string replayId = (model.ReplayId ?? "").Trim();
            RoomInfo roomInfo = gm.room != null ? gm.room.curRoomInfo : null;
            long roomId = model.RoomId != 0 ? model.RoomId : (roomInfo != null ? roomInfo.Id : 0);
            string dedupKey = (!string.IsNullOrEmpty(replayId) ? replayId : roomId.ToString()) + ":" + model.FinishTime;
            if (!_reportedFinishKeys.Add(dedupKey)) return;

            bool isLoggedIn;
            long selfUid;
            string region;
            string sceneName;
            bool isInRoom;
            bool inMatchTeam;
            BridgeRoomSnapshot unusedRoom;
            ReadRuntimeSnapshot(out isLoggedIn, out selfUid, out region, out sceneName, out isInRoom, out inMatchTeam, out unusedRoom);

            int mapType = model.MapType != 0 ? model.MapType : (roomInfo != null ? roomInfo.MapType : 0);
            int mapId = roomInfo != null ? roomInfo.MapId : 0;
            int round = roomInfo != null ? roomInfo.Round : 0;

            List<Dictionary<string, object>> players = BuildFinishPlayers(gm, model, roomInfo, mapType);

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "region", region },
                { "uid", selfUid },
                { "replayId", replayId },
                { "gameRoomId", roomId },
                { "winnerId", model.Winer },
                { "finishTime", model.FinishTime },
                { "mapId", mapId },
                { "mapType", mapType },
                { "round", round },
                { "gameVersion", model.Version ?? "" },
                { "players", players },
            };

            EnqueueEvent("game_finished", payload);
            _nextHeartbeatAt = 0f;
            log?.Info("捕获对局结束: replayId=" + replayId + ", roomId=" + roomId);
        }

        private static List<Dictionary<string, object>> BuildFinishPlayers(
            GameLogicManager gm,
            GameFinishS2C model,
            RoomInfo roomInfo,
            int mapType)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            List<BattlePlayerData> battlePlayers = gm.battle != null ? gm.battle.PlayerDatas : null;

            if (battlePlayers != null && battlePlayers.Count > 0)
            {
                Dictionary<long, int> fallbackRanks = ComputeFallbackRanks(battlePlayers, model.Winer, mapType);

                for (int i = 0; i < battlePlayers.Count; i++)
                {
                    BattlePlayerData pd = battlePlayers[i];
                    if (pd == null || pd.player == null) continue;

                    long pid = pd.player.Id;
                    int rank;
                    if (model.Rank != null && model.Rank.TryGetValue(pid, out int serverRank))
                    {
                        rank = serverRank;
                    }
                    else if (!fallbackRanks.TryGetValue(pid, out rank))
                    {
                        rank = pd.rank;
                    }

                    int heroId = pd.player.Hero != null ? pd.player.Hero.HeroId : 0;
                    int level = pd.Property != null && pd.Property.level != null
                        ? pd.Property.level.Value
                        : (pd.player.Hero != null ? pd.player.Hero.Lv : 0);
                    int gold = pd.Property != null && pd.Property.gold != null
                        ? pd.Property.gold.Value
                        : (pd.player.Hero != null ? pd.player.Hero.Gold : 0);
                    int hp = pd.Property != null && pd.Property.HP != null
                        ? pd.Property.HP.Value
                        : (pd.player.Hero != null ? pd.player.Hero.Hp : 0);
                    int maxHp = pd.Property != null
                        ? pd.Property.maxHP
                        : (pd.player.Hero != null ? pd.player.Hero.MaxHp : 0);

                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        { "playerId", pid },
                        { "nick", pd.player.GetNick() ?? "" },
                        { "slot", pd.player.Slot },
                        { "accountLevel", pd.player.Level },
                        { "teamId", pd.player.TeamId },
                        { "isBot", pd.player.IsBot },
                        { "offline", pd.player.OffLine },
                        { "heroId", heroId },
                        { "level", level },
                        { "gold", gold },
                        { "hp", hp },
                        { "maxHp", maxHp },
                        { "rank", rank },
                    };

                    if (model.NewAchieve != null && model.NewAchieve.TryGetValue(pid, out PlayerFinishAchieve achieve) && achieve != null)
                    {
                        row["killCount"] = achieve.KillCount;
                        row["totalDamage"] = achieve.TotalDamage;
                        row["totalInjured"] = achieve.TotalInjured;
                        row["totalDie"] = achieve.TotalDie;
                        row["treatmentScore"] = achieve.TreatmentScore;
                        row["movePoint"] = achieve.MovePoint;
                        row["totalGold"] = achieve.TotalGold;
                        row["pveTransferGold"] = achieve.PveTransferGold;
                        row["pvpResultGold"] = achieve.PvpResultGold;
                        row["pvpResultLv"] = achieve.PvpResultLv;
                        row["winCount"] = achieve.WinCount;
                        row["pkDamageMax"] = achieve.PkDamageMax;
                        row["finalKillBoss"] = achieve.FinalKillBoss;
                    }

                    result.Add(row);
                }
            }
            else if (roomInfo != null && roomInfo.Players != null)
            {
                for (int i = 0; i < roomInfo.Players.Count; i++)
                {
                    RoomPlayer rp = roomInfo.Players[i];
                    if (rp == null) continue;
                    long pid = rp.Id;
                    int rank = model.Rank != null && model.Rank.TryGetValue(pid, out int serverRank) ? serverRank : 0;
                    result.Add(new Dictionary<string, object>
                    {
                        { "playerId", pid },
                        { "nick", rp.GetNick() ?? "" },
                        { "slot", rp.Slot },
                        { "accountLevel", rp.Level },
                        { "teamId", rp.TeamId },
                        { "isBot", rp.IsBot },
                        { "offline", rp.OffLine },
                        { "heroId", rp.Hero != null ? rp.Hero.HeroId : 0 },
                        { "level", rp.Hero != null ? rp.Hero.Lv : 0 },
                        { "gold", rp.Hero != null ? rp.Hero.Gold : 0 },
                        { "hp", rp.Hero != null ? rp.Hero.Hp : 0 },
                        { "maxHp", rp.Hero != null ? rp.Hero.MaxHp : 0 },
                        { "rank", rank },
                    });
                }
            }

            return result;
        }

        private static Dictionary<long, int> ComputeFallbackRanks(List<BattlePlayerData> players, long winTeamId, int mapType)
        {
            Dictionary<long, int> ranks = new Dictionary<long, int>();
            if (players == null || players.Count == 0) return ranks;

            if (BattleConfig.IsAsymmetricalBattle(mapType) || BattleConfig.IsLuckyStarBattle(mapType))
            {
                for (int i = 0; i < players.Count; i++)
                {
                    BattlePlayerData pd = players[i];
                    if (pd?.player == null) continue;
                    ranks[pd.player.Id] = pd.player.TeamId != winTeamId ? 1 : 0;
                }
                return ranks;
            }

            List<BattlePlayerData> sorted = new List<BattlePlayerData>(players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i]?.player != null) sorted.Add(players[i]);
            }

            sorted.Sort((a, b) =>
            {
                int al = a.Property != null && a.Property.level != null ? a.Property.level.Value : 0;
                int bl = b.Property != null && b.Property.level != null ? b.Property.level.Value : 0;
                int cmp = bl.CompareTo(al);
                if (cmp != 0) return cmp;
                int ag = a.Property != null && a.Property.gold != null ? a.Property.gold.Value : 0;
                int bg = b.Property != null && b.Property.gold != null ? b.Property.gold.Value : 0;
                return bg.CompareTo(ag);
            });

            int rank = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                ranks[sorted[i].player.Id] = rank;
                if (i < sorted.Count - 1)
                {
                    int curLv = sorted[i].Property?.level?.Value ?? 0;
                    int nextLv = sorted[i + 1].Property?.level?.Value ?? 0;
                    int curGold = sorted[i].Property?.gold?.Value ?? 0;
                    int nextGold = sorted[i + 1].Property?.gold?.Value ?? 0;
                    if (curLv != nextLv || curGold != nextGold) rank++;
                }
            }
            return ranks;
        }

        private void ExecuteCommands(List<BridgeCommand> commands, ModLogger log)
        {
            if (commands == null || commands.Count == 0) return;

            for (int i = 0; i < commands.Count; i++)
            {
                BridgeCommand cmd = commands[i];
                if (cmd == null || string.IsNullOrEmpty(cmd.CommandId)) continue;
                if (!_handledCommandIds.Add(cmd.CommandId)) continue;

                if (string.Equals(cmd.Action, "join_room", StringComparison.Ordinal))
                {
                    HandleJoinRoomCommand(cmd, log);
                }
            }
        }

        private void HandleJoinRoomCommand(BridgeCommand cmd, ModLogger log)
        {
            if (_joinInFlight)
            {
                EnqueueEvent("join_failed", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "errorCode", "join_in_flight" },
                    { "message", "已有进房请求正在处理中，请稍候。" },
                });
                return;
            }

            bool isLoggedIn;
            long uid;
            string region;
            string sceneName;
            bool isInRoom;
            bool inMatchTeam;
            BridgeRoomSnapshot room;
            ReadRuntimeSnapshot(out isLoggedIn, out uid, out region, out sceneName, out isInRoom, out inMatchTeam, out room);

            long currentRoomId = room != null ? room.RoomId : 0;
            string errorCode;
            string reason;
            bool eligible = HubBridgeProtocol.EvaluateJoinEligibility(
                isLoggedIn, uid, sceneName, isInRoom, currentRoomId, inMatchTeam, cmd.GameRoomId,
                out errorCode, out reason);

            if (!eligible)
            {
                EnqueueEvent("join_failed", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "errorCode", errorCode },
                    { "message", reason },
                });
                return;
            }

            if (errorCode == "already_in_target_room")
            {
                EnqueueEvent("room_joined", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "roomServerId", room != null ? room.RoomServerId : cmd.RoomServerId },
                });
                return;
            }

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance)
            {
                EnqueueEvent("join_failed", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "errorCode", "game_not_ready" },
                    { "message", "游戏逻辑尚未就绪。" },
                });
                return;
            }

            GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
            if (gm?.room == null || gm.account == null || !gm.account.StartGameLicense())
            {
                EnqueueEvent("join_failed", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "errorCode", "license_not_ready" },
                    { "message", "游戏登录许可尚未就绪。" },
                });
                return;
            }

            _joinInFlight = true;
            log?.Info("正在执行自动进房: gameRoomId=" + cmd.GameRoomId + ", serverId=" + cmd.RoomServerId);

            try
            {
                gm.room.RequestJoinRoomC2S(cmd.GameRoomId, cmd.GamePwd ?? "", cmd.RoomServerId).OnFinished.AddOnce(result =>
                {
                    if (_stopped) return;
                    _joinInFlight = false;
                    if (result != null && result.errId == 0)
                    {
                        EnqueueEvent("room_joined", new Dictionary<string, object>
                        {
                            { "commandId", cmd.CommandId },
                            { "gameRoomId", cmd.GameRoomId },
                            { "roomServerId", cmd.RoomServerId },
                        });
                        log?.Info("自动进房成功: gameRoomId=" + cmd.GameRoomId);
                    }
                    else
                    {
                        int errId = result != null ? result.errId : -1;
                        EnqueueEvent("join_failed", new Dictionary<string, object>
                        {
                            { "commandId", cmd.CommandId },
                            { "gameRoomId", cmd.GameRoomId },
                            { "errorCode", "rpc_error_" + errId },
                            { "errId", errId },
                            { "message", "游戏返回加入房间失败（错误码 " + errId + "）。" },
                        });
                        log?.Warn("自动进房失败: gameRoomId=" + cmd.GameRoomId + ", errId=" + errId);
                    }
                });
            }
            catch (Exception ex)
            {
                _joinInFlight = false;
                EnqueueEvent("join_failed", new Dictionary<string, object>
                {
                    { "commandId", cmd.CommandId },
                    { "gameRoomId", cmd.GameRoomId },
                    { "errorCode", "exception" },
                    { "message", ex.Message ?? "join_exception" },
                });
            }
        }

        private void SendHeartbeat(HubBridgeConfig cfg, ModLogger log)
        {
            if (_endpoint == null) return;

            bool isLoggedIn;
            long uid;
            string region;
            string sceneName;
            bool isInRoom;
            bool inMatchTeam;
            BridgeRoomSnapshot room;
            ReadRuntimeSnapshot(out isLoggedIn, out uid, out region, out sceneName, out isInRoom, out inMatchTeam, out room);

            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "protocol", HubBridgeProtocol.ProtocolVersion },
                { "sessionId", _sessionId },
                { "atMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                { "isLoggedIn", isLoggedIn },
                { "uid", uid },
                { "region", region },
                { "scene", sceneName },
                { "isInRoom", isInRoom },
                { "inMatchTeam", inMatchTeam },
                { "joinInFlight", _joinInFlight },
                { "room", room != null ? room.ToDictionary() : null },
            };

            string url = _endpoint.BaseUrl() + "/api/bridge/heartbeat";
            string err;
            HttpRequestHandle handle = UnityHttpClient.StartPostJson(url, CesiumJson.Serialize(body), _endpoint.Token, cfg.RequestTimeoutSeconds, out err);
            if (handle == null)
            {
                if (!_warnedHttpMissing)
                {
                    _warnedHttpMissing = true;
                    log?.Warn("HubBridgeMod 本地请求初始化失败: " + (err ?? "unknown"));
                }
                return;
            }

            _inflightRequest = handle;
            _inflightEvent = null;
            _inflightIsHeartbeat = true;
        }

        private void SendNextEvent(HubBridgeConfig cfg, ModLogger log)
        {
            if (_endpoint == null || _eventQueue.Count == 0) return;
            BridgeEvent ev = _eventQueue.Peek();
            string url = _endpoint.BaseUrl() + "/api/bridge/events";
            string err;
            HttpRequestHandle handle = UnityHttpClient.StartPostJson(url, ev.ToJson(_sessionId), _endpoint.Token, cfg.RequestTimeoutSeconds, out err);
            if (handle == null)
            {
                if (!_warnedHttpMissing)
                {
                    _warnedHttpMissing = true;
                    log?.Warn("HubBridgeMod 事件上报初始化失败: " + (err ?? "unknown"));
                }
                return;
            }

            _inflightRequest = handle;
            _inflightEvent = ev;
            _inflightIsHeartbeat = false;
        }

        private void PollInflight(float now, HubBridgeConfig cfg, ModLogger log)
        {
            if (_inflightRequest == null) return;
            if (!_inflightRequest.Poll()) return;

            HttpRequestHandle finished = _inflightRequest;
            BridgeEvent sentEvent = _inflightEvent;
            bool wasHeartbeat = _inflightIsHeartbeat;
            _inflightRequest = null;
            _inflightEvent = null;
            _inflightIsHeartbeat = false;

            try
            {
                bool ok = !finished.IsNetworkError && finished.StatusCode >= 200 && finished.StatusCode < 300;
                if (wasHeartbeat)
                {
                    if (ok)
                    {
                        List<BridgeCommand> cmds = HubBridgeProtocol.ParseCommandsResponse(finished.ResponseText);
                        ExecuteCommands(cmds, log);
                    }
                    else if (finished.StatusCode == 401 || finished.IsNetworkError)
                    {
                        _lastEndpointMtimeUtc = DateTime.MinValue;
                        _nextEndpointCheckAt = now + 1.0f;
                    }
                }
                else if (sentEvent != null)
                {
                    if (ok || (finished.StatusCode >= 400 && finished.StatusCode < 500 && finished.StatusCode != 401 && finished.StatusCode != 429))
                    {
                        if (_eventQueue.Count > 0 && ReferenceEquals(_eventQueue.Peek(), sentEvent))
                        {
                            _eventQueue.Dequeue();
                        }
                    }
                    else
                    {
                        sentEvent.Attempts++;
                        if (sentEvent.Attempts >= MaxEventRetries)
                        {
                            if (_eventQueue.Count > 0 && ReferenceEquals(_eventQueue.Peek(), sentEvent))
                            {
                                _eventQueue.Dequeue();
                            }
                            log?.Warn("HubBridgeMod 事件重传超限已丢弃: " + sentEvent.Type);
                        }
                        else if (finished.StatusCode == 401 || finished.IsNetworkError)
                        {
                            _lastEndpointMtimeUtc = DateTime.MinValue;
                            _nextEndpointCheckAt = now + 1.0f;
                        }
                    }
                }
            }
            finally
            {
                finished.Dispose();
            }
        }

        private void EnqueueEvent(string type, Dictionary<string, object> data)
        {
            while (_eventQueue.Count >= MaxEventQueueSize)
            {
                _eventQueue.Dequeue();
            }
            string id = Guid.NewGuid().ToString("N");
            long atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _eventQueue.Enqueue(new BridgeEvent(id, type, atMs, data));
        }

        private void AbortInflight()
        {
            if (_inflightRequest != null)
            {
                try { _inflightRequest.Dispose(); }
                catch { }
                _inflightRequest = null;
                _inflightEvent = null;
                _inflightIsHeartbeat = false;
            }
        }
    }
}
