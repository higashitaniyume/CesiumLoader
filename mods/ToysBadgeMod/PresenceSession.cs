using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using GameLogic;
using Tools;
using UnityEngine;

namespace ToysBadgeMod
{
    internal sealed class MatchSnapshot
    {
        public string Region;
        public string WatchCode;
        public string Nick;
        public int HeroId;

        public MatchSnapshot()
        {
            Region = "cn";
            WatchCode = "";
            Nick = "";
            HeroId = 0;
        }
    }

    internal sealed class PresenceSession : IDisposable
    {
        private const string ModVersion = "1.0.0";
        private const string UserAgent = "AstralParty.Toys Mod/1.0.0 (ToysBadgeMod)";
        private const float ProbeIntervalSeconds = 0.5f;

        private float _nextProbeAt;
        private float _nextSyncAt;
        private bool _forceImmediateSync;

        private string _activeRegion = "";
        private string _activeWatchCode = "";
        private string _activeNick = "";
        private int _activeHeroId;
        private string _activeClientId = "";
        private bool _sharedWithActiveChatMod;

        private string _token = "";
        private long _epoch = -1;
        private int _joinCount;
        private int _consecutiveFailures;
        private bool _haltRetriesForMatch;
        private bool _warnedHttpSupportMissing;
        private bool _warnedMatchError;
        private readonly List<RoomMember> _members = new List<RoomMember>();

        private HttpRequestHandle _inflightJoin;
        private readonly List<HttpRequestHandle> _inflightLeaves = new List<HttpRequestHandle>();

        public bool HasActiveMatch
        {
            get { return !string.IsNullOrEmpty(_activeWatchCode); }
        }

        public void OnConnectionSettingsChanged(ToysBadgeConfig cfg)
        {
            if (cfg == null || !cfg.Enabled || string.IsNullOrEmpty(cfg.ServiceUrl))
            {
                LeaveCurrentRoom(cfg, false);
                return;
            }

            if (!string.IsNullOrEmpty(_activeWatchCode))
            {
                bool shared;
                string newCid = ResolveEffectiveClientId(cfg, out shared);
                if (!string.Equals(newCid, _activeClientId, StringComparison.Ordinal))
                {
                    LeaveCurrentRoom(cfg, false);
                }
                else
                {
                    _sharedWithActiveChatMod = shared;
                    _forceImmediateSync = true;
                    _nextSyncAt = 0f;
                }
            }
        }

        public void Tick(float now, ToysBadgeConfig cfg)
        {
            PollLeaves();
            PollInflightJoin(now, cfg);

            if (cfg == null || !cfg.Enabled || string.IsNullOrEmpty(cfg.ServiceUrl))
            {
                if (!string.IsNullOrEmpty(_activeWatchCode))
                {
                    LeaveCurrentRoom(cfg, false);
                }
                return;
            }

            if (now >= _nextProbeAt)
            {
                _nextProbeAt = now + ProbeIntervalSeconds;
                MatchSnapshot snap;
                if (!ProbeMatch(out snap))
                {
                    if (!string.IsNullOrEmpty(_activeWatchCode))
                    {
                        LeaveCurrentRoom(cfg, true);
                    }
                    return;
                }

                if (!string.Equals(snap.Region, _activeRegion, StringComparison.Ordinal)
                    || !string.Equals(snap.WatchCode, _activeWatchCode, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(_activeWatchCode))
                    {
                        LeaveCurrentRoom(cfg, true);
                    }
                    EnterMatch(now, snap, cfg);
                }
                else
                {
                    _activeNick = snap.Nick;
                    _activeHeroId = snap.HeroId;
                }
            }

            if (string.IsNullOrEmpty(_activeWatchCode) || _haltRetriesForMatch)
            {
                return;
            }

            if (_inflightJoin == null && (_forceImmediateSync || now >= _nextSyncAt))
            {
                _forceImmediateSync = false;
                StartJoin(now, cfg);
            }
        }

