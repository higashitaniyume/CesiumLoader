using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AstralParty.Agent
{
    /// <summary>
    /// 桥接目录与文件约定的**唯一出处**。
    ///
    /// 同一份源码被两边编译(见 mcp\README.md):
    ///   - 游戏内: <c>mcp\AstralParty.AgentMod</c>  (netstandard2.0, HybridCLR 热更)
    ///   - 进程外: <c>mcp\AstralParty.Mcp.Core</c>  (net8.0, MCP server)
    ///
    /// 硬约束(与加载器一致, 别在这里破坏):
    ///   - 热更程序集**不能 P/Invoke**, 所以只能靠"文件 + 环境变量"通信;
    ///   - 这里只允许用 netstandard2.0 的 BCL, 且**不得**引用 SDK / 游戏 / Unity 类型;
    ///   - 写文件一律"先写 .tmp 再原子替换", 避免对端读到写了一半的内容。
    /// </summary>
    public static partial class AgentBridgeLayout
    {
        /// <summary>协议版本。字段含义变化时 +1, 两边都校验。</summary>
        public const int SchemaVersion = 1;

        /// <summary>可覆盖桥接目录的环境变量(**两边都读**, 用来把桥接目录挪到别处做隔离测试)。</summary>
        public const string EnvAgentDir = "CESIUM_AGENT_DIR";

        /// <summary>加载器在游戏目录下用的文件夹名。</summary>
        public const string LoaderFolderName = "AstralParty_ModLoader";

        /// <summary>加载器文件夹下放桥接文件的子目录名。</summary>
        public const string AgentFolderName = "agent";

        public const string StateFileName = "state.json";
        public const string EventsFileName = "events.jsonl";
        public const string ActionsFileName = "actions.jsonl";
        public const string BridgeFileName = "bridge.json";
        public const string ControlFileName = "control.json";
        public const string CommandsFolderName = "commands";
        public const string ResultsFolderName = "results";

        /// <summary>命令信封字段名(两边共用, 避免拼写漂移)。</summary>
        public static class Field
        {
            public const string Schema = "schema";
            public const string Id = "id";
            public const string Seq = "seq";
            public const string Tool = "tool";
            public const string Args = "args";
            public const string IssuedAtMs = "issuedAtMs";
            public const string ExecutedAtMs = "executedAtMs";
            public const string Ok = "ok";
            public const string Code = "code";
            public const string Error = "error";
            public const string Detail = "detail";
            public const string Sn = "sn";
        }

        /// <summary>
        /// 命令工具名(game 侧执行)。外部 MCP 工具名是 <c>astral_&lt;tool&gt;</c>, 例如
        /// <c>astral_throw_dice</c> → <see cref="ThrowDice"/>。
        /// </summary>
        public static class Tool
        {
            public const string Prefix = "astral_";
            public const string Ping = "ping";
            public const string ThrowDice = "throw_dice";
            public const string Move = "move";
            public const string UseCard = "use_card";
            /// <summary>战斗询问(动作 5047): 接受/拒绝这场战斗。</summary>
            public const string AskBattle = "ask_battle";
            /// <summary>战斗内闪避选择(动作 5039)。</summary>
            public const string BattleChoice = "battle_choice";
            public const string UseEffectCard = "use_effect_card";
            public const string UseQuickCard = "use_quick_card";
            public const string AbandonCard = "abandon_card";
            public const string SelectRelic = "select_relic";
            public const string SelectRewardCard = "select_reward_card";
            /// <summary>棋盘事件弹窗的候选选择(cmd 5317, 候选 = SelectEventC2S.Events)。</summary>
            public const string SelectEvent = "select_event";
            public const string ShopBuy = "shop_buy";
            public const string AtmTransfer = "atm_transfer";
            public const string BuyRelic = "buy_relic";
            public const string Speed = "speed";
            /// <summary>加油站/出生点(动作 5077): 停留(true)还是继续走(false)。</summary>
            public const string StopOrContinue = "stop_or_continue";
            /// <summary>怪物追击(动作 5213): 追哪只怪(monsterId), 或 0 = 不追。</summary>
            public const string PursueMonster = "pursue_monster";
            /// <summary>商人买卡(动作 5323): 花 Gold 星币买下/不买。</summary>
            public const string VendorBuyCard = "vendor_buy_card";
            /// <summary>控制移动卡选点(动作 5067): 用 1..MaxPoint 点移动力。</summary>
            public const string SelectPoint = "select_point";
            /// <summary>复活队友(动作 5233): 要不要花星币复活倒下的队友。超时 = 不复活。</summary>
            public const string ReviveTeammate = "revive_teammate";
            /// <summary>机制选择(动作 5259): 启动(true)/不启动(false)这个地块机制。超时 = 不启动。</summary>
            public const string SelectMechanism = "select_mechanism";
            /// <summary>医院(动作 5093): 接受检查。这是该窗口**唯一**的合法上行(超时也走它)。</summary>
            public const string HospitalCheck = "hospital_check";
            /// <summary>炮台选目标(动作 5063): 选 1..TargetNum 个英雄当目标(targetIds), 或 leave=true 离开。超时 = 离开。</summary>
            public const string BatteryPick = "battery_pick";
            /// <summary>占卜(动作 5069): 两张牌选一张(候选 = CanChoiceIds)。超时 = 第 1 张。</summary>
            public const string DivinationPick = "divination_pick";
            /// <summary>赌场押注(动作 5081): GuessCode 1=奇数 2=偶数。超时 = 押奇数。</summary>
            public const string GambleGuess = "gamble_guess";
            /// <summary>赌场掷骰(动作 5083): 唯一合法上行(没有选择)。超时 = 也走它。</summary>
            public const string GambleDice = "gamble_dice";
            /// <summary>抽奖选号(动作 5041): 从 1..上限里选 Num 个还没占的号码(候选 = 本地算)。超时 = 最小的那几个。</summary>
            public const string LotteryPick = "lottery_pick";
        }

        /// <summary>把这些名字拼成外部工具名(<c>astral_xxx</c>)。</summary>
        public static string ExternalToolName(string tool)
        {
            return Tool.Prefix + tool;
        }

        /// <summary>结果码。ok 之外都表示命令没有真正作用到对局。</summary>
        public static class Code
        {
            public const string Ok = "ok";
            public const string BadArgs = "bad_args";
            public const string UnknownTool = "unknown_tool";
            public const string Rejected = "rejected";          // 游戏侧拒绝(权限/不在对局/没轮到)
            public const string Paused = "paused";              // 急停开关生效
            public const string DryRun = "dry_run";             // 演练模式: 只记录不发送
            public const string Expired = "expired";            // 命令过期(游戏侧清理)
            public const string Exception = "exception";
        }

        /// <summary>control.json 的字段名(外部写, 游戏内读)。</summary>
        public static class ControlField
        {
            public const string EnableActions = "EnableActions";
            public const string PauseActions = "PauseActions";
            public const string DryRun = "DryRun";
            public const string UpdatedAtMs = "UpdatedAtMs";
            public const string UpdatedBy = "UpdatedBy";
            public const string Note = "Note";
        }

        /// <summary>bridge.json(心跳)的字段名(游戏内写, 外部读)。</summary>
        public static class BridgeField
        {
            public const string Schema = "Schema";
            public const string ModVersion = "ModVersion";
            public const string SdkVersion = "SdkVersion";
            public const string ProcessId = "ProcessId";
            public const string StartedAtMs = "StartedAtMs";
            public const string LastTickMs = "LastTickMs";
            public const string TickCount = "TickCount";
            public const string StateSeq = "StateSeq";
            public const string AgentDir = "AgentDir";
            public const string Scene = "Scene";
            public const string InRoom = "InRoom";
            public const string InBattle = "InBattle";
            public const string CommandsExecuted = "CommandsExecuted";
            public const string CommandsRejected = "CommandsRejected";
        }

        /// <summary>
        /// 解析桥接根目录。**两边必须得到同一个值**, 否则永远连不上。
        ///
        /// 优先级:
        ///   1) 环境变量 <see cref="EnvAgentDir"/>
        ///   2) <c>%LocalAppData%\AstralParty_ModLoader\agent</c>
        ///
        /// 为什么用 LocalAppData 而不是游戏目录: 游戏装在 <c>Program Files (x86)</c> 下,
        /// 不保证普通权限可写; LocalAppData 一定可写, 且外部进程能算出同一个路径。
        /// </summary>
        public static string ResolveRoot()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable(EnvAgentDir); } catch { }
            if (!string.IsNullOrEmpty(env)) return env.Trim();

            string local = null;
            try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
            if (string.IsNullOrEmpty(local))
            {
                try { local = Path.GetTempPath(); } catch { local = "."; }
            }
            return Path.Combine(Path.Combine(local, LoaderFolderName), AgentFolderName);
        }

        public static string StatePath(string root) { return Path.Combine(root, StateFileName); }
        public static string EventsPath(string root) { return Path.Combine(root, EventsFileName); }
        public static string ActionsPath(string root) { return Path.Combine(root, ActionsFileName); }
        public static string BridgePath(string root) { return Path.Combine(root, BridgeFileName); }
        public static string ControlPath(string root) { return Path.Combine(root, ControlFileName); }
        public static string CommandsDir(string root) { return Path.Combine(root, CommandsFolderName); }
        public static string ResultsDir(string root) { return Path.Combine(root, ResultsFolderName); }

        /// <summary>命令文件名: <c>{seq:D8}-{id}.json</c>。零填充的 seq 保证按文件名排序 = 按下发顺序。</summary>
        public static string CommandFileName(long seq, string id)
        {
            return seq.ToString("D8") + "-" + Sanitize(id) + ".json";
        }

        public static string CommandPath(string root, long seq, string id)
        {
            return Path.Combine(CommandsDir(root), CommandFileName(seq, id));
        }

        /// <summary>结果文件名: <c>{id}.json</c>。</summary>
        public static string ResultPath(string root, string id)
        {
            return Path.Combine(ResultsDir(root), Sanitize(id) + ".json");
        }

        /// <summary>从命令文件名解出 (seq, id); 名字不合规返回 false。</summary>
        public static bool TryParseCommandFileName(string fileName, out long seq, out string id)
        {
            seq = 0;
            id = null;
            if (string.IsNullOrEmpty(fileName)) return false;
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

            string stem = fileName.Substring(0, fileName.Length - 5);
            int dash = stem.IndexOf('-');
            if (dash <= 0 || dash >= stem.Length - 1) return false;

            if (!long.TryParse(stem.Substring(0, dash), out seq)) return false;
            id = stem.Substring(dash + 1);
            return id.Length > 0;
        }

        /// <summary>把 id 清洗成合法文件名(只留字母数字和 - _), 防止外部输入穿越目录。</summary>
        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    sb.Append(c);
            }
            return sb.Length == 0 ? "unnamed" : sb.ToString();
        }

        /// <summary>建齐目录(幂等)。</summary>
        public static void EnsureDirectories(string root)
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(CommandsDir(root));
            Directory.CreateDirectory(ResultsDir(root));
        }

        /// <summary>
        /// 原子写: 先写 <c>{path}.tmp</c>, 再替换目标。
        /// 优先 <see cref="File.Replace"/> (NTFS 上原子, 读者要么看到旧内容要么看到新内容);
        /// 不支持时退化成 delete+move (会有一个目标短暂不存在的窗口, 但绝不会读到半截内容)。
        /// </summary>
        public static bool WriteAtomic(string path, string content)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                // ⚠ 这里**不能**用 FileStream + Flush: 热更程序集(HybridCLR/IL2CPP)的 BCL 是残缺的,
                // 真机实测 `fs.Flush(true)` 直接报 `MethodNotFind System.IO.FileStream::Flush`
                // (整个 state.json 一条都写不出来, 而离线 net8.0 测试永远绿)。
                // File.WriteAllText 是 AOT 侧实现, 且默认就是 UTF-8 **无 BOM** —— 正是我们要的。
                File.WriteAllText(tmp, content ?? string.Empty);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return true;
                    }
                    catch
                    {
                        // 有些文件系统/BCL 不支持 Replace, 退化处理
                    }
                }
                try { File.Delete(path); } catch { }
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读文件; 不存在/被占用/内容非法一律返回 null(带几次重试)。</summary>
        /// <remarks>
        /// 用 1 参数的 <see cref="File.ReadAllText(string)"/>(游戏内其它 mod 读 config.json 走的就是它);
        /// 不要换成带 <c>Encoding</c> 的重载 —— AGENTS.md §12.1 记的那批"热更侧缺失重载"。
        /// </remarks>
        public static string ReadAllTextOrNull(string path, int retries = 3)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    return File.ReadAllText(path);
                }
                catch
                {
                    if (i == retries) return null;
                    try { Thread.Sleep(10); } catch { }
                }
            }
            return null;
        }

        // ReadTail(读 jsonl 末尾, 用了游戏内没验证过的 FileStream.Seek)不在这里:
        // 它被拆到只编进 MCP server 的 partial 文件 `AgentBridgeReadTail.cs` 里,
        // 让 mod 侧既看不到也调不到 —— 见那个文件顶部说明。

        /// <summary>当前 UTC 毫秒时间戳(**纪元是 0001-01-01, 不是 Unix 纪元**)。</summary>
        /// <remarks>
        /// 必须用 <see cref="DateTime.UtcNow"/>: 加载器的变速引擎 hook 了
        /// <c>GetTickCount/GetTickCount64/timeGetTime/QueryPerformanceCounter</c>,
        /// 所以在游戏进程里 <c>Stopwatch</c> / <c>Environment.TickCount</c> 走的是**虚拟时间**,
        /// 拿来当"现实时间"会随倍率漂移(加载器的冒烟工程也踩过同一个坑)。
        ///
        /// ⚠ 这个值等于 <c>DateTime.UtcNow.Ticks / 10000</c>, 也就是"自 0001-01-01 起的毫秒",
        /// **不是** <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c>(Unix 纪元, 今天约 1.7e12)。
        /// 两者相差约 3.5 万倍, 混用会让心跳年龄算出天文数字(离线冒烟脚本就这么翻过一次车)。
        /// 之所以不换成 Unix 纪元: 两侧共用本文件, 且所有相对量(RemainingMs 等)都是本函数差分,
        /// 改纪元只会制造"新 server + 旧 mod"的混装风险, 没有任何功能收益。
        /// 外部写夹具/解析 state.json 时请一律用这个函数(或照抄它的算法)。
        /// </remarks>
        public static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>当前 UTC 时间的 ISO 字符串(human-readable, 只给人看)。</summary>
        /// <remarks>
        /// 用 1 参数的 <c>ToString("o")</c> —— SDK 的相机快照/诊断转储在游戏内跑的就是这一条。
        /// (double 的 <c>ToString("0.###", CultureInfo.InvariantCulture)</c> 已在游戏内验证可用,
        /// 但 <c>DateTime.ToString(format, provider)</c> 这个组合没有实证, 所以这里不冒险。)
        /// 机器可读的时间请用 <see cref="NowMs"/>。
        /// </remarks>
        public static string NowUtcIso()
        {
            return DateTime.UtcNow.ToString("o");
        }

        /// <summary>追加一段文本(一次打开写完, 适合批量刷 jsonl)。</summary>
        public static bool AppendText(string path, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 同样避开 FileStream: 用 SDK 日志(SdkLog)在游戏里跑通的那条路。
                File.AppendAllText(path, text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>追加一行(jsonl)。文件保持"写完即关", 让外部进程随时能读。</summary>
        public static bool AppendLine(string path, string line)
        {
            return AppendText(path, line + "\n");
        }

        /// <summary>文件超过 limitBytes 就改名成 <c>{name}.1{ext}</c>(覆盖旧的), 保持单文件有界。</summary>
        public static void RotateIfLarge(string path, long limitBytes)
        {
            try
            {
                if (!File.Exists(path)) return;
                var fi = new FileInfo(path);
                if (fi.Length < limitBytes) return;
                string rotated = path + ".1";
                try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
                try { File.Move(path, rotated); } catch { }
            }
            catch { }
        }
    }
}
