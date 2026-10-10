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
        private SayPhraseNotifyS2CRPC _rpc;
        private SayPhraseNotifyS2CRPC.OnSayPhraseNotifyS2CServerDelegate _original, _wrapped;
        private long _generation;
        private bool _acceptNotices;
        private RoomInfo _sessionRoom;
        private long _sessionAccount;
        private long _nextConfigRead, _nextWarning;
        private bool _enabled = true, _healing = true, _cards = true, _transfers = true;
        private int _cooldown = 2;

        public override string Name { get { return "自动感谢"; } }
        public override string Version { get { return "1.0.1"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description { get { return "队友治疗你、给你牌或转星币给你时，自动发送快捷回复：感谢！"; } }

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
                config.Save();
            }
            ReadConfig();
            Log.Info("自动感谢已初始化：治疗/给牌/转星币触发原版感谢短语，回放和观战不发送。");
        }

        public override void OnUpdate()
        {
            long now = DateTime.UtcNow.Ticks;
            try
            {
                if (now >= _nextConfigRead)
                {
                    _nextConfigRead = now + 2 * ThanksPolicy.TicksPerSecond;
                    Config?.Load();
                    ReadConfig();
                }
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!_enabled || !IsLiveBattle(gm))
                {
                    StopListening();
                    _sessionRoom = null;
                    _sessionAccount = 0;
                    _policy.Reset();
                    return;
                }
                long accountId = gm.account.GetPlayerID(); // IsSelf/GetSelfPlayerData can follow a spectator target.
                var room = gm.room.curRoomInfo;
                if (!ReferenceEquals(room, _sessionRoom) || accountId != _sessionAccount)
                {
                    StopListening();
                    _policy.Reset();
                    _sessionRoom = room;
                    _sessionAccount = accountId;
                }
                EnsureHooked();
                Notice notice;
                while (TryTake(out notice))
                {
                    now = DateTime.UtcNow.Ticks;
                    var sender = gm.battle.GetPlayerDataById(notice.Sender);
                    var self = gm.battle.GetPlayerDataById(accountId);
                    bool sameTeam = sender?.player != null && self?.player != null &&
                        sender.player.TeamId == self.player.TeamId;
                    bool blocked = gm.lockExpressionPlayerID.Contains(notice.Sender);
                    int chatId;
                    if (!_policy.TryGetChatId(notice.Sender, notice.Recipient, accountId, notice.Type,
                        sameTeam, blocked, _healing, _cards, _transfers, notice.ReceivedTicks, now, _cooldown, out chatId)) continue;
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
                    Log.Warn("自动感谢暂未执行：" + e.Message);
                }
            }
        }

        private static bool IsLiveBattle(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null || gm.battle == null || gm.communicate == null ||
                !gm.room.IsInRoom || gm.room.roomController == null ||
                gm.room.roomController.roomStateType != RoomStateType.RUNNING || !gm.battle.clientFinishReady) return false;
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

        private bool TryTake(out Notice notice)
        {
            lock (_gate)
            {
                if (_pending.Count == 0) { notice = default(Notice); return false; }
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

        public override void OnUnload()
        {
            StopListening();
            _sessionRoom = null;
            _policy.Reset();
        }
    }
}
