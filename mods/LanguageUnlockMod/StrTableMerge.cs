using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 把语言数据包合并进游戏的 STR 配置表。
    ///
    /// 每张 STR 表都是 protobuf 生成的 <c>MapField&lt;int, STR*LocalConfigure&gt;</c>，
    /// 每个条目自带 Simplified / English / Japanese / Traditional 五列（韩语列暂无数据）。
    /// 属性只有 private set，这里用反射写入，与游戏自己的 MergeFrom 走同一批 setter。
    ///
    /// 数据包里没有的 Id 一律回退成该条目现有的简中文本，避免新内容变成空白。
    /// </summary>
    internal static class StrTableMerge
    {
        private sealed class Accessor
        {
            internal PropertyInfo Key;
            internal PropertyInfo Value;
            internal PropertyInfo Simplified;
            internal PropertyInfo Target;
        }

        private static readonly Dictionary<Type, Dictionary<string, Accessor>> Cache =
            new Dictionary<Type, Dictionary<string, Accessor>>();

        /// <summary>把所有 STR 表里目标语言的列写一遍；返回写入的条目数。</summary>
        internal static int ApplyAll(LanguagePack pack, LanguageType language)
        {
            string column = LanguageCodes.StrColumn(language);
            if (column == null) return 0;

            var properties = typeof(StaticConfigure).GetProperties(BindingFlags.Public | BindingFlags.Static);
            int changed = 0;

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.Name.StartsWith("STR", StringComparison.Ordinal)) continue;
                if (property.PropertyType == typeof(string)) continue;

                object table;
                try { table = property.GetValue(null); }
                catch { continue; }
                if (table == null) continue;    // 配置表还没加载

                PropertyInfo dictProperty = table.GetType().GetProperty("LocalDict");
                if (dictProperty == null) continue;

                object map;
                try { map = dictProperty.GetValue(table); }
                catch { continue; }
                if (map == null) continue;

                Dictionary<int, string> texts = null;
                if (pack != null) pack.Tables.TryGetValue(property.Name, out texts);

                changed += ApplyMap(map, texts, column);
            }

            return changed;
        }

        /// <summary>把一张表里的目标列写成 <paramref name="texts"/>，缺失的条目回退简中。</summary>
        internal static int ApplyMap(object mapField, Dictionary<int, string> texts, string targetColumn)
        {
            if (mapField == null || string.IsNullOrEmpty(targetColumn)) return 0;

            int changed = 0;
            var enumerable = mapField as IEnumerable;
            if (enumerable == null) return 0;

            foreach (object item in enumerable)
            {
                if (item == null) continue;
                Accessor accessor = Resolve(item.GetType(), targetColumn);
                if (accessor == null) continue;

                int id;
                try { id = (int)accessor.Key.GetValue(item); }
                catch { continue; }

                object entry;
                try { entry = accessor.Value.GetValue(item); }
                catch { continue; }
                if (entry == null) continue;

                string desired;
                if (texts == null || !texts.TryGetValue(id, out desired) || string.IsNullOrEmpty(desired))
                {
                    desired = Get(accessor.Simplified, entry);
                    if (desired == null) desired = string.Empty;
                }

                if (string.Equals(Get(accessor.Target, entry), desired, StringComparison.Ordinal)) continue;

                try
                {
                    accessor.Target.SetValue(entry, desired);
                    changed++;
                }
                catch { }
            }

            return changed;
        }

        private static string Get(PropertyInfo property, object entry)
        {
            try { return property.GetValue(entry) as string; }
            catch { return null; }
        }

        private static Accessor Resolve(Type pairType, string targetColumn)
        {
            Dictionary<string, Accessor> byColumn;
            if (Cache.TryGetValue(pairType, out byColumn))
            {
                Accessor cached;
                if (byColumn.TryGetValue(targetColumn, out cached)) return cached;
            }
            else
            {
                byColumn = new Dictionary<string, Accessor>(StringComparer.Ordinal);
                Cache[pairType] = byColumn;
            }

            try
            {
                PropertyInfo value = pairType.GetProperty("Value");
                PropertyInfo key = pairType.GetProperty("Key");
                if (value == null || key == null) return null;

                Type entryType = value.PropertyType;
                PropertyInfo simplified = entryType.GetProperty("Simplified");
                PropertyInfo target = entryType.GetProperty(targetColumn);
                if (simplified == null || target == null) return null;

                var accessor = new Accessor
                {
                    Key = key,
                    Value = value,
                    Simplified = simplified,
                    Target = target
                };
                byColumn[targetColumn] = accessor;
                return accessor;
            }
            catch
            {
                return null;
            }
        }
    }
}

