using System;
using System.Text;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Core;

namespace UnlockFrameRateMod
{
    /// <summary>
    /// 入口。原生加载器调用 <c>UnlockFrameRateMod.ModEntry.Main()</c>(boot 线程, 非主线程)。
    ///
    /// 启动延迟取 10 秒: 只读写游戏配置表与 Unity 的帧率属性, 不碰 NetManager / UIManager,
    /// 用不着完整的 30 秒安全窗口; 与变速 mod 早接管热键所等待的时间相同。
    /// </summary>
    public static class ModEntry
    {
        private const int StartupDelayMs = 10000;

        public static void Main()
        {
            ModBase.Run(new FrameRateController(), StartupDelayMs, "UnlockFrameRate");
        }
    }

    /// <summary>
    /// 在游戏「设置 → 屏幕 → 帧率」下拉框里追加 144 / 165 / 240 / 360 / 不限帧 选项。
    ///
    /// 做法是运行期往两张配置表里补条目:
    ///   - <c>Settings.RefreshRates</c> — 下拉框的数据来源, 选中后游戏自己会写
    ///     <c>Application.targetFrameRate = GetFrames()</c>;
    ///   - <c>STRSettings.LocalDict</c> — 条目文案(与原生“30帧/60帧/90帧/120帧”同样的格式)。
    ///
    /// 配置表在游戏启动/重登时可能整体重建, 因此每 2 秒检查一次并补回条目;
    /// 每秒检查一次“当前选中的是不是本 mod 追加的选项”, 是的话把它的值写进引擎。
    /// 这一步只用于修正启动时索引越界被压回 120 的情况, 不会覆盖游戏原生选项。
    /// </summary>
    public sealed class FrameRateController : ModBase
    {
        private const long EnsureIntervalTicks = 20000000;   // 2 秒
        private const long EnforceIntervalTicks = 10000000;  // 1 秒

        private ModConfig _config;
        private bool _enabled = true;
        private bool _settingsReady;
        private bool _loggedNotReady;
        private bool _loggedWriteFailure;
        private long _nextEnsureTicks;
        private long _nextEnforceTicks;

        public override string Name { get { return "帧率解锁"; } }
        public override string Version { get { return "1.1.0"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        public override void OnLoad()
        {
            // 配置表可能还没加载完; 失败就等 OnUpdate 的巡检重试。
            TryEnsure("加载");
        }

        public override void OnInitialize()
        {
            _config = Config;
            EnsureConfigDefaults();
            LoadConfig();

            Log.Info("配置: " + (_config != null ? (_config.Path ?? "(未知路径)") : "(不可用)"));
            Log.Info("目标: 在游戏内 设置→屏幕→帧率 列表追加 " + DescribeOptions());

            if (_enabled) TryEnsure("启动");
            else RemoveEntries("启动");

            EnforceSelectedRate();

            long now = DateTime.UtcNow.Ticks;
            _nextEnsureTicks = now + EnsureIntervalTicks;
            _nextEnforceTicks = now + EnforceIntervalTicks;
        }

        public override void OnUpdate()
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;

                if (now >= _nextEnsureTicks)
                {
                    _nextEnsureTicks = now + EnsureIntervalTicks;
                    if (_enabled) TryEnsure("巡检");
                }

                if (!_settingsReady) return;

                if (now >= _nextEnforceTicks)
                {
                    _nextEnforceTicks = now + EnforceIntervalTicks;
                    EnforceSelectedRate();
                }
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnUpdate", e);
            }
        }

        public override void OnConfigChanged()
        {
            EnsureConfigDefaults();
            LoadConfig();

            if (_enabled)
            {
                TryEnsure("配置");
                EnforceSelectedRate();
            }
            else
            {
                RemoveEntries("配置");
            }
        }

        // =====================================================================
        // 配置表
        // =====================================================================

