using System;
using System.Reflection;

namespace UnlockFrameRateMod
{
    /// <summary>
    /// 把额外帧率写进游戏的两张配置表:
    ///   - <c>Settings.RefreshRates</c>: 设置窗口“帧率”下拉框的数据来源;
    ///   - <c>STRSettings.LocalDict</c>: 下拉框文案(原生条目是“30帧/60帧/90帧/120帧”)。
    ///
    /// 两张表都是 protobuf 生成的消息, 字段只有 private set; 这里用反射赋值,
    /// 与游戏自己的 MergeFrom 走同一批 setter。所有方法只在主线程调用。
    /// </summary>
    internal static class FrameRateTable
    {
        private static int _stringBase = FrameRateOptions.DescriptionIdBase;

        /// <summary>补齐缺少的帧率条目; 返回本次新增的条数(已存在则 0)。</summary>
        internal static int Ensure(SettingsConfigure settings, STRSettingsConfigure strings, Action<string> warn)
        {
            if (settings == null || strings == null) return 0;

            _stringBase = ResolveStringBase(strings);
            int added = 0;

            for (int i = 0; i < FrameRateOptions.Values.Length; i++)
            {
                int value = FrameRateOptions.Values[i];
                int existingIndex = IndexOfValue(settings, value);
                if (existingIndex >= 0)
                {
                    // 条目已在(上次补过 / 游戏以后自带同值): 只补可能被重建掉的文案
                    var existing = settings.RefreshRates[existingIndex];
                    if (FrameRateOptions.IsExtraDescriptionId(existing.DescriptionID))
                        EnsureString(strings, existing.DescriptionID, value, warn);
                    continue;
                }

                int descriptionId = _stringBase + i;
                if (!EnsureString(strings, descriptionId, value, warn)) continue;

                var entry = CreateRate(FrameRateOptions.RateIdBase + i, value, descriptionId, warn);
                if (entry == null) continue;

                settings.RefreshRates.Add(entry);
                TryAddToDict(settings, FrameRateOptions.RateIdBase + i, entry, warn);
                added++;
            }

            return added;
        }

        /// <summary>移除本 mod 追加的条目与文案; 返回移除的帧率条数。</summary>
        internal static int Remove(SettingsConfigure settings, STRSettingsConfigure strings)
        {
            if (settings == null) return 0;

            int removed = 0;
            var rates = settings.RefreshRates;
            for (int i = rates.Count - 1; i >= 0; i--)
            {
                if (!FrameRateOptions.IsExtraDescriptionId(rates[i].DescriptionID)) continue;
                rates.RemoveAt(i);
                removed++;
            }

            if (strings != null)
            {
                for (int i = 0; i < FrameRateOptions.Values.Length; i++)
                    strings.LocalDict.Remove(_stringBase + i);
            }

            for (int i = 0; i < FrameRateOptions.Values.Length; i++)
                settings.RefreshRateDict.Remove(FrameRateOptions.RateIdBase + i);

            return removed;
        }

        /// <summary>构造一条帧率配置(供测试与补表共用)。</summary>
        internal static SettingsRefreshRateConfigure CreateRate(int id, int value, int descriptionId, Action<string> warn)
        {
            var entry = new SettingsRefreshRateConfigure();
            if (!Set(entry, "Id", id) || !Set(entry, "Value", value) || !Set(entry, "DescriptionID", descriptionId))
            {
                WarnOnce(warn, "无法写入帧率配置(Id/Value/DescriptionID)");
                return null;
            }
            return entry;
        }

        /// <summary>构造一条多语言文案(供测试与补表共用)。</summary>
        internal static STRSettingsLocalConfigure CreateString(int id, int framesPerSecond, Action<string> warn)
        {
            var text = new STRSettingsLocalConfigure();
            bool ok = Set(text, "Id", id)
                      && Set(text, "Simplified", FrameRateOptions.Simplified(framesPerSecond))
                      && Set(text, "Traditional", FrameRateOptions.Traditional(framesPerSecond))
                      && Set(text, "English", FrameRateOptions.English(framesPerSecond))
                      && Set(text, "Japanese", FrameRateOptions.Japanese(framesPerSecond))
                      && Set(text, "Korean", FrameRateOptions.Korean(framesPerSecond));
            if (!ok)
            {
                WarnOnce(warn, "无法写入帧率文案(STR Id=" + id + ")");
                return null;
            }
            return text;
        }

        private static bool EnsureString(STRSettingsConfigure strings, int descriptionId, int value, Action<string> warn)
        {
            if (strings.LocalDict.ContainsKey(descriptionId)) return true;
            var text = CreateString(descriptionId, value, warn);
            if (text == null) return false;
            strings.LocalDict[descriptionId] = text;
            return true;
        }

        private static int IndexOfValue(SettingsConfigure settings, int value)
        {
            var rates = settings.RefreshRates;
            for (int i = 0; i < rates.Count; i++)
            {
                if (rates[i].Value == value) return i;
            }
            return -1;
        }

        private static void TryAddToDict(SettingsConfigure settings, int id, SettingsRefreshRateConfigure entry, Action<string> warn)
        {
            try { settings.RefreshRateDict[id] = entry; }
            catch (Exception e) { WarnOnce(warn, "写入 RefreshRateDict 失败: " + e.Message); }
        }

        /// <summary>选一个不与现有文案冲突的 STR Id 段(通常就是 900001 起)。</summary>
        private static int ResolveStringBase(STRSettingsConfigure strings)
        {
            int baseId = FrameRateOptions.DescriptionIdBase;
            int limit = FrameRateOptions.DescriptionIdBase + FrameRateOptions.DescriptionIdSpan - FrameRateOptions.Values.Length;
            while (baseId <= limit)
            {
                bool clash = false;
                for (int i = 0; i < FrameRateOptions.Values.Length; i++)
                {
                    if (strings.LocalDict.ContainsKey(baseId + i)) { clash = true; break; }
                }
                if (!clash) return baseId;
                baseId += 1000;
            }
            return FrameRateOptions.DescriptionIdBase;
        }

        private static bool Set(object target, string propertyName, object value)
        {
            try
            {
                var property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WarnOnce(Action<string> warn, string message)
        {
            if (warn != null) warn(message);
        }
    }
}
