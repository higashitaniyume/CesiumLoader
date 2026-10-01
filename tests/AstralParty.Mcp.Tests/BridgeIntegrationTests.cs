using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstralParty.Agent;
using Xunit;

namespace AstralParty.Mcp.Tests
{
    /// <summary>
    /// MCP server 与"游戏内桥接文件通道"的对接。用假的桥接目录(写 state.json/bridge.json、
    /// 扮演游戏侧消费 commands 并回 results), 因此不需要开游戏就能验整条链路。
    /// </summary>
    public class BridgeIntegrationTests : IDisposable
    {
        private readonly string _root;
        private readonly AgentBridgeClient _client;
        private readonly AstralToolHost _host;

        public BridgeIntegrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "astral-mcp-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentBridgeLayout.EnsureDirectories(_root);
            _client = new AgentBridgeClient(_root);
            _host = new AstralToolHost(_client, 3000);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // ============================== 夹具 ==============================

        private void WriteHeartbeat(bool alive = true, long ageMs = 120)
        {
            long now = AgentBridgeLayout.NowMs();
            var fields = new Dictionary<string, object>
            {
                { AgentBridgeLayout.BridgeField.Schema, 1 },
                { AgentBridgeLayout.BridgeField.ModVersion, "1.0.0" },
                { AgentBridgeLayout.BridgeField.SdkVersion, "2.2.4" },
                { AgentBridgeLayout.BridgeField.ProcessId, 4242 },
                { AgentBridgeLayout.BridgeField.StartedAtMs, now - 60000 },
                { AgentBridgeLayout.BridgeField.LastTickMs, alive ? now - ageMs : now - 60000 },
                { AgentBridgeLayout.BridgeField.TickCount, 999 },
                { AgentBridgeLayout.BridgeField.StateSeq, 42 },
                { AgentBridgeLayout.BridgeField.AgentDir, _root },
                { AgentBridgeLayout.BridgeField.Scene, "RoomScene" },
                { AgentBridgeLayout.BridgeField.InRoom, true },
                { AgentBridgeLayout.BridgeField.InBattle, false },
                { AgentBridgeLayout.BridgeField.CommandsExecuted, 3 },
                { AgentBridgeLayout.BridgeField.CommandsRejected, 1 }
            };
            AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.BridgePath(_root), JsonSerializer.Serialize(fields));
        }

