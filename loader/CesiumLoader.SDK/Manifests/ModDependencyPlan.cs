using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>依赖解析结果。</summary>
    public sealed class ModDependencyPlan
    {
        internal ModDependencyPlan()
        {
            Order = new List<string>();
            Rejected = new List<string>();
            Issues = new List<ModDependencyIssue>();
        }

        /// <summary>可加载顺序(依赖在前; 同层按名字排序, 与加载器一致)。</summary>
        public List<string> Order { get; private set; }

        /// <summary>被拒绝的 mod id。</summary>
        public List<string> Rejected { get; private set; }

        /// <summary>问题清单。</summary>
        public List<ModDependencyIssue> Issues { get; private set; }

        /// <summary>是否没有发现问题。</summary>
        public bool Ok { get { return Issues.Count == 0; } }

        /// <summary>多行文本(工具输出)。</summary>
        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("加载顺序(" + Order.Count + "): " + (Order.Count == 0 ? "(空)" : string.Join(" -> ", Order.ToArray())));
            if (Rejected.Count > 0)
                sb.AppendLine("已拒绝(" + Rejected.Count + "): " + string.Join(", ", Rejected.ToArray()));
            for (int i = 0; i < Issues.Count; i++) sb.AppendLine("  ! " + Issues[i]);
            return sb.ToString();
        }

        public override string ToString() { return ToText(); }
    }
}
