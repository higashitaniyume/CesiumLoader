using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK
{
    /// <summary>依赖图中的一个 mod 节点(工具/校验用输入)。</summary>
    public sealed class ModDependencyNode
    {
        public ModDependencyNode(string id, string version = null, string sdkVersion = null,
                                 IEnumerable<ModDependency> dependencies = null)
        {
            Id = id;
            Version = version;
            SdkVersion = sdkVersion;
            Dependencies = dependencies == null
                ? new List<ModDependency>()
                : new List<ModDependency>(dependencies);
        }

        /// <summary>mod 标识(程序集名)。</summary>
        public string Id { get; private set; }

        /// <summary>mod 版本(SemVer)。</summary>
        public string Version { get; private set; }

        /// <summary>mod 声明的 SDK 版本要求(SemVer; 空 = 不要求)。</summary>
        public string SdkVersion { get; private set; }

        /// <summary>依赖列表。</summary>
        public List<ModDependency> Dependencies { get; private set; }

        /// <summary>从 [ModManifest] 构造。</summary>
        public static ModDependencyNode FromManifest(ModManifestAttribute manifest)
        {
            if (manifest == null) return null;
            return new ModDependencyNode(manifest.Name, manifest.Version, manifest.SdkVersion, manifest.Dependencies);
        }
    }

    /// <summary>依赖问题类别。</summary>
    public enum ModDependencyIssueKind
    {
        /// <summary>依赖的 mod 不在本次集合里。</summary>
        MissingDependency,

        /// <summary>依赖的 mod 存在, 但版本低于 minVersion。</summary>
        VersionTooLow,

        /// <summary>mod 要求的 SDK 版本高于当前 SDK。</summary>
        SdkVersionTooNew,

        /// <summary>依赖的 mod 自身已被拒绝(缺失/版本不符/循环), 导致本 mod 无法排序。</summary>
        DependencyRejected,

        /// <summary>循环依赖。</summary>
        CircularDependency,

        /// <summary>id 为空。</summary>
        InvalidId,

        /// <summary>id 重复。</summary>
        DuplicateId,
    }

    /// <summary>一条依赖问题。</summary>
    public sealed class ModDependencyIssue
    {
        public ModDependencyIssue(string modId, ModDependencyIssueKind kind, string detail)
        {
            ModId = modId;
            Kind = kind;
            Detail = detail;
        }

        /// <summary>出问题的 mod。</summary>
        public string ModId { get; private set; }

        /// <summary>问题类别。</summary>
        public ModDependencyIssueKind Kind { get; private set; }

        /// <summary>面向人的说明。</summary>
        public string Detail { get; private set; }

        public override string ToString() { return ModId + ": " + Kind + " (" + Detail + ")"; }
    }

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

    /// <summary>
    /// 依赖图解析(拓扑排序) —— 原生加载器 <c>modmeta.cpp</c> 中同一算法的托管镜像。
    ///
    /// <para>
    /// <b>为什么还要有一份 C# 实现:</b> 运行期真正的加载顺序由原生加载器决定(那是权威),
    /// 但工具(<c>cesium verify</c>)、编辑器内检查、以及 mod 自己的能力探测都需要在
    /// <b>不启动游戏</b>的情况下提前发现"依赖缺失 / 版本不满足 / 循环依赖"。
    /// 两份实现共用同一套规则, 顺序结果一致:
    /// </para>
    ///
    /// <list type="number">
    /// <item>mod 要求的 SDK 版本高于当前 SDK → 拒绝;</item>
    /// <item>依赖的 mod 不在集合中 → 拒绝; 存在但版本低于 minVersion → 拒绝;</item>
    /// <item>对通过检查的 mod 建立边并做 Kahn 拓扑排序, 队列按名字排序保证确定性;</item>
    /// <item>排序结束后仍有入度的节点 = 循环依赖(或依赖了被拒绝的 mod) → 拒绝。</item>
    /// </list>
    ///
    /// <para>
    /// 被拒绝的 mod <b>不参与</b>边构建, 与原生实现一致 —— 因此依赖它的 mod 也不会被加载,
    /// 只是诊断上会被标成 <see cref="ModDependencyIssueKind.DependencyRejected"/> 而不是笼统的循环。
    /// </para>
    /// </summary>
    public static class ModDependencyResolver
    {
        /// <summary>用当前 SDK 版本解析。</summary>
        public static ModDependencyPlan Resolve(IEnumerable<ModDependencyNode> mods)
        {
            return Resolve(mods, SdkVersion.Current);
        }

        /// <summary>从 [ModManifest] 集合解析。</summary>
        public static ModDependencyPlan ResolveManifests(IEnumerable<ModManifestAttribute> manifests)
        {
            var nodes = new List<ModDependencyNode>();
            if (manifests != null)
            {
                foreach (var m in manifests)
                {
                    var node = ModDependencyNode.FromManifest(m);
                    if (node != null) nodes.Add(node);
                }
            }
            return Resolve(nodes, SdkVersion.Current);
        }

        /// <summary>
        /// 按指定 SDK 版本解析依赖图。
        /// </summary>
        /// <param name="mods">候选 mod 集合。</param>
        /// <param name="sdkVersion">当前 SDK 版本(空 = 跳过 SDK 版本协商)。</param>
        public static ModDependencyPlan Resolve(IEnumerable<ModDependencyNode> mods, string sdkVersion)
        {
            var plan = new ModDependencyPlan();
            if (mods == null) return plan;

            // ---- 归一化输入: id 去重、清理空依赖 ----
            var byId = new Dictionary<string, ModDependencyNode>(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            foreach (var m in mods)
            {
                if (m == null) continue;
                string id = m.Id == null ? null : m.Id.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    plan.Issues.Add(new ModDependencyIssue("(空)", ModDependencyIssueKind.InvalidId, "mod id 为空"));
                    continue;
                }
                if (byId.ContainsKey(id))
                {
                    plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.DuplicateId, "id 重复, 已忽略后一个"));
                    continue;
                }
                byId[id] = m;
                ids.Add(id);
            }

            // ---- 第一遍: SDK 版本协商 + 依赖可用性检查, 判定直接拒绝 ----
            var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 依赖了被拒绝的 mod

            foreach (var id in ids)
            {
                var m = byId[id];

                if (!string.IsNullOrEmpty(m.SdkVersion) && !string.IsNullOrEmpty(sdkVersion) &&
                    SdkVersion.Compare(m.SdkVersion, sdkVersion) > 0)
                {
                    rejected.Add(id);
                    plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.SdkVersionTooNew,
                        "要求 SDK " + m.SdkVersion + ", 当前 " + sdkVersion));
                    continue;
                }

                bool failed = false;
                foreach (var dep in m.Dependencies)
                {
                    if (dep == null || string.IsNullOrEmpty(dep.Id)) continue;
                    string depId = dep.Id.Trim();

                    ModDependencyNode target;
                    if (!byId.TryGetValue(depId, out target))
                    {
                        rejected.Add(id);
                        plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.MissingDependency,
                            "缺少依赖 " + depId));
                        failed = true;
                        break;
                    }

                    if (!string.IsNullOrEmpty(dep.MinVersion) && !string.IsNullOrEmpty(target.Version) &&
                        SdkVersion.Compare(target.Version, dep.MinVersion) < 0)
                    {
                        rejected.Add(id);
                        plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.VersionTooLow,
                            "依赖 " + depId + " 需要 >= " + dep.MinVersion + ", 实际 " + target.Version));
                        failed = true;
                        break;
                    }

                    if (rejected.Contains(depId) || deferred.Contains(depId))
                    {
                        deferred.Add(id);
                        plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.DependencyRejected,
                            "依赖的 " + depId + " 已被拒绝"));
                        failed = true;
                        break;
                    }
                }
                if (failed) continue;
            }

            // ---- 第二遍: 建图 + Kahn 拓扑排序 ----
            var indeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                indeg[id] = 0;
                adj[id] = new List<string>();
            }

            foreach (var id in ids)
            {
                if (rejected.Contains(id) || deferred.Contains(id)) continue;
                var m = byId[id];
                int count = 0;
                foreach (var dep in m.Dependencies)
                {
                    if (dep == null || string.IsNullOrEmpty(dep.Id)) continue;
                    string depId = dep.Id.Trim();
                    if (rejected.Contains(depId) || deferred.Contains(depId)) continue;
                    if (!indeg.ContainsKey(depId)) continue;
                    adj[depId].Add(id);
                    count++;
                }
                indeg[id] += count;
            }

            var queue = new List<string>();
            foreach (var id in ids)
                if (indeg[id] == 0 && !rejected.Contains(id) && !deferred.Contains(id)) queue.Add(id);
            queue.Sort(StringComparer.OrdinalIgnoreCase);

            while (queue.Count > 0)
            {
                string cur = queue[0];
                queue.RemoveAt(0);
                plan.Order.Add(cur);

                foreach (var next in adj[cur])
                {
                    if (rejected.Contains(next) || deferred.Contains(next)) continue;
                    indeg[next] = indeg[next] - 1;
                    if (indeg[next] == 0) queue.Add(next);
                }
                queue.Sort(StringComparer.OrdinalIgnoreCase);
            }

            // ---- 剩余仍有入度的: 循环依赖 ----
            foreach (var id in ids)
            {
                if (rejected.Contains(id) || deferred.Contains(id)) continue;
                if (indeg[id] > 0)
                {
                    rejected.Add(id);
                    plan.Issues.Add(new ModDependencyIssue(id, ModDependencyIssueKind.CircularDependency,
                        "存在循环依赖"));
                }
            }

            // ---- 输出: 保持确定性排序 ----
            foreach (var id in ids)
            {
                if (rejected.Contains(id) || deferred.Contains(id)) plan.Rejected.Add(id);
            }
            plan.Rejected.Sort(StringComparer.OrdinalIgnoreCase);

            return plan;
        }
    }
}
