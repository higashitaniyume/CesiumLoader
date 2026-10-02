using System.Collections.Generic;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// state.json 的结构(供外部 MCP server 读取)。
    ///
    /// 命名约定: 公有字段名 = JSON 字段名, 与 SDK 的 <c>RuntimeDump</c> 一致
    /// (CesiumJson 直接按字段名写; 外部用 System.Text.Json 大小写不敏感读)。
    /// </summary>
    public sealed class AgentState
    {
        public int Schema;
        public string ModVersion;
        public string SdkVersion;
        public long UpdatedAtMs;
        public string UpdatedAtUtc;

        /// <summary>状态序号, 单调递增。外部靠它判断"是否读到新状态"。</summary>
        public long StateSeq;

        /// <summary>当前 Unity 场景名。</summary>
        public string Scene;

        /// <summary>是否在房间里(能读到玩家/怪物面板)。</summary>
        public bool InRoom;

        /// <summary>是否在战斗中。</summary>
        public bool InBattle;

        /// <summary>是否轮到我行动(<c>GameActions.CanThrowDice</c>)。</summary>
        public bool IsMyTurn;

        /// <summary>服务器下发的当前待响应 sn(<c>GameActions.CurrentSn</c>)。</summary>
        public long CurrentSn;

        /// <summary>本次行动不允许移动(<c>ActionLogic.NotMove</c>)。</summary>
        public bool NotMove;

        public AgentSelf Self;
        public List<AgentUnit> Units = new List<AgentUnit>();
        public List<AgentCard> Hand = new List<AgentCard>();
        public List<AgentBuff> SelfBuffs = new List<AgentBuff>();

        /// <summary>当前待 agent 做的决策(核心字段: agent 的循环就是"等它变 + 回应它")。</summary>
        public AgentPending Pending = new AgentPending();

        public AgentCounters Counters = new AgentCounters();
        public AgentControl Control = new AgentControl();

        /// <summary>最近若干条原始动作(来自服务器 1002), 用于观测事件系统没覆盖的信息。</summary>
        public List<AgentActionRecord> RecentActions = new List<AgentActionRecord>();
    }

    /// <summary>我方玩家信息。</summary>
    public sealed class AgentSelf
    {
        public long PlayerId;
        public string Nick;
        public int Gold;
        public int HandCount;
    }

    /// <summary>房间里的一个单位(玩家或怪物), 取自 <c>Players.Roster()</c>。</summary>
    public sealed class AgentUnit
    {
        public long Id;
        public string Name;
        public bool IsSelf;
        public bool IsMonster;
        public bool IsBot;
        public int Atk;
        public int Def;
        public int Hp;
        public int MaxHp;
        public int Gold;
        public int HandCount;
    }

    /// <summary>手牌(只有自己的能看到; 队友手牌被服务器掩码)。</summary>
    public sealed class AgentCard
    {
        /// <summary>手牌唯一 id(战斗用牌走这个)。</summary>
        public int Guid;
        /// <summary>卡牌配置 id。</summary>
        public int CardId;
        public string Name;
        public bool IsTemp;
        public int PurifyNum;
        public int BattleCost;
    }

    public sealed class AgentBuff
    {
        public int BuffId;
        public int Layers;
        public int KeepRound;
    }

    /// <summary>一个候选项(筹码 / 奖励卡 / 商店卡 / 地块 / 可出的牌 / 怪物)。</summary>
    public sealed class AgentCandidate
    {
        public int Id;
        /// <summary>候选 id 的 64 位版本(怪物/玩家 id 走这个; 其余为 0)。</summary>
        public long LongId;
        public string Name;
        /// <summary>relic | rewardCard | shopCard | card | land | player</summary>
        public string Kind;
        /// <summary>商店卡价格(其他类型为 0)。</summary>
        public int Price;
        /// <summary>战斗用牌的消耗(战斗点数; 非战斗候选为 0)。</summary>
        public int Cost;
        /// <summary>商店卡是否已售罄。</summary>
        public bool SoldOut;
        /// <summary>商店卡是否免费(天赋免费格/免费卡位)。</summary>
        public bool Free;
    }

    /// <summary>
    /// 当前待响应窗口。Kind=throwDice 时 Options 给出可用的投骰变体;
    /// Actionable=false 表示桥接目前没有能应答它的工具(缺口会写进 Notes)。
    /// </summary>
    public sealed class AgentPending
    {
        public string Kind = AgentPendingKind.None;
        public bool Actionable;
        public long Sn;
        /// <summary>这个 pending 是从哪来的: sdk(CanThrowDice/UsableCards) | event(候选事件) | action(原始动作流)</summary>
        public string Source;
        public long SinceMs;
        /// <summary>绝对截止时刻(0 = 未知)。</summary>
        public long DeadlineMs;
        /// <summary>剩余毫秒(-1 = 未知)。到点游戏会自己代打(选第一项/空购买离店), 所以别拖。</summary>
        public long RemainingMs = -1;
        public List<AgentCandidate> Candidates = new List<AgentCandidate>();
        /// <summary>5047 战斗询问: 是谁在挑战我(AskBattleC2S.AskPlayerId); 其它窗口为 0。</summary>
        public long AskPlayerId;
        /// <summary>5039 闪避窗口: true = 这一击不能闪避(反编译 FightWindow.ChooseActive 里会直接拒绝闪避请求)。</summary>
        public bool NoDodge;
        /// <summary>5035 战斗用牌: 我方剩余战斗点数(超额的牌服务器会拒)。-1 = 未读到。</summary>
        public int ResidueCost = -1;
        /// <summary>5035 战斗用牌: 我在这场战斗里是攻击方(true)还是防守方; 决定哪些手牌可用。only 该窗口有意义。</summary>
        public bool IsAttacker;
        /// <summary>5077 加油站/出生点: 我站在哪种地块上(born/fillingStation/other; 空 = 没读到)。</summary>
        public string Land;
        /// <summary>5323 商人买卡: 要买的卡牌配置 id(客户端窗口其实不展示, 只给 agent 做参考)。</summary>
        public long VendorCardId;
        /// <summary>5323 商人买卡: 价格(星币)。</summary>
        public int VendorPrice;
        /// <summary>5067 控制移动卡选点: 可选点数上限(应答 Point ∈ 1..MaxPoint)。</summary>
        public int MaxPoint;
        /// <summary>5063 炮台选目标: 最多能选几个英雄(应答 TargetIds 的数量 1..TargetNum)。</summary>
        public int TargetNum;
        /// <summary>5063 炮台选目标: 候选英雄 playerId(本地按 characterType==Hero &amp;&amp; CanTargetIds[id] 过滤)。</summary>
        public long[] TargetIds;
        /// <summary>5081/5083 赌场: 客户端这边的按钮可不可点(false = 已死/星币不足, 按钮被置灰, 真人点不动)。</summary>
        public bool GambleCanAct = true;
        /// <summary>5081 赌场押注: 这一注多少星币(纯展示)。</summary>
        public int BetGold;
        /// <summary>可用来应答它的工具名(自解释, 让 agent 不必猜)。</summary>
        public List<string> Options = new List<string>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>pending 的种类常量(两边共用字面量)。</summary>
    public static class AgentPendingKind
    {
        public const string None = "none";
        public const string ThrowDice = "throwDice";
        public const string BattleDice = "battleDice";
        public const string SelectRelic = "selectRelic";
        public const string RewardCard = "rewardCard";
        public const string Shop = "shop";
        public const string Move = "move";
        /// <summary>服务器给了可用牌列表(棋盘效果牌/跟牌), 等我出牌。</summary>
        public const string CardChoice = "cardChoice";
        /// <summary>筹码地块: 要不要花星币买这个筹码(5249)。</summary>
        public const string BuyRelic = "buyRelic";
        /// <summary>棋盘事件弹窗: 从若干个事件里选一个(5317, 候选 = SelectEventC2S.Events)。</summary>
        public const string SelectEvent = "selectEvent";
        /// <summary>战斗询问 5047(AskFight): 要不要接这一场。超时不答 = 不打。</summary>
        public const string AskFight = "askFight";
        /// <summary>战斗准备阶段用牌 5035(ReadyFightUseCard)。候选 = 手牌里 EffectType 匹配我方角色的牌; 超时 = 不出牌。</summary>
        public const string FightCard = "fightCard";
        /// <summary>战斗内闪避 5039(ReadyFightChoice)。超时 = 不闪避。</summary>
        public const string FightChoice = "fightChoice";
        /// <summary>加油站/出生点 5077(StopOrContinue): 停留还是继续走。超时 = 继续走。</summary>
        public const string StopOrContinue = "stopOrContinue";
        /// <summary>怪物追击 5213(MonsterPursuit): 追哪只怪。候选由客户端本地算; 超时 = 不追击。</summary>
        public const string PursueMonster = "pursueMonster";
        /// <summary>商人买卡 5323(VendorBuyCard): 花 N 星币买下这张卡。超时 = 不买。</summary>
        public const string VendorCard = "vendorCard";
        /// <summary>控制移动卡选点 5067(ThrowDiceResult): 用几点移动力(1..MaxPoint)。超时 = 1 点。</summary>
        public const string SelectPoint = "selectPoint";
        /// <summary>复活队友 5233(AskReviveTeammate): 救不救倒下的队友。超时 = 不复活。</summary>
        public const string ReviveTeammate = "reviveTeammate";
        /// <summary>机制选择 5259(SelectMechanism): 启不启动地块机制。超时 = 不启动。</summary>
        public const string SelectMechanism = "selectMechanism";
        /// <summary>医院 5093(TriggerHospital): 接受检查(唯一合法上行)。超时 = 同样发检查。</summary>
        public const string HospitalCheck = "hospitalCheck";
        /// <summary>炮台选目标 5063(LandChoiceTarget, 仅 LandType==11): 选 1..TargetNum 个英雄, 或离开。超时 = 离开。</summary>
        public const string BatteryTarget = "batteryTarget";
        /// <summary>占卜 5069(TriggerDivination): 两张占卜牌选一张(候选 = CanChoiceIds)。超时 = 第 1 张。</summary>
        public const string Divination = "divination";
        /// <summary>赌场押注 5081(StartGamble): GuessCode 1=奇数 2=偶数。超时 = 押奇数。</summary>
        public const string GambleGuess = "gambleGuess";
        /// <summary>赌场掷骰 5083(GambleThrowDic): 唯一合法上行(没有可选参数)。超时 = 也走它。</summary>
        public const string GambleDice = "gambleDice";
        /// <summary>抽奖选号 5041(LotteryChoice): 选 Num 个还没被自己占的号码。超时 = 最小的那几个。</summary>
        public const string LotteryPick = "lotteryPick";
        /// <summary>追击地块 5033(Pursuit, 与 5213 怪物追击不是同一个窗口): 追哪个敌方英雄。超时 = 停留。</summary>
        public const string PursuePlayer = "pursuePlayer";
    }

    public sealed class AgentCounters
    {
        public long CommandsExecuted;
        public long CommandsRejected;
        public long EventsLogged;
        public long ActionsLogged;
        public long Ticks;
        public long StateWrites;
    }

    /// <summary>当前生效的开关(配置 + control.json 合并后的结果)。</summary>
    public sealed class AgentControl
    {
        public bool EnableActions;
        public bool PauseActions;
        public bool DryRun;
        public long ControlReadAtMs;
        public string ControlSource;
    }

    /// <summary>原始动作流的一条记录。</summary>
    public sealed class AgentActionRecord
    {
        public long AtMs;
        public long Id;
        public long Sn;
        public long PlayerId;
        public bool IsSelf;
        public int Len;
        /// <summary>已知消息类型时给出解码摘要, 否则为空。</summary>
        public string Decoded;
    }
}
