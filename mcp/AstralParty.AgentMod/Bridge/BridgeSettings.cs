using System;
using System.Collections.Generic;
using AstralParty.Agent;
using CesiumLoader.SDK;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 桥接设置: 静态部分来自 mod 自己的 <c>config.json</c>, 运行时开关来自桥接目录的
    /// <c>control.json</c>(外部 MCP server 写, 优先级更高 —— 急停必须随时生效)。
    /// </summary>
    internal sealed class BridgeSettings
    {
        /// <summary>桥接目录覆盖(空 = 用 <see cref="AgentBridgeLayout.ResolveRoot"/>)。</summary>
        public string AgentDir;

        /// <summary>写 state.json 的间隔。</summary>
        public int StateIntervalMs = 250;
        /// <summary>轮询 commands/ 的间隔。</summary>
        public int PollIntervalMs = 120;
        /// <summary>写 bridge.json 心跳 + 维护(轮转/清理)的间隔。</summary>
        public int HeartbeatMs = 1000;
        /// <summary>重新读 control.json 的间隔。</summary>
        public int ControlPollMs = 500;
        /// <summary>超过这个年龄的命令视为过期(防止上次会话残留的命令在重启后突然执行)。</summary>
        public int CommandTtlMs = 10000;
        /// <summary>state.json 里回带多少条原始动作。</summary>
        public int RecentActionLimit = 24;
        /// <summary>jsonl 超过这个大小就轮转成 .1。</summary>
        public long MaxJournalBytes = 8L * 1024 * 1024;
        /// <summary>单 tick 最多执行几条命令(避免一帧里连发一堆操作)。</summary>
        public int MaxCommandsPerTick = 8;
        /// <summary>结果文件保留时长(超过就删, 防止目录无限增长)。</summary>
        public int ResultRetentionMs = 10 * 60 * 1000;

        /// <summary>总开关: false = 桥接只读(所有动作命令返回 rejected)。默认 false —— 这是实验性功能, 要动手得显式打开。</summary>
        public bool EnableActions = false;
        /// <summary>急停: 只记录不执行。</summary>
        public bool PauseActions;
        /// <summary>演练: 走完整流程但不真的发 C2S。</summary>
        public bool DryRun;

        /// <summary>是否记录事件流 / 原始动作流。</summary>
        public bool LogEvents = true;
        public bool LogActions = true;

        /// <summary>control.json 是否生效过(用于在 state.json 里说明开关来源)。</summary>
        public bool ControlFileApplied;
        public long ControlReadAtMs;

        public static BridgeSettings FromConfig(ModConfig config)
        {
            var s = new BridgeSettings();
            if (config == null) return s;
            try
            {
                s.AgentDir = config.GetString("agentDir", null);
                s.StateIntervalMs = ClampInt(config.GetInt("stateIntervalMs", s.StateIntervalMs), 50, 5000);
                s.PollIntervalMs = ClampInt(config.GetInt("pollIntervalMs", s.PollIntervalMs), 20, 2000);
                s.HeartbeatMs = ClampInt(config.GetInt("heartbeatMs", s.HeartbeatMs), 200, 10000);
                s.ControlPollMs = ClampInt(config.GetInt("controlPollMs", s.ControlPollMs), 100, 10000);
                s.CommandTtlMs = ClampInt(config.GetInt("commandTtlMs", s.CommandTtlMs), 500, 120000);
                s.RecentActionLimit = ClampInt(config.GetInt("recentActionLimit", s.RecentActionLimit), 0, 200);
                s.MaxCommandsPerTick = ClampInt(config.GetInt("maxCommandsPerTick", s.MaxCommandsPerTick), 1, 64);
                s.EnableActions = config.GetBool("enableActions", s.EnableActions);
                s.PauseActions = config.GetBool("pauseActions", s.PauseActions);
                s.DryRun = config.GetBool("dryRun", s.DryRun);
                s.LogEvents = config.GetBool("logEvents", s.LogEvents);
                s.LogActions = config.GetBool("logActions", s.LogActions);
            }
            catch { }
            return s;
        }

        /// <summary>把默认值写进 config.json(首次运行时), 并留下自解释的说明字段。</summary>
        public static void WriteDefaults(ModConfig config, string agentDir)
        {
            if (config == null) return;
            try
            {
                if (config.Has("agentDir")) return;
                config.Set("_说明", "AI Agent 桥接(实验性/未完成, 真机只验过只读链路)。桥接目录放 state.json/events.jsonl/actions.jsonl/commands/。**默认只读**: enableActions=false 时任何操作都拒绝执行; 想放开动作要自己改成 true 并自负风险。");
                config.Set("agentDir", agentDir ?? string.Empty);
                config.Set("enableActions", false);
                config.Set("pauseActions", false);
                config.Set("dryRun", false);
                config.Set("stateIntervalMs", 250);
                config.Set("pollIntervalMs", 120);
                config.Set("logEvents", true);
                config.Set("logActions", true);
                config.Save();
            }
            catch { }
        }

        /// <summary>读 control.json(存在就覆盖运行时开关)。</summary>
        public void ApplyControl(string root)
        {
            string text = AgentBridgeLayout.ReadAllTextOrNull(AgentBridgeLayout.ControlPath(root));
            if (string.IsNullOrEmpty(text))
            {
                ControlFileApplied = false;
                return;
            }
            try
            {
                var map = CesiumJson.Deserialize(text) as IDictionary<string, object>;
                if (map == null) return;

                // 大小写容错: MCP 侧写的是 PascalCase(EnableActions), 而隔壁 config.json 用的是
                // camelCase(enableActions) —— 用户手改 control.json 时很容易照着 config.json 写。
                // CesiumJson 解出来的字典是 Ordinal 比较, 不兜这一层就会"改了没反应"。
                map = CaseInsensitive(map);

                EnableActions = CesiumJson.GetBool(map, AgentBridgeLayout.ControlField.EnableActions, EnableActions);
                PauseActions = CesiumJson.GetBool(map, AgentBridgeLayout.ControlField.PauseActions, PauseActions);
                DryRun = CesiumJson.GetBool(map, AgentBridgeLayout.ControlField.DryRun, DryRun);
                ControlReadAtMs = CesiumJson.GetDouble(map, AgentBridgeLayout.ControlField.UpdatedAtMs, 0) > 0
                    ? (long)CesiumJson.GetDouble(map, AgentBridgeLayout.ControlField.UpdatedAtMs, 0) : 0;
                ControlFileApplied = true;
            }
            catch { }
        }

        /// <summary>把字典重建成忽略大小写的键表(仅在读 control.json 时用, 文件很小)。</summary>
        private static IDictionary<string, object> CaseInsensitive(IDictionary<string, object> map)
        {
            if (map == null) return null;
            var copy = new Dictionary<string, object>(map.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map) copy[kv.Key] = kv.Value;
            return copy;
        }

        private static int ClampInt(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
