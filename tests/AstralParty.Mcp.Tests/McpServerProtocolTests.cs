using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AstralParty.Mcp.Tests
{
    /// <summary>
    /// MCP 协议层(JSON-RPC 2.0 / stdio)。手写的协议实现必须能被真客户端用,
    /// 所以这里把 初始化协商 / 通知不回复 / 工具列举 / 工具调用 / 错误码 全钉住。
    /// </summary>
    public class McpServerProtocolTests
    {
        /// <summary>假工具宿主: 只回显参数, 便于断言协议层有没有把 arguments 正确透传。</summary>
        private sealed class FakeHost : IMcpToolHost
        {
            public string LastTool;
            public string LastArgsJson;
            public bool ThrowOnCall;

            public string Instructions { get { return "instruction-text"; } }

            public IReadOnlyList<McpTool> ListTools()
            {
                return new List<McpTool>
                {
                    new McpTool
                    {
                        Name = "astral_echo",
                        Title = "回显",
                        Description = "把参数回显出来",
                        ReadOnly = true,
                        InputSchema = (JsonObject)JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}")
                    },
                    new McpTool
                    {
                        Name = "astral_write",
                        Title = "写",
                        Description = "会改状态",
                        Destructive = true,
                        InputSchema = (JsonObject)JsonNode.Parse("{\"type\":\"object\"}")
                    }
                };
            }

            public McpToolResult CallTool(string name, JsonElement arguments)
            {
                LastTool = name;
                LastArgsJson = arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : null;
                if (ThrowOnCall) throw new InvalidOperationException("炸了");
                if (name == "astral_unknown") return McpToolResult.Error("未知工具: " + name);
                return McpToolResult.Ok("echo:" + (arguments.ValueKind == JsonValueKind.Object
                    ? AgentBridgeClient.GetString(arguments, "text") : null));
            }
        }

        private static McpServer NewServer(FakeHost host)
        {
            return new McpServer(new StringReader(""), new StringWriter(), host);
        }

        private static JsonElement Parse(string response)
        {
            Assert.NotNull(response);
            return JsonDocument.Parse(response).RootElement.Clone();
        }

        private static JsonElement ResultOf(string response) { return Parse(response).GetProperty("result"); }

        [Fact]
        public void initialize_回协商版本与服务器信息()
        {
            var server = NewServer(new FakeHost());

            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}"));

            Assert.Equal("2025-06-18", res.GetProperty("protocolVersion").GetString());
            Assert.Equal("astral-party-mcp", res.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.False(res.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
            Assert.Equal("instruction-text", res.GetProperty("instructions").GetString());
        }

        [Fact]
        public void initialize_客户端要旧版本时沿用旧版本()
        {
            var server = NewServer(new FakeHost());
            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}"));
            Assert.Equal("2024-11-05", res.GetProperty("protocolVersion").GetString());
        }

        [Fact]
        public void initialize_客户端要未知版本时回落到本端最新()
        {
            var server = NewServer(new FakeHost());
            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}"));
            Assert.Equal(McpServer.SupportedProtocolVersions[0], res.GetProperty("protocolVersion").GetString());
        }

        [Fact]
        public void 通知_不产生任何响应()
        {
            var server = NewServer(new FakeHost());
            Assert.Null(server.HandleLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
            Assert.Null(server.HandleLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{}}"));
        }

        [Fact]
        public void 通知即使方法未知也不回复()
        {
            var server = NewServer(new FakeHost());
            Assert.Null(server.HandleLine("{\"jsonrpc\":\"2.0\",\"method\":\"something/weird\"}"));
        }

        [Fact]
        public void tools_list_带schema与注解()
        {
            var server = NewServer(new FakeHost());
            var res = ResultOf(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"));
            var tools = res.GetProperty("tools");

            Assert.Equal(2, tools.GetArrayLength());
            Assert.Equal("astral_echo", tools[0].GetProperty("name").GetString());
            Assert.Equal("回显", tools[0].GetProperty("title").GetString());
            Assert.Equal("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.True(tools[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(tools[1].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        }

        [Fact]
        public void tools_call_透传参数并回content()
        {
            var host = new FakeHost();
            var server = NewServer(host);

            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"astral_echo\",\"arguments\":{\"text\":\"你好\"}}}"));

            Assert.Equal("astral_echo", host.LastTool);
            Assert.Contains("你好", host.LastArgsJson);
            Assert.False(res.GetProperty("isError").GetBoolean());
            Assert.Equal("text", res.GetProperty("content")[0].GetProperty("type").GetString());
            Assert.Equal("echo:你好", res.GetProperty("content")[0].GetProperty("text").GetString());
        }

        [Fact]
        public void tools_call_无参数也能调用()
        {
            var host = new FakeHost();
            var server = NewServer(host);

            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"astral_write\"}}"));

            Assert.Equal("astral_write", host.LastTool);
            Assert.False(res.GetProperty("isError").GetBoolean());
        }

        [Fact]
        public void tools_call_工具报错时isError为真()
        {
            var server = NewServer(new FakeHost());
            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"astral_unknown\"}}"));

            Assert.True(res.GetProperty("isError").GetBoolean());
            Assert.Contains("未知工具", res.GetProperty("content")[0].GetProperty("text").GetString());
        }

        [Fact]
        public void tools_call_宿主抛异常也要回一个isError而不是崩掉循环()
        {
            var host = new FakeHost { ThrowOnCall = true };
            var server = NewServer(host);

            var res = ResultOf(server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"astral_echo\"}}"));

            Assert.True(res.GetProperty("isError").GetBoolean());
            Assert.Contains("InvalidOperationException", res.GetProperty("content")[0].GetProperty("text").GetString());
        }

        [Fact]
        public void tools_call_缺工具名时回isError()
        {
            var server = NewServer(new FakeHost());
            var res = ResultOf(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{}}"));
            Assert.True(res.GetProperty("isError").GetBoolean());
        }

        [Fact]
        public void 未知方法_回32601()
        {
            var server = NewServer(new FakeHost());
            var root = Parse(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"no/such/method\"}"));

            Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
        }

        [Fact]
        public void 坏JSON_回32700且id为null()
        {
            var server = NewServer(new FakeHost());
            var root = Parse(server.HandleLine("{ this is not json"));

            Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        }

        [Fact]
        public void 缺method_回32600()
        {
            var server = NewServer(new FakeHost());
            var root = Parse(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":9}"));
            Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
        }

        [Fact]
        public void id_原样回显_数字与字符串都保持类型()
        {
            var server = NewServer(new FakeHost());

            var numeric = Parse(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\"}"));
            Assert.Equal(42, numeric.GetProperty("id").GetInt32());

            var textual = Parse(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"ping\"}"));
            Assert.Equal("abc", textual.GetProperty("id").GetString());
        }

        [Fact]
        public void ping与空资源列表_按规范返回空对象空数组()
        {
            var server = NewServer(new FakeHost());

            var ping = ResultOf(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"));
            Assert.Equal(JsonValueKind.Object, ping.ValueKind);
            Assert.Empty(ping.EnumerateObject());

            var resources = ResultOf(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/list\"}"));
            Assert.Equal(0, resources.GetProperty("resources").GetArrayLength());

            var prompts = ResultOf(server.HandleLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"prompts/list\"}"));
            Assert.Equal(0, prompts.GetProperty("prompts").GetArrayLength());
        }

        [Fact]
        public void Run_逐行处理并写出响应()
        {
            var input = new StringReader(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
            var output = new StringWriter();

            new McpServer(input, output, new FakeHost()).Run();

            var lines = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length); // 通知不产生行
            Assert.Contains("\"id\":1", lines[0]);
            Assert.Contains("astral_echo", lines[1]);
        }

        [Fact]
        public void 响应里的中文不被转义成unicode码点()
        {
            var server = NewServer(new FakeHost());
            string response = server.HandleLine(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"astral_echo\",\"arguments\":{\"text\":\"吉星派对\"}}}");

            Assert.Contains("吉星派对", response);
        }
    }
}
