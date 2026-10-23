using System;
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
    /// 轮到自己需要操作时, 通过加载器的窗口置前通道把游戏窗口切到前台。
    ///
    /// 两个触发来源:
    ///   - 行动开始通知(ActionStartNotifyS2C, 1026): 轮到自己的回合;
    ///   - SDK 原始动作流(RawAction): 5211 筹码三选一 / 5047 战斗询问 /
    ///     5035 战斗出牌 / 5037 攻击骰 / 5039 防御选择。
    /// 只处理 PlayerId 为自己的动作; 回放、观战不触发。
    /// </summary>
    public sealed class TurnFocus : ModBase
    {
        private const int MaxHookInstallsPerBattle = 4;
        private const long HookCheckIntervalTicks = TimeSpan.TicksPerSecond;
        private const long WarnIntervalTicks = 30 * TimeSpan.TicksPerSecond;

        private ActionStartNotifyS2CRPC _rpc;
        private ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate _original;
        private ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate _wrapped;
        private BattleLogic _battle;
        private int _installs;

        private bool _enabled = true;
        private int _cooldownSeconds = TurnFocusPolicy.DefaultCooldownSeconds;
        private long _lastRequestTicks;
        private long _nextHookCheckTicks;
        private long _nextWarnTicks;
        private bool _subscribed;

        public override string Name { get { return "回合置前"; } }
        public override string Version { get { return "1.1.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "轮到自己回合、选择筹码、战斗出牌、攻击或防御时把游戏窗口切换到前台，窗口已在前台时不打扰。"; }
        }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                if (!config.Has("enabled")) config.Set("enabled", true);
                if (!config.Has("cooldownSeconds")) config.Set("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds);
                config.Save();
            }

            ReadConfig();
            GameEvents.RawAction += OnRawAction;
            _subscribed = true;
            Context?.RegisterCleanup(Unhook);

            Log.Info("已启用回合置前：轮到自己回合、选择筹码、战斗出牌、攻击或防御时切换到前台。");
            if (!WindowFocus.IsAvailable)
                Log.Warn("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("回合置前配置已热重载: enabled=" + _enabled + ", cooldownSeconds=" + _cooldownSeconds);
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
        }

        private void ReadConfig()
        {
            var config = Config;
            if (config == null) return;

            _enabled = config.GetBool("enabled", true);
            _cooldownSeconds = TurnFocusPolicy.ClampCooldownSeconds(
                config.GetInt("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds));
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
                if (WindowFocus.RequestForeground())
                    Log.Info("轮到我" + reason + "，已请求把游戏窗口切到前台。");
                else
                    WarnThrottled("窗口置前请求失败：" + (WindowFocus.UnavailableReason ?? "原因未知"));
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

        private static bool IsLiveBattle(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null || gm.battle == null) return false;
            if (!gm.room.IsInRoom || gm.room.roomController == null) return false;
            if (gm.room.roomController.roomStateType != RoomStateType.RUNNING) return false;
            if (!gm.battle.clientFinishReady) return false;
            if (gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0) return false;

            // 回放与观战不是"轮到玩家操作", 不抢焦点。
            try { if (gm.replay != null && gm.replay.Session != null && gm.replay.Session.IsReplay) return false; }
            catch { }
            try { if (gm.watch != null && gm.watch.PlayerIsWatcher()) return false; }
            catch { }

            return true;
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

            _rpc = null;
            _original = null;
            _wrapped = null;
        }
    }
}
