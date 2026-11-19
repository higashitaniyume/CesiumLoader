using System;
using CesiumLoader.SDK.Configuration;
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
    /// 系统字体替换。
    ///
    /// 只动 FairyGUI 的旧版动态字体（默认正文 <c>JingNanBoBoHei</c> 等），
    /// 带 <c>_TMP</c> 的预烘焙 SDF 字体不在本版本范围内。
    /// 配置变化由 SDK 热重载（每帧轮询），登录/切语言后 <c>FontManager.Clear()</c>
    /// 会重建字体，所以用一个低频轮询把替换补回去。
    /// </summary>
    public sealed class FontController : ModBase
    {
        private const double CheckIntervalSeconds = 0.5;

        private readonly FontReplacement _replacement = new FontReplacement();
        private FontSettings _settings = new FontSettings();
        private double _nextCheck;

        public override string Name { get { return "系统字体"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "把游戏界面字体替换成系统里已安装的字体，可随时换回游戏默认。"; }
        }

        public override void OnInitialize()
        {
            FontSettings.EnsureDefaults(Config);
            FontConfigDocs.Apply(Config);
            if (Config != null) Config.SaveIfDirty();

            _settings = FontSettings.Read(Config);
            Log.Info("系统字体: enabled=" + _settings.Enabled
                + ", fontFamily=" + (_settings.FontFamily.Length == 0 ? "(未设置)" : _settings.FontFamily)
                + ", applyToAllFonts=" + _settings.ApplyToAllFonts);

            // 字体页要拿到游戏真正认识的字体名，所以即使没配置字体也要尝试导出。
            _replacement.ExportInstalledFontNames(Log);

            if (!_settings.Enabled || _settings.FontFamily.Length == 0)
            {
                Log.Info("未启用字体替换，保持游戏默认字体。");
                return;
            }

            ApplySafely();
        }

        public override void OnUpdate()
        {
            double now = NowSeconds();
            if (now < _nextCheck) return;
            _nextCheck = now + CheckIntervalSeconds;

            // 字体注册晚于模组初始化时，由轮询把字体名列表导出给 Toys。
            _replacement.ExportInstalledFontNames(Log);

            if (!_settings.Enabled || _settings.FontFamily.Length == 0) return;

            // 登录/切语言会让 UIManager.AsyncLoadFonts 重新注册字体，这里把替换补回去；
            // 已经替换过的实例会被跳过，开销只是一次字典遍历。
            ApplySafely();
        }

        public override void OnConfigChanged()
        {
            FontSettings next = FontSettings.Read(Config);
            bool retarget = !next.SameTarget(_settings);
            _settings = next;

            if (!_settings.Enabled || _settings.FontFamily.Length == 0)
            {
                _replacement.Restore(Log);
                Log.Info("字体替换已关闭，恢复游戏默认字体。");
                return;
            }

            if (retarget) _replacement.Reset(Log);
            Log.Info("字体配置已热重载: fontFamily=" + _settings.FontFamily
                + ", applyToAllFonts=" + _settings.ApplyToAllFonts);
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
                _replacement.Apply(
                    _settings.FontFamily,
                    _settings.FallbackFonts,
                    _settings.ApplyToAllFonts,
                    _settings.CheckCoverage,
                    Log);
            }
            catch (Exception e)
            {
                // 任何异常都不能逃进游戏的每帧循环；记录后下次轮询再试。
                Log.Warn("应用字体失败: " + e.Message);
            }
        }

        private static double NowSeconds()
        {
            // 变速 hook 的是系统计时 API，DateTime.UtcNow 不受影响。
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}
