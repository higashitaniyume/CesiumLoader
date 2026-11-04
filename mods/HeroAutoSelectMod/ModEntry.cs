using System;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Mods;
using party.model;

namespace HeroAutoSelectMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new HeroAutoSelect()); }
    }

    /// <summary>
    /// 示例 mod：匹配成功/进入选人阶段后自动选择指定角色，并在需要时确认。
    ///
    /// 演示的 SDK 能力：
    ///   · <see cref="GameEvents.MatchSuccess"/> —— 匹配成功；
    ///   · <see cref="GameEvents.HeroSelectionStarted"/> —— 进入选人阶段；
    ///   · <see cref="GameEvents.HeroSelected"/> —— 自己/他人的角色确认结果；
    ///   · <see cref="GameActions.CanSelectHero"/> / <see cref="GameActions.SelectHero"/> /
    ///     <see cref="GameActions.ConfirmHero"/> —— 选人与确认。
    /// </summary>
    public sealed class HeroAutoSelect : ModBase
    {
        /// <summary>芬妮「古怪神探」的角色 ID。</summary>
        internal const int DefaultHeroId = 102;

        private const int MaxSelectAttempts = 12;
        private const int MaxDelayMilliseconds = 10000;
        private const long RetryIntervalTicks = TimeSpan.TicksPerMillisecond * 500;

        private bool _enabled = true;
        private int _heroId = DefaultHeroId;
        private bool _autoConfirm = true;
        private int _delayMilliseconds = 500;
        private int _confirmDelayMilliseconds = 500;

        private bool _waiting;
        private bool _selectSent;
        private int _selectAttempts;
        private long _nextSelectTicks;
        private bool _confirmQueued;
        private long _confirmAtTicks;

        public override string Name { get { return "自动选角色"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description { get { return "匹配成功或进入选人阶段后自动选择指定角色并确认。"; } }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                try
                {
                    config.SetDefault("Enabled", true);
                    config.SetDefault("HeroId", DefaultHeroId);
                    config.SetDefault("AutoConfirm", true);
                    config.SetDefault("DelayMilliseconds", 500);
                    config.SetDefault("ConfirmDelayMilliseconds", 500);
                    HeroAutoSelectConfigDocs.Apply(config);
                    config.SaveIfDirty();
                }
                catch (Exception e)
                {
                    Log.Warn("写入默认配置失败（继续使用内存默认值）: " + e.Message);
                }
            }
            ReadConfig();

            GameEvents.MatchSuccess += OnMatchSuccess;
            GameEvents.HeroSelectionStarted += OnHeroSelectionStarted;
            GameEvents.HeroSelected += OnHeroSelected;
            GameEvents.StartAutoHook();

            Log.Info("已订阅匹配/选人事件；目标角色=" + HeroLabel() + "，自动确认=" + _autoConfirm);
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("配置已热重载：目标角色=" + HeroLabel() + "，自动确认=" + _autoConfirm);
        }

        public override void OnUpdate()
        {
            if (!_enabled || !_waiting) return;
            long now = DateTime.UtcNow.Ticks;

            if (_confirmQueued)
            {
                if (now < _confirmAtTicks) return;
                _confirmQueued = false;
                _waiting = false;
                if (GameActions.ConfirmHero(false))
                    Log.Info("已确认角色：" + HeroLabel());
                else
                    Log.Warn("角色确认发送失败（当前不在选人阶段？）");
                return;
            }

            if (!_selectSent)
            {
                if (_selectAttempts >= MaxSelectAttempts) return;
                if (now < _nextSelectTicks) return;
                if (!GameActions.CanSelectHero)
                {
                    _selectAttempts++;
                    _nextSelectTicks = now + RetryIntervalTicks;
                    if (_selectAttempts >= MaxSelectAttempts)
                    {
                        _waiting = false;
                        Log.Warn("自动选角色放弃：等待选人阶段超时");
                    }
                    return;
                }

                _selectSent = true;
                if (!GameActions.SelectHero(_heroId))
                {
                    _waiting = false;
                    Log.Warn("选择角色发送失败：" + HeroLabel());
                    return;
                }
                Log.Info("已选择角色：" + HeroLabel());
                if (_autoConfirm)
                {
                    _confirmQueued = true;
                    _confirmAtTicks = now + DelayTicks(_confirmDelayMilliseconds);
                }
                else
                {
                    _waiting = false;
                }
            }
        }

        public override void OnUnload()
        {
            GameEvents.MatchSuccess -= OnMatchSuccess;
            GameEvents.HeroSelectionStarted -= OnHeroSelectionStarted;
            GameEvents.HeroSelected -= OnHeroSelected;
        }

        private void OnMatchSuccess(Room room)
        {
            if (!_enabled) return;
            Log.Info("匹配成功" + (room != null ? "（房间 " + room.Id + "）" : "") + "，等待进入选人阶段");
        }

        private void OnHeroSelectionStarted(Room room)
        {
            if (!_enabled) return;
            _waiting = true;
            _selectSent = false;
            _selectAttempts = 0;
            _confirmQueued = false;
            _nextSelectTicks = DateTime.UtcNow.Ticks + DelayTicks(_delayMilliseconds);
            Log.Info("进入选人阶段，准备自动选择 " + HeroLabel() +
                (room != null ? "（房间 " + room.Id + "）" : ""));
        }

        private void OnHeroSelected(long playerId, int heroId, bool heroTaken)
        {
            if (!Players.IsSelf(playerId)) return;
            if (heroTaken)
            {
                _waiting = false;
                _confirmQueued = false;
                Log.Warn("角色确认被拒绝：" + Names.Character(heroId) + "(" + heroId + ") 已被其他玩家占用，请手动选择其他角色");
                return;
            }
            _waiting = false;
            _confirmQueued = false;
            Log.Info("角色已确认：" + Names.Character(heroId) + "(" + heroId + ")");
        }

        private void ReadConfig()
        {
            var config = Config;
            _enabled = config == null || config.GetBool("Enabled", true);
            int heroId = config != null ? config.GetInt("HeroId", DefaultHeroId) : DefaultHeroId;
            _heroId = heroId > 0 ? heroId : DefaultHeroId;
            _autoConfirm = config == null || config.GetBool("AutoConfirm", true);
            _delayMilliseconds = ClampDelay(config != null
                ? config.GetInt("DelayMilliseconds", 500) : 500);
            _confirmDelayMilliseconds = ClampDelay(config != null
                ? config.GetInt("ConfirmDelayMilliseconds", 500) : 500);
        }

        private string HeroLabel()
        {
            return Names.Character(_heroId) + "(" + _heroId + ")";
        }

        private static int ClampDelay(int milliseconds)
        {
            if (milliseconds < 0) return 0;
            return milliseconds > MaxDelayMilliseconds ? MaxDelayMilliseconds : milliseconds;
        }

        private static long DelayTicks(int milliseconds)
        {
            return milliseconds <= 0 ? 0 : milliseconds * TimeSpan.TicksPerMillisecond;
        }
    }
}
