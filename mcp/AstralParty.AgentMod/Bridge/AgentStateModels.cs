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

    /// <summary>一个候选项(筹码 / 奖励卡 / 商店卡 / 地块 / 可出的牌)。</summary>
    public sealed class AgentCandidate
    {
        public int Id;
        public string Name;
        /// <summary>relic | rewardCard | shopCard | card | land | player</summary>
        public string Kind;
        /// <summary>商店卡价格(其他类型为 0)。</summary>
        public int Price;
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
