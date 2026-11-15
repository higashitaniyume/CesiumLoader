using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// mod 支持的平台标记，与 <see cref="ModPermission"/> 一样是位标记：
    /// 声明时直接按位组合，落盘（sidecar 的 <c>platforms</c>）再转成字符串数组。
    ///
    /// <see cref="None"/> 表示<b>没有声明</b>，按两个平台都支持处理 —— 旧 mod 与第三方
    /// mod 不写这个字段也能照常加载。平台只用于界面标注：加载器/引导不会因为平台
    /// 不匹配拒绝某个 mod。
    /// </summary>
    [Flags]
    public enum ModPlatform
    {
        /// <summary>没有声明（= 两个平台都支持）。</summary>
        None = 0,

        /// <summary>Windows（桌面版加载器）。</summary>
        Windows = 1 << 0,

        /// <summary>Android（安卓加载器）。</summary>
        Android = 1 << 1,

        /// <summary>两个平台都支持。</summary>
        All = Windows | Android,
    }

    /// <summary>
    /// <see cref="ModPlatform"/> 与 sidecar 字符串数组（<c>["windows","android"]</c>）之间的转换。
    /// 只认 <c>windows</c> / <c>android</c>；认不出的取值忽略，结果按 Windows 在前排列。
    /// </summary>
    public static class ModPlatforms
    {
        /// <summary>枚举 → 规范化的字符串数组（Windows 在前）；<see cref="ModPlatform.None"/> → 空数组。</summary>
        public static string[] ToNames(ModPlatform platforms)
        {
            if ((platforms & ModPlatform.Windows) != 0 && (platforms & ModPlatform.Android) != 0)
                return new[] { "windows", "android" };
            if ((platforms & ModPlatform.Windows) != 0) return new[] { "windows" };
            if ((platforms & ModPlatform.Android) != 0) return new[] { "android" };
            return Array.Empty<string>();
        }

        /// <summary>字符串数组（sidecar 原样）→ 枚举；认不出的取值忽略。</summary>
        public static ModPlatform Parse(IEnumerable<string> names)
        {
            if (names == null) return ModPlatform.None;

            ModPlatform result = ModPlatform.None;
            foreach (var raw in names)
            {
                if (raw == null) continue;
                string value = raw.Trim();
                if (string.Equals(value, "windows", StringComparison.OrdinalIgnoreCase))
                    result |= ModPlatform.Windows;
                else if (string.Equals(value, "android", StringComparison.OrdinalIgnoreCase))
                    result |= ModPlatform.Android;
            }
            return result;
        }

        /// <summary>字符串数组 → 规范化的字符串数组（只保留已知取值、去重、Windows 在前）。</summary>
        public static string[] Normalize(IEnumerable<string> platforms) => ToNames(Parse(platforms));

        /// <summary>声明里是否包含某个平台；未声明（<see cref="ModPlatform.None"/>）按两个平台都支持处理。</summary>
        public static bool Supports(ModPlatform declared, string platform)
        {
            if (declared == ModPlatform.None) return true;

            string target = platform == null ? "" : platform.Trim();
            if (string.Equals(target, "windows", StringComparison.OrdinalIgnoreCase))
                return (declared & ModPlatform.Windows) != 0;
            if (string.Equals(target, "android", StringComparison.OrdinalIgnoreCase))
                return (declared & ModPlatform.Android) != 0;
            return false;
        }
    }
}
