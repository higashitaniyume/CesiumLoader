using System;
using CesiumLoader.SDK.Mods;

namespace FontMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new FontController());
        }
    }

    /// <summary>
    /// 系统字体 v1.1.0：
    /// 统一接管 FairyGUI 全部 _TMP SDF 字体（含 JingNanBoBoHei_TMP 及标题/数字字体）、
    /// 旧版 DynamicFont（通过 DynamicBridgeTmpFont 无损桥接至高清 SDF）、场景 3D TMP_Text 与 SDK 浮层字体；
    /// 自动导出系统与用户目录字体列表供 Toys 选择，支持配置热重载与随时恢复游戏默认。
    /// </summary>
    public sealed class FontController : ModBase
    {
        private const double CheckIntervalSeconds = 0.5;

        private readonly FontReplacement _replacement = new FontReplacement();
        private FontSettings _settings = new FontSettings();
        private double _nextCheck;

        public override string Name { get { return "系统字体"; } }
        public override string Version { get { return "1.1.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get
            {
                return "全面替换游戏界面字体（含全部 _TMP SDF 字体与旧版动态字体）；支持系统/用户目录字体及本地字体文件直读，游戏中即时生效，可随时换回默认。";
            }
        }

        public override void OnInitialize()
        {
            FontSettings.EnsureDefaults(Config);
            FontConfigDocs.Apply(Config);
            if (Config != null) Config.SaveIfDirty();

            _settings = FontSettings.Read(Config);
            Log.Info("系统字体 v1.1.0: " + _settings.DescribeTarget());

            _replacement.ExportInstalledFontNames(Log);

            if (!_settings.HasTarget)
            {
                Log.Info("未启用字体替换，保持游戏默认字体。");
                return;
            }

            ApplySafely();
        }

        public override void OnUpdate()
        {
            // 每帧轻量刷新 TMP 动态图集 GPU 上传队列
            _replacement.FlushDynamicAtlases();

            double now = NowSeconds();
            if (now < _nextCheck) return;
            _nextCheck = now + CheckIntervalSeconds;

            _replacement.ExportInstalledFontNames(Log);

            if (!_settings.HasTarget) return;

            ApplySafely();
        }

        public override void OnConfigChanged()
        {
            FontSettings next = FontSettings.Read(Config);
            bool retarget = !next.SameAssetConfig(_settings);
            _settings = next;

            if (!_settings.HasTarget)
            {
                _replacement.Restore(Log);
                Log.Info("字体替换已关闭，恢复游戏默认字体。");
                return;
            }

            if (retarget) _replacement.Reset(Log);
            Log.Info("字体配置已热重载: " + _settings.DescribeTarget());
            ApplySafely();
        }

        public override void OnUnload()
        {
            _replacement.Restore(Log);
        }

        private void ApplySafely()
        {
            try
            {
                _replacement.Apply(_settings, SafeModRoot(), SafeLoaderRoot(), Log);
            }
            catch (Exception e)
            {
                Log.Warn("应用系统字体失败: " + e.Message);
            }
        }

        private static string SafeLoaderRoot()
        {
            try { return CesiumLoader.SDK.IO.Path.LoaderRootDirectory; }
            catch { return ""; }
        }

        /// <summary>
        /// 模组自身目录（用于解析相对模组目录的字体文件路径）。
        /// 走 SDK 的 ModContext：热更程序集由 Assembly.Load(byte[]) 载入，
        /// Assembly.Location 在 HybridCLR 解释器下会抛 MissingMethodException。
        /// </summary>
        private static string SafeModRoot()
        {
            try { return CesiumLoader.SDK.IO.Path.CurrentModDirectory; }
            catch { return ""; }
        }

        private static double NowSeconds()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}
