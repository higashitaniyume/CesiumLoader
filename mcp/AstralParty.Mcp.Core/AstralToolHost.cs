using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using AstralParty.Agent;

namespace AstralParty.Mcp
{
    /// <summary>
    /// 工具面: 把"游戏内桥接文件通道"包装成 MCP 工具。
    ///
    /// 分工: 这个进程**不做任何决策**, 只负责
    ///   - 读: 状态/事件/原始动作 → 给 agent 看;
    ///   - 写: 命令文件 → 游戏内 mod 在主线程执行 → 读回执。
    /// 决策全在 agent 那边, 所以这里的工具描述要写得让 agent 一眼看懂什么时候该用哪个。
    /// </summary>
    public sealed class AstralToolHost : IMcpToolHost
    {
        private readonly AgentBridgeClient _client;
        private readonly int _defaultTimeoutMs;
        private readonly List<McpTool> _tools = new List<McpTool>();

        public AstralToolHost(AgentBridgeClient client, int defaultTimeoutMs = 5000)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _defaultTimeoutMs = defaultTimeoutMs;
            BuildTools();
        }

        public AgentBridgeClient Client { get { return _client; } }

        public string Instructions
        {
            get
            {
                return
                    "《吉星派对 / Astral Party》对局接管 server(派对模式, 服务器权威)。\n" +
                    "典型循环:\n" +
                    "  1) astral_status —— 确认游戏内桥接活着(心跳 < 5s)以及三个开关的状态;\n" +
                    "  2) astral_pending {\"waitMs\":15000} —— 等你需要响应的窗口(轮到你投骰/选筹码/选奖励卡…);\n" +
                    "  3) 看清 pending.kind 后出招: astral_throw_dice / astral_move / astral_use_card /\n" +
                    "     astral_select_relic / astral_select_reward_card / astral_use_quick_card /\n" +
                    "     astral_shop_buy / astral_buy_relic / astral_atm_transfer …(pending.Options 里会列出本窗口可用的操作);\n" +
                    "  4) 重复 2-3。需要手牌/场上数值时用 astral_state; 想复盘刚发生了什么用 astral_events / astral_actions。\n" +
                    "注意:\n" +
                    "  - 桥接只发\"和玩家手动点 UI 完全相同\"的 C2S 请求, 不修改内存、不伪造结果, 服务器照常校验;\n" +
                    "  - 动作失败会返回 isError, 先读 error/notes(常见原因: 不在对局、没轮到你、窗口已过、开关没开);\n" +
                    "  - 不确定就先用只读工具(`astral_state`/`astral_pending`), 别盲目连发动作;\n" +
                    "  - 要立刻停手用 astral_emergency_stop。";
            }
        }

        public IReadOnlyList<McpTool> ListTools() { return _tools; }

        // ============================== 工具定义 ==============================

