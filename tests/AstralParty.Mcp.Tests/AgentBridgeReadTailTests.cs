using System;
using System.IO;
using System.Text;
using AstralParty.Agent;
using Xunit;

namespace AstralParty.Mcp.Tests
{
    /// <summary>
    /// <see cref="AgentBridgeLayout.ReadTail"/> 的用例(从 AgentMod 测试搬过来)。
    ///
    /// 它是 **server-only** 的: 实现里用了 <c>FileStream.Seek</c>, 而真机已经证明
    /// 游戏内的 BCL 会缺 <c>FileStream</c> 的成员(`Flush(bool)` → `MethodNotFind`)。
    /// 所以它只编进 mcp.Core(net8.0), mod 侧看不到也测不了 —— 测试自然跟着它走。
    /// </summary>
    public sealed class AgentBridgeReadTailTests : IDisposable
    {
        private readonly string _root;

        public AgentBridgeReadTailTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "astral-readtail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void ReadTail_截断时从整行开始_不返回半行()
        {
            string path = Path.Combine(_root, "events.jsonl");
            var sb = new StringBuilder();
            for (int i = 0; i < 200; i++) sb.Append("{\"i\":").Append(i).Append("}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

            string tail = AgentBridgeLayout.ReadTail(path, 64);

            Assert.NotNull(tail);
            Assert.True(tail.Length > 0);
            // 不能有半行: 每一行都必须是完整 JSON
            foreach (var line in tail.Split('\n'))
            {
                if (line.Length == 0) continue;
                Assert.StartsWith("{", line);
                Assert.EndsWith("}", line);
            }
            Assert.Contains("{\"i\":199}", tail);
        }

        [Fact]
        public void ReadTail_文件比上限小时原样返回()
        {
            string path = Path.Combine(_root, "small.jsonl");
            File.WriteAllText(path, "{\"a\":1}\n{\"a\":2}\n", new UTF8Encoding(false));

            string tail = AgentBridgeLayout.ReadTail(path, 4096);

            Assert.Equal("{\"a\":1}\n{\"a\":2}\n", tail);
        }

        [Fact]
        public void ReadTail_文件不存在返回null()
        {
            Assert.Null(AgentBridgeLayout.ReadTail(Path.Combine(_root, "nope.jsonl"), 1024));
        }

        [Fact]
        public void ReadTail_空文件返回空串()
        {
            string path = Path.Combine(_root, "empty.jsonl");
            File.WriteAllText(path, string.Empty);

            Assert.Equal(string.Empty, AgentBridgeLayout.ReadTail(path, 1024));
        }
    }
}
