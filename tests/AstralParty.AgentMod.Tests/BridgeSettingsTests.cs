using System;
using System.IO;
using AstralParty.Agent;          // AgentBridgeLayout(两侧共享的路径/字段名契约)
using AstralParty.AgentMod.Bridge;
using Xunit;

namespace AstralParty.AgentMod.Tests
{
    /// <summary>
    /// control.json 的读写契约。
    ///
    /// 背景: MCP server 侧写的是 PascalCase(<c>EnableActions</c>, 见 AgentBridgeLayout.ControlField),
    /// 而隔壁 config.json 用的是 camelCase(<c>enableActions</c>)。用户手改 control.json 时很容易
    /// 照着 config.json 写小写 —— CesiumJson 解出来的字典是 Ordinal 比较, 不兜容错就会"改了没反应"。
    /// </summary>
    public sealed class BridgeSettingsTests : IDisposable
    {
        private readonly string _root;

        public BridgeSettingsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "astral-bridge-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private void WriteControl(string json)
        {
            File.WriteAllText(AgentBridgeLayout.ControlPath(_root), json);
        }

        [Fact]
        public void 没有_control_json_时不动开关且不标记已生效()
        {
            var s = new BridgeSettings();
            s.ApplyControl(_root);

            Assert.True(s.EnableActions);      // 默认放行(接管开箱即用)
            Assert.False(s.PauseActions);
            Assert.False(s.DryRun);
            Assert.False(s.ControlFileApplied);
        }

        [Fact]
        public void MCP_写的_PascalCase_生效()
        {
            WriteControl("{\"EnableActions\":false,\"PauseActions\":true,\"DryRun\":true,\"UpdatedAtMs\":123}");

            var s = new BridgeSettings();
            s.ApplyControl(_root);

            Assert.False(s.EnableActions);
            Assert.True(s.PauseActions);
            Assert.True(s.DryRun);
            Assert.True(s.ControlFileApplied);
            Assert.Equal(123, s.ControlReadAtMs);
        }

        [Fact]
        public void 用户照_config_json_写小写也生效()
        {
            WriteControl("{\"enableActions\":false,\"pauseActions\":true,\"dryRun\":true}");

            var s = new BridgeSettings();
            s.ApplyControl(_root);

            Assert.False(s.EnableActions);
            Assert.True(s.PauseActions);
            Assert.True(s.DryRun);
        }

        [Fact]
        public void 乱七八糟的大小写混写也生效()
        {
            WriteControl("{\"ENABLEACTIONS\":false,\"pauseactions\":true}");

            var s = new BridgeSettings();
            s.ApplyControl(_root);

            Assert.False(s.EnableActions);
            Assert.True(s.PauseActions);
        }

        [Fact]
        public void 用_astral_resume_写回_false_能解除急停()
        {
            WriteControl("{\"PauseActions\":true}");
            var s = new BridgeSettings();
            s.ApplyControl(_root);
            Assert.True(s.PauseActions);

            // 模拟 astral_resume: server 侧只改 PauseActions 一个开关, 其它保持不变
            WriteControl("{\"EnableActions\":true,\"PauseActions\":false,\"DryRun\":false,\"UpdatedBy\":\"mcp\"}");
            s.ApplyControl(_root);

            Assert.False(s.PauseActions);
            Assert.True(s.EnableActions);
        }

        [Fact]
        public void 坏_json_不改变已有开关()
        {
            var s = new BridgeSettings { EnableActions = false, PauseActions = true };
            WriteControl("{ 这不是 json");

            s.ApplyControl(_root);

            Assert.False(s.EnableActions);
            Assert.True(s.PauseActions);
        }
    }
}