        private void WriteState(string pendingKind, long sn, bool actionable = true, long remainingMs = 4500)
        {
            long now = AgentBridgeLayout.NowMs();
            string json =
                "{\"Schema\":1,\"StateSeq\":7,\"Scene\":\"RoomScene\",\"InRoom\":true,\"InBattle\":false," +
                "\"IsMyTurn\":false,\"CurrentSn\":0,\"NotMove\":false," +
                "\"Self\":{\"PlayerId\":1001,\"Nick\":\"测试\",\"Gold\":12,\"HandCount\":2}," +
                "\"Pending\":{\"Kind\":\"" + pendingKind + "\",\"Actionable\":" + (actionable ? "true" : "false") +
                ",\"Sn\":" + sn + ",\"Source\":\"event\",\"SinceMs\":" + (now - 800) +
                ",\"DeadlineMs\":" + (now + remainingMs) + ",\"RemainingMs\":" + remainingMs + "," +
                "\"Candidates\":[{\"Id\":50001,\"Name\":\"幸运硬币\",\"Kind\":\"relic\"}," +
                "{\"Id\":50042,\"Name\":\"暴击手套\",\"Kind\":\"relic\",\"Price\":3}," +
                "{\"Id\":50093,\"Name\":\"时空沙漏\",\"Kind\":\"relic\",\"SoldOut\":true}]," +
                "\"Options\":[\"astral_select_relic {\\\"index\\\":0..N-1}\"],\"Notes\":[\"备注一条\"]}}";
            AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.StatePath(_root), json);
        }

        private static JsonElement Args(string json)
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private string TextOf(McpToolResult r) { return r.Text; }

        // ============================== 只读工具 ==============================

        [Fact]
        public void 没有心跳时_status报未连接并给出排查步骤()
        {
            var r = _host.CallTool("astral_status", Args("{}"));

            Assert.True(r.IsError);
            Assert.Contains("桥接未连接", r.Text);
            Assert.Contains(_root, r.Text);
        }

        [Fact]
        public void 心跳过期时_status判定桥接已停()
        {
            WriteHeartbeat(alive: false);
            var r = _host.CallTool("astral_status", Args("{}"));

            Assert.True(r.IsError);
            Assert.Contains("没有心跳", r.Text);
        }

        [Fact]
        public void 活着时_status汇总开关与待响应()
        {
            WriteHeartbeat();
            WriteState("selectRelic", 5211);
            _client.WriteControl(false, true, true, "测试", "test");

            var r = _host.CallTool("astral_status", Args("{}"));

            Assert.False(r.IsError);
            Assert.Contains("桥接活着", r.Text);
            Assert.Contains("RoomScene", r.Text);
            Assert.Contains("发送操作=只读", r.Text);
            Assert.Contains("急停=开", r.Text);
            Assert.Contains("演练=开", r.Text);
            Assert.Contains("selectRelic", r.Text);
        }

        [Fact]
        public void state_原样回状态JSON()
        {
            WriteHeartbeat();
            WriteState("selectRelic", 5211);

            var r = _host.CallTool("astral_state", Args("{}"));

            Assert.False(r.IsError);
            Assert.Contains("\"StateSeq\":7", r.Text);
            Assert.Contains("幸运硬币", r.Text);
        }

        [Fact]
        public void pending_没有窗口时说明在等别人()
        {
            WriteHeartbeat();
            WriteState("none", 0, actionable: false);

            var r = _host.CallTool("astral_pending", Args("{}"));

            Assert.False(r.IsError);
            Assert.Contains("没有需要你响应", r.Text);
        }

        /// <summary>
        /// 契约: 真机 state.json 里没有窗口时 <c>Kind</c> = <c>"none"</c>(小写 camelCase 常量,
        /// 见 <c>AgentPendingKind</c> —— 桥接模型里没有枚举)。
        /// 这里额外覆盖大小写变体: 早先按 Ordinal 比较, 一旦哪一侧改了大小写,
        /// "没有窗口"就会被当成一个名叫 None 的真窗口报给 agent(sn=0 且没有候选), agent 会空转。
        /// </summary>
        [Theory]
        [InlineData("none")]
        [InlineData("None")]
        [InlineData("NONE")]
        [InlineData(" none ")]
        [InlineData("")]
        public void pending_没有窗口的判定忽略大小写(string kind)
        {
            WriteHeartbeat();
            WriteState(kind, 0, actionable: false);

            var r = _host.CallTool("astral_pending", Args("{}"));

            Assert.False(r.IsError);
            Assert.Contains("没有需要你响应", r.Text);
            Assert.DoesNotContain("待响应:", r.Text);
        }

        [Fact]
        public void pending_列出候选价格与可用操作()
        {
            WriteHeartbeat();
            WriteState("selectRelic", 5211);

            var r = _host.CallTool("astral_pending", Args("{}"));

            Assert.False(r.IsError);
            Assert.Contains("待响应: selectRelic", r.Text);
            Assert.Contains("sn=5211", r.Text);
            Assert.Contains("[0] 50001  幸运硬币", r.Text);
            Assert.Contains("价格 3", r.Text);
            Assert.Contains("已售罄", r.Text);
            Assert.Contains("剩余时间: 4500ms", r.Text);
            Assert.Contains("astral_select_relic", r.Text);
        }

        [Fact]
        public void pending_读不到状态时提示先看status()
        {
            var r = _host.CallTool("astral_pending", Args("{}"));

            Assert.True(r.IsError);
            Assert.Contains("astral_status", r.Text);
        }

        [Fact]
        public void events与actions_没内容时不报错()
        {
            WriteHeartbeat();
            var e = _host.CallTool("astral_events", Args("{}"));
            var a = _host.CallTool("astral_actions", Args("{}"));

            Assert.False(e.IsError);
            Assert.False(a.IsError);
            Assert.Contains("还没有内容", e.Text);
            Assert.Contains("还没有内容", a.Text);
        }

        [Fact]
        public void events_只回尾部limit条()
        {
            var lines = new List<string>();
            for (int i = 1; i <= 60; i++) lines.Add("{\"AtMs\":" + i + ",\"Kind\":\"CardUsed\",\"N\":" + i + "}");
            File.WriteAllText(AgentBridgeLayout.EventsPath(_root), string.Join("\n", lines) + "\n");

            var r = _host.CallTool("astral_events", Args("{\"limit\":5}"));

            Assert.False(r.IsError);
            Assert.Contains("最后 5 条", r.Text);
            Assert.Contains("\"N\":60", r.Text);
            Assert.DoesNotContain("\"N\":55", r.Text);
        }

        // ============================== 动作工具的参数校验 ==============================

        [Fact]
        public void move_缺landId时直接报错_不下发命令()
        {
            WriteHeartbeat();
            var r = _host.CallTool("astral_move", Args("{}"));

            Assert.True(r.IsError);
            Assert.Contains("landId", r.Text);
            Assert.Empty(Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root)));
        }

        [Fact]
        public void use_quick_card_缺targetId时报错()
        {
            WriteHeartbeat();
            var r = _host.CallTool("astral_use_quick_card", Args("{\"cardId\":90001}"));
            Assert.True(r.IsError);
            Assert.Contains("targetId", r.Text);
        }

        [Fact]
        public void use_card_既没cardId也没cardGuid时报错()
        {
            WriteHeartbeat();
            var r = _host.CallTool("astral_use_card", Args("{}"));
            Assert.True(r.IsError);
            Assert.Contains("cardId", r.Text);
        }

        [Fact]
        public void atm_transfer_缺targetId时报错()
        {
            WriteHeartbeat();
            var r = _host.CallTool("astral_atm_transfer", Args("{}"));
            Assert.True(r.IsError);
            Assert.Contains("targetId", r.Text);
        }

        [Fact]
        public void speed_缺speed时报错()
        {
            WriteHeartbeat();
            var r = _host.CallTool("astral_speed", Args("{}"));
            Assert.True(r.IsError);
            Assert.Contains("speed", r.Text);
        }

        [Fact]
        public void 桥接没有心跳时_动作工具拒绝下发并提示()
        {
            var r = _host.CallTool("astral_throw_dice", Args("{}"));

            Assert.True(r.IsError);
            Assert.Contains("桥接没有心跳", r.Text);
            Assert.Empty(Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root)));
        }

        [Fact]
        public void 未知工具名_回错误()
        {
            var r = _host.CallTool("astral_nope", Args("{}"));
            Assert.True(r.IsError);
            Assert.Contains("未知工具", r.Text);
        }

        [Fact]
        public void 所有动作工具都会把参数写进命令文件()
        {
            WriteHeartbeat();
            StartFakeGame();

            var cases = new[]
            {
                Tuple.Create("astral_throw_dice", "{\"noOper\":true}"),
                Tuple.Create("astral_move", "{\"landId\":31}"),
                Tuple.Create("astral_use_card", "{\"cardId\":90001}"),
                Tuple.Create("astral_use_effect_card", "{\"cardId\":90001,\"targetIds\":[1001],\"landIds\":[31]}"),
                Tuple.Create("astral_use_quick_card", "{\"cardId\":90001,\"targetId\":1002}"),
                Tuple.Create("astral_abandon_card", "{\"cardIds\":[90001,90002]}"),
                Tuple.Create("astral_select_relic", "{\"index\":1}"),
                Tuple.Create("astral_select_reward_card", "{\"index\":0}"),
                Tuple.Create("astral_shop_buy", "{\"indexes\":[0,2]}"),
                Tuple.Create("astral_atm_transfer", "{\"targetId\":1002}"),
                Tuple.Create("astral_buy_relic", "{\"confirm\":true}"),
                Tuple.Create("astral_speed", "{\"speed\":2.0}")
            };

            foreach (var c in cases)
            {
                var r = _host.CallTool(c.Item1, Args(c.Item2));
                Assert.False(r.IsError, c.Item1 + " 应该成功, 实际: " + r.Text);
                Assert.Contains("往返", r.Text);
            }
        }

        // ============================== 开关 ==============================

        [Fact]
        public void 急停写入control并保留其它开关()
        {
            _client.WriteControl(false, false, true, "初始", "test");

            var stop = _host.CallTool("astral_emergency_stop", Args("{\"reason\":\"误操作\"}"));
            Assert.False(stop.IsError);
            Assert.Contains("已急停", stop.Text);

            using (var doc = _client.ReadControl())
            {
                var r = doc.RootElement;
                Assert.True(r.GetProperty("PauseActions").GetBoolean());
                // 没被这次操作碰到的开关必须保持原值
                Assert.False(r.GetProperty("EnableActions").GetBoolean());
                Assert.True(r.GetProperty("DryRun").GetBoolean());
                Assert.Equal("误操作", r.GetProperty("Note").GetString());
            }
        }

        [Fact]
        public void 解除急停只改急停()
        {
            _client.WriteControl(null, true, null, "先急停", "test");

            var resume = _host.CallTool("astral_resume", Args("{}"));
            Assert.False(resume.IsError);

            using (var doc = _client.ReadControl())
            {
                Assert.False(doc.RootElement.GetProperty("PauseActions").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("EnableActions").GetBoolean());
            }
        }

        [Fact]
        public void control_一次只改传进来的开关()
        {
            var r = _host.CallTool("astral_control", Args("{\"dryRun\":true,\"reason\":\"先演练\"}"));
            Assert.False(r.IsError);
            Assert.Contains("演练", r.Text);

            using (var doc = _client.ReadControl())
            {
                Assert.True(doc.RootElement.GetProperty("DryRun").GetBoolean());
                Assert.False(doc.RootElement.GetProperty("PauseActions").GetBoolean());
            }
        }

        // ============================== 命令通道(真往返) ==============================

        /// <summary>扮演游戏内 mod: 轮询 commands, 立刻回一个成功回执并删掉命令文件。</summary>
        private CancellationTokenSource StartFakeGame(string detail = "已发送(假游戏)", bool ok = true)
        {
            var cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root), "*.json"))
                        {
                            long seq;
                            string id;
                            if (!AgentBridgeLayout.TryParseCommandFileName(Path.GetFileName(file), out seq, out id)) continue;

                            AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.ResultPath(_root, id),
                                "{\"Schema\":1,\"Id\":\"" + id + "\",\"Ok\":" + (ok ? "true" : "false") +
                                ",\"Code\":\"" + (ok ? "ok" : "rejected") + "\",\"Detail\":\"" + detail + "\",\"LatencyMs\":4}");
                            File.Delete(file);
                            break;
                        }
                    }
                    catch { }
                    Thread.Sleep(5);
                }
            }, cts.Token);
            return cts;
        }

        [Fact]
        public void 命令往返_拿到回执并且两侧文件都被清掉()
        {
            WriteHeartbeat();
            using (var game = StartFakeGame())
            {
                var r = _host.CallTool("astral_throw_dice", Args("{\"noOper\":true}"));

                Assert.False(r.IsError);
                Assert.Contains("已发送(假游戏)", r.Text);
                Assert.Contains("往返 4ms", r.Text);
                Assert.Empty(Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root), "*.json"));
                Assert.Empty(Directory.GetFiles(AgentBridgeLayout.ResultsDir(_root), "*.json"));
            }
        }

        /// <summary>扮演游戏内 mod: 把收到的命令 JSON 原样记下来, 再回一个成功回执。</summary>
        private CancellationTokenSource StartCapturingGame(List<string> captured)
        {
            var cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root), "*.json"))
                        {
                            long seq;
                            string id;
                            if (!AgentBridgeLayout.TryParseCommandFileName(Path.GetFileName(file), out seq, out id)) continue;

                            string body;
                            try { body = File.ReadAllText(file); }
                            catch { continue; }   // 可能已被清掉, 下一轮再看

                            lock (captured) captured.Add(body);
                            AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.ResultPath(_root, id),
                                "{\"Schema\":1,\"Id\":\"" + id + "\",\"Ok\":true,\"Code\":\"ok\",\"Detail\":\"captured\",\"LatencyMs\":1}");
                            File.Delete(file);
                            break;
                        }
                    }
                    catch { }
                    Thread.Sleep(5);
                }
            }, cts.Token);
            return cts;
        }

        [Fact]
        public void 事件选择工具_把index送进命令文件()
        {
            WriteHeartbeat();
            var captured = new List<string>();
            using (var game = StartCapturingGame(captured))
            {
                var r = _host.CallTool("astral_select_event", Args("{\"index\":1}"));
                Assert.False(r.IsError);
            }

            Assert.Single(captured);
            Assert.Contains(AgentBridgeLayout.Tool.SelectEvent, captured[0]);
            Assert.Contains("index", captured[0]);
        }

        [Fact]
        public void 重摇筹码_命令里带reroll标记()
        {
            WriteHeartbeat();
            var captured = new List<string>();
            using (var game = StartCapturingGame(captured))
            {
                var r = _host.CallTool("astral_select_relic", Args("{\"reroll\":true}"));
                Assert.False(r.IsError);
            }

            Assert.Single(captured);
            Assert.Contains(AgentBridgeLayout.Tool.SelectRelic, captured[0]);
            Assert.Contains("reroll", captured[0]);
            Assert.Contains("true", captured[0]);
        }

        [Fact]
        public void 命令被游戏侧拒绝_回isError并带错误码()
        {
            WriteHeartbeat();
            using (var game = StartFakeGame(ok: false))
            {
                var r = _host.CallTool("astral_throw_dice", Args("{}"));

                Assert.True(r.IsError);
                Assert.Contains("rejected", r.Text);
            }
        }

        [Fact]
        public void 命令超时_标记超时并清理命令文件()
        {
            WriteHeartbeat();
            var client = new AgentBridgeClient(_root);
            var res = client.SendCommand(AgentBridgeLayout.Tool.Ping, null, 300);

            Assert.False(res.Ok);
            Assert.True(res.TimedOut);
            Assert.Equal("timeout", res.Code);
            Assert.Empty(Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root), "*.json"));
        }

        [Fact]
        public void 命令文件与结果文件的命名规则一致()
        {
            var client = new AgentBridgeClient(_root);
            Task.Run(() =>
            {
                // 只检查文件名形态: {seq:D8}-{id}.json 且在 commands 目录下
                for (int i = 0; i < 100; i++)
                {
                    var files = Directory.GetFiles(AgentBridgeLayout.CommandsDir(_root), "*.json");
                    if (files.Length == 0) { Thread.Sleep(10); continue; }

                    long seq;
                    string id;
                    Assert.True(AgentBridgeLayout.TryParseCommandFileName(Path.GetFileName(files[0]), out seq, out id));
                    Assert.True(seq > 0);
                    Assert.True(id.Length > 0);
                    AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.ResultPath(_root, id),
                        "{\"Id\":\"" + id + "\",\"Ok\":true,\"Detail\":\"ok\"}");
                    File.Delete(files[0]);
                    return;
                }
            });

            var res = client.SendCommand(AgentBridgeLayout.Tool.Move, new Dictionary<string, object> { { "landId", 31 } }, 3000);
            Assert.True(res.Ok);
        }

        [Fact]
        public void 多条命令按seq顺序排队()
        {
            var a = AgentBridgeLayout.CommandFileName(3, "a");
            var b = AgentBridgeLayout.CommandFileName(1, "b");
            var c = AgentBridgeLayout.CommandFileName(2, "c");

            var sorted = new List<string> { a, b, c };
            sorted.Sort(StringComparer.Ordinal);

            Assert.Equal(new[] { b, c, a }, sorted);
        }

        // ============================== 工具清单 ==============================

        [Fact]
        public void 工具清单_包含全部接管所需的能力()
        {
            var names = new List<string>();
            foreach (var t in _host.ListTools()) names.Add(t.Name);

            Assert.Contains("astral_status", names);
            Assert.Contains("astral_state", names);
            Assert.Contains("astral_pending", names);
            Assert.Contains("astral_events", names);
            Assert.Contains("astral_actions", names);
            Assert.Contains("astral_throw_dice", names);
            Assert.Contains("astral_move", names);
            Assert.Contains("astral_use_card", names);
            Assert.Contains("astral_use_effect_card", names);
            Assert.Contains("astral_use_quick_card", names);
            Assert.Contains("astral_abandon_card", names);
            Assert.Contains("astral_select_relic", names);
            Assert.Contains("astral_select_reward_card", names);
            Assert.Contains("astral_select_event", names);
            Assert.Contains("astral_shop_buy", names);
            Assert.Contains("astral_atm_transfer", names);
            Assert.Contains("astral_buy_relic", names);
            Assert.Contains("astral_speed", names);
            Assert.Contains("astral_control", names);
            Assert.Contains("astral_emergency_stop", names);
            Assert.Contains("astral_resume", names);
        }

        [Fact]
        public void 工具清单_只读与破坏性分类正确()
        {
            foreach (var t in _host.ListTools())
            {
                bool readOnlyTool = t.Name == "astral_status" || t.Name == "astral_state" ||
                                    t.Name == "astral_pending" || t.Name == "astral_events" ||
                                    t.Name == "astral_actions";
                Assert.Equal(readOnlyTool, t.ReadOnly);
                Assert.False(t.ReadOnly && t.Destructive, t.Name + " 不能既只读又有破坏性");
                Assert.False(string.IsNullOrEmpty(t.Description), t.Name + " 必须有说明");
                Assert.NotNull(t.InputSchema);
            }
        }

        [Fact]
        public void 说明里告诉agent先看status再动手()
        {
            Assert.Contains("astral_status", _host.Instructions);
            Assert.Contains("astral_emergency_stop", _host.Instructions);
        }

        [Fact]
        public void 新工具的入参说明_带上重摇与事件选择()
        {
            string relicSchema = null, eventSchema = null;
            foreach (var t in _host.ListTools())
            {
                if (t.Name == "astral_select_relic") relicSchema = t.InputSchema?.ToJsonString();
                if (t.Name == "astral_select_event") eventSchema = t.InputSchema?.ToJsonString();
            }

            Assert.NotNull(relicSchema);
            Assert.Contains("reroll", relicSchema);   // 重摇是筹码窗口的一个动作
            Assert.NotNull(eventSchema);
            Assert.Contains("eventId", eventSchema);   // 可按事件 id 选
            Assert.Contains("index", eventSchema);
        }
    }
}
