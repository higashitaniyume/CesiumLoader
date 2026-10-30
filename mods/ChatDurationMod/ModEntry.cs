using System;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using UnityEngine.SceneManagement;

namespace ChatDurationMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new ChatDurationController());
        }
    }

    /// <summary>
    /// 让战斗中的快捷消息与表情按真实时间停留更久。
    ///
    /// 游戏原本用 <c>GAME_CHARACTER_EXPRESSION_DURATION</c>（毫秒，跟随时钟变速）给
    /// 消息列表条目计时；变速时游戏时间走得更快，消息就会在更短的真实时间里消失。
    /// 本模组把游戏计时压到极大，再按 <see cref="DateTime.UtcNow"/> 逐条隐藏，
    /// 默认 15 秒，可在 config.json 调整。
    /// </summary>
    public sealed class ChatDurationController : ModBase
    {
        private const double TickIntervalSeconds = 0.05;

        private ChatDurationSettings _settings = new ChatDurationSettings();
        private readonly ExpressionListLifetime _lifetime = new ExpressionListLifetime();
        private bool _durationApplied;
        private double _nextTick;

        public override string Name { get { return "消息停留时间"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "让战斗中的快捷消息和表情按真实时间停留更久（默认 15 秒），不受变速影响。"; }
        }

        public override void OnInitialize()
        {
            ChatDurationSettings.EnsureDefaults(Config);
            ChatDurationConfigDocs.Apply(Config);
            if (Config != null) Config.SaveIfDirty();

            _settings = ChatDurationSettings.Read(Config, message => Log.Warn(message));
            Log.Info("消息停留时间: enabled=" + _settings.Enabled
                + ", durationSeconds=" + _settings.DurationSeconds);

            if (_settings.Enabled) ApplyDuration();
        }

        public override void OnConfigChanged()
        {
            _settings = ChatDurationSettings.Read(Config, message => Log.Warn(message));

            if (!_settings.Enabled)
            {
                Disable();
                Log.Info("消息停留时间已停用，消息与表情恢复游戏原版停留时长。");
                return;
            }

            if (!_durationApplied) ApplyDuration();
            Log.Info("消息停留时间配置已热重载: durationSeconds=" + _settings.DurationSeconds);
        }

        public override void OnUpdate()
        {
            if (!_settings.Enabled || !_durationApplied) return;

            // 游戏登录或重载配置时可能重新填充 StaticGlobalData 缓存，每帧补一次抑制值。
            if (!ExpressionDurationOverride.Ensure())
            {
                _durationApplied = false;
                Log.Warn("游戏的消息停留配置已不可用，模组停止介入。");
                return;
            }

            double now = NowSeconds();
            if (now < _nextTick) return;
            _nextTick = now + TickIntervalSeconds;

            try
            {
                _lifetime.Tick(now, _settings.DurationSeconds, message => Log.Warn(message));
            }
            catch (Exception e)
            {
                // 场景切换或窗口重建期间可能取不到 UI；节流后继续尝试。
                Log.Warn("更新消息停留时间失败: " + e.Message);
            }
        }

        public override void OnSceneUnloaded(Scene scene)
        {
            _lifetime.Reset();
        }

        public override void OnUnload()
        {
            _lifetime.HideAllAndReset();
            ExpressionDurationOverride.Restore();
            _durationApplied = false;
        }

        private void ApplyDuration()
        {
            if (!ExpressionDurationOverride.Apply())
            {
                _durationApplied = false;
                Log.Warn("找不到游戏的消息停留配置，模组未启用。");
                return;
            }

            _durationApplied = true;
            int original = ExpressionDurationOverride.OriginalMilliseconds;
            Log.Info(original > 0
                ? "已按真实时间接管消息停留；游戏原停留时长 " + original + " 毫秒。"
                : "已按真实时间接管消息停留。");
        }

        private void Disable()
        {
            _lifetime.HideAllAndReset();
            ExpressionDurationOverride.Restore();
            _durationApplied = false;
        }

        private static double NowSeconds()
        {
            // 变速 hook 的是系统计时 API（QPC / GetTickCount 等），DateTime.UtcNow 不受影响。
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}
