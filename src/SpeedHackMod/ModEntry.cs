using System;
using System.Runtime.InteropServices;
using CesiumLoader.SDK;

namespace SpeedHackMod
{
    /// <summary>
    /// 变速 mod —— 演示 SDK 的 SpeedHack 接口。
    ///
    /// 作用: 用热键控制游戏时间流速(2x 加速 / 0.5x 减速 / 恢复 1x),
    ///       也可以设置"基础倍率"让游戏全程保持某个速度。
    ///
    /// ⚠️ 联机对局慎用: 变速影响本地感知的所有时间(动画/回合/网络超时),
    ///    服务器权威时间戳可能检测到异常节奏, 有断线/封号风险。
    ///
    /// 配置(configs/SpeedHackMod.json, 全部可选):
    ///   Enabled            = true   总开关(false 时 mod 不启动, 变速不生效)
    ///   BaseSpeed          = 1.0    基础倍率(0.1~10, 1.0=正常)。设为 2.0 则全程 2 倍速
    ///   SpeedUpKey         = "F1"   加速热键(按住/切换, 见 IsToggle)
    ///   SpeedUpValue       = 2.0    加速时倍率
    ///   SlowDownKey        = "F2"   减速热键
    ///   SlowDownValue      = 0.5    减速时倍率
    ///   ResetKey           = "F3"   恢复 1x 热键
    ///   IsToggle           = false  true=按一下切换(再按恢复 BaseSpeed), false=按住生效松手恢复
    ///   ReloadConfigOnTick = false  每次 tick 重读配置(改配置即时生效)
    /// </summary>
    [ModManifest("游戏变速", "1.0.0", "CesiumLoader", "热键控制游戏时间流速(SpeedHack SDK 接口示例)")]
    public static class ModEntry
    {
        private static SpeedHackConfig _cfg = new SpeedHackConfig();

        public static void Main()
        {
            SdkManifest.ExportSidecar();
            var cfg = SdkConfig.Load<SpeedHackConfig>("SpeedHackMod");
            if (!SdkConfig.Exists("SpeedHackMod")) SdkConfig.Save("SpeedHackMod", cfg);
            _cfg = cfg;

            if (!cfg.Enabled)
            {
                SdkLog.Info("SpeedHack", "mod 已禁用(Enabled=false), 跳过");
                return;
            }

            if (!SpeedHack.IsAvailable)
            {
                SdkLog.Warn("SpeedHack", "变速引擎不可用(加载器未安装 hook 或版本过旧), 请更新 version.dll");
                return;
            }

            SdkLog.Info("SpeedHack", $"变速引擎可用, 当前倍率 {SpeedHack.Speed:F1}");

            // 基础倍率: 全程保持
            if (cfg.BaseSpeed > 0.01 && Math.Abs(cfg.BaseSpeed - 1.0) > 0.01)
            {
                if (SpeedHack.SetSpeed(cfg.BaseSpeed))
                    SdkLog.Info("SpeedHack", $"基础倍率已设置: {cfg.BaseSpeed:F1}x");
            }

            ModBase.Run(OnInit, OnTick, delayMs: 5000, tag: "SpeedHack");
        }

        private static void OnInit()
        {
            SdkLog.Info("SpeedHack", "=== SpeedHack 就绪 ===");
            SdkLog.Info("SpeedHack", $"热键: {_cfg.SpeedUpKey}={_cfg.SpeedUpValue}x  {_cfg.SlowDownKey}={_cfg.SlowDownValue}x  {_cfg.ResetKey}=恢复1x  ({(_cfg.IsToggle ? "切换" : "按住")}模式)");
        }

        private static void OnTick()
        {
            try
            {
                if (_cfg.ReloadConfigOnTick)
                {
                    var fresh = SdkConfig.Load<SpeedHackConfig>("SpeedHackMod");
                    if (fresh != null) _cfg = fresh;
                }

                // 热键轮询(GetAsyncKeyState: 前台窗口按键即生效, 无需聚焦游戏)
                bool speedUp = IsKeyDown(_cfg.SpeedUpKey);
                bool slowDown = IsKeyDown(_cfg.SlowDownKey);
                bool reset = IsKeyDown(_cfg.ResetKey);

                double target = _cfg.BaseSpeed > 0.01 ? _cfg.BaseSpeed : 1.0;

                if (reset)
                {
                    target = 1.0;
                }
                else if (speedUp)
                {
                    target = _cfg.SpeedUpValue;
                }
                else if (slowDown)
                {
                    target = _cfg.SlowDownValue;
                }
                else if (!_cfg.IsToggle)
                {
                    // 按住模式: 松手恢复基础倍率
                    target = _cfg.BaseSpeed > 0.01 ? _cfg.BaseSpeed : 1.0;
                }

                // 只在倍率变化时调用(避免每 tick 写日志)
                if (Math.Abs(target - SpeedHack.Speed) > 0.001)
                {
                    if (SpeedHack.SetSpeed(target))
                        SdkLog.Info("SpeedHack", $"倍率 -> {target:F1}x");
                }
            }
            catch (Exception e)
            {
                SdkLog.Warn("SpeedHack", "tick 异常: " + e.Message);
            }
        }

        // ---------- 键盘 ----------

        // 虚拟键码表(常用): F1-F12 / 数字 / 字母
        private static int KeyCode(string key)
        {
            switch (key.ToUpperInvariant())
            {
                case "F1": return 0x70;
                case "F2": return 0x71;
                case "F3": return 0x72;
                case "F4": return 0x73;
                case "F5": return 0x74;
                case "F6": return 0x75;
                case "F7": return 0x76;
                case "F8": return 0x77;
                case "F9": return 0x78;
                case "F10": return 0x79;
                case "F11": return 0x7A;
                case "F12": return 0x7B;
                case "0": return 0x30; case "1": return 0x31; case "2": return 0x32;
                case "3": return 0x33; case "4": return 0x34; case "5": return 0x35;
                case "6": return 0x36; case "7": return 0x37; case "8": return 0x38;
                case "9": return 0x39;
                case "A": return 0x41; case "B": return 0x42; case "C": return 0x43;
                case "D": return 0x44; case "E": return 0x45; case "F": return 0x46;
                case "G": return 0x47; case "H": return 0x48; case "I": return 0x49;
                case "J": return 0x4A; case "K": return 0x4B; case "L": return 0x4C;
                case "M": return 0x4D; case "N": return 0x4E; case "O": return 0x4F;
                case "P": return 0x50; case "Q": return 0x51; case "R": return 0x52;
                case "S": return 0x53; case "T": return 0x54; case "U": return 0x55;
                case "V": return 0x56; case "W": return 0x57; case "X": return 0x58;
                case "Y": return 0x59; case "Z": return 0x5A;
                case "SHIFT": return 0x10;
                case "CTRL": case "CONTROL": return 0x11;
                case "ALT": return 0x12;
                default: return 0;
            }
        }

        private static bool IsKeyDown(string key)
        {
            int vk = KeyCode(key);
            if (vk == 0) return false;
            // 高位为 1 表示按下
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }

    /// <summary>SpeedHackMod 配置(公开字段, 见 SdkConfig 约定)。</summary>
    public class SpeedHackConfig
    {
        public bool Enabled = true;
        public double BaseSpeed = 1.0;
        public string SpeedUpKey = "F1";
        public double SpeedUpValue = 2.0;
        public string SlowDownKey = "F2";
        public double SlowDownValue = 0.5;
        public string ResetKey = "F3";
        public bool IsToggle = false;
        public bool ReloadConfigOnTick = false;
    }
}