        private void BuildTools()
        {
            Add("astral_status", "桥接状态", true, false,
                "检查游戏内桥接是否活着(心跳年龄)、当前场景/开关/计数、以及此刻待响应的窗口摘要。**每次接管前先调它**。",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

            Add("astral_state", "完整局面快照", true, false,
                "读 state.json: 自己(星币/手牌数)、全体单位(玩家+怪物: 攻/防/血/星币)、自己的手牌(CardId/Guid/费用)、自己的 buff、当前待响应窗口、最近原始动作。",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

            Add("astral_pending", "等我响应的窗口", true, false,
                "只取\"现在服务器在等我做什么\"(kind/候选/sn/已等待多久/可用操作), 可带 waitMs 阻塞等待窗口出现或变化。轮到你时通常第一步就调它。",
                "{\"type\":\"object\",\"properties\":{\"waitMs\":{\"type\":\"integer\",\"description\":\"非 0 时阻塞等待, 直到窗口出现/变化或超时(毫秒, 上限 60000)\"}},\"additionalProperties\":false}");

            Add("astral_events", "事件流(尾部)", true, false,
                "SDK 强类型事件的最后 N 条(JSON Lines): CardUsed/EffectCardUsed/QuickCardUsed/DiceResult/Move/BattleDice/RelicCandidates/RelicSelected/ShopCandidates/RewardCardSelected/HandChanged/RelicsSynced/BattleUpdate/SceneLoaded。",
                "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"description\":\"返回条数, 默认 40, 上限 500\"}},\"additionalProperties\":false}");

            Add("astral_actions", "原始动作流(尾部)", true, false,
                "服务器 1002 推送的原始动作最后 N 条(JSON Lines: Id=动作类型, Sn=序列号, PlayerId, Len, Decoded)。事件流没覆盖的信息(如 5249 买筹码 offer、未知动作)从这里看。",
                "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"description\":\"返回条数, 默认 40, 上限 500\"}},\"additionalProperties\":false}");

            Add("astral_throw_dice", "投骰子", false, true,
                "行动回合投骰。普通回合用 noOper=true 表示无牌可出直接过、moveNow=true 表示投完立刻移动; 战斗中(battle=true)是攻击判定投骰。",
                "{\"type\":\"object\",\"properties\":{\"battle\":{\"type\":\"boolean\",\"description\":\"true=战斗攻击骰(BattleThrowDice)\"},\"noOper\":{\"type\":\"boolean\",\"description\":\"无牌可出直接跳过\"},\"moveNow\":{\"type\":\"boolean\",\"description\":\"投完立即移动\"},\"sn\":{\"type\":\"integer\",\"description\":\"可选: 指定响应哪个 sn(默认用当前待响应 sn)\"}},\"additionalProperties\":false}");

            Add("astral_move", "移动到目标地块", false, true,
                "投骰后选择移动目标。landId 从 pending(kind=move)的候选里取 —— 候选是客户端按棋盘拓扑算出来的合法落点。",
                "{\"type\":\"object\",\"properties\":{\"landId\":{\"type\":\"integer\",\"description\":\"目标地块 id(取 pending 候选)\"},\"sn\":{\"type\":\"integer\"}},\"required\":[\"landId\"],\"additionalProperties\":false}");

            Add("astral_use_card", "战斗用牌", false, true,
                "战斗中使用一张手牌。cardId 用手牌 CardId(astral_state 的 Hand[].CardId); 也可以给 cardGuid 让桥接反查。",
                "{\"type\":\"object\",\"properties\":{\"cardId\":{\"type\":\"integer\"},\"cardGuid\":{\"type\":\"integer\",\"description\":\"手牌 Guid(会反查成 CardId)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_use_effect_card", "使用棋盘效果牌", false, true,
                "使用一张棋盘效果牌(可能带目标玩家/目标地块/效果项下标)。",
                "{\"type\":\"object\",\"properties\":{\"cardId\":{\"type\":\"integer\"},\"cardGuid\":{\"type\":\"integer\"},\"targetIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"目标玩家 id 列表\"},\"landIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"目标地块 id 列表\"},\"effectIndex\":{\"type\":\"integer\",\"description\":\"选择的效果项下标, 默认 0\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_use_quick_card", "跟牌/快速卡", false, true,
                "用一张快速卡跟别人的牌。targetId 是被跟的玩家 id。",
                "{\"type\":\"object\",\"properties\":{\"cardId\":{\"type\":\"integer\"},\"cardGuid\":{\"type\":\"integer\"},\"targetId\":{\"type\":\"integer\",\"description\":\"被跟的玩家 id\"},\"sn\":{\"type\":\"integer\"}},\"required\":[\"targetId\"],\"additionalProperties\":false}");

            Add("astral_abandon_card", "弃牌", false, true,
                "弃掉一张或多张手牌(按 CardId)。",
                "{\"type\":\"object\",\"properties\":{\"cardIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"cardId\":{\"type\":\"integer\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_relic", "选择筹码(三选一)", false, true,
                "筹码三选一窗口选一个。只给 index 即可(桥接会带上当前候选列表); 也可显式给 relicIds+index 或单个 relicId。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"description\":\"候选下标 0..N-1\"},\"relicIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"候选筹码 id 列表(通常不用给)\"},\"relicId\":{\"type\":\"integer\",\"description\":\"直接指定要选的筹码 id\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_reward_card", "选择奖励卡", false, true,
                "回合结束的奖励卡选择(cmd 5377)。只给 index 即可(桥接会带候选列表), 也可显式给 cardIds+index。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\"},\"cardIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_shop_buy", "卡牌商店购买/离店", false, true,
                "在商店里买卡或空手离店。indexes 是**槽位下标**(见 pending 的候选序号), 不是卡牌 id; 不传 indexes 就是离店。桥接会自动按 PVE(5215)/PVP(5029) 选对消息类。",
                "{\"type\":\"object\",\"properties\":{\"indexes\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"要买的槽位下标列表; 空/不传=离店\"},\"index\":{\"type\":\"integer\",\"description\":\"只买一格时的简写\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_atm_transfer", "ATM 给队友转星币", false, true,
                "PVE 商店里的 ATM: 转 5 星币给指定队友(AssistPlayer=目标玩家 id)。只能在 PVE 商店窗口内用。",
                "{\"type\":\"object\",\"properties\":{\"targetId\":{\"type\":\"integer\",\"description\":\"收款的队友 playerId\"},\"sn\":{\"type\":\"integer\"}},\"required\":[\"targetId\"],\"additionalProperties\":false}");

            Add("astral_buy_relic", "筹码地块购买", false, true,
                "筹码地块问你要不要花星币买下这个筹码(cmd 5249)。confirm=true 买(Select=2), false 不买离开(Select=0)。",
                "{\"type\":\"object\",\"properties\":{\"confirm\":{\"type\":\"boolean\",\"description\":\"true=买下, false=放弃离开(默认 true)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_speed", "设置游戏倍速", false, true,
                "通过加载器的变速通道调整游戏时间流速(加速等待动画/演出)。下限 1.0, 上限 100。别调太高(会影响网络超时与演出)。",
                "{\"type\":\"object\",\"properties\":{\"speed\":{\"type\":\"number\",\"description\":\"倍率, 1.0 - 100\"}},\"required\":[\"speed\"],\"additionalProperties\":false}");

            Add("astral_control", "设置开关(只读/演练/急停)", false, false,
                "改桥接开关: enableActions=false 变只读(任何动作都拒绝)、dryRun=true 只演练不发送、pause=true 急停。写入 agent\\control.json, 游戏侧 ~500ms 内生效。",
                "{\"type\":\"object\",\"properties\":{\"enableActions\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"},\"pause\":{\"type\":\"boolean\"},\"reason\":{\"type\":\"string\"}},\"additionalProperties\":false}");

            Add("astral_emergency_stop", "急停", false, false,
                "立刻停手: 写 PauseActions=true, 之后所有动作命令都会被拒绝(只读工具仍可用)。",
                "{\"type\":\"object\",\"properties\":{\"reason\":{\"type\":\"string\"}},\"additionalProperties\":false}");

            Add("astral_resume", "解除急停", false, false,
                "解除急停(PauseActions=false), 恢复执行动作命令。",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");
        }

        private void Add(string name, string title, bool readOnly, bool destructive, string description, string schemaJson)
        {
            _tools.Add(new McpTool
            {
                Name = name,
                Title = title,
                Description = description,
                InputSchema = (JsonObject)JsonNode.Parse(schemaJson),
                ReadOnly = readOnly,
                Destructive = destructive
            });
        }

        // ============================== 分发 ==============================

        public McpToolResult CallTool(string name, JsonElement args)
        {
            switch (name)
            {
                case "astral_status": return Status();
                case "astral_state": return State();
                case "astral_pending": return Pending(args);
                case "astral_events": return Tail(args, true);
                case "astral_actions": return Tail(args, false);

                case "astral_throw_dice": return ThrowDice(args);
                case "astral_move": return Move(args);
                case "astral_use_card": return UseCard(args);
                case "astral_use_effect_card": return UseEffectCard(args);
                case "astral_use_quick_card": return UseQuickCard(args);
                case "astral_abandon_card": return AbandonCard(args);
                case "astral_select_relic": return SelectRelic(args);
                case "astral_select_reward_card": return SelectRewardCard(args);
                case "astral_shop_buy": return ShopBuy(args);
                case "astral_atm_transfer": return AtmTransfer(args);
                case "astral_buy_relic": return BuyRelic(args);
                case "astral_speed": return Speed(args);

                case "astral_control": return Control(args);
                case "astral_emergency_stop": return EmergencyStop(args);
                case "astral_resume": return Resume();

                default:
                    return McpToolResult.Error("未知工具: " + name + "(用 tools/list 看可用工具)");
            }
        }

        // ============================== 只读工具 ==============================

        private McpToolResult Status()
        {
            var lv = _client.ReadLiveness();
            var sb = new StringBuilder();

            if (!lv.HeartbeatExists)
            {
                sb.AppendLine("❌ 桥接未连接: 找不到心跳文件 bridge.json");
                sb.AppendLine("桥接目录: " + _client.Root);
                sb.AppendLine("排查: 1) 游戏是否在运行 2) 加载器是否装了 version.dll 3) mods\\AstralParty.AgentMod\\AstralParty.AgentMod.dll 是否存在");
                return McpToolResult.Error(sb.ToString());
            }

            if (lv.Alive) sb.AppendLine("✅ 桥接活着 (心跳 " + lv.AgeMs + "ms 前, 进程 " + lv.ProcessId + ")");
            else
            {
                sb.AppendLine("❌ 桥接没有心跳 (最后心跳 " + FormatAge(lv.AgeMs) + " 前) —— 游戏可能已退出/卡死, 或 mod 被卸载");
                sb.AppendLine("桥接目录: " + _client.Root);
                return McpToolResult.Error(sb.ToString());
            }

            sb.AppendLine("mod: AstralParty.AgentMod " + lv.ModVersion + " (SDK " + lv.SdkVersion + ")");
            sb.AppendLine("场景: " + (lv.Scene ?? "?") + "   房间: " + Yes(lv.InRoom) + "   战斗: " + Yes(lv.InBattle));
            sb.AppendLine("状态版本: " + lv.StateSeq + "   命令: 成功 " + lv.CommandsExecuted + " / 拒绝 " + lv.CommandsRejected);

            using (var ctrl = _client.ReadControl())
            {
                if (ctrl == null) sb.AppendLine("开关: 无 control.json(按 mod 的 config.json 默认值运行)");
                else
                {
                    var r = ctrl.RootElement;
                    sb.AppendLine("开关: 发送操作=" + (AgentBridgeClient.GetBool(r, "EnableActions") ? "允许" : "只读") +
                                  "  急停=" + (AgentBridgeClient.GetBool(r, "PauseActions") ? "开" : "关") +
                                  "  演练=" + (AgentBridgeClient.GetBool(r, "DryRun") ? "开" : "关") +
                                  "  (由 " + (AgentBridgeClient.GetString(r, "UpdatedBy") ?? "?") + " 写入)");
                }
            }

            var pendingText = DescribePending();
            if (pendingText != null) sb.AppendLine(pendingText);

            return McpToolResult.Ok(sb.ToString().TrimEnd());
        }

        private McpToolResult State()
        {
            string text = _client.ReadStateText();
            if (string.IsNullOrEmpty(text))
                return McpToolResult.Error("读不到 state.json(桥接可能还没产出状态)。路径: " + AgentBridgeLayout.StatePath(_client.Root));
            return McpToolResult.Ok(text);
        }

        private McpToolResult Pending(JsonElement args)
        {
            int waitMs = Int(args, "waitMs", 0);
            if (waitMs > 0)
            {
                if (waitMs > 60000) waitMs = 60000;
                var before = ReadPendingSignature();
                long deadline = AgentBridgeLayout.NowMs() + waitMs;
                while (AgentBridgeLayout.NowMs() < deadline)
                {
                    Thread.Sleep(120);
                    var now = ReadPendingSignature();
                    if (now != null && now != before && !IsNoPending(now.Kind)) break;
                }
            }
            return PendingResult();
        }

        private McpToolResult PendingResult()
        {
            using (var doc = _client.ReadState())
            {
                if (doc == null)
                {
                    var lv = _client.ReadLiveness();
                    return McpToolResult.Error("读不到状态: " + (lv.HeartbeatExists
                        ? "桥接有心跳但 state.json 缺失(等 1 秒再试)"
                        : "桥接未连接 —— 先 astral_status"));
                }

                if (!AgentBridgeClient.TryGetObject(doc.RootElement, "Pending", out var pending))
                    return McpToolResult.Error("状态里没有 Pending 字段(状态结构不兼容?)");

                string kind = AgentBridgeClient.GetString(pending, "Kind");
                long sn = AgentBridgeClient.GetLong(pending, "Sn");
                long since = AgentBridgeClient.GetLong(pending, "SinceMs");
                long remaining = AgentBridgeClient.GetLong(pending, "RemainingMs");
                bool actionable = AgentBridgeClient.GetBool(pending, "Actionable");
                string source = AgentBridgeClient.GetString(pending, "Source");

                var sb = new StringBuilder();
                if (IsNoPending(kind))
                {
                    sb.AppendLine("当前没有需要你响应的窗口(可能在等别的玩家, 或不在对局里)。");
                    string notes = JoinStringArray(pending, "Notes");
                    if (!string.IsNullOrEmpty(notes)) sb.AppendLine("备注: " + notes);
                    sb.AppendLine("建议: astral_pending {\"waitMs\":15000} 等窗口出现, 或 astral_status 看是否在对局。");
                    return McpToolResult.Ok(sb.ToString().TrimEnd());
                }

                sb.AppendLine("待响应: " + kind + "   (sn=" + sn + ", 来源 " + (source ?? "?") + ", 已等待 " + FormatAge(since > 0 ? AgentBridgeLayout.NowMs() - since : -1) + ")");
                if (remaining >= 0) sb.AppendLine("剩余时间: " + remaining + "ms");
                sb.AppendLine("是否可直接应答: " + (actionable ? "是" : "否"));

                string candidates = DescribeCandidates(pending);
                if (candidates != null) sb.Append(candidates);

                string options = JoinStringArray(pending, "Options");
                if (!string.IsNullOrEmpty(options)) sb.AppendLine("可用操作:\n" + options);

                string pendingNotes = JoinStringArray(pending, "Notes");
                if (!string.IsNullOrEmpty(pendingNotes)) sb.AppendLine("备注:\n" + pendingNotes);

                return McpToolResult.Ok(sb.ToString().TrimEnd());
            }
        }

        private McpToolResult Tail(JsonElement args, bool events)
        {
            int limit = Int(args, "limit", 40);
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            var list = events ? _client.ReadEvents(limit) : _client.ReadActions(limit);
            if (list.Count == 0)
                return McpToolResult.Ok((events ? "events.jsonl" : "actions.jsonl") + " 还没有内容(对局还没开始, 或该流被配置关掉了)。");

            var sb = new StringBuilder();
            sb.AppendLine((events ? "事件" : "原始动作") + " 最后 " + list.Count + " 条:");
            foreach (var el in list) sb.AppendLine(el.GetRawText());
            return McpToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ============================== 动作工具 ==============================

        private McpToolResult ThrowDice(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "battle")) dict["battle"] = Bool(args, "battle", false);
            if (Has(args, "noOper")) dict["noOper"] = Bool(args, "noOper", false);
            if (Has(args, "moveNow")) dict["moveNow"] = Bool(args, "moveNow", false);
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.ThrowDice, dict);
        }

