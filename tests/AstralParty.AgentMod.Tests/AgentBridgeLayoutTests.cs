using System;
using System.IO;
using System.Text;
using AstralParty.Agent;
using Xunit;

namespace AstralParty.AgentMod.Tests
{
    /// <summary>
    /// 桥接目录/文件协议。这是"游戏内 mod"与"外部 MCP server"唯一的契约,
    /// 两边各有一份实现(mod 用 CesiumJson, server 用 System.Text.Json), 所以这里把
    /// 名字规则、原子写、尾部读这几条最容易写歪的地方钉住。
    /// </summary>
    public class AgentBridgeLayoutTests
    {
        private static string NewTempRoot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "astral-bridge-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void CommandFileName_零填充保证按名字排序等于下发顺序()
        {
            string a = AgentBridgeLayout.CommandFileName(5, "cmd-a");
            string b = AgentBridgeLayout.CommandFileName(12, "cmd-b");

            Assert.Equal("00000005-cmd-a.json", a);
            Assert.True(string.CompareOrdinal(a, b) < 0, "seq 小的文件名必须排在前面");
        }

        [Fact]
        public void TryParseCommandFileName_能往返()
        {
            string name = AgentBridgeLayout.CommandFileName(42, "abc123");
            long seq;
            string id;

            Assert.True(AgentBridgeLayout.TryParseCommandFileName(name, out seq, out id));
            Assert.Equal(42, seq);
            Assert.Equal("abc123", id);
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-seq.json")]
        [InlineData("00000001-.json")]
        [InlineData("00000001-abc.txt")]
        [InlineData("-abc.json")]
        public void TryParseCommandFileName_非法名字一律拒绝(string name)
        {
            long seq;
            string id;
            Assert.False(AgentBridgeLayout.TryParseCommandFileName(name, out seq, out id));
        }

        [Fact]
        public void Sanitize_挡掉目录穿越()
        {
            Assert.Equal("unnamed", AgentBridgeLayout.Sanitize(null));
            // 点与斜杠会被剥掉, 只剩字母数字: "../../etc" → "etc"(仍然落在目标目录里)
            Assert.Equal("etc", AgentBridgeLayout.Sanitize("../../etc"));
            Assert.Equal("abc-_12", AgentBridgeLayout.Sanitize("abc-_12"));
            Assert.DoesNotContain("..", AgentBridgeLayout.Sanitize("..\\..\\windows"));
            Assert.DoesNotContain("/", AgentBridgeLayout.Sanitize("a/b"));
            Assert.DoesNotContain("\\", AgentBridgeLayout.Sanitize("a\\b"));
            // 全是非法字符时兜底
            Assert.Equal("unnamed", AgentBridgeLayout.Sanitize("..\\../"));
        }

        [Fact]
        public void 路径助手都落在根目录下面()
        {
            string root = @"C:\bridge";
            Assert.Equal(Path.Combine(root, "state.json"), AgentBridgeLayout.StatePath(root));
            Assert.Equal(Path.Combine(root, "bridge.json"), AgentBridgeLayout.BridgePath(root));
            Assert.Equal(Path.Combine(root, "control.json"), AgentBridgeLayout.ControlPath(root));
            Assert.Equal(Path.Combine(root, "commands"), AgentBridgeLayout.CommandsDir(root));
            Assert.Equal(Path.Combine(root, "results"), AgentBridgeLayout.ResultsDir(root));
            Assert.StartsWith(AgentBridgeLayout.CommandsDir(root), AgentBridgeLayout.CommandPath(root, 1, "x"));
            Assert.StartsWith(AgentBridgeLayout.ResultsDir(root), AgentBridgeLayout.ResultPath(root, "x"));
        }

        [Fact]
        public void ResultPath_会用清洗过的id_不会穿越目录()
        {
            string path = AgentBridgeLayout.ResultPath(@"C:\bridge", "../../evil");
            Assert.StartsWith(AgentBridgeLayout.ResultsDir(@"C:\bridge"), path);
            Assert.DoesNotContain("..", path);
        }

        [Fact]
        public void EnsureDirectories_幂等且建出命令与结果目录()
        {
            string root = NewTempRoot();
            AgentBridgeLayout.EnsureDirectories(root);
            AgentBridgeLayout.EnsureDirectories(root);

            Assert.True(Directory.Exists(AgentBridgeLayout.CommandsDir(root)));
            Assert.True(Directory.Exists(AgentBridgeLayout.ResultsDir(root)));
            Directory.Delete(root, true);
        }

        [Fact]
        public void WriteAtomic_覆盖写入且不留tmp()
        {
            string root = NewTempRoot();
            AgentBridgeLayout.EnsureDirectories(root);
            string path = AgentBridgeLayout.StatePath(root);

            Assert.True(AgentBridgeLayout.WriteAtomic(path, "{\"a\":1}"));
            Assert.Equal("{\"a\":1}", AgentBridgeLayout.ReadAllTextOrNull(path));

            // 再写一次(走 File.Replace 路径): 内容必须被换掉
            Assert.True(AgentBridgeLayout.WriteAtomic(path, "{\"a\":2}"));
            Assert.Equal("{\"a\":2}", AgentBridgeLayout.ReadAllTextOrNull(path));
            Assert.False(File.Exists(path + ".tmp"), "原子写不应留下 .tmp");

            Directory.Delete(root, true);
        }

        [Fact]
        public void ReadAllTextOrNull_不存在返回null()
        {
            Assert.Null(AgentBridgeLayout.ReadAllTextOrNull(Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N"))));
        }

        // ReadTail 的用例搬去了 AstralParty.Mcp.Tests(ReadTail 只编进 MCP server,
        // 用了游戏内没验证过的 FileStream.Seek, 故意不给 mod 侧编 —— 见 BclCompatibilityTests)。

        [Fact]
        public void AppendLine_累加且可读回()
        {
            string root = NewTempRoot();
            string path = Path.Combine(root, "a.jsonl");
            Assert.True(AgentBridgeLayout.AppendLine(path, "{\"n\":1}"));
            Assert.True(AgentBridgeLayout.AppendLine(path, "{\"n\":2}"));
            Assert.Equal("{\"n\":1}\n{\"n\":2}\n", AgentBridgeLayout.ReadAllTextOrNull(path));
            Directory.Delete(root, true);
        }

        [Fact]
        public void RotateIfLarge_超限改名_不丢内容()
        {
            string root = NewTempRoot();
            string path = Path.Combine(root, "big.jsonl");
            File.WriteAllText(path, new string('x', 100));

            AgentBridgeLayout.RotateIfLarge(path, 10);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));

            // 没超限时不动
            File.WriteAllText(path, "small");
            AgentBridgeLayout.RotateIfLarge(path, 1000);
            Assert.True(File.Exists(path));
            Directory.Delete(root, true);
        }

        [Fact]
        public void NowMs_是真实时间_UTC毫秒量级()
        {
            long now = AgentBridgeLayout.NowMs();
            long expected = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            // 变速 hook 会让 Stopwatch 漂移, 所以桥接统一用 DateTime.UtcNow; 这里守住这个量级
            Assert.InRange(Math.Abs(now - expected), 0, 2000);
        }

        [Fact]
        public void NowUtcIso_格式正确()
        {
            string iso = AgentBridgeLayout.NowUtcIso();

            // 用 "o"(round-trip) 而不是固定字面量: SDK 的相机/诊断在游戏内跑的就是 "o",
            // 这里只断言语义(ISO-8601 + UTC 标记 + 能解析回来 + 时间和现在对得上)。
            Assert.EndsWith("Z", iso);
            DateTime parsed;
            Assert.True(DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed));
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            Assert.True(Math.Abs((DateTime.UtcNow - parsed).TotalMinutes) < 5, "NowUtcIso 应该就是当前 UTC 时间");

            // 机器可读的时间一律走 NowMs(同一纪元), 别让人误用 ISO 字符串去算差值
            long ms = AgentBridgeLayout.NowMs();
            Assert.True(ms > 0);
            Assert.Contains(parsed.Year.ToString(), iso);
        }

        [Fact]
        public void ExternalToolName_带astral前缀()
        {
            Assert.Equal("astral_throw_dice", AgentBridgeLayout.ExternalToolName(AgentBridgeLayout.Tool.ThrowDice));
            Assert.Equal("astral_shop_buy", AgentBridgeLayout.ExternalToolName(AgentBridgeLayout.Tool.ShopBuy));
        }

        [Fact]
        public void ResolveRoot_环境变量优先()
        {
            string old = Environment.GetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir);
            try
            {
                string custom = Path.Combine(Path.GetTempPath(), "custom-agent-dir");
                Environment.SetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir, custom);
                Assert.Equal(custom, AgentBridgeLayout.ResolveRoot());
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir, old);
            }
        }

        [Fact]
        public void ResolveRoot_没有环境变量时落在LocalAppData()
        {
            string old = Environment.GetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir);
            try
            {
                Environment.SetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir, null);
                string root = AgentBridgeLayout.ResolveRoot();
                Assert.Contains(AgentBridgeLayout.LoaderFolderName, root);
                Assert.EndsWith(AgentBridgeLayout.AgentFolderName, root);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentBridgeLayout.EnvAgentDir, old);
            }
        }
    }
}
