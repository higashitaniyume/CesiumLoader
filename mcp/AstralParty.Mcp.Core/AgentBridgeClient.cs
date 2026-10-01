using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using AstralParty.Agent;

namespace AstralParty.Mcp
{
    /// <summary>一条命令的执行结果(游戏侧回执)。</summary>
    public sealed class BridgeCommandResult
    {
        public bool Ok;
        public string Code;
        public string Error;
        public string Detail;
        public long LatencyMs;
        public bool TimedOut;
        public string Raw;

        public override string ToString()
        {
            if (TimedOut) return "TIMEOUT";
            return (Ok ? "OK" : "FAIL(" + Code + ")") + (Error != null ? " " + Error : Detail != null ? " " + Detail : "");
        }
    }

    /// <summary>检查器: 游戏内桥接是否活着。</summary>
    public sealed class BridgeLiveness
    {
        public bool HeartbeatExists;
        public bool StateExists;
        public bool Alive;
        public long AgeMs = -1;
        public long LastTickMs;
        public string ModVersion;
        public string SdkVersion;
        public int ProcessId;
        public string Scene;
        public bool InRoom;
        public bool InBattle;
        public long StateSeq;
        public long CommandsExecuted;
        public long CommandsRejected;
        public string Root;
    }

    /// <summary>
    /// 桥接客户端: MCP server 侧对"文件通道"的全部读写都走这里。
    ///
    /// 协议约定见 <see cref="AgentBridgeLayout"/>: 命令"一命令一文件 + 一结果一文件",
    /// 写命令用原子替换, 读结果带超时重试。
    /// </summary>
    public sealed class AgentBridgeClient
    {
        private readonly string _root;
        private long _seq;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 写文件时不做 ASCII 转义: control.json / 命令文件是给人看也给人改的,
        /// 中文保持原样(CesiumJson 与 System.Text.Json 都能解析, 两种写法等价)。
        /// </summary>
        private static readonly JsonSerializerOptions FileJsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public AgentBridgeClient(string root)
        {
            _root = root ?? AgentBridgeLayout.ResolveRoot();
            AgentBridgeLayout.EnsureDirectories(_root);
        }

        public static AgentBridgeClient CreateDefault()
        {
            return new AgentBridgeClient(AgentBridgeLayout.ResolveRoot());
        }

        public string Root { get { return _root; } }

        /// <summary>默认判定"桥接已死"的阈值(心跳超过这个时间没更新)。</summary>
        public const int DefaultStaleMs = 5000;

        // ============================== 读 ==============================

        /// <summary>读心跳并按年龄判断存活。</summary>
        public BridgeLiveness ReadLiveness(int staleMs = DefaultStaleMs)
        {
            var lv = new BridgeLiveness { Root = _root };
            string path = AgentBridgeLayout.BridgePath(_root);
            lv.HeartbeatExists = File.Exists(path);
            lv.StateExists = File.Exists(AgentBridgeLayout.StatePath(_root));
            if (!lv.HeartbeatExists) return lv;

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    var r = doc.RootElement;
                    lv.LastTickMs = GetLong(r, AgentBridgeLayout.BridgeField.LastTickMs);
                    lv.ModVersion = GetString(r, AgentBridgeLayout.BridgeField.ModVersion);
                    lv.SdkVersion = GetString(r, AgentBridgeLayout.BridgeField.SdkVersion);
                    lv.ProcessId = (int)GetLong(r, AgentBridgeLayout.BridgeField.ProcessId);
                    lv.Scene = GetString(r, AgentBridgeLayout.BridgeField.Scene);
                    lv.InRoom = GetBool(r, AgentBridgeLayout.BridgeField.InRoom);
                    lv.InBattle = GetBool(r, AgentBridgeLayout.BridgeField.InBattle);
                    lv.StateSeq = GetLong(r, AgentBridgeLayout.BridgeField.StateSeq);
                    lv.CommandsExecuted = GetLong(r, AgentBridgeLayout.BridgeField.CommandsExecuted);
                    lv.CommandsRejected = GetLong(r, AgentBridgeLayout.BridgeField.CommandsRejected);
                }
            }
            catch
            {
                return lv;
            }