        private McpToolResult Move(JsonElement args)
        {
            if (!Has(args, "landId")) return McpToolResult.Error("缺少 landId(目标地块 id)");
            var dict = new Dictionary<string, object> { { "landId", Int(args, "landId", 0) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.Move, dict);
        }

        private McpToolResult UseCard(JsonElement args)
        {
            var dict = CardArgs(args);
            if (dict == null) return McpToolResult.Error("需要 cardId 或 cardGuid");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.UseCard, dict);
        }

        private McpToolResult UseEffectCard(JsonElement args)
        {
            var dict = CardArgs(args);
            if (dict == null) return McpToolResult.Error("需要 cardId 或 cardGuid");
            if (Has(args, "targetIds")) dict["targetIds"] = IntList(args, "targetIds");
            if (Has(args, "landIds")) dict["landIds"] = IntList(args, "landIds");
            if (Has(args, "effectIndex")) dict["effectIndex"] = Int(args, "effectIndex", 0);
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.UseEffectCard, dict);
        }

        private McpToolResult UseQuickCard(JsonElement args)
        {
            var dict = CardArgs(args);
            if (dict == null) return McpToolResult.Error("需要 cardId 或 cardGuid");
            if (!Has(args, "targetId")) return McpToolResult.Error("缺少 targetId(被跟的玩家 id)");
            dict["targetId"] = Long(args, "targetId", 0);
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.UseQuickCard, dict);
        }

        private McpToolResult AbandonCard(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "cardIds")) dict["cardIds"] = IntList(args, "cardIds");
            else if (Has(args, "cardId")) dict["cardId"] = Int(args, "cardId", 0);
            else return McpToolResult.Error("需要 cardIds 或 cardId");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.AbandonCard, dict);
        }

        private McpToolResult SelectRelic(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "index")) dict["index"] = Int(args, "index", 0);
            if (Has(args, "relicIds")) dict["relicIds"] = IntList(args, "relicIds");
            if (Has(args, "relicId")) dict["relicId"] = Int(args, "relicId", 0);
            if (dict.Count == 0) dict["index"] = 0; // 默认选第 0 个(桥接会带候选)
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectRelic, dict);
        }

        private McpToolResult SelectRewardCard(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "index")) dict["index"] = Int(args, "index", 0);
            if (Has(args, "cardIds")) dict["cardIds"] = IntList(args, "cardIds");
            if (dict.Count == 0) dict["index"] = 0;
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectRewardCard, dict);
        }

        private McpToolResult ShopBuy(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "indexes")) dict["indexes"] = IntList(args, "indexes");
            else if (Has(args, "index")) dict["index"] = Int(args, "index", 0);
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.ShopBuy, dict);
        }

        private McpToolResult AtmTransfer(JsonElement args)
        {
            if (!Has(args, "targetId")) return McpToolResult.Error("缺少 targetId(收款的队友 playerId)");
            var dict = new Dictionary<string, object> { { "targetId", Long(args, "targetId", 0) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.AtmTransfer, dict);
        }

        private McpToolResult BuyRelic(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "confirm", Bool(args, "confirm", true) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.BuyRelic, dict);
        }

        private McpToolResult Speed(JsonElement args)        {
            if (!Has(args, "speed")) return McpToolResult.Error("缺少 speed(倍率 1.0-100)");
            var dict = new Dictionary<string, object> { { "speed", Double(args, "speed", 1.0) } };
            return Send(AgentBridgeLayout.Tool.Speed, dict, 8000);
        }

        // ============================== 开关工具 ==============================

        private McpToolResult Control(JsonElement args)
        {
            bool? enable = Has(args, "enableActions") ? Bool(args, "enableActions", true) : (bool?)null;
            bool? dry = Has(args, "dryRun") ? Bool(args, "dryRun", false) : (bool?)null;
            bool? pause = Has(args, "pause") ? Bool(args, "pause", false) : (bool?)null;
            string reason = String(args, "reason") ?? "agent 请求";

            string json = _client.WriteControl(enable, pause, dry, reason, "mcp");
            var sb = new StringBuilder();
            sb.AppendLine("已写入 control.json(游戏侧 ~500ms 内生效):");
            sb.AppendLine(json);
            if (pause == true) sb.AppendLine("⚠ 急停已开: 所有动作命令都会被拒绝, 只读工具仍可用(用 astral_resume 恢复)。");
            if (enable == false) sb.AppendLine("⚠ 只读模式: 桥接不会再发送任何 C2S。");
            return McpToolResult.Ok(sb.ToString().TrimEnd());
        }

        private McpToolResult EmergencyStop(JsonElement args)
        {
            string reason = String(args, "reason") ?? "agent 主动急停";
            _client.WriteControl(null, true, null, reason, "mcp");
            return McpToolResult.Ok("✅ 已急停(PauseActions=true, 原因: " + reason + ")。所有动作命令立刻停止执行; 用 astral_resume 恢复。");
        }

        private McpToolResult Resume()
        {
            _client.WriteControl(null, false, null, "agent 解除急停", "mcp");
            return McpToolResult.Ok("✅ 已解除急停(PauseActions=false), 动作命令恢复执行。");
        }

        // ============================== 公共辅助 ==============================

        private McpToolResult Send(string tool, Dictionary<string, object> args, int timeoutMs = 0)
        {
            var lv = _client.ReadLiveness();
            if (!lv.Alive)
            {
                return McpToolResult.Error("桥接没有心跳(" + (lv.HeartbeatExists ? FormatAge(lv.AgeMs) + " 未更新" : "没有 bridge.json") +
                                           "), 命令没有下发。先 astral_status 排查。");
            }

            var res = _client.SendCommand(tool, args, timeoutMs > 0 ? timeoutMs : _defaultTimeoutMs);
            if (res.Ok) return McpToolResult.Ok("✅ " + (res.Detail ?? "已发送") + "\n(往返 " + res.LatencyMs + "ms)");
            return McpToolResult.Error("❌ " + (res.Code ?? "failed") + ": " + (res.Error ?? "未知错误") +
                                       (res.TimedOut ? "\n提示: 游戏没开 / 桥接 mod 未加载 / 命令被跳过。" : ""));
        }

        private static Dictionary<string, object> CardArgs(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "cardId")) dict["cardId"] = Int(args, "cardId", 0);
            else if (Has(args, "cardGuid")) dict["cardGuid"] = Int(args, "cardGuid", 0);
            else if (Has(args, "cardIds"))
            {
                var list = IntList(args, "cardIds");
                if (list.Count == 0) return null;
                dict["cardId"] = list[0];
            }
            else return null;
            return dict;
        }

        private static void PutSn(JsonElement args, Dictionary<string, object> dict)
        {
            if (Has(args, "sn"))
            {
                long sn = Long(args, "sn", 0);
                if (sn > 0) dict["sn"] = sn;
            }
        }

        private string DescribePending()
        {
            var sig = ReadPendingSignature();
            if (sig == null || IsNoPending(sig.Kind)) return "待响应窗口: 无";
            return "待响应窗口: " + sig.Kind + " (sn=" + sig.Sn + (sig.Actionable ? ", 可直接应答" : ", 暂不可应答") + ")";
        }

        /// <summary>
        /// <c>Pending.Kind</c> 是否为"没有窗口"。
        ///
        /// 真机契约(2026-10-01 从真实 state.json 抄下来): 桥接状态模型里**没有枚举**,
        /// <c>Kind</c> 是 <c>AgentPendingKind</c> 里的小写 camelCase 字符串常量
        /// (<c>none</c>/<c>move</c>/<c>selectRelic</c>…), 游戏侧 <c>CesiumJson</c> 原样写出。
        ///
        /// 这里仍按 <c>OrdinalIgnoreCase</c> 比较, 是**防御**: 早先按 Ordinal 比 <c>"none"</c>,
        /// 只要哪一侧把大小写改了(或夹具照着别处的 camelCase/PascalCase 写), "当前没有窗口"就会被
        /// 当成一个名叫 <c>None</c> 的真窗口报给 agent(sn=0、没有任何候选), agent 会白白空转——
        /// 这类错法不抛异常, 只是行为诡异, 所以固定成忽略大小写。
        /// </summary>
        private static bool IsNoPending(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return true;
            return string.Equals(kind.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class PendingSignature
        {
            public string Kind;
            public long Sn;
            public bool Actionable;
            public long StateSeq;
            public override string ToString() { return Kind + "#" + Sn + "#" + StateSeq; }
        }

        private PendingSignature ReadPendingSignature()
        {
            using (var doc = _client.ReadState())
            {
                if (doc == null) return null;
                if (!AgentBridgeClient.TryGetObject(doc.RootElement, "Pending", out var p)) return null;
                return new PendingSignature
                {
                    Kind = AgentBridgeClient.GetString(p, "Kind"),
                    Sn = AgentBridgeClient.GetLong(p, "Sn"),
                    Actionable = AgentBridgeClient.GetBool(p, "Actionable"),
                    StateSeq = AgentBridgeClient.GetLong(doc.RootElement, "StateSeq")
                };
            }
        }

        private static string DescribeCandidates(JsonElement pending)
        {
            JsonElement arr;
            if (!TryGetArray(pending, "Candidates", out arr)) return null;
            var sb = new StringBuilder();
            sb.AppendLine("候选:");
            int i = 0;
            foreach (var c in arr.EnumerateArray())
            {
                string name = AgentBridgeClient.GetString(c, "Name");
                var line = new StringBuilder();
                line.Append("  [").Append(i).Append("] ").Append(AgentBridgeClient.GetLong(c, "Id"));
                if (!string.IsNullOrEmpty(name)) line.Append("  ").Append(name);

                long price = AgentBridgeClient.GetLong(c, "Price");
                if (price > 0) line.Append("  价格 ").Append(price);
                if (AgentBridgeClient.GetBool(c, "Free")) line.Append("  (免费)");
                if (AgentBridgeClient.GetBool(c, "SoldOut")) line.Append("  (已售罄)");

                sb.AppendLine(line.ToString());
                i++;
            }
            return i == 0 ? null : sb.ToString();
        }

        private static string JoinStringArray(JsonElement obj, string name)
        {
            JsonElement arr;
            if (!TryGetArray(obj, name, out arr)) return null;
            var sb = new StringBuilder();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("  - ").Append(el.GetString());
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static bool TryGetArray(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in obj.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.Array) return false;
                value = p.Value;
                return true;
            }
            return false;
        }

        private static bool Has(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in args.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind != JsonValueKind.Null) return true;
            }
            return false;
        }

        private static int Int(JsonElement args, string name, int fallback)
        {
            if (args.ValueKind != JsonValueKind.Object) return fallback;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v)) return v;
                if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out int s)) return s;
                return fallback;
            }
            return fallback;
        }

        private static long Long(JsonElement args, string name, long fallback)
        {
            if (args.ValueKind != JsonValueKind.Object) return fallback;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out long v)) return v;
                if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out long s)) return s;
                return fallback;
            }
            return fallback;
        }

        private static double Double(JsonElement args, string name, double fallback)
        {
            if (args.ValueKind != JsonValueKind.Object) return fallback;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double v)) return v;
                return fallback;
            }
            return fallback;
        }

        private static bool Bool(JsonElement args, string name, bool fallback)
        {
            if (args.ValueKind != JsonValueKind.Object) return fallback;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.True) return true;
                if (p.Value.ValueKind == JsonValueKind.False) return false;
                return fallback;
            }
            return fallback;
        }

        private static string String(JsonElement args, string name)
        {
            return AgentBridgeClient.GetString(args, name);
        }

        private static List<int> IntList(JsonElement args, string name)
        {
            var list = new List<int>();
            if (args.ValueKind != JsonValueKind.Object) return list;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in p.Value.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) list.Add(v);
                }
                return list;
            }
            return list;
        }

        private static string Yes(bool b) { return b ? "是" : "否"; }

        private static string FormatAge(long ageMs)
        {
            if (ageMs < 0) return "未知";
            if (ageMs < 1000) return ageMs + "ms";
            if (ageMs < 60000) return (ageMs / 1000.0).ToString("0.0") + "s";
            return (ageMs / 60000.0).ToString("0.0") + "min";
        }
    }
}
