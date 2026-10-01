using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AstralParty.AgentMod.Tests
{
    /// <summary>
    /// 编进 mod 的源码不许用"游戏内未验证"的 BCL 成员。
    ///
    /// 为什么需要这条 lint: 热更程序集(HybridCLR/IL2CPP)用的是被裁剪过的 BCL,
    /// 缺方法时**编译期完全正常、离线单元测试也全绿**(测试跑在 net8.0 上),
    /// 只有在游戏里真的执行到那一行才抛 <c>MethodNotFind</c>。
    /// 2026-10-01 真机就是这么炸的: `state.json` 一条都写不出来, mod 日志里只有
    /// <c>[ERR] AgentBridge/WriteState: MethodNotFind System.IO.FileStream::Flush</c>。
    /// 那次踩的是 `fs.Flush(true)`(FileStream.Flush(bool) 重载)。
    ///
    /// 判据不是"这个 API 一定坏", 而是**仓库里在游戏内跑通的文件读写一律只用
    /// File.WriteAllText / File.AppendAllText / File.ReadAllText + File.Exists/Delete/Move**
    /// (SDK 的 SdkLog/SdkConfig/ModConfig 与四个内置 mod 都是这么写的)。要破例就得先有真机证据。
    /// </summary>
    public sealed class BclCompatibilityTests
    {
        /// <summary>禁用模式 → 为什么要禁(写清楚依据, 免得后来人以为是迷信)。</summary>
        private static readonly (string Pattern, string Reason)[] Forbidden =
        {
            ("Flush(true)", "真机 MethodNotFind System.IO.FileStream::Flush —— 就是这么炸的"),
            ("Flush(false)", "同上(FileStream.Flush(bool) 这个重载)"),
            ("new FileStream", "游戏内没有验证过 FileStream 的 Seek/Length/Flush 组合; 仓库里跑通的写入全走 File.* 静态方法"),
            ("File.Open", "同上(FileStream 的另一条构造路径)"),
            ("new StreamWriter", "未验证; 用 File.WriteAllText/AppendAllText 代替"),
            ("new StreamReader", "未验证; 用 File.ReadAllText 代替"),
            ("new BinaryWriter", "未验证"),
            ("new BinaryReader", "未验证"),
            ("File.ReadAllLines", "AGENTS.md §12.1 明确点名(热更侧 BCL 缺)"),
            ("Assembly.Location", "AGENTS.md §12.1 明确点名(热更程序集没有磁盘位置)"),
            ("AppDomain", "AGENTS.md §12.1: 热更侧拿不到 BaseDirectory"),
        };

        private static string SourceRoot()
        {
            var meta = typeof(BclCompatibilityTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BridgeSourceRoot");
            Assert.False(meta == null, "csproj 里少了 AssemblyMetadata BridgeSourceRoot");
            return Path.GetFullPath(meta.Value);
        }

        /// <summary>会被编进 AgentMod 的源码(与 AgentMod.csproj / 本测试 csproj 的 Compile 项一致)。</summary>
        private static List<string> ModCompiledSources()
        {
            string root = SourceRoot();
            var files = new List<string> { Path.Combine(root, "Shared", "AgentBridgeLayout.cs") };
            string modDir = Path.Combine(root, "AstralParty.AgentMod");
            if (Directory.Exists(modDir))
                files.AddRange(Directory.EnumerateFiles(modDir, "*.cs", SearchOption.AllDirectories));
            return files;
        }

        [Fact]
        public void lint_扫描的文件集合是对的()
        {
            var files = ModCompiledSources();

            // 防"路径写错 → 扫到 0 个文件 → 测试假装通过"
            Assert.True(files.Count >= 5, "只扫到 " + files.Count + " 个文件, 路径多半不对");
            Assert.All(files, f => Assert.True(File.Exists(f), "文件不存在: " + f));
            Assert.Contains(files, f => f.EndsWith("AgentBridgeLayout.cs", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("Bridge\\PendingTracker.cs", StringComparison.Ordinal)
                                      || f.EndsWith("Bridge/PendingTracker.cs", StringComparison.Ordinal));

            // server-only 分片不许出现在 mod 侧文件里
            Assert.DoesNotContain(files, f => f.EndsWith("AgentBridgeReadTail.cs", StringComparison.Ordinal));
        }

        [Fact]
        public void 编进_mod_的源码不许用游戏内未验证的_BCL_成员()
        {
            var violations = new List<string>();
            foreach (string file in ModCompiledSources())
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    // 注释行里提到这些名字是为了说明原因(比如本文件/布局文件里的解释), 不算违规
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("///", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                    foreach (var (pattern, reason) in Forbidden)
                    {
                        if (line.Contains(pattern, StringComparison.Ordinal))
                            violations.Add(Path.GetFileName(file) + ":" + (i + 1) + "  命中 `" + pattern + "` —— " + reason);
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "编进 mod 的源码里出现了游戏内未验证的 BCL 成员:" + Environment.NewLine +
                string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void server_only_分片确实用了_file_stream()
        {
            // 反向断言: 说明"拆分"不是摆设 —— 如果哪天 ReadTail 被搬回共享文件,
            // 上面两条 lint 会立刻变红, 这里则是告诉读者"搬回去就会踩什么"。
            string tail = Path.Combine(SourceRoot(), "Shared", "AgentBridgeReadTail.cs");
            Assert.True(File.Exists(tail), "server-only 分片不见了: " + tail);
            Assert.Contains("new FileStream", File.ReadAllText(tail), StringComparison.Ordinal);
        }
    }
}
