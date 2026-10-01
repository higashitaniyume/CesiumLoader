using System;
using System.IO;
using System.Text;
using AstralParty.Agent;

namespace AstralParty.Mcp
{
    /// <summary>
    /// MCP server 入口(stdio)。
    ///
    /// 用法:
    ///   AstralParty.Mcp.exe                     ← 自动定位桥接目录(CESIUM_AGENT_DIR → %LocalAppData%)
    ///   AstralParty.Mcp.exe --agent-dir &lt;路径&gt;   ← 显式指定桥接目录
    ///   AstralParty.Mcp.exe --timeout &lt;毫秒&gt;     ← 动作命令等回执的默认超时(默认 5000)
    ///   AstralParty.Mcp.exe --print-config       ← 打印解析出来的目录与工具清单(排障用, 不跑协议)
    ///
    /// 注意: 这是**标准输入输出协议**进程。除了日志(stderr)之外, stdout 只能出现 JSON-RPC 消息,
    /// 所以这里任何提示都往 stderr 写 —— 混进 stdout 会让 MCP 客户端直接解析失败。
    /// </summary>
    internal static class Program
    {
        private const string ServerVersion = "1.0.0";

        private static int Main(string[] args)
        {
            string agentDir = null;
            int timeoutMs = 5000;
            bool printConfig = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--agent-dir":
                    case "-d":
                        if (i + 1 < args.Length) agentDir = args[++i];
                        break;
                    case "--timeout":
                    case "-t":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out timeoutMs);
                        break;
                    case "--print-config":
                        printConfig = true;
                        break;
                    case "--help":
                    case "-h":
                        Console.Error.WriteLine(Usage);
                        return 0;
                    default:
                        Console.Error.WriteLine("未知参数: " + a);
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }

            if (timeoutMs < 200) timeoutMs = 200;

            AgentBridgeClient client;
            try
            {
                client = string.IsNullOrEmpty(agentDir)
                    ? AgentBridgeClient.CreateDefault()
                    : new AgentBridgeClient(agentDir);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("初始化桥接目录失败: " + e.Message);
                return 1;
            }

            var host = new AstralToolHost(client, timeoutMs);

            if (printConfig)
            {
                Console.WriteLine("桥接目录: " + client.Root);
                Console.WriteLine("命令超时: " + timeoutMs + "ms");
                var lv = client.ReadLiveness();
                Console.WriteLine(lv.HeartbeatExists
                    ? "心跳: " + (lv.Alive ? "活着" : "已停止") + " (" + lv.AgeMs + "ms 前)"
                    : "心跳: 没有 bridge.json(游戏没开 / mod 未加载)");
                Console.WriteLine("工具 (" + host.ListTools().Count + "):");
                foreach (var t in host.ListTools()) Console.WriteLine("  " + t.Name + "  — " + t.Title);
                return 0;
            }

            // stdout 必须是干净的 JSON-RPC 流。UTF-8 无 BOM, 关掉自动刷新以外的干扰。
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

            Console.Error.WriteLine("astral-party-mcp " + ServerVersion + " 桥接目录: " + client.Root);

            try
            {
                var server = new McpServer(stdin, stdout, host, ServerVersion);
                server.Run();
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("协议循环异常: " + e);
                return 1;
            }
            finally
            {
                try { stdout.Flush(); } catch { }
            }
        }

        private const string Usage =
            "AstralParty MCP server (stdio) —— 让 AI agent 接管《吉星派对》对局\n" +
            "\n" +
            "  --agent-dir <路径>   桥接目录(默认 CESIUM_AGENT_DIR 或 %LocalAppData%\\AstralParty_ModLoader\\agent)\n" +
            "  --timeout <毫秒>     动作命令等回执的默认超时(默认 5000)\n" +
            "  --print-config       打印解析结果与工具清单后退出\n" +
            "\n" +
            "在 MCP 客户端里的典型配置(Claude Desktop / 任意 MCP host):\n" +
            "  {\"mcpServers\":{\"astral-party\":{\"command\":\"<本exe路径>\"}}}\n";
    }
}