        /// <summary>补条目; 配置表未就绪时返回 false 并等待下次巡检。</summary>
        private bool TryEnsure(string reason)
        {
            SettingsConfigure settings = StaticConfigure.Settings;
            STRSettingsConfigure strings = StaticConfigure.STRSettings;

            if (settings == null || strings == null)
            {
                if (!_loggedNotReady)
                {
                    _loggedNotReady = true;
                    Log.Info("游戏配置表尚未就绪, 等待初始化后重试");
                }
                return false;
            }

            _loggedNotReady = false;

            int added = FrameRateTable.Ensure(settings, strings, message => Log.Warn(message));
            _settingsReady = true;

            if (added > 0)
            {
                Log.Info("帧率列表已扩展(" + reason + "): " + DescribeOptions() +
                         " (当前 " + settings.RefreshRates.Count + " 项)");
            }
            else if (settings.RefreshRates.Count > 0 && !HasAnyExtra(settings))
            {
                Log.Warn("帧率列表没有出现额外选项, 下一次巡检会重试");
            }

            return true;
        }

        /// <summary>移除追加条目, 并让游戏当前(被压回过的)设置重新生效。</summary>
        private void RemoveEntries(string reason)
        {
            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null)
            {
                _settingsReady = false;
                return;
            }

            int removed = FrameRateTable.Remove(settings, StaticConfigure.STRSettings);
            _settingsReady = false;

            if (removed > 0)
            {
                Log.Info("已移除额外帧率(" + reason + "): " + removed + " 项");
                try
                {
                    int frames = GameSettings.GetFrames();
                    UnityFrameRate.SetTarget(frames);
                }
                catch (Exception e)
                {
                    Log.Warn("恢复游戏帧率设置失败: " + e.Message);
                }
            }
        }

        /// <summary>
        /// 选中的是本 mod 追加的条目时, 把它的值写进引擎。
        /// 游戏在启动时若还没看到追加条目, 会把越界的存档索引压回 120 —— 这里把它纠正回来。
        /// </summary>
        private void EnforceSelectedRate()
        {
            if (!_enabled) return;

            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null) return;

            int index;
            try { index = GameSettings.GetFramesIndex(); }
            catch (Exception e)
            {
                Log.Warn("读取帧率设置失败: " + e.Message);
                return;
            }

            var rates = settings.RefreshRates;
            if (index < 0 || index >= rates.Count) return;

            var selected = rates[index];
            if (!FrameRateOptions.IsExtraDescriptionId(selected.DescriptionID)) return;

            int wanted = selected.Value;
            if (UnityFrameRate.Target == wanted) return;

            if (UnityFrameRate.SetTarget(wanted))
            {
                Log.Info("按游戏内选择应用帧率: " + FrameRateOptions.Describe(wanted));
            }
            else if (!_loggedWriteFailure)
            {
                _loggedWriteFailure = true;
                Log.Warn("写入目标帧率失败: " + UnityFrameRate.Describe());
            }
        }

        private static bool HasAnyExtra(SettingsConfigure settings)
        {
            var rates = settings.RefreshRates;
            for (int i = 0; i < rates.Count; i++)
            {
                if (FrameRateOptions.IsExtraDescriptionId(rates[i].DescriptionID)) return true;
            }
            return false;
        }

        // =====================================================================
        // 配置
        // =====================================================================

        private void EnsureConfigDefaults()
        {
            if (_config == null) return;

            _config.SetDefault("enabled", true, ConfigDoc.Of(
                "总开关：关闭后从游戏的帧率列表里移除额外选项，恢复原版列表。",
                "總開關：關閉後從遊戲的幀率列表移除額外選項，恢復原版列表。",
                "Master switch: when off, the extra frame-rate options are removed from the game's list.",
                "全体スイッチ：オフにするとゲームのフレームレート一覧から追加項目を外します。"));

            // 1.0 的配置键已被“游戏内列表选择”取代, 免得用户以为改它们还有用。
            bool removed = false;
            removed |= _config.Remove("frameRate");
            removed |= _config.Remove("disableVSync");
            removed |= _config.Remove("toggleKey");
            removed |= _config.Remove("notify");
            if (removed) Log.Info("已清理旧版配置键: 帧率改由游戏内列表选择");

            _config.SaveIfDirty();
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            _enabled = _config.GetBool("enabled", true);
        }

        private static string DescribeOptions()
        {
            var text = new StringBuilder();
            for (int i = 0; i < FrameRateOptions.Values.Length; i++)
            {
                if (i > 0) text.Append(" / ");
                text.Append(FrameRateOptions.Describe(FrameRateOptions.Values[i]));
            }
            return text.ToString();
        }
    }
}
