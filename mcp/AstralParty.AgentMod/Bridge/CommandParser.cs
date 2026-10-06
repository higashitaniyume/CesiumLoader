using System;
using System.Collections.Generic;
using AstralParty.Agent;
using CesiumLoader.SDK.Configuration;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>一条来自 MCP server 的命令(已解析)。</summary>
    internal sealed class BridgeCommand
    {
        public long Seq;
        public string Id;
        public string Tool;
        public long IssuedAtMs;
        public Dictionary<string, object> Args = new Dictionary<string, object>();

        public bool Has(string key)
        {
            return Args != null && Args.ContainsKey(key);
        }

        public int GetInt(string key, int fallback)
        {
            return Args == null ? fallback : CesiumJson.GetInt(Args, key, fallback);
        }

        public long GetLong(string key, long fallback)
        {
            if (Args == null || !Args.ContainsKey(key)) return fallback;
            double d = CesiumJson.GetDouble(Args, key, fallback);
            return (long)d;
        }

        public bool GetBool(string key, bool fallback)
        {
            return Args == null ? fallback : CesiumJson.GetBool(Args, key, fallback);
        }

        public double GetDouble(string key, double fallback)
        {
            return Args == null ? fallback : CesiumJson.GetDouble(Args, key, fallback);
        }

        public string GetString(string key, string fallback)
        {
            return Args == null ? fallback : CesiumJson.GetString(Args, key, fallback);
        }

        public List<int> GetIntList(string key)
        {
            return Args == null ? new List<int>() : CesiumJson.GetList<int>(Args, key);
        }

        /// <summary>取 long 数组(玩家/怪物 id 是 64 位, 不能用 int 列表)。</summary>
        public long[] GetLongArray(string key)
        {
            return Args == null ? new long[0] : CesiumJson.GetList<long>(Args, key).ToArray();
        }

        /// <summary>取单个 int(既接受 <c>cardId: 123</c> 也接受 <c>cardIds: [123]</c>)。</summary>
        public bool TryGetIntFlexible(string key, string listKey, out int value)
        {
            value = 0;
            if (Args == null) return false;
            if (Args.ContainsKey(key)) { value = CesiumJson.GetInt(Args, key, 0); return value != 0; }
            if (listKey != null && Args.ContainsKey(listKey))
            {
                var list = CesiumJson.GetList<int>(Args, listKey);
                if (list.Count > 0) { value = list[0]; return value != 0; }
            }
            return false;
        }
    }

    /// <summary>命令解析(纯逻辑, 可离线单测)。</summary>
    internal static class BridgeCommandParser
    {
        /// <summary>解析命令 JSON。失败时 error 说明原因, 调用方应回一个 error 结果。</summary>
        public static bool TryParse(string json, out BridgeCommand command, out string error)
        {
            command = null;
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "空文件"; return false; }

            object node;
            if (!CesiumJson.TryDeserialize(json, out node)) { error = "JSON 解析失败"; return false; }
            var map = node as IDictionary<string, object>;
            if (map == null) { error = "顶层不是对象"; return false; }

            var cmd = new BridgeCommand();
            cmd.Id = CesiumJson.GetString(map, AgentBridgeLayout.Field.Id, null);
            cmd.Tool = CesiumJson.GetString(map, AgentBridgeLayout.Field.Tool, null);
            cmd.Seq = (long)CesiumJson.GetDouble(map, AgentBridgeLayout.Field.Seq, 0);
            cmd.IssuedAtMs = (long)CesiumJson.GetDouble(map, AgentBridgeLayout.Field.IssuedAtMs, 0);

            if (string.IsNullOrEmpty(cmd.Tool)) { error = "缺少 tool 字段"; return false; }
            if (string.IsNullOrEmpty(cmd.Id)) { error = "缺少 id 字段"; return false; }

            object argsNode;
            if (map.TryGetValue(AgentBridgeLayout.Field.Args, out argsNode))
            {
                var argsMap = argsNode as IDictionary<string, object>;
                if (argsMap != null)
                {
                    foreach (var kv in argsMap) cmd.Args[kv.Key] = kv.Value;
                }
            }

            command = cmd;
            return true;
        }
    }

    /// <summary>命令结果(写进 results\{id}.json)。</summary>
    internal sealed class BridgeResult
    {
        public int Schema = AgentBridgeLayout.SchemaVersion;
        public string Id;
        public string Tool;
        public bool Ok;
        public string Code = AgentBridgeLayout.Code.Ok;
        public string Error;
        public string Detail;
        public long ExecutedAtMs;
        public long LatencyMs;

        public static BridgeResult Success(string id, string tool, string detail, long latencyMs)
        {
            return new BridgeResult
            {
                Id = id,
                Tool = tool,
                Ok = true,
                Code = AgentBridgeLayout.Code.Ok,
                Detail = detail,
                ExecutedAtMs = AgentBridgeLayout.NowMs(),
                LatencyMs = latencyMs
            };
        }

        public static BridgeResult Failure(string id, string tool, string code, string error)
        {
            return new BridgeResult
            {
                Id = id,
                Tool = tool,
                Ok = false,
                Code = code,
                Error = error,
                ExecutedAtMs = AgentBridgeLayout.NowMs()
            };
        }

        public string ToJson()
        {
            try { return CesiumJson.Serialize(this); }
            catch { return "{\"Schema\":1,\"Id\":\"" + Id + "\",\"Ok\":false,\"Code\":\"exception\",\"Error\":\"结果序列化失败\"}"; }
        }
    }
}
