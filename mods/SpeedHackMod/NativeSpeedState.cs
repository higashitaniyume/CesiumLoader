using System;
using System.IO;
using CesiumLoader.SDK.Speed;

namespace SpeedHackMod
{
    /// <summary>
    /// Read an explicit acknowledgement without SpeedHack.Speed's 1x error fallback.
    /// Keep this inside the mod so existing SDK installations remain compatible.
    /// </summary>
    internal static class NativeSpeedState
    {
        internal static double? Read()
        {
            try { return Parse(File.ReadAllText(Path.Combine(SpeedHack.Directory, "state.txt"))); }
            catch { return null; }
        }

        internal static double? Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 64 * 1024) return null;
            double? speed = null, hooks = null;
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length;
                int equals = text.IndexOf('=', start);
                if (equals >= start && equals < end)
                {
                    string key = text.Substring(start, equals - start).Trim();
                    if (key == "speed" || key == "hooks")
                    {
                        double value;
                        if (!TryNumber(text.Substring(equals + 1, end - equals - 1).Trim(), out value)) return null;
                        if (key == "speed")
                        {
                            if (speed.HasValue || value < SpeedHack.MinSpeed || value > SpeedHack.HardMaxSpeed) return null;
                            speed = value;
                        }
                        else
                        {
                            if (hooks.HasValue || value <= 0 || value % 1 != 0) return null;
                            hooks = value;
                        }
                    }
                }
                start = end + 1;
            }
            return hooks.HasValue ? speed : null;
        }

        // Native state uses plain ASCII decimals. Avoid parsing overloads that may
        // be absent from HybridCLR's BCL, as the SDK's state reader does.
        private static bool TryNumber(string text, out double value)
        {
            value = 0;
            bool dot = false;
            double fraction = 1;
            if (text.Length == 0 || text[0] < '0' || text[0] > '9' || text[text.Length - 1] == '.') return false;
            foreach (char c in text)
            {
                if (c == '.' && !dot) { dot = true; continue; }
                if (c < '0' || c > '9') return false;
                if (dot) { fraction *= 0.1; value += (c - '0') * fraction; }
                else value = value * 10 + c - '0';
                if (value > 1000000 || fraction < 1e-9) return false;
            }
            return true;
        }
    }
}