            if (lv.LastTickMs > 0)
            {
                lv.AgeMs = AgentBridgeLayout.NowMs() - lv.LastTickMs;
                lv.Alive = lv.AgeMs <= staleMs;
            }
            return lv;
        }

        /// <summary>读 state.json 原文(可能为 null)。</summary>
        public string ReadStateText()
        {
            return AgentBridgeLayout.ReadAllTextOrNull(AgentBridgeLayout.StatePath(_root));
        }

        /// <summary>读 state.json 并解析(调用方负责 Dispose)。</summary>
        public JsonDocument ReadState()
        {
            string text = ReadStateText();
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonDocument.Parse(text); }
            catch { return null; }
        }

        /// <summary>读 control.json(调用方 Dispose)。</summary>
        public JsonDocument ReadControl()
        {
            string text = AgentBridgeLayout.ReadAllTextOrNull(AgentBridgeLayout.ControlPath(_root));
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonDocument.Parse(text); }
            catch { return null; }
        }

        /// <summary>读事件流的末尾若干行(每行一个 JSON 对象)。</summary>
        public List<JsonElement> ReadEvents(int limit)
        {
            return ReadJsonLines(AgentBridgeLayout.EventsPath(_root), limit);
        }

        /// <summary>读原始动作流的末尾若干行。</summary>
        public List<JsonElement> ReadActions(int limit)
        {
            return ReadJsonLines(AgentBridgeLayout.ActionsPath(_root), limit);
        }

        private static List<JsonElement> ReadJsonLines(string path, int limit)
        {
            var list = new List<JsonElement>();
            if (limit <= 0) limit = 50;

            // 粗略按每行 <= 256B 估需要读多少尾部字节
            string text = AgentBridgeLayout.ReadTail(path, Math.Max(64 * 1024, limit * 256));
            if (string.IsNullOrEmpty(text)) return list;

            // 用 JsonDocument 解析每一行, 但 JsonElement 的生命周期绑在 document 上,
            // 所以这里把行原样 Clone() 出来交给调用方(小对象, 代价可接受)。
            using (var doc = JsonDocument.Parse("[" + string.Join(",", SplitLines(text)) + "]"))
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }

            if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
            return list;
        }

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                if (end > start) lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            // 丢掉解析不了的半行/坏行会破坏整体解析, 所以这里只保留看起来是对象的行
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length < 2 || l[0] != '{' || l[l.Length - 1] != '}') lines.RemoveAt(i);
            }
            return lines;
        }

        // ============================== 写 ==============================

        /// <summary>
        /// 下发一条命令并等待游戏侧回执。
        /// </summary>
        /// <param name="tool">工具名(不含 astral_ 前缀, 见 <see cref="AgentBridgeLayout.Tool"/>)。</param>
        /// <param name="args">参数(可为 null)。</param>
        /// <param name="timeoutMs">等待回执的超时。</param>
        public BridgeCommandResult SendCommand(string tool, IDictionary<string, object> args, int timeoutMs)
        {
            long seq = Interlocked.Increment(ref _seq);
            string id = "c" + AgentBridgeLayout.NowMs().ToString() + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = AgentBridgeLayout.CommandPath(_root, seq, id);
            string resultPath = AgentBridgeLayout.ResultPath(_root, id);

            string json = BuildCommandJson(id, seq, tool, args);
            try
            {
                if (!AgentBridgeLayout.WriteAtomic(path, json))
                    return new BridgeCommandResult { Ok = false, Code = "write_failed", Error = "命令文件写入失败: " + path };
            }
            catch (Exception e)
            {
                return new BridgeCommandResult { Ok = false, Code = "write_failed", Error = e.Message };
            }

            long deadline = AgentBridgeLayout.NowMs() + Math.Max(200, timeoutMs);
            while (AgentBridgeLayout.NowMs() < deadline)
            {
                string text = AgentBridgeLayout.ReadAllTextOrNull(resultPath);
                if (!string.IsNullOrEmpty(text))
                {
                    var res = ParseResult(text);
                    TryDelete(resultPath);
                    TryDelete(path);
                    return res;
                }
                Thread.Sleep(25);
            }

            // 超时: 再读一次(竞态), 并把命令文件删掉(避免它稍后被"补发")
            string late = AgentBridgeLayout.ReadAllTextOrNull(resultPath);
            if (!string.IsNullOrEmpty(late))
            {
                var res = ParseResult(late);
                TryDelete(resultPath);
                TryDelete(path);
                return res;
            }
            TryDelete(path);
            return new BridgeCommandResult
            {
                Ok = false,
                Code = "timeout",
                TimedOut = true,
                Error = "游戏侧 " + timeoutMs + "ms 内没有回执(游戏没开 / 桥接 mod 没加载 / 该命令被跳过)"
            };
        }

        private static string BuildCommandJson(string id, long seq, string tool, IDictionary<string, object> args)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"schema\":").Append(AgentBridgeLayout.SchemaVersion);
            sb.Append(",\"id\":").Append(JsonSerializer.Serialize(id));
            sb.Append(",\"seq\":").Append(seq);
            sb.Append(",\"tool\":").Append(JsonSerializer.Serialize(tool));
            sb.Append(",\"issuedAtMs\":").Append(AgentBridgeLayout.NowMs());
            sb.Append(",\"args\":");
            sb.Append(args == null ? "{}" : JsonSerializer.Serialize(args));
            sb.Append('}');
            return sb.ToString();
        }

        private static BridgeCommandResult ParseResult(string text)
        {
            var res = new BridgeCommandResult { Raw = text };
            try
            {
                using (var doc = JsonDocument.Parse(text))
                {
                    var r = doc.RootElement;
                    res.Ok = GetBool(r, "Ok") || GetBool(r, "ok");
                    res.Code = GetString(r, "Code") ?? GetString(r, "code");
                    res.Error = GetString(r, "Error") ?? GetString(r, "error");
                    res.Detail = GetString(r, "Detail") ?? GetString(r, "detail");
                    res.LatencyMs = GetLong(r, "LatencyMs");
                }
            }
            catch (Exception e)
            {
                res.Ok = false;
                res.Code = "bad_result";
                res.Error = "回执解析失败: " + e.Message;
            }
            return res;
        }

        /// <summary>写 control.json(急停/演练/只读)。未传的开关保持原值(没文件时用给定默认)。</summary>
        public string WriteControl(bool? enableActions, bool? pauseActions, bool? dryRun, string note, string updatedBy)
        {
            bool enable = enableActions ?? true;
            bool pause = pauseActions ?? false;
            bool dry = dryRun ?? false;

            using (var doc = ReadControl())
            {
                if (doc != null)
                {
                    var r = doc.RootElement;
                    bool v;
                    // 没传的开关保持文件里的原值(手改过 control.json 的人不会因为一次 astral_resume 被覆盖)
                    if (!enableActions.HasValue && TryGetBool(r, AgentBridgeLayout.ControlField.EnableActions, out v)) enable = v;
                    if (!pauseActions.HasValue && TryGetBool(r, AgentBridgeLayout.ControlField.PauseActions, out v)) pause = v;
                    if (!dryRun.HasValue && TryGetBool(r, AgentBridgeLayout.ControlField.DryRun, out v)) dry = v;
                }
            }

            var payload = new Dictionary<string, object>
            {
                { AgentBridgeLayout.ControlField.EnableActions, enable },
                { AgentBridgeLayout.ControlField.PauseActions, pause },
                { AgentBridgeLayout.ControlField.DryRun, dry },
                { AgentBridgeLayout.ControlField.UpdatedAtMs, AgentBridgeLayout.NowMs() },
                { AgentBridgeLayout.ControlField.UpdatedBy, updatedBy ?? "mcp" },
                { AgentBridgeLayout.ControlField.Note, note ?? "" }
            };

            string json = JsonSerializer.Serialize(payload, FileJsonOptions);
            AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.ControlPath(_root), json);
            return json;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ============================== JSON 辅助(大小写不敏感) ==============================

        public static string GetString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
            }
            return null;
        }

        public static long GetLong(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return 0;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out long v)) return v;
                if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out long s)) return s;
            }
            return 0;
        }

        public static bool GetBool(JsonElement e, string name)
        {
            bool v;
            return TryGetBool(e, name, out v) && v;
        }

        /// <summary>按名字取布尔(大小写不敏感), 取不到返回 false。</summary>
        public static bool TryGetBool(JsonElement e, string name, out bool value)
        {
            value = false;
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (p.Value.ValueKind == JsonValueKind.False) { value = false; return true; }
                return false;
            }
            return false;
        }

        /// <summary>按名字取子对象(大小写不敏感), 取不到返回 false。</summary>
        public static bool TryGetObject(JsonElement e, string name, out JsonElement value)
        {
            value = default;
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.Object) return false;
                value = p.Value;
                return true;
            }
            return false;
        }
    }
}
