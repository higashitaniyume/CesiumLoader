using System;
using System.IO;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Windowing;
using Core.Net;
using GameLogic;
using party.protocol;
using Tools;

namespace TurnFocusMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new TurnFocus()); }
    }

    /// <summary>
    /// 轮到自己需要操作, 匹配成功, 或匹配成功开房后队友喊"准备"时, 通过加载器的
    /// 窗口置前通道把游戏窗口切到前台。
    ///
    /// 四个触发来源:
    ///   - 行动开始通知(ActionStartNotifyS2C, 1026): 轮到自己的回合;
    ///   - SDK 原始动作流(RawAction): 5211 筹码三选一 / 5047 战斗询问 /
    ///     5035 战斗出牌 / 5037 攻击骰 / 5039 防御选择;
    ///   - 匹配成功(MatchSuccessS2C, 5226): 匹配成功时立即提醒;
    ///   - 房间快捷消息(RoomShortChatS2C / MatchTeamChatS2C): 匹配成功开房后,
    ///     其他玩家发送"准备"之类的消息。
    /// 回合相关只处理 PlayerId 为自己的动作; 房间消息只处理别人发的消息;
    /// 回放、观战不触发。
    /// </summary>
    public sealed class TurnFocus : ModBase
    {
        private const int MaxHookInstallsPerBattle = 4;
        private const int MaxRoomHookInstallsPerLogin = 8;
        private const long HookCheckIntervalTicks = TimeSpan.TicksPerSecond;
        private const long WarnIntervalTicks = 30 * TimeSpan.TicksPerSecond;

        private ActionStartNotifyS2CRPC _rpc;
        private ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate _original;
        private ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate _wrapped;
        private BattleLogic _battle;
        private int _installs;

        private RoomShortChatS2CRPC _roomChatRpc;
        private RoomShortChatS2CRPC.OnRoomShortChatS2CServerDelegate _roomChatOriginal;
        private RoomShortChatS2CRPC.OnRoomShortChatS2CServerDelegate _roomChatWrapped;
        private int _roomChatInstalls;

        private MatchTeamChatS2CRPC _teamChatRpc;
        private MatchTeamChatS2CRPC.OnMatchTeamChatS2CServerDelegate _teamChatOriginal;
        private MatchTeamChatS2CRPC.OnMatchTeamChatS2CServerDelegate _teamChatWrapped;
        private int _teamChatInstalls;

        private MatchSuccessS2CRPC _matchSuccessRpc;
        private MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate _matchSuccessOriginal;
        private MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate _matchSuccessWrapped;
        private int _matchSuccessInstalls;

        private Core.Net.RPCMsgManager _rpcManager;

        private bool _enabled = true;
        private bool _roomChatFocus = true;
        private bool _matchSuccessFocus = true;
        private bool _soundEnabled = true;
        private string _soundFile = DefaultSoundFile;
        private bool _warnedMissingSound;
        private int _cooldownSeconds = TurnFocusPolicy.DefaultCooldownSeconds;
        private long _lastRequestTicks;
        private long _nextHookCheckTicks;
        private long _nextWarnTicks;
        private bool _subscribed;

        /// <summary>
        /// 默认不播放提示音: 只有用户显式在 soundFile 里填了文件才出声。
        /// 相对路径按 mod 目录解析(随包提供了一个示例音 sounds/your-turn.wav), 也可用绝对路径。
        /// </summary>
        internal const string DefaultSoundFile = "";

        public override string Name { get { return "回合置前"; } }
        public override string Version { get { return "1.4.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "轮到自己回合、选择筹码、战斗出牌、攻击或防御、匹配成功，或开房后有人发准备消息时把游戏窗口切换到前台；配置了提示音时同时播放，窗口已在前台时不打扰。"; }
        }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                if (!config.Has("enabled")) config.Set("enabled", true);
                if (!config.Has("cooldownSeconds")) config.Set("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds);
                if (!config.Has("focusOnRoomChat")) config.Set("focusOnRoomChat", true);
                if (!config.Has("focusOnMatchSuccess")) config.Set("focusOnMatchSuccess", true);
                if (!config.Has("soundEnabled")) config.Set("soundEnabled", true);
                if (!config.Has("soundFile")) config.Set("soundFile", DefaultSoundFile);
                config.Save();
            }

            ReadConfig();
            GameEvents.RawAction += OnRawAction;
            _subscribed = true;
            Context?.RegisterCleanup(Unhook);

            Log.Info("已启用回合置前：轮到自己回合、选择筹码、战斗出牌、攻击或防御、匹配成功，或开房后有人发准备消息时切换到前台。");
            if (!WindowFocus.IsAvailable)
                Log.Warn("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
            else if (!_soundEnabled)
                Log.Info("提示音已关闭，仅置前。");
            else if (string.IsNullOrEmpty(_soundFile))
                Log.Info("未指定提示音，仅置前（soundFile 填 WAV 绝对路径或 sounds/your-turn.wav 即可启用）。");
            else if (!WindowFocus.SupportsSound)
                Log.Warn("加载器未提供提示音（需要 CesiumLoader 2.7.1 及以上），本次只置前。");
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("回合置前配置已热重载: enabled=" + _enabled
                + ", cooldownSeconds=" + _cooldownSeconds
                + ", focusOnMatchSuccess=" + _matchSuccessFocus
                + ", focusOnRoomChat=" + _roomChatFocus
                + ", soundEnabled=" + _soundEnabled
                + ", soundFile=" + (_soundFile ?? ""));
        }

        public override void OnUnload()
        {
            Unhook();
        }

        public override void OnUpdate()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextHookCheckTicks) return;
            _nextHookCheckTicks = now + HookCheckIntervalTicks;

            // SDK 负责包装 PredictActionS2C(不能重复包装); 每秒保持一次挂钩即可。
            GameEvents.EnsureHooked();
            EnsureActionStartHook();
            EnsureRoomHooks();
        }

        private void ReadConfig()
        {
            var config = Config;
            if (config == null) return;

            _enabled = config.GetBool("enabled", true);
            _roomChatFocus = config.GetBool("focusOnRoomChat", true);
            _matchSuccessFocus = config.GetBool("focusOnMatchSuccess", true);
            _soundEnabled = config.GetBool("soundEnabled", true);
            _soundFile = config.GetString("soundFile", DefaultSoundFile);
            if (_soundFile != null) _soundFile = _soundFile.Trim();
            _warnedMissingSound = false;
            _cooldownSeconds = TurnFocusPolicy.ClampCooldownSeconds(
                config.GetInt("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds));
        }

        /// <summary>
        /// 写一条置前请求。提示音路径跟请求一起交给加载器, 由它决定"这次是否真的
        /// 需要提醒"(窗口已在前台时置前和提示音一起跳过)。
        /// </summary>
        private void RequestFocus(string what)
        {
            string sound = ResolveSoundPath();

            if (WindowFocus.RequestForeground(sound))
            {
                Log.Info(what + "，已请求把游戏窗口切到前台"
                    + (sound != null ? "，并播放提示音。" : "。"));
                return;
            }

            WarnThrottled("窗口置前请求失败：" + (WindowFocus.UnavailableReason ?? "原因未知"));
        }

        /// <summary>
        /// 解析提示音路径: 相对路径按 mod 目录展开, 文件不存在时只提示一次并退回"只置前"。
        /// </summary>
        private string ResolveSoundPath()
        {
            if (!_soundEnabled || string.IsNullOrEmpty(_soundFile)) return null;

            string path;
            try
            {
                // Path.Combine 的第二参数本身是绝对路径时会原样返回, 不用另判。
                string dir = Context?.Directory;
                path = string.IsNullOrEmpty(dir) ? _soundFile : Path.Combine(dir, _soundFile);
            }
            catch { return null; }

            if (string.IsNullOrEmpty(path)) return null;

            try { if (File.Exists(path)) return path; }
            catch { }

            if (!_warnedMissingSound)
            {
                _warnedMissingSound = true;
                Log.Warn("提示音文件不存在，本次只置前：" + path);
            }
            return null;
        }

        private void OnRawAction(RawActionEvent action)
        {
            if (!_enabled || action == null) return;
            if (!TurnFocusPolicy.IsFocusAction(action.Id)) return;
            FocusFor(TurnFocusPolicy.ActionName(action.Id), action.PlayerId);
        }

        private void OnActionStart(ActionStartNotifyS2C model, int errId)
        {
            if (errId != 0 || model == null) return;
            FocusFor("回合开始", model.PlayerId);
        }

        /// <summary>所有触发点共用: 校验上下文/身份/冷却, 再写一条置前请求。</summary>
        private void FocusFor(string reason, long playerId)
        {
            if (!_enabled) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsLiveBattle(gm)) return;

                long selfPlayerId = gm.account.GetPlayerID();
                if (!TurnFocusPolicy.IsMyTurn(playerId, selfPlayerId)) return;

                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequest(playerId, selfPlayerId, true, false,
                        available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("轮到我" + reason);
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 匹配成功触发点: 游戏刚匹配到对局、准备创建房间时置前。
        /// 这条通知到达时房间还没建好, 所以不检查房间状态, 只排除回放/观战。
        /// </summary>
        private void FocusForMatchSuccess()
        {
            if (!_enabled || !_matchSuccessFocus) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequestMatchSuccess(_enabled, _matchSuccessFocus,
                        IsReplayOrWatcher(gm), available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("匹配成功");
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 房间快捷消息触发点: 匹配成功开房后, 其他玩家发"准备"之类的消息时置前。
        /// 只处理房间等待阶段, 战斗内的快捷消息不抢焦点。
        /// </summary>
        private void FocusForRoomChat(long senderPlayerId, int index)
        {
            if (!_enabled || !_roomChatFocus || senderPlayerId <= 0) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsRoomWaiting(gm)) return;

                long selfPlayerId = gm.account.GetPlayerID();
                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequestRoomChat(_enabled, _roomChatFocus, true,
                        TurnFocusPolicy.IsOtherPlayer(senderPlayerId, selfPlayerId),
                        IsReplayOrWatcher(gm), available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("房间里有人发送快捷消息(Index=" + index + ")");
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 包装 ActionStartNotifyS2C(1026)。这条 RPC 不在 SDK 的包装清单里,
        /// 按"捕获原始委托 / 只装一次 / 每局限额"策略安装, 并照常转给游戏自己的处理。
        /// </summary>
        private void EnsureActionStartHook()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsLiveBattle(gm)) return;

                if (!ReferenceEquals(_battle, gm.battle))
                {
                    _battle = gm.battle;
                    _installs = 0;
                    _rpc = null;
                    _original = null;
                    _wrapped = null;
                }

                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.ActionStartNotifyS2C;
                if (rpc == null) return;

                var current = rpc.OnActionStartNotifyS2CServerCallBackAsync;
                if (current == null || ReferenceEquals(current, _wrapped)) return;
                if (_installs >= MaxHookInstallsPerBattle) return;

                var original = current;
                ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate wrapped =
                    (model, errId, isDispatch) =>
                    {
                        try { OnActionStart(model, errId); }
                        catch (Exception e) { WarnThrottled("处理行动开始通知失败：" + e.Message); }
                        return original(model, errId, isDispatch);
                    };

                _installs++;
                _rpc = rpc;
                _original = original;
                _wrapped = wrapped;
                rpc.OnActionStartNotifyS2CServerCallBackAsync = wrapped;
            }
            catch (Exception e)
            {
                WarnThrottled("安装行动开始挂钩失败：" + e.Message);
            }
        }

        /// <summary>
        /// 包装匹配成功与房间快捷消息的 RPC。它们由游戏在登录后常驻注册, 不依赖战斗,
        /// 所以不能用 IsLiveBattle 作为安装条件; 用 hasInstance 避免提前创建 NetManager。
        /// 房间消息有两种通道: 组队匹配成功后走 MatchTeamChatS2C, 否则走 RoomShortChatS2C。
        /// </summary>
        private void EnsureRoomHooks()
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;

                var manager = MonoSingletonProvider<NetManager>.inst;
                if (manager == null) return;

                var net = manager.RPC;
                if (net == null) return;

                // 重新登录或重连会换一个 RPCMsgManager, 挂接额度按实例重置。
                if (!ReferenceEquals(net, _rpcManager))
                {
                    _rpcManager = net;
                    _roomChatInstalls = 0;
                    _teamChatInstalls = 0;
                    _matchSuccessInstalls = 0;
                    _roomChatRpc = null;
                    _roomChatOriginal = null;
                    _roomChatWrapped = null;
                    _teamChatRpc = null;
                    _teamChatOriginal = null;
                    _teamChatWrapped = null;
                    _matchSuccessRpc = null;
                    _matchSuccessOriginal = null;
                    _matchSuccessWrapped = null;
                }

                HookMatchSuccess(net);
                HookRoomShortChat(net);
                HookMatchTeamChat(net);
            }
            catch (Exception e)
            {
                WarnThrottled("安装房间快捷消息挂钩失败：" + e.Message);
            }
        }

        private void HookMatchSuccess(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MatchSuccessS2C;
            if (rpc == null) return;

            var current = rpc.OnMatchSuccessS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _matchSuccessWrapped)) return;
            if (_matchSuccessInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForMatchSuccess();
                    }
                    catch (Exception e) { WarnThrottled("处理匹配成功通知失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _matchSuccessInstalls++;
            _matchSuccessRpc = rpc;
            _matchSuccessOriginal = original;
            _matchSuccessWrapped = wrapped;
            rpc.OnMatchSuccessS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩匹配成功通知。");
        }

        private void HookRoomShortChat(Core.Net.RPCMsgManager net)
        {
            var rpc = net.RoomShortChatS2C;
            if (rpc == null) return;

            var current = rpc.OnRoomShortChatS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _roomChatWrapped)) return;
            if (_roomChatInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            RoomShortChatS2CRPC.OnRoomShortChatS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForRoomChat(model.PlayerId, model.Index);
                    }
                    catch (Exception e) { WarnThrottled("处理房间快捷消息失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _roomChatInstalls++;
            _roomChatRpc = rpc;
            _roomChatOriginal = original;
            _roomChatWrapped = wrapped;
            rpc.OnRoomShortChatS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩房间快捷消息。");
        }

        private void HookMatchTeamChat(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MatchTeamChatS2C;
            if (rpc == null) return;

            var current = rpc.OnMatchTeamChatS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _teamChatWrapped)) return;
            if (_teamChatInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            MatchTeamChatS2CRPC.OnMatchTeamChatS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForRoomChat(model.PlayerId, model.Index);
                    }
                    catch (Exception e) { WarnThrottled("处理组队快捷消息失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _teamChatInstalls++;
            _teamChatRpc = rpc;
            _teamChatOriginal = original;
            _teamChatWrapped = wrapped;
            rpc.OnMatchTeamChatS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩组队快捷消息。");
        }

        private static bool IsLiveBattle(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null || gm.battle == null) return false;
            if (!gm.room.IsInRoom || gm.room.roomController == null) return false;
            if (gm.room.roomController.roomStateType != RoomStateType.RUNNING) return false;
            if (!gm.battle.clientFinishReady) return false;
            if (gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0) return false;

            // 回放与观战不是"轮到玩家操作", 不抢焦点。
            if (IsReplayOrWatcher(gm)) return false;

            return true;
        }

        /// <summary>匹配成功开房后、游戏开始前的房间等待阶段(选人/准备)。</summary>
        private static bool IsRoomWaiting(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null) return false;
            if (!gm.room.IsInRoom || gm.room.roomController == null) return false;

            var state = gm.room.roomController.roomStateType;
            return state == RoomStateType.WAIT || state == RoomStateType.CHOICE || state == RoomStateType.READY;
        }

        private static bool IsReplayOrWatcher(GameLogicManager gm)
        {
            try { if (gm.replay != null && gm.replay.Session != null && gm.replay.Session.IsReplay) return true; }
            catch { }
            try { if (gm.watch != null && gm.watch.PlayerIsWatcher()) return true; }
            catch { }

            return false;
        }

        private void WarnThrottled(string message)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextWarnTicks) return;
            _nextWarnTicks = now + WarnIntervalTicks;
            Log.Warn(message);
        }

        private void Unhook()
        {
            if (_subscribed)
            {
                _subscribed = false;
                try { GameEvents.RawAction -= OnRawAction; } catch { }
            }

            try
            {
                if (_rpc != null && ReferenceEquals(_rpc.OnActionStartNotifyS2CServerCallBackAsync, _wrapped))
                    _rpc.OnActionStartNotifyS2CServerCallBackAsync = _original;
            }
            catch { }

            try
            {
                if (_roomChatRpc != null && ReferenceEquals(_roomChatRpc.OnRoomShortChatS2CServerCallBackAsync, _roomChatWrapped))
                    _roomChatRpc.OnRoomShortChatS2CServerCallBackAsync = _roomChatOriginal;
            }
            catch { }

            try
            {
                if (_teamChatRpc != null && ReferenceEquals(_teamChatRpc.OnMatchTeamChatS2CServerCallBackAsync, _teamChatWrapped))
                    _teamChatRpc.OnMatchTeamChatS2CServerCallBackAsync = _teamChatOriginal;
            }
            catch { }

            try
            {
                if (_matchSuccessRpc != null && ReferenceEquals(_matchSuccessRpc.OnMatchSuccessS2CServerCallBackAsync, _matchSuccessWrapped))
                    _matchSuccessRpc.OnMatchSuccessS2CServerCallBackAsync = _matchSuccessOriginal;
            }
            catch { }

            _rpc = null;
            _original = null;
            _wrapped = null;
            _roomChatRpc = null;
            _roomChatOriginal = null;
            _roomChatWrapped = null;
            _teamChatRpc = null;
            _teamChatOriginal = null;
            _teamChatWrapped = null;
            _matchSuccessRpc = null;
            _matchSuccessOriginal = null;
            _matchSuccessWrapped = null;
            _rpcManager = null;
        }
    }
}
