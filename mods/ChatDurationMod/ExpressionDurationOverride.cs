using System;
using System.Collections.Generic;
using System.Reflection;

namespace ChatDurationMod
{
    /// <summary>
    /// 把游戏静态配置里的消息停留时长临时抬到极大值，让游戏自己的计时器在正常对局中永远不会先隐藏条目。
    /// 实际隐藏由 <see cref="ExpressionListLifetime"/> 按真实时间完成。
    ///
    /// 只改 <c>StaticGlobalData</c> 的整数缓存，不动配置表、不落盘；停用/卸载时恢复原值。
    /// 游戏在登录或重载配置时可能重新填充缓存，所以运行期间每次扫描都会调用 <see cref="Ensure"/> 补回。
    /// </summary>
    internal static class ExpressionDurationOverride
    {
        internal const string ConfigKey = "GAME_CHARACTER_EXPRESSION_DURATION";

        /// <summary>
        /// 用 1 天毫秒数压住游戏计时：即使 100 倍速也要约 14 分钟真实时间才会到点，
        /// 远高于可配置的 300 秒；同时避免极大 float 秒数在第三方计时器里的精度问题。
        /// </summary>
        internal const int SuppressedMilliseconds = 24 * 60 * 60 * 1000;

        private static FieldInfo _cacheField;
        private static bool _resolved;
        private static bool _captured;
        private static bool _hadOriginal;
        private static int _original;
        private static bool _applied;

        /// <summary>被覆盖前的原值；尚未捕获时返回 -1。</summary>
        internal static int OriginalMilliseconds
        {
            get { return _captured && _hadOriginal ? _original : -1; }
        }

        /// <summary>捕获原值并写入抑制值；找不到游戏字段时返回 false。</summary>
        internal static bool Apply()
        {
            var cache = Cache();
            if (cache == null) return false;

            CaptureOriginal(cache);
            cache[ConfigKey] = SuppressedMilliseconds;
            _applied = true;
            return true;
        }

        /// <summary>运行期间保持抑制值，处理游戏重填缓存的情况。</summary>
        internal static bool Ensure()
        {
            var cache = Cache();
            if (cache == null) return false;

            CaptureOriginal(cache);
            int current;
            if (!cache.TryGetValue(ConfigKey, out current) || current != SuppressedMilliseconds)
            {
                cache[ConfigKey] = SuppressedMilliseconds;
                _applied = true;
            }
            return true;
        }

        /// <summary>恢复游戏原值。</summary>
        internal static void Restore()
        {
            var cache = Cache();
            if (cache != null)
            {
                if (_captured)
                {
                    if (_hadOriginal) cache[ConfigKey] = _original;
                    else cache.Remove(ConfigKey);
                }
                else if (_applied)
                {
                    cache.Remove(ConfigKey);
                }
            }

            _captured = false;
            _hadOriginal = false;
            _original = 0;
            _applied = false;
        }

        private static void CaptureOriginal(Dictionary<string, int> cache)
        {
            if (_captured) return;

            int current;
            if (cache.TryGetValue(ConfigKey, out current) && current != SuppressedMilliseconds)
            {
                _original = current;
                _hadOriginal = true;
                _captured = true;
            }
        }

        private static Dictionary<string, int> Cache()
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    _cacheField = typeof(global::StaticGlobalData).GetField(
                        "_cachedIntDict", BindingFlags.Static | BindingFlags.NonPublic);
                }
                catch
                {
                    _cacheField = null;
                }
            }

            if (_cacheField == null) return null;
            try { return _cacheField.GetValue(null) as Dictionary<string, int>; }
            catch { return null; }
        }
    }
}
