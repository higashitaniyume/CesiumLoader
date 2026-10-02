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
                    "     astral_ask_battle / astral_battle_choice / astral_select_relic / astral_select_reward_card /\n" +
                    "     astral_select_event / astral_use_quick_card / astral_stop_or_continue / astral_pursue_monster /\n" +
                    "     astral_vendor_buy_card / astral_select_point / astral_revive_teammate /\n" +
                    "     astral_select_mechanism / astral_hospital_check / astral_battery_pick /\n" +
                    "     astral_divination_pick / astral_gamble_guess / astral_gamble_dice / astral_lottery_pick /\n" +
                    "     astral_shop_buy / astral_buy_relic / astral_atm_transfer …(pending.Options 里会列出本窗口可用的操作);\n" +
                    "  4) 重复 2-3。需要手牌/场上数值时用 astral_state; 想复盘刚发生了什么用 astral_events / astral_actions。\n" +
                    "注意:\n" +
                    "  - 桥接只发\"和玩家手动点 UI 完全相同\"的 C2S 请求, 不修改内存、不伪造结果, 服务器照常校验;\n" +
                    "  - 战斗类窗口都有倒计时(10/20/40 秒), **超时会被服务器按默认值代答**: 战斗询问=不打、\n" +
                    "    出牌=不出牌、闪避=不闪避、加油站/出生点=继续走、怪物追击=不追、商人买卡=不买、控移选点=1 点\n" +
                    "    —— 别拖到超时, 而且反复超时会被判挂机(AFK)惩罚;\n" +
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

            Add("astral_use_card", "战斗出牌", false, true,
                "战斗出牌窗口(5035)里出一张手牌, 或不出牌。cardId 取 pending(kind=fightCard)候选里的 id —— " +
                "**那个 id 就是手牌 Guid**(服务器这条链路上用的就是 Guid, 不是卡牌配置 CardId, 别拿 astral_state 的 Hand[].CardId 来填); " +
                "pass=true 表示这一轮不出牌(与客户端点\"结束出牌\"/超时同一条路径)。候选已按客户端口径过滤: 我是攻方只能出攻击牌、守方只能出防御牌, Cost 是这张牌的战斗消耗。",
                "{\"type\":\"object\",\"properties\":{\"cardId\":{\"type\":\"integer\",\"description\":\"要出的手牌 id(取 pending 候选, 就是手牌 Guid)\"},\"cardUid\":{\"type\":\"integer\",\"description\":\"同上, 显式给手牌 Guid\"},\"pass\":{\"type\":\"boolean\",\"description\":\"true=这一轮不出牌(CardUid=0)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_ask_battle", "战斗询问: 打不打", false, true,
                "别人对你发起战斗时的询问(5047)。accept=true 接受这场战斗, false 拒绝。注意**服务器超时代答 = 拒绝(不打)**, " +
                "而且反击(服务器下发 FightBack)那一次是客户端自动接受、不会开窗口, 桥接也不会给你这个窗口。",
                "{\"type\":\"object\",\"properties\":{\"accept\":{\"type\":\"boolean\",\"description\":\"true=接受战斗, false=不打\"},\"sn\":{\"type\":\"integer\"}},\"required\":[\"accept\"],\"additionalProperties\":false}");

            Add("astral_battle_choice", "战斗闪避选择", false, true,
                "战斗结算前问你要不要闪避(5039)。dodge=true 闪避(消耗防守点), false 硬吃。**超时代答 = 不闪避**; " +
                "若 pending 里 noDodge=true 则这一击不能闪避, 只能 dodge=false。",
                "{\"type\":\"object\",\"properties\":{\"dodge\":{\"type\":\"boolean\",\"description\":\"true=闪避, false=不闪避\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

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
                "筹码三选一窗口选一个。只给 index 即可(桥接会带上当前候选列表); 也可显式给 relicIds+index 或单个 relicId。reroll=true 是重摇这组候选(不结束窗口, 服务器会推新的一组)。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"description\":\"候选下标 0..N-1\"},\"relicIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"候选筹码 id 列表(通常不用给)\"},\"relicId\":{\"type\":\"integer\",\"description\":\"直接指定要选的筹码 id\"},\"reroll\":{\"type\":\"boolean\",\"description\":\"true=重摇候选(SelectRelicC2S.IsReroll=true), 与 index 互斥\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_reward_card", "选择奖励卡", false, true,
                "回合结束的奖励卡选择(cmd 5377)。只给 index 即可(桥接会带候选列表), 也可显式给 cardIds+index。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\"},\"cardIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_event", "选择棋盘事件", false, true,
                "棋盘事件弹窗(cmd 5317)从若干事件里选一个。候选见 pending(kind=selectEvent)的 candidates。不选的话服务器超时会代选第 0 项。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"description\":\"候选下标 0..N-1(默认 0)\"},\"eventId\":{\"type\":\"integer\",\"description\":\"直接按事件 id 选(桥接会在候选里查下标)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_shop_buy", "卡牌商店购买/离店", false, true,
                "在商店里买卡或空手离店。indexes 是**槽位下标**(见 pending 的候选序号), 不是卡牌 id; 不传 indexes 就是离店。桥接会自动按 PVE(5215)/PVP(5029) 选对消息类。",
                "{\"type\":\"object\",\"properties\":{\"indexes\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"要买的槽位下标列表; 空/不传=离店\"},\"index\":{\"type\":\"integer\",\"description\":\"只买一格时的简写\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_atm_transfer", "ATM 给队友转星币", false, true,
                "PVE 商店里的 ATM: 转 5 星币给指定队友(AssistPlayer=目标玩家 id)。只能在 PVE 商店窗口内用。",
                "{\"type\":\"object\",\"properties\":{\"targetId\":{\"type\":\"integer\",\"description\":\"收款的队友 playerId\"},\"sn\":{\"type\":\"integer\"}},\"required\":[\"targetId\"],\"additionalProperties\":false}");

            Add("astral_buy_relic", "筹码地块购买", false, true,
                "筹码地块问你要不要花星币买下这个筹码(cmd 5249)。confirm=true 买(Select=2), false 不买离开(Select=0)。",
                "{\"type\":\"object\",\"properties\":{\"confirm\":{\"type\":\"boolean\",\"description\":\"true=买下, false=放弃离开(默认 true)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_stop_or_continue", "加油站/出生点: 停留还是继续走", false, true,
                "走到加油站/出生点时服务器问\"停下还是继续走\"(5077)。stop=true 就地停留(拿地块收益/买东西), false 继续走。 " +
                "**超时代答 = 继续走**; 窗口的信息(站在哪种地块、星币、等级)全在本地, 见 pending 的 land 与 astral_state。",
                "{\"type\":\"object\",\"properties\":{\"stop\":{\"type\":\"boolean\",\"description\":\"true=停留, false=继续走(默认)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_pursue_monster", "怪物追击: 追哪只怪", false, true,
                "服务器问要不要追击怪物(5213)。monsterId 取 pending(kind=pursueMonster)候选里的 LongId(怪物也是玩家, 用它的 playerId); " +
                "pass=true 表示不追击(SelectId=0)。候选**不在协议里**, 是本地按客户端同口径过滤出来的(血量>0、非医院地块、不同队伍), " +
                "所以桥接只接受候选里的 id。**超时代答 = 不追**。",
                "{\"type\":\"object\",\"properties\":{\"monsterId\":{\"type\":\"integer\",\"description\":\"要追的怪物 playerId(取 pending 候选的 LongId)\"},\"pass\":{\"type\":\"boolean\",\"description\":\"true=不追击\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_vendor_buy_card", "商人买卡", false, true,
                "商人问要不要花 N 星币买下这张卡(5323)。buy=true 买下, false 不买。价格见 pending 的 vendorPrice。 " +
                "**超时代答 = 不买**; 星币不足时客户端会拒绝购买请求, 桥接同样不发。",
                "{\"type\":\"object\",\"properties\":{\"buy\":{\"type\":\"boolean\",\"description\":\"true=买下, false=不买(默认)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_point", "控移卡: 选移动点数", false, true,
                "控制移动卡要你选这次用几点移动力(5067)。point 必须落在 1..pending.maxPoint。 " +
                "**超时代答 = 1 点**。",
                "{\"type\":\"object\",\"properties\":{\"point\":{\"type\":\"integer\",\"description\":\"用几点移动力(1..pending.MaxPoint), 默认 1\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_revive_teammate", "复活队友: 救不救", false, true,
                "队友倒下后服务器问要不要花星币复活(5233)。revive=true 复活, false 不复活。 " +
                "**超时代答 = 不复活**; 救谁、花多少星币由服务器决定(这条上行只有 Info 与 IsRevive 两个字段), " +
                "筹码/星币现状看 astral_state。",
                "{\"type\":\"object\",\"properties\":{\"revive\":{\"type\":\"boolean\",\"description\":\"true=复活(花星币), false=不复活(默认)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_select_mechanism", "机制选择: 启不启动", false, true,
                "地块机制问要不要启动(5259)。select=true 启动, false 不启动。 **超时代答 = 不启动**。",
                "{\"type\":\"object\",\"properties\":{\"select\":{\"type\":\"boolean\",\"description\":\"true=启动, false=不启动(默认)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_hospital_check", "医院: 接受检查", false, true,
                "走到医院时服务器问要不要接受检查(5093)。这条窗口**只有一个合法上行**" +
                "(TriggerHospitalC2S 里没有\"拒绝\"字段), 所以工具不带选项; 不答的话客户端倒计时结束也会自己发这条。 " +
                "是否住院由服务器在回执里告知。",
                "{\"type\":\"object\",\"properties\":{\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_battery_pick", "炮台: 选目标英雄", false, true,
                "炮台地块问要打哪几个英雄(5063)。targetIds 取 pending(kind=batteryTarget)候选里的 LongId, 共 1..TargetNum 个; " +
                "也可以 leave=true 不选目标直接离开。**超时代答 = 离开**。候选是协议给的 CanTargetIds 再按客户端同口径" +
                "(只收英雄、且要在本地战斗数据里)过滤出来的, 所以桥接只接受候选里的 playerId。",
                "{\"type\":\"object\",\"properties\":{\"targetIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"1..TargetNum 个英雄 playerId(取 pending 候选的 LongId)\"},\"leave\":{\"type\":\"boolean\",\"description\":\"true=不选目标直接离开\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_divination_pick", "占卜: 两张牌选一张", false, true,
                "走到占卜地块要翻一张牌(5069)。两张候选牌就在 pending(kind=divination)里, 用 index(0/1)或 divinationId 选一张。 " +
                "不传参数按 index=0 处理。**超时代答 = 第 1 张牌**。",
                "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"description\":\"候选下标(0 或 1, 默认 0)\"},\"divinationId\":{\"type\":\"integer\",\"description\":\"占卜卡 id(取 pending 候选)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_gamble_guess", "赌场: 押奇数/偶数", false, true,
                "赌场开庄后问押奇数还是偶数(5081)。guessCode 1=奇数 2=偶数(也可写 guess=\"odd\"/\"even\")。 " +
                "**必须明确选** —— 这一注要花星币, 不像选点那样给默认值; **超时代答 = 押奇数**。 " +
                "已死或星币不足时客户端把按钮置灰, 桥接会拒答(客户端自己的超时仍会押奇数)。",
                "{\"type\":\"object\",\"properties\":{\"guessCode\":{\"type\":\"integer\",\"description\":\"1=奇数, 2=偶数\"},\"guess\":{\"type\":\"string\",\"description\":\"odd / even(与 guessCode 等价)\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_gamble_dice", "赌场: 掷骰", false, true,
                "轮到你掷骰时(5083)。这条**没有可选参数** —— 唯一合法上行就是掷骰, 客户端超时发的也是它。 " +
                "已死或星币不足时客户端把按钮置灰, 桥接会拒答。",
                "{\"type\":\"object\",\"properties\":{\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            Add("astral_lottery_pick", "抽奖: 选号码", false, true,
                "抽奖地块选号码(5041)。要从 pending(kind=lotteryPick)候选里选 **正好 Num 个**还没被你占的号码(候选就是 1..上限里剩下的)。 " +
                "不传 numbers 就按客户端超时的口径: 从最小的可用号码开始补满。**超时代答 = 最小的那几个**。",
                "{\"type\":\"object\",\"properties\":{\"numbers\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"选中的号码(取 pending 候选的数字), 个数必须等于 Num\"},\"sn\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

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
                case "astral_ask_battle": return AskBattle(args);
                case "astral_battle_choice": return BattleChoice(args);
                case "astral_use_effect_card": return UseEffectCard(args);
                case "astral_use_quick_card": return UseQuickCard(args);
                case "astral_abandon_card": return AbandonCard(args);
                case "astral_select_relic": return SelectRelic(args);
                case "astral_select_reward_card": return SelectRewardCard(args);
                case "astral_select_event": return SelectEvent(args);
                case "astral_shop_buy": return ShopBuy(args);
                case "astral_atm_transfer": return AtmTransfer(args);
                case "astral_buy_relic": return BuyRelic(args);
                case "astral_stop_or_continue": return StopOrContinue(args);
                case "astral_pursue_monster": return PursueMonster(args);
                case "astral_vendor_buy_card": return VendorBuyCard(args);
                case "astral_select_point": return SelectPoint(args);
                case "astral_revive_teammate": return ReviveTeammate(args);
                case "astral_select_mechanism": return SelectMechanism(args);
                case "astral_hospital_check": return HospitalCheck(args);
                case "astral_battery_pick": return BatteryPick(args);
                case "astral_divination_pick": return DivinationPick(args);
                case "astral_gamble_guess": return GambleGuess(args);
                case "astral_gamble_dice": return GambleDice(args);
                case "astral_lottery_pick": return LotteryPick(args);
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

                // 窗口自带的标量参数(商人价格/选点上限/加油站地块)单独列一行, 免得 agent 去翻 astral_state
                long vendorPrice = AgentBridgeClient.GetLong(pending, "VendorPrice");
                if (vendorPrice > 0) sb.AppendLine("价格: " + vendorPrice + " 星币");
                long maxPoint = AgentBridgeClient.GetLong(pending, "MaxPoint");
                if (maxPoint > 0) sb.AppendLine("可选点数: 1.." + maxPoint);
                string land = AgentBridgeClient.GetString(pending, "Land");
                if (!string.IsNullOrEmpty(land)) sb.AppendLine("当前地块: " + land);

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
            var dict = new Dictionary<string, object>();
            if (Bool(args, "pass", false)) dict["pass"] = true;
            else if (Has(args, "cardUid")) dict["cardUid"] = Int(args, "cardUid", 0);
            else if (Has(args, "cardId")) dict["cardId"] = Int(args, "cardId", 0);
            else if (Has(args, "cardGuid")) dict["cardGuid"] = Int(args, "cardGuid", 0);
            else return McpToolResult.Error("需要 cardId(取 pending 候选里那个手牌 id)或 pass=true(不出牌)");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.UseCard, dict);
        }

        private McpToolResult AskBattle(JsonElement args)
        {
            if (!Has(args, "accept")) return McpToolResult.Error("缺少 accept(true=接受战斗 / false=不打)");
            var dict = new Dictionary<string, object> { { "accept", Bool(args, "accept", true) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.AskBattle, dict);
        }

        private McpToolResult BattleChoice(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "dodge", Bool(args, "dodge", false) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.BattleChoice, dict);
        }

        private McpToolResult StopOrContinue(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "stop", Bool(args, "stop", false) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.StopOrContinue, dict);
        }

        private McpToolResult PursueMonster(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Bool(args, "pass", false)) dict["pass"] = true;
            else if (Has(args, "monsterId")) dict["monsterId"] = Long(args, "monsterId", 0);
            else return McpToolResult.Error("需要 monsterId(取 pending 候选的 LongId)或 pass=true(不追)");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.PursueMonster, dict);
        }

        private McpToolResult VendorBuyCard(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "buy", Bool(args, "buy", false) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.VendorBuyCard, dict);
        }

        private McpToolResult SelectPoint(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "point", Int(args, "point", 1) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectPoint, dict);
        }

        private McpToolResult ReviveTeammate(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "revive", Bool(args, "revive", false) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.ReviveTeammate, dict);
        }

        private McpToolResult SelectMechanism(JsonElement args)
        {
            var dict = new Dictionary<string, object> { { "select", Bool(args, "select", false) } };
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectMechanism, dict);
        }

        /// <summary>医院(5093): 唯一合法上行, 没有可选参数(所以不传布尔值, 免得给 agent 错觉)。</summary>
        private McpToolResult HospitalCheck(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.HospitalCheck, dict);
        }

        private McpToolResult BatteryPick(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Bool(args, "leave", false) || Bool(args, "exit", false)) dict["leave"] = true;
            else if (Has(args, "targetIds")) dict["targetIds"] = LongList(args, "targetIds").ToArray();
            else if (Has(args, "targetId")) dict["targetIds"] = new long[] { Long(args, "targetId", 0) };
            else return McpToolResult.Error("需要 targetIds(1..TargetNum 个英雄 id, 取 pending 候选的 LongId)或 leave=true(离开)");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.BatteryPick, dict);
        }

        /// <summary>占卜(5069): 不传参数 = index 0(与客户端超时的选择一致)。</summary>
        private McpToolResult DivinationPick(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "divinationId")) dict["divinationId"] = Int(args, "divinationId", 0);
            else if (Has(args, "index")) dict["index"] = Int(args, "index", 0);
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.DivinationPick, dict);
        }

        /// <summary>赌场押注(5081): 必须明确选边(要花星币), 不给默认值。</summary>
        private McpToolResult GambleGuess(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "guessCode")) dict["guessCode"] = Int(args, "guessCode", 0);
            else if (Has(args, "guess")) dict["guess"] = String(args, "guess");
            else return McpToolResult.Error("需要 guessCode(1=奇数, 2=偶数)或 guess(\"odd\"/\"even\") —— 要花星币, 不替你默认");
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.GambleGuess, dict);
        }

        /// <summary>赌场掷骰(5083): 唯一合法上行, 没有可选参数。</summary>
        private McpToolResult GambleDice(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.GambleDice, dict);
        }

        /// <summary>抽奖选号(5041): 不传 numbers = 让桥接按客户端超时的口径补满。</summary>
        private McpToolResult LotteryPick(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "numbers")) dict["numbers"] = IntList(args, "numbers").ToArray();
            else if (Has(args, "vals")) dict["numbers"] = IntList(args, "vals").ToArray();
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.LotteryPick, dict);
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
            bool reroll = Bool(args, "reroll", false);
            if (reroll) dict["reroll"] = true;
            // 重摇不带 index(客户端也只填 Info+IsReroll); 其余情况默认选第 0 个
            if (dict.Count == 0) dict["index"] = 0;
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectRelic, dict);
        }

        private McpToolResult SelectEvent(JsonElement args)
        {
            var dict = new Dictionary<string, object>();
            if (Has(args, "index")) dict["index"] = Int(args, "index", 0);
            if (Has(args, "eventId")) dict["eventId"] = Int(args, "eventId", 0);
            if (dict.Count == 0) dict["index"] = 0; // 与游戏一致: 不选=第 0 项
            PutSn(args, dict);
            return Send(AgentBridgeLayout.Tool.SelectEvent, dict);
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
                // 怪物候选的 id 是 playerId(64 位, 放在 LongId 里); 其余候选只有 Id
                long cid = AgentBridgeClient.GetLong(c, "LongId");
                if (cid == 0) cid = AgentBridgeClient.GetLong(c, "Id");
                line.Append("  [").Append(i).Append("] ").Append(cid);
                if (!string.IsNullOrEmpty(name)) line.Append("  ").Append(name);

                long price = AgentBridgeClient.GetLong(c, "Price");
                if (price > 0) line.Append("  价格 ").Append(price);
                long cost = AgentBridgeClient.GetLong(c, "Cost");
                if (cost > 0) line.Append("  消耗 ").Append(cost);
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

        private static List<long> LongList(JsonElement args, string name)
        {
            var list = new List<long>();
            if (args.ValueKind != JsonValueKind.Object) return list;
            foreach (var p in args.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in p.Value.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v)) list.Add(v);
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
