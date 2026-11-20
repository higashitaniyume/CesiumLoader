using System;
using System.Collections.Generic;
using System.Text;

namespace FontMod
{
    /// <summary>
    /// 字体名处理：拼候选列表、做大小写/空格无关的比较。
    /// </summary>
    internal static class FontNames
    {
        private static readonly string[] BuiltInFallback =
        {
            "Microsoft YaHei",
            "Microsoft YaHei UI",
            "SimHei",
            "SimSun",
            "SimSun-ExtB",
            "Microsoft JhengHei",
            "Yu Gothic",
            "Meiryo",
            "Noto Sans CJK SC",
            "Segoe UI Symbol",
        };

        internal static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        internal static string[] BuildCandidates(string family, string fallbackCsv)
        {
            var list = new List<string>();
            AddUnique(list, family);
            if (!string.IsNullOrEmpty(fallbackCsv))
            {
                string[] parts = fallbackCsv.Split(',');
                for (int i = 0; i < parts.Length; i++) AddUnique(list, parts[i]);
            }
            for (int i = 0; i < BuiltInFallback.Length; i++) AddUnique(list, BuiltInFallback[i]);
            return list.ToArray();
        }

        internal static bool Contains(string[] installed, string family)
        {
            if (installed == null || installed.Length == 0) return true;
            string target = Normalize(family);
            if (target.Length == 0) return false;
            for (int i = 0; i < installed.Length; i++)
            {
                if (string.Equals(Normalize(installed[i]), target, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AddUnique(List<string> list, string name)
        {
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            string normalized = Normalize(name);
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(Normalize(list[i]), normalized, StringComparison.Ordinal)) return;
            }
            list.Add(name);
        }
    }
}
