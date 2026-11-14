using System;
using CesiumLoader.SDK.Logging;
using UnityEngine;

namespace CrashProbeMod
{
    /// <summary>config.json 的强类型视图(带范围钳制与热键解析)。</summary>
    internal sealed class CrashProbeConfig
    {
        internal bool Enabled = true;
        internal bool TriggerOnSettings = true;
        internal int DelayMs = 1500;
        internal KeyCode Hotkey = KeyCode.F8;

        internal static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            return value;
        }

        /// <summary>解析热键名; "none"/空 表示不绑定; 不能识别的名字退回 F8 并记一条日志。</summary>
        internal static KeyCode ParseKey(string name, ModLogger log)
        {
            if (string.IsNullOrEmpty(name)) return KeyCode.None;
            if (string.Equals(name.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return KeyCode.None;

            try
            {
                KeyCode parsed;
                if (Enum.TryParse(name.Trim(), true, out parsed) && parsed != KeyCode.None) return parsed;
            }
            catch { }

            if (log != null) log.Warn("无法识别热键 \"" + name + "\", 改用 F8");
            return KeyCode.F8;
        }
    }
}
