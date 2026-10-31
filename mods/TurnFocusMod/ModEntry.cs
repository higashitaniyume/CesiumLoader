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
    /// 轮到自己需要操作, 匹配成功, 收到房间邀请, 或匹配成功开房后队友喊"准备"时,
    /// 通过加载器的窗口置前通道把游戏窗口切到前台。
    ///
    /// 五个触发来源:
    ///   - 行动开始通知(ActionStartNotifyS2C, 1026): 轮到自己的回合;
    ///   - SDK 原始动作流(RawAction): 5211 筹码三选一 / 5047 战斗询问 /
    ///     5035 战斗出牌 / 5037 攻击骰 / 5039 防御选择;
    ///   - 匹配成功(MatchSuccessS2C, 5226): 匹配成功时立即提醒;
    ///   - 房间邀请(FriendInviteNotifyS2C): 好友邀请你加入房间, 游戏在右上角
    ///     显示邀请按钮时提醒;
    ///   - 房间快捷消息(RoomShortChatS2C / MatchTeamChatS2C): 匹配成功开房后,
    ///     其他玩家发送"准备"之类的消息。
    /// 回合相关只处理 PlayerId 为自己的动作; 房间消息只处理别人发的消息;
    /// 回放、观战不触发。
    /// </summary>
    public sealed partial class TurnFocus : ModBase
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

        private FriendInviteNotifyS2CRPC _inviteRpc;
        private FriendInviteNotifyS2CRPC.OnFriendInviteNotifyS2CServerDelegate _inviteOriginal;
        private FriendInviteNotifyS2CRPC.OnFriendInviteNotifyS2CServerDelegate _inviteWrapped;
        private int _inviteInstalls;

        private Core.Net.RPCMsgManager _rpcManager;

        private bool _enabled = true;
        private bool _roomChatFocus = true;
        private bool _roomInviteFocus = true;
        private bool _matchSuccessFocus = true;
        private bool _soundEnabled = true;
        private string _soundFile = DefaultSoundFile;
        private bool _alwaysPlaySound;
        private bool _warnedMissingSound;
        private int _cooldownSeconds = TurnFocusPolicy.DefaultCooldownSeconds;
        private long _lastRequestTicks;
        private long _nextHookCheckTicks;
        private long _nextConfigCheckTicks;
        private long _nextWarnTicks;
        private bool _subscribed;

        /// <summary>
        /// 默认不播放提示音: 只有用户显式在 soundFile 里填了文件才出声。
        /// 相对路径按 mod 目录解析(随包提供了一个示例音 sounds/your-turn.wav), 也可用绝对路径。
        /// </summary>
        internal const string DefaultSoundFile = "";

        public override string Name { get { return "回合置前"; } }
        public override string Version { get { return "1.6.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "轮到自己回合、选择筹码、战斗出牌、攻击或防御、匹配成功、收到房间邀请，或开房后有人发准备消息时把游戏窗口切换到前台；配置了提示音时同时播放，窗口已在前台时不打扰。"; }
        }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                // 只在真的补了缺失键时才落盘: 无条件 Save 会把"启动后、初始化前"
                // 用户手动改的 config.json 覆盖回去(看起来就像热重载没生效)。
                bool added = TurnFocusConfigDocs.Apply(config);
                if (!config.Has("enabled")) { config.Set("enabled", true); added = true; }
                if (!config.Has("cooldownSeconds")) { config.Set("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds); added = true; }
                if (!config.Has("focusOnRoomChat")) { config.Set("focusOnRoomChat", true); added = true; }
                if (!config.Has("focusOnRoomInvite")) { config.Set("focusOnRoomInvite", true); added = true; }
                if (!config.Has("focusOnMatchSuccess")) { config.Set("focusOnMatchSuccess", true); added = true; }
                if (!config.Has("soundEnabled")) { config.Set("soundEnabled", true); added = true; }
                if (!config.Has("soundFile")) { config.Set("soundFile", DefaultSoundFile); added = true; }
                if (!config.Has("alwaysPlaySound")) { config.Set("alwaysPlaySound", false); added = true; }
                if (added) config.SaveIfDirty();
            }

            ReadConfig();
            GameEvents.RawAction += OnRawAction;
            _subscribed = true;
            Context?.RegisterCleanup(Unhook);

            Log.Info("已启用回合置前：轮到自己回合、选择筹码、战斗出牌、攻击或防御、匹配成功、收到房间邀请，或开房后有人发准备消息时切换到前台。");
            if (!WindowFocus.IsAvailable)
                Log.Warn("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
            else if (!_soundEnabled)
                Log.Info("提示音已关闭，仅置前。");
            else if (string.IsNullOrEmpty(_soundFile))
                Log.Info("未指定提示音，仅置前（soundFile 填 WAV 绝对路径或 sounds/your-turn.wav 即可启用）。");
            else if (!WindowFocus.SupportsSound)
                Log.Warn("加载器未提供提示音（需要 CesiumLoader 2.7.2 及以上），本次只置前。");
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            LogConfigReload();
        }

        public override void OnUnload()
        {
            Unhook();
        }

        public override void OnUpdate()
        {
            long now = DateTime.UtcNow.Ticks;

            // 自带一套 1 秒配置轮询: 掉配置/改 soundFile 不必重启游戏。
            // 与 SDK 的全局轮询互为兜底 —— ReloadIfChanged 自己去重, 不会重复触发。
            if (now >= _nextConfigCheckTicks)
            {
                _nextConfigCheckTicks = now + HookCheckIntervalTicks;
                ReloadConfigIfChanged();
            }

            if (now < _nextHookCheckTicks) return;
            _nextHookCheckTicks = now + HookCheckIntervalTicks;

            // SDK 负责包装 PredictActionS2C(不能重复包装); 每秒保持一次挂钩即可。
            GameEvents.EnsureHooked();
            EnsureActionStartHook();
            EnsureRoomHooks();
        }

        /// <summary>配置文件被外部修改时立刻应用(下次触发就用新值, 不等 SDK 的轮询)。</summary>
        private void ReloadConfigIfChanged()
        {
            var config = Config;
            if (config == null) return;

            bool changed;
            try { changed = config.ReloadIfChanged(); }
            catch { return; }

            if (!changed) return;

            ReadConfig();
            LogConfigReload();
        }

        private void LogConfigReload()
        {
            Log.Info("回合置前配置已热重载: enabled=" + _enabled
                + ", cooldownSeconds=" + _cooldownSeconds
                + ", focusOnMatchSuccess=" + _matchSuccessFocus
                + ", focusOnRoomChat=" + _roomChatFocus
                + ", focusOnRoomInvite=" + _roomInviteFocus
                + ", soundEnabled=" + _soundEnabled
                + ", soundFile=" + (_soundFile ?? "")
                + ", alwaysPlaySound=" + _alwaysPlaySound);
        }

        private void ReadConfig()
        {
            var config = Config;
            if (config == null) return;

            _enabled = config.GetBool("enabled", true);
            _roomChatFocus = config.GetBool("focusOnRoomChat", true);
            _roomInviteFocus = config.GetBool("focusOnRoomInvite", true);
            _matchSuccessFocus = config.GetBool("focusOnMatchSuccess", true);
            _soundEnabled = config.GetBool("soundEnabled", true);
            _soundFile = config.GetString("soundFile", DefaultSoundFile);
            if (_soundFile != null) _soundFile = _soundFile.Trim();
            _alwaysPlaySound = config.GetBool("alwaysPlaySound", false);
            _warnedMissingSound = false;
            _cooldownSeconds = TurnFocusPolicy.ClampCooldownSeconds(
                config.GetInt("cooldownSeconds", TurnFocusPolicy.DefaultCooldownSeconds));
        }

        /// <summary>
        /// 写一条置前请求。提示音路径和"前台也响"的开关一起交给加载器,
        /// 由它按配置决定这次要不要出声(默认只在窗口不在前台时响)。
        /// </summary>
        private void RequestFocus(string what)
        {
            string sound = ResolveSoundPath();

            if (WindowFocus.RequestForeground(sound, _alwaysPlaySound))
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

    }
}
