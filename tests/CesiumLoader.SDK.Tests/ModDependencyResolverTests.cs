using System.Collections.Generic;
using System.Linq;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 依赖图解析(拓扑排序)。规则与原生加载器 modmeta.cpp 一致:
    /// SDK 版本过高 / 依赖缺失 / 依赖版本过低 → 拒绝; 循环依赖 → 拒绝; 同层按名字排序。
    /// </summary>
    public class ModDependencyResolverTests
    {
        // ---- 构造辅助(避免命名实参 + params 的歧义) ----
        private static ModDependencyNode Node(string id)
        {
            return new ModDependencyNode(id, "1.0.0");
        }

        private static ModDependencyNode NodeVer(string id, string version)
        {
            return new ModDependencyNode(id, version);
        }

        private static ModDependencyNode NodeSdk(string id, string sdkVersion)
        {
            return new ModDependencyNode(id, "1.0.0", sdkVersion);
        }

        private static ModDependencyNode NodeDeps(string id, params ModDependency[] deps)
        {
            return new ModDependencyNode(id, "1.0.0", null, deps);
        }

        private static ModDependency Dep(string id, string minVersion = null)
        {
            return new ModDependency(id, minVersion);
        }

        [Fact]
        public void IndependentMods_AreOrderedByName()
        {
            var plan = ModDependencyResolver.Resolve(new[] { Node("Zeta"), Node("Alpha"), Node("Mid") });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { "Alpha", "Mid", "Zeta" }, plan.Order.ToArray());
            Assert.Empty(plan.Rejected);
        }

        [Fact]
        public void LinearChain_LoadsDependenciesFirst()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeDeps("C", Dep("B")),
                NodeDeps("B", Dep("A")),
                Node("A"),
            });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { "A", "B", "C" }, plan.Order.ToArray());
        }

        [Fact]
        public void DiamondDependency_IsResolvedDeterministically()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeDeps("Top", Dep("Beta"), Dep("Alpha")),
                NodeDeps("Beta", Dep("Core")),
                NodeDeps("Alpha", Dep("Core")),
                Node("Core"),
            });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { "Core", "Alpha", "Beta", "Top" }, plan.Order.ToArray());
        }

        [Fact]
        public void MissingDependency_RejectsDependentAndIsReported()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeDeps("NeedsGhost", Dep("Ghost")),
                Node("Fine"),
            });

            Assert.False(plan.Ok);
            Assert.Equal(new[] { "Fine" }, plan.Order.ToArray());
            Assert.Contains("NeedsGhost", plan.Rejected);
            Assert.Contains(plan.Issues, i => i.ModId == "NeedsGhost" &&
                                              i.Kind == ModDependencyIssueKind.MissingDependency);
        }

        [Fact]
        public void DependencyOfRejectedMod_IsItselfRejected()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeDeps("Broken", Dep("Ghost")),
                NodeDeps("Leaf", Dep("Broken")),
                Node("Fine"),
            });

            Assert.Equal(new[] { "Fine" }, plan.Order.ToArray());
            Assert.Contains("Broken", plan.Rejected);
            Assert.Contains("Leaf", plan.Rejected);
            Assert.Contains(plan.Issues, i => i.ModId == "Leaf" &&
                                              i.Kind == ModDependencyIssueKind.DependencyRejected);
        }

        [Fact]
        public void MinVersionNotSatisfied_RejectsDependentButKeepsDependency()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeVer("Lib", "1.0.0"),
                NodeDeps("App", Dep("Lib", "2.0.0")),
            });

            Assert.Equal(new[] { "Lib" }, plan.Order.ToArray());
            Assert.Contains("App", plan.Rejected);
            Assert.Contains(plan.Issues, i => i.ModId == "App" &&
                                              i.Kind == ModDependencyIssueKind.VersionTooLow);
        }

        [Fact]
        public void MinVersionSatisfied_LoadsInOrder()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeVer("Lib", "2.1.0"),
                NodeDeps("App", Dep("Lib", "2.0.0")),
            });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { "Lib", "App" }, plan.Order.ToArray());
        }

        [Fact]
        public void SdkVersionTooNew_RejectsMod()
        {
            var plan = ModDependencyResolver.Resolve(new[] { NodeSdk("Future", "9.9.9") }, "2.0.0");

            Assert.Empty(plan.Order);
            Assert.Contains("Future", plan.Rejected);
            Assert.Contains(plan.Issues, i => i.Kind == ModDependencyIssueKind.SdkVersionTooNew);
        }

        [Fact]
        public void SdkVersionEqualOrOlder_IsAccepted()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeSdk("Old", "1.0.0"),
                NodeSdk("Now", "2.0.0"),
                Node("NoDecl"),
            }, "2.0.0");

            Assert.True(plan.Ok);
            Assert.Equal(3, plan.Order.Count);
        }

        [Fact]
        public void CircularDependency_RejectsWholeCycle()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                NodeDeps("A", Dep("B")),
                NodeDeps("B", Dep("A")),
                Node("Standalone"),
            });

            Assert.False(plan.Ok);
            Assert.Equal(new[] { "Standalone" }, plan.Order.ToArray());
            Assert.Contains("A", plan.Rejected);
            Assert.Contains("B", plan.Rejected);
            Assert.Contains(plan.Issues, i => i.Kind == ModDependencyIssueKind.CircularDependency);
        }

        [Fact]
        public void DuplicateAndEmptyIds_AreReportedWithoutThrowing()
        {
            var plan = ModDependencyResolver.Resolve(new[]
            {
                Node("Dup"), Node("Dup"), Node("  "), null, Node("Ok"),
            });

            Assert.Contains("Ok", plan.Order);
            Assert.Contains(plan.Issues, i => i.Kind == ModDependencyIssueKind.DuplicateId);
            Assert.Contains(plan.Issues, i => i.Kind == ModDependencyIssueKind.InvalidId);
        }

        [Fact]
        public void NullAndEmptyInput_AreSafe()
        {
            Assert.NotNull(ModDependencyResolver.Resolve((IEnumerable<ModDependencyNode>)null));
            Assert.True(ModDependencyResolver.Resolve((IEnumerable<ModDependencyNode>)null).Ok);
            Assert.Empty(ModDependencyResolver.Resolve(new ModDependencyNode[0]).Order);
        }

        [Fact]
        public void ResolveManifests_MapsManifestMetadata()
        {
            var plan = ModDependencyResolver.ResolveManifests(new[]
            {
                new ModManifestAttribute("App", "1.0.0") { Dependencies = new[] { new ModDependency("Lib") } },
                new ModManifestAttribute("Lib", "1.0.0"),
            });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { "Lib", "App" }, plan.Order.ToArray());
        }

        [Fact]
        public void Plan_ToText_MentionsOrderAndIssues()
        {
            var plan = ModDependencyResolver.Resolve(new[] { NodeDeps("Solo", Dep("Ghost")) });

            string text = plan.ToText();

            Assert.Contains("加载顺序", text);
            Assert.Contains("Solo", text);
            Assert.Contains("MissingDependency", text);
        }
    }
}
