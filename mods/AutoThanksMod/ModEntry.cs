using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using Core;
using Core.Net;
using Core.Scene;
using UI;
using GameLogic;
using Tools;
using party.protocol;

namespace AutoThanksMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new AutoThanks()); }
    }

    public sealed class AutoThanks : ModBase
    {
        private struct Notice
        {
            internal long Sender, Recipient, ReceivedTicks;
            internal int Type;
        }

        private readonly object _gate = new object();
        private readonly Queue<Notice> _pending = new Queue<Notice>();
        private readonly ThanksPolicy _policy = new ThanksPolicy();
        private readonly SettlementLikes _likes = new SettlementLikes();
        private SayPhraseNotifyS2CRPC _rpc;
        private SayPhraseNotifyS2CRPC.OnSayPhraseNotifyS2CServerDelegate _original, _wrapped;
        private long _generation;
        private bool _acceptNotices;
        private RoomInfo _sessionRoom;
        private long _sessionAccount;
        private long _nextWarning;
        private bool _enabled = true, _healing = true, _cards = true, _transfers = true;
        private int _cooldown = 2;
        private int _thankDelay = ThanksPolicy.DefaultDelayMilliseconds;
        private bool _autoLike = true;
        private int _likeInterval = LikePolicy.DefaultIntervalMilliseconds;

        public override string Name { get { return "自动感谢"; } }
        public override string Version { get { return "1.1.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description { get { return "队友帮助后延迟发送感谢，游戏结束后按间隔自动点赞其他真人玩家。"; } }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                if (!config.Has("enabled")) config.Set("enabled", true);
                if (!config.Has("thankForHealing")) config.Set("thankForHealing", true);
                if (!config.Has("thankForCards")) config.Set("thankForCards", true);
                if (!config.Has("thankForTransfers")) config.Set("thankForTransfers", true);
                if (!config.Has("cooldownSeconds")) config.Set("cooldownSeconds", 2);
                if (!config.Has("thankDelayMilliseconds")) config.Set("thankDelayMilliseconds", ThanksPolicy.DefaultDelayMilliseconds);
                if (!config.Has("autoLikeOnGameEnd")) config.Set("autoLikeOnGameEnd", true);
                if (!config.Has("likeIntervalMilliseconds")) config.Set("likeIntervalMilliseconds", LikePolicy.DefaultIntervalMilliseconds);
                config.Save();
            }
            ReadConfig();
            Log.Info("自动感谢已初始化：队友帮助后延迟发送感谢，结算时逐个点赞其他真人玩家，回放和观战不发送。");
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("自动感谢配置已热重载: enabled=" + _enabled + ", autoLike=" + _autoLike + ", delay=" + _thankDelay + "ms");
        }

        public override void OnUpdate()
        {
            long now = DateTime.UtcNow.Ticks;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsParticipantContext(gm))
                {
                    ResetSession();
                    return;
                }
                long accountId = gm.account.GetPlayerID(); // IsSelf/GetSelfPlayerData can follow a spectator target.
                var room = gm.room.curRoomInfo;
                var state = gm.room.roomController.roomStateType;
                if (_sessionRoom != null && (!ReferenceEquals(room, _sessionRoom) || accountId != _sessionAccount))
                    ResetSession();
                if (state == RoomStateType.SETTLEMENT)
                {
                    StopListening();
                    _policy.Reset();
                    // Only a previously observed real RUNNING battle can arm automatic likes.
                    // Keep this guard in addition to IsReplay/IsWatcher; settlement alone must never arm them.
                    if (_enabled && _autoLike && ReferenceEquals(room, _sessionRoom) && accountId == _sessionAccount)
                    {
                        var panel = SimpleSingletonProvider<UIManager>.inst.currentPanel as BattleSettlementPanel;
                        long target;
                        if (_likes.Tick(panel, gm.battle, accountId, now, _likeInterval, out target))
                            Log.Info("已触发原版结算点赞：玩家=" + target + "，间隔=" + _likeInterval + "ms");
                    }
                    return;
                }
                if (state != RoomStateType.RUNNING || !gm.battle.clientFinishReady)
                {
                    ResetSession();
                    return;
                }
                if (!ReferenceEquals(room, _sessionRoom) || accountId != _sessionAccount)
                {
                    ResetSession();
                    _sessionRoom = room;
                    _sessionAccount = accountId;
                }
                if (!_enabled)
                {
                    StopListening();
                    _policy.Reset();
                    return;
                }
                EnsureHooked();
                Notice notice;
                while (TryTakeReady(DateTime.UtcNow.Ticks, out notice))
                {
                    now = DateTime.UtcNow.Ticks;
                    var sender = gm.battle.GetPlayerDataById(notice.Sender);
                    var self = gm.battle.GetPlayerDataById(accountId);
                    bool sameTeam = sender?.player != null && self?.player != null &&
                        sender.player.TeamId == self.player.TeamId;
                    bool blocked = gm.lockExpressionPlayerID.Contains(notice.Sender);
                    int chatId;
                    if (!_policy.TryGetChatId(notice.Sender, notice.Recipient, accountId, notice.Type,
                        sameTeam, blocked, _healing, _cards, _transfers, notice.ReceivedTicks, now, _cooldown, out chatId, _thankDelay)) continue;
                    if (!Permissions.Require(ModPermission.GameActions, "AutoThanksMod.SendShortChat")) continue;
                    _policy.MarkAttempt(now);
                    var result = gm.communicate.RequestRoomShortChatC2S(chatId);
                    if (result == null) continue;
                    // The server callback intentionally ignores the sender, so render the local message as the UI does.
                    gm.communicate.Signal.battleMessage.Dispatch(new BattleMessage(MessageType.SHORTINFO, self, chatId));
                    Log.Info("已请求发送感谢：" + (notice.Type == 3 ? "队友治疗" : (notice.Type == 2 ? "队友给牌" : "队友转星币")) +
                        "，帮助者=" + notice.Sender + "，短语=" + chatId);
                }
            }
            catch (Exception e)
            {
                StopListening();
                if (now >= _nextWarning)
                {
                    _nextWarning = now + 10 * ThanksPolicy.TicksPerSecond;
                    Log.Warn("自动感谢/结算点赞暂未执行：" + e.Message);
                }
            }
        }

        private static bool IsParticipantContext(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null || gm.battle == null || gm.communicate == null ||
                !gm.room.IsInRoom || gm.room.roomController == null) return false;
            if (SimpleSingletonProvider<Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle) return false;
            if (gm.replay?.Session != null && gm.replay.Session.IsReplay) return false;
            if (gm.watch != null && gm.watch.PlayerIsWatcher()) return false;
            long id = gm.account.GetPlayerID();
            return id != 0 && gm.battle.GetPlayerDataById(id)?.player != null;
        }

        private void ReadConfig()
        {
            var config = Config;
            _enabled = config?.GetBool("enabled", true) ?? true;
            _healing = config?.GetBool("thankForHealing", true) ?? true;
            _cards = config?.GetBool("thankForCards", true) ?? true;
            _transfers = config?.GetBool("thankForTransfers", true) ?? true;
            int cooldown = config?.GetInt("cooldownSeconds", 2) ?? 2;
            _cooldown = cooldown < 0 ? 0 : (cooldown > 30 ? 30 : cooldown);
            _thankDelay = ThanksPolicy.ClampDelayMilliseconds(config?.GetInt("thankDelayMilliseconds", ThanksPolicy.DefaultDelayMilliseconds)
                ?? ThanksPolicy.DefaultDelayMilliseconds);
            _autoLike = config?.GetBool("autoLikeOnGameEnd", true) ?? true;
            _likeInterval = LikePolicy.ClampIntervalMilliseconds(config?.GetInt("likeIntervalMilliseconds", LikePolicy.DefaultIntervalMilliseconds)
                ?? LikePolicy.DefaultIntervalMilliseconds);
        }

        private void EnsureHooked()
        {
            var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.SayPhraseNotifyS2C;
            if (rpc == null || rpc.OnSayPhraseNotifyS2CServerCallBackAsync == null) return;
            if (ReferenceEquals(rpc, _rpc) && ReferenceEquals(rpc.OnSayPhraseNotifyS2CServerCallBackAsync, _wrapped)) return;
            // Restore only our own top-level wrapper. Never overwrite another mod's callback.
            StopListening();
            var original = rpc.OnSayPhraseNotifyS2CServerCallBackAsync;
            long generation;
            lock (_gate) { generation = ++_generation; _acceptNotices = true; }
            SayPhraseNotifyS2CRPC.OnSayPhraseNotifyS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { Enqueue(model, errId, generation); } catch { }
                return original(model, errId, isDispatch);
            };
            _rpc = rpc;
            _original = original;
            _wrapped = wrapped;
            rpc.OnSayPhraseNotifyS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩治疗/给牌/转星币快捷回复通知。");
        }

        private void Enqueue(SayPhraseNotifyS2C model, int errId, long generation)
        {
            var phrase = model?.Phrase;
            if (errId != 0 || phrase == null) return;
            int type = (int)phrase.TriggerType;
            if (type != 1 && type != 2 && type != 3) return;
            var notice = new Notice { Sender = phrase.ActivePlayerId, Recipient = phrase.PassivePlayerId,
                Type = type, ReceivedTicks = DateTime.UtcNow.Ticks };
            lock (_gate)
            {
                if (!_acceptNotices || generation != _generation) return;
                if (_pending.Count >= 16) _pending.Dequeue();
                _pending.Enqueue(notice);
            }
        }

        private bool TryTakeReady(long nowTicks, out Notice notice)
        {
            lock (_gate)
            {
                notice = default(Notice);
                // A clock correction must not leave a future-dated head blocking all later notices.
                while (_pending.Count != 0 && _pending.Peek().ReceivedTicks > nowTicks) _pending.Dequeue();
                if (_pending.Count == 0 || !ThanksPolicy.IsDelayElapsed(_pending.Peek().ReceivedTicks, nowTicks, _thankDelay))
                    return false;
                notice = _pending.Dequeue();
                return true;
            }
        }

        private void StopListening()
        {
            lock (_gate) { _acceptNotices = false; ++_generation; _pending.Clear(); }
            if (_rpc != null && ReferenceEquals(_rpc.OnSayPhraseNotifyS2CServerCallBackAsync, _wrapped))
                _rpc.OnSayPhraseNotifyS2CServerCallBackAsync = _original;
            _rpc = null;
            _original = null;
            _wrapped = null;
        }

        private void ResetSession()
        {
            StopListening();
            _sessionRoom = null;
            _sessionAccount = 0;
            _policy.Reset();
            _likes.Reset();
        }

        public override void OnUnload()
        {
            ResetSession();
        }
    }
}