        public bool IsPlayerMatched(long playerId, string rawNick, bool isSelf, bool showSelfBadge, bool showOfflineMembers)
        {
            if (string.IsNullOrEmpty(_activeWatchCode)) return false;

            if (isSelf)
            {
                if (!showSelfBadge) return false;
                for (int i = 0; i < _members.Count; i++)
                {
                    RoomMember m = _members[i];
                    if (m.Self)
                    {
                        return m.Online || showOfflineMembers;
                    }
                }
                return !string.IsNullOrEmpty(_token);
            }

            string cleanNick = PresenceProtocol.SanitizeNick(rawNick);
            if (string.IsNullOrEmpty(cleanNick)) return false;

            for (int i = 0; i < _members.Count; i++)
            {
                RoomMember m = _members[i];
                if (m.Self) continue;
                if (!m.Online && !showOfflineMembers) continue;
                if (string.Equals(m.NormalizedNick, cleanNick, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            for (int i = 0; i < _members.Count; i++)
            {
                RoomMember m = _members[i];
                if (m.Self) continue;
                if (!m.Online && !showOfflineMembers) continue;
                if (string.Equals(m.NormalizedNick, cleanNick, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_inflightJoin != null)
            {
                _inflightJoin.Dispose();
                _inflightJoin = null;
            }

            for (int i = 0; i < _inflightLeaves.Count; i++)
            {
                if (_inflightLeaves[i] != null)
                {
                    _inflightLeaves[i].Dispose();
                }
            }
            _inflightLeaves.Clear();

            _members.Clear();
            _activeWatchCode = "";
            _activeRegion = "";
            _token = "";
        }

        public void Shutdown(ToysBadgeConfig cfg)
        {
            if (!string.IsNullOrEmpty(_activeWatchCode))
            {
                LeaveCurrentRoom(cfg, false);
            }
            Dispose();
        }

        private void EnterMatch(float now, MatchSnapshot snap, ToysBadgeConfig cfg)
        {
            bool shared;
            string cid = ResolveEffectiveClientId(cfg, out shared);

            _activeRegion = snap.Region;
            _activeWatchCode = snap.WatchCode;
            _activeNick = snap.Nick;
            _activeHeroId = snap.HeroId;
            _activeClientId = cid;
            _sharedWithActiveChatMod = shared;

            _token = "";
            _epoch = -1;
            _joinCount = 0;
            _consecutiveFailures = 0;
            _haltRetriesForMatch = false;
            _warnedMatchError = false;
            _members.Clear();

            _forceImmediateSync = true;
            _nextSyncAt = now;
        }

        private void LeaveCurrentRoom(ToysBadgeConfig cfg, bool matchEnded)
        {
            string url = null;
            string body = null;
            string region = _activeRegion;

            bool shouldSendLeave = cfg != null
                && !string.IsNullOrEmpty(cfg.ServiceUrl)
                && !string.IsNullOrEmpty(_activeWatchCode)
                && !string.IsNullOrEmpty(_token)
                && (!_sharedWithActiveChatMod || matchEnded);

            if (shouldSendLeave)
            {
                url = PresenceProtocol.BuildLeaveUrl(cfg.ServiceUrl, _activeWatchCode);
                body = PresenceProtocol.BuildLeaveBody(region, _token);
            }

            if (_inflightJoin != null)
            {
                _inflightJoin.Dispose();
                _inflightJoin = null;
            }

            _activeRegion = "";
            _activeWatchCode = "";
            _activeNick = "";
            _activeHeroId = 0;
            _activeClientId = "";
            _sharedWithActiveChatMod = false;
            _token = "";
            _epoch = -1;
            _joinCount = 0;
            _consecutiveFailures = 0;
            _haltRetriesForMatch = false;
            _warnedMatchError = false;
            _forceImmediateSync = false;
            _members.Clear();

            if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(body))
            {
                string err;
                int timeout = cfg != null ? cfg.TimeoutSeconds : 15;
                HttpRequestHandle leaveHandle = UnityHttpClient.StartPostJson(url, body, region, timeout, UserAgent, out err);
                if (leaveHandle != null)
                {
                    _inflightLeaves.Add(leaveHandle);
                }
            }
        }

        private void StartJoin(float now, ToysBadgeConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.ServiceUrl)) return;

            bool shared;
            string effectiveCid = ResolveEffectiveClientId(cfg, out shared);
            _activeClientId = effectiveCid;
            _sharedWithActiveChatMod = shared;

            string url = PresenceProtocol.BuildJoinUrl(cfg.ServiceUrl, _activeWatchCode);
            string body = PresenceProtocol.BuildJoinBody(
                _activeRegion,
                _activeNick,
                _activeClientId,
                ModVersion,
                _activeHeroId);

            string err;
            HttpRequestHandle handle = UnityHttpClient.StartPostJson(
                url,
                body,
                _activeRegion,
                cfg.TimeoutSeconds,
                UserAgent,
                out err);

            if (handle == null)
            {
                if (!_warnedHttpSupportMissing)
                {
                    _warnedHttpSupportMissing = true;
                    SdkLog.Warn("ToysBadgeMod", "HTTP 请求初始化失败: " + (err ?? "unknown"));
                }
                ScheduleBackoff(now);
                return;
            }

            _inflightJoin = handle;
        }

        private void PollInflightJoin(float now, ToysBadgeConfig cfg)
        {
            if (_inflightJoin == null) return;
            if (!_inflightJoin.Poll()) return;

            HttpRequestHandle done = _inflightJoin;
            _inflightJoin = null;

            try
            {
                if (done.IsNetworkError || done.StatusCode <= 0)
                {
                    ScheduleBackoff(now);
                    return;
                }

                if (done.StatusCode == 200)
                {
                    JoinResponse parsed = PresenceProtocol.ParseJoinResponse(done.ResponseText);
                    if (!string.IsNullOrEmpty(parsed.Token))
                    {
                        _token = parsed.Token;
                    }
                    if (parsed.Epoch > 0)
                    {
                        _epoch = parsed.Epoch;
                    }

                    _members.Clear();
                    for (int i = 0; i < parsed.Members.Count; i++)
                    {
                        _members.Add(parsed.Members[i]);
                    }

                    _consecutiveFailures = 0;
                    _joinCount++;
                    ScheduleNextSuccessSync(now, cfg, parsed);
                    return;
                }

                JoinResponse errResp = PresenceProtocol.ParseJoinResponse(done.ResponseText);
                string code = errResp != null ? (errResp.ErrorCode ?? "") : "";
                if (done.StatusCode == 403
                    || string.Equals(code, "banned", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "bad_code", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "bad_nick", StringComparison.OrdinalIgnoreCase))
                {
                    _haltRetriesForMatch = true;
                    if (!_warnedMatchError)
                    {
                        _warnedMatchError = true;
                        SdkLog.Warn("ToysBadgeMod", "房间加入被服务端拒绝，本局暂停重试: HTTP "
                            + done.StatusCode + (string.IsNullOrEmpty(code) ? "" : " (" + code + ")"));
                    }
                    return;
                }

                ScheduleBackoff(now);
            }
            catch (Exception ex)
            {
                if (!_warnedMatchError)
                {
                    _warnedMatchError = true;
                    SdkLog.Warn("ToysBadgeMod", "解析房间状态失败: " + ex.Message);
                }
                ScheduleBackoff(now);
            }
            finally
            {
                done.Dispose();
            }
        }

        private void ScheduleNextSuccessSync(float now, ToysBadgeConfig cfg, JoinResponse res)
        {
            int interval;
            if (_joinCount <= 1)
            {
                interval = 6;
            }
            else if (_joinCount == 2 && res != null && res.OnlineCount < 4)
            {
                interval = 10;
            }
            else
            {
                int steady = cfg != null ? cfg.RefreshSeconds : 25;
                if (res != null && res.PollWaitSeconds >= 15 && res.PollWaitSeconds < steady)
                {
                    steady = res.PollWaitSeconds;
                }
                if (steady < 15) steady = 15;
                if (steady > 55) steady = 55;
                interval = steady;
            }
            _nextSyncAt = now + interval;
        }

        private void ScheduleBackoff(float now)
        {
            _consecutiveFailures++;
            int delay = _consecutiveFailures * 30;
            if (delay < 30) delay = 30;
            if (delay > 120) delay = 120;
            _nextSyncAt = now + delay;
        }

        private void PollLeaves()
        {
            for (int i = _inflightLeaves.Count - 1; i >= 0; i--)
            {
                HttpRequestHandle h = _inflightLeaves[i];
                if (h == null || h.Poll())
                {
                    if (h != null) h.Dispose();
                    _inflightLeaves.RemoveAt(i);
                }
            }
        }

        private static string ResolveEffectiveClientId(ToysBadgeConfig cfg, out bool sharedWithActiveChatMod)
        {
            sharedWithActiveChatMod = false;
            if (cfg == null) return "";

            if (cfg.ShareChatClientId)
            {
                try
                {
                    string loaderRoot = CesiumLoader.SDK.IO.Path.LoaderRootDirectory;
                    if (!string.IsNullOrEmpty(loaderRoot))
                    {
                        string chatCfgPath = Path.Combine(
                            Path.Combine(Path.Combine(loaderRoot, "mods"), "InMatchChatMod"),
                            "config.json");
                        if (File.Exists(chatCfgPath))
                        {
                            string text = File.ReadAllText(chatCfgPath);
                            object parsed;
                            if (CesiumJson.TryDeserialize(text, out parsed))
                            {
                                IDictionary<string, object> map = parsed as IDictionary<string, object>;
                                if (map != null)
                                {
                                    bool chatEnabled = ReadBoolField(map, "Enabled", true);
                                    string chatUrl = ReadStringField(map, "ServiceUrl", "");
                                    string chatCid = ReadStringField(map, "ClientId", "").Trim();
                                    if (chatEnabled
                                        && !string.IsNullOrWhiteSpace(chatUrl)
                                        && PresenceProtocol.IsValidClientId(chatCid))
                                    {
                                        sharedWithActiveChatMod = true;
                                        return chatCid;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return cfg.ClientId ?? "";
        }

        private static bool ReadBoolField(IDictionary<string, object> map, string key, bool fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            if (val is bool) return (bool)val;
            return fallback;
        }

        private static string ReadStringField(IDictionary<string, object> map, string key, string fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            string s = val as string;
            return s != null ? s : fallback;
        }

        private static bool ProbeMatch(out MatchSnapshot snap)
        {
            snap = null;
            try
            {
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return false;
                GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null) return false;

                ReplayLogic replay = gm.replay;
                if (replay != null && replay.Session != null && replay.Session.IsReplay)
                {
                    return false;
                }

                RoomLogic room = gm.room;
                if (room == null) return false;

                RoomController controller = room.roomController;
                if (controller == null || controller.roomStateType != RoomStateType.RUNNING)
                {
                    return false;
                }

                RoomInfo info = room.curRoomInfo;
                if (info == null || info.IsSingleGameModel())
                {
                    return false;
                }

                string watchCode = info.watchCode;
                if (!PresenceProtocol.IsValidWatchCode(watchCode))
                {
                    return false;
                }

                string rawZone = "";
                try { rawZone = Convert.ToString(StaticGlobalData.GAME_ZONE_TYPE); }
                catch { rawZone = ""; }
                string region = PresenceProtocol.MapRegion(rawZone);

                string nick = "";
                if (gm.account != null)
                {
                    try { nick = PresenceProtocol.SanitizeNick(gm.account.GetName()); }
                    catch { nick = ""; }
                }

                if (string.IsNullOrEmpty(nick) && gm.battle != null && gm.battle.PlayerDatas != null && gm.account != null)
                {
                    for (int i = 0; i < gm.battle.PlayerDatas.Count; i++)
                    {
                        BattlePlayerData pd = gm.battle.PlayerDatas[i];
                        if (pd == null || pd.player == null) continue;
                        if (gm.account.IsSelf(pd.player.Id))
                        {
                            nick = PresenceProtocol.SanitizeNick(pd.player.GetNick());
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(nick))
                {
                    return false;
                }

                int heroId = 0;
                try
                {
                    RoomPlayer selfInfo = info.GetSelfInfo();
                    if (selfInfo != null && selfInfo.characterConfig != null)
                    {
                        heroId = selfInfo.characterConfig.Id;
                    }
                }
                catch { heroId = 0; }

                snap = new MatchSnapshot();
                snap.Region = region;
                snap.WatchCode = watchCode;
                snap.Nick = nick;
                snap.HeroId = heroId;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
