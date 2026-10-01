using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstralParty.Mcp
{
    /// <summary>一次工具调用的结果(会被包成 MCP 的 content 数组)。</summary>
    public sealed class McpToolResult
    {
        public bool IsError;
        public string Text;

        public static McpToolResult Ok(string text) { return new McpToolResult { IsError = false, Text = text ?? string.Empty }; }
        public static McpToolResult Error(string text) { return new McpToolResult { IsError = true, Text = text ?? string.Empty }; }
    }

    /// <summary>工具描述(名字/说明/入参 JSON Schema/注解)。</summary>
    public sealed class McpTool
    {
        public string Name;
        public string Title;
        public string Description;
        public JsonObject InputSchema;
        /// <summary>只读工具(不改对局状态)。</summary>
        public bool ReadOnly;
        /// <summary>会真实改变对局(发 C2S)。</summary>
        public bool Destructive;
    }

    /// <summary>工具宿主: 协议层只认这个接口, 因此可以脱离真实桥接做协议测试。</summary>
    public interface IMcpToolHost
    {
        IReadOnlyList<McpTool> ListTools();
        McpToolResult CallTool(string name, JsonElement arguments);
        /// <summary>initialize 回给客户端的说明(可选)。</summary>
        string Instructions { get; }
    }

    /// <summary>
    /// 手写的 MCP server 协议层(JSON-RPC 2.0, stdio, **换行分隔**)。
    ///
    /// 为什么手写而不是引官方 SDK: 这个仓库的哲学是零依赖、离线可还原、单文件可分发
    /// (见 mcp\README.md)。MCP 的 stdio 传输就是"一行一个 JSON-RPC 消息", 需要的
    /// 方法只有 initialize / tools/list / tools/call 三个, 手写比引包更可控。
    ///
    /// 实现范围(保守但标准):
    ///   initialize · notifications/initialized · ping · tools/list · tools/call
    ///   resources/list · resources/templates/list · prompts/list · logging/setLevel
    /// 其余方法按规范回 -32601(Method not found); 通知(无 id)一律不回。
    /// </summary>
    public sealed class McpServer
    {
        public const string ServerName = "astral-party-mcp";

        /// <summary>本 server 支持的 MCP 协议版本(新的在前)。</summary>
        public static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly IMcpToolHost _host;
        private readonly string _serverVersion;

        public McpServer(TextReader input, TextWriter output, IMcpToolHost host, string serverVersion = "1.0.0")
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _serverVersion = serverVersion;
        }

        /// <summary>跑主循环(读到 EOF 退出)。</summary>
        public void Run()
        {
            string line;
            while ((line = _input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var response = HandleLine(line);
                if (response == null) continue;
                _output.WriteLine(response);
                _output.Flush();
            }
        }

        /// <summary>
        /// 处理一行输入, 返回要写出的响应(null = 不需要响应, 例如通知)。
        /// 单独暴露出来是为了能离线测协议(见 tests\AstralParty.Mcp.Tests)。
        /// </summary>
        public string HandleLine(string line)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (Exception e)
            {
                return ErrorResponse(null, -32700, "Parse error: " + e.Message);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return ErrorResponse(null, -32600, "Invalid Request: 顶层必须是对象");

                bool hasId = root.TryGetProperty("id", out var idElement);
                string idJson = hasId ? idElement.GetRawText() : null;

                string method = null;
                if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                    method = m.GetString();

                if (string.IsNullOrEmpty(method))
                    return hasId ? ErrorResponse(idJson, -32600, "Invalid Request: 缺少 method") : null;

                // 通知(没有 id)不需要响应
                bool isNotification = !hasId;
                if (method.StartsWith("notifications/", StringComparison.Ordinal))
                    return null;

                JsonElement paramsElement = default;
                bool hasParams = root.TryGetProperty("params", out paramsElement) &&
                                 paramsElement.ValueKind != JsonValueKind.Null;

                try
                {
                    switch (method)
                    {
                        case "initialize":
                            return ResultResponse(idJson, BuildInitializeResult(hasParams ? paramsElement : default));

                        case "ping":
                            return ResultResponse(idJson, new JsonObject());

                        case "tools/list":
                            return ResultResponse(idJson, BuildToolsList());

                        case "tools/call":
                            return ResultResponse(idJson, HandleToolCall(hasParams ? paramsElement : default));

                        case "resources/list":
                            return ResultResponse(idJson, new JsonObject { ["resources"] = new JsonArray() });

                        case "resources/templates/list":
                            return ResultResponse(idJson, new JsonObject { ["resourceTemplates"] = new JsonArray() });

                        case "prompts/list":
                            return ResultResponse(idJson, new JsonObject { ["prompts"] = new JsonArray() });

                        case "logging/setLevel":
                        case "completion/complete":
                            return ResultResponse(idJson, new JsonObject());

                        default:
                            if (isNotification) return null;
                            return ErrorResponse(idJson, -32601, "Method not found: " + method);
                    }
                }
                catch (Exception e)
                {
                    return ErrorResponse(idJson, -32603, "Internal error: " + e.Message);
                }
            }
        }

        private JsonObject BuildInitializeResult(JsonElement paramsElement)
        {
            string requested = paramsElement.ValueKind == JsonValueKind.Object
                ? AgentBridgeClient.GetString(paramsElement, "protocolVersion")
                : null;

            string negotiated = null;
            foreach (var v in SupportedProtocolVersions)
            {
                if (string.Equals(v, requested, StringComparison.Ordinal)) { negotiated = v; break; }
            }
            if (negotiated == null) negotiated = SupportedProtocolVersions[0];

            var caps = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            };

            var result = new JsonObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = caps,
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = ServerName,
                    ["version"] = _serverVersion
                }
            };

            if (!string.IsNullOrEmpty(_host.Instructions))
                result["instructions"] = _host.Instructions;

            return result;
        }

        private JsonObject BuildToolsList()
        {
            var arr = new JsonArray();
            foreach (var tool in _host.ListTools())
            {
                var annotations = new JsonObject
                {
                    ["readOnlyHint"] = tool.ReadOnly,
                    ["destructiveHint"] = tool.Destructive,
                    ["idempotentHint"] = false,
                    ["openWorldHint"] = true
                };

                var obj = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.InputSchema ?? new JsonObject { ["type"] = "object" },
                    ["annotations"] = annotations
                };
                if (!string.IsNullOrEmpty(tool.Title)) obj["title"] = tool.Title;
                arr.Add(obj);
            }
            return new JsonObject { ["tools"] = arr };
        }

        private JsonObject HandleToolCall(JsonElement paramsElement)
        {
            string name = AgentBridgeClient.GetString(paramsElement, "name");
            if (string.IsNullOrEmpty(name))
                return ContentResult(McpToolResult.Error("缺少工具名(name)"));

            JsonElement args = default;
            bool hasArgs = false;
            if (paramsElement.ValueKind == JsonValueKind.Object &&
                paramsElement.TryGetProperty("arguments", out args) &&
                args.ValueKind == JsonValueKind.Object)
            {
                hasArgs = true;
            }

            McpToolResult result;
            try
            {
                result = _host.CallTool(name, hasArgs ? args : default);
            }
            catch (Exception e)
            {
                result = McpToolResult.Error("工具执行异常: " + e.GetType().Name + ": " + e.Message);
            }
            return ContentResult(result ?? McpToolResult.Error("工具没有返回结果"));
        }

        private static JsonObject ContentResult(McpToolResult result)
        {
            var content = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Text ?? string.Empty
                }
            };
            return new JsonObject
            {
                ["content"] = content,
                ["isError"] = result.IsError
            };
        }

        private static readonly JsonSerializerOptions ResponseJsonOptions = new JsonSerializerOptions
        {
            // 让响应里的中文保持可读(默认会把非 ASCII 转义成 \uXXXX, 日志/排障时很难看)。
            // 这仍然是合法 JSON, 只是不再做多余转义。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string ResultResponse(string idJson, JsonObject result)
        {
            var obj = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idJson == null ? null : JsonNode.Parse(idJson),
                ["result"] = result
            };
            return obj.ToJsonString(ResponseJsonOptions);
        }

        private static string ErrorResponse(string idJson, int code, string message)
        {
            var obj = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idJson == null ? null : JsonNode.Parse(idJson),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
            return obj.ToJsonString(ResponseJsonOptions);
        }
    }
}
