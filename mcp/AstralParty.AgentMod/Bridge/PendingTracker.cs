using System;
using System.Collections.Generic;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// "现在服务器在等我做什么"的状态机。
    ///
    /// 依据(见 docs/MCP-Agent桥接.md 的坐标表):
    ///   - 投骰 5021: SDK 的 <c>GameActions.CanThrowDice</c> / <c>CurrentSn</c>(ActionLogic.throwDiceSn);
    ///   - 用牌 5055/5073: <c>ActionLogic.CardSN</c>(>0 = 等我对效果牌/快速卡表态) + <c>UsableCards</c>(可出的牌);
    ///   - 筹码三选一 5211: Action.Data = SelectRelicC2S{Relics[]};
    ///   - 奖励卡 5377: Action.Data = SelectRewardCardC2S{CardIds[]};
    ///   - 商店 5029(PVP)/5215(PVE): Action.Data = ShopBuyC2S / PVEShopBuyC2S{Cards[], Gold, Alreadys[]…};
    ///   - 筹码地块购买 5249: Action.Data = BuyRelicC2S{RelicGold, DivinationGold};
    ///   - 移动 5027: 服务器只说"该你走了", 候选地块由客户端本地按棋盘拓扑算(BridgeMoveProbe)。
    ///
    /// 设计取舍: 服务器对**同一个玩家**一次只等一件事, 所以只保留**一个活跃窗口**(新窗口顶掉旧的),
    /// 投骰/用牌这类"状态型"待响应单独放在运行时提示里。
    /// 纯逻辑(只吃基元 + 名字解析委托), 可离线单测 —— 这是桥接最容易出错的地方。
    /// </summary>
    internal sealed partial class PendingTracker
    {
        private readonly object _lock = new object();

        private Window _window;

        // 运行时提示(主线程每 tick 刷新), 供 Build 决定优先级
        private bool _canThrowDice;
        private long _throwDiceSn;
        private long _cardSn;
        private int[] _usableCards;
        private int[] _moveLands;
        /// <summary>我方 playerId(由 StateProbe 喂)。用于"别人的窗口不许顶掉我的窗口"。</summary>
        private long _selfId;

        /// <summary>
        /// 已经应答过的窗口 sn(有界, 见 <see cref="AnsweredCap"/>)。
        ///
        /// 为什么需要: 服务器把我自己的"决定"也当成一条动作广播回来(复现: 5029 商店的购买、5037 战斗投骰),
        /// 那条动作的 sn 与我应答的窗口 sn **相同**。若不管它, 刚回完的窗口会被自己的回声重新打开,
        /// agent 就会对同一个窗口反复出招。sn 在一次对局里单调唯一, 所以"同 sn 已应答就忽略"是安全的。
        /// </summary>
        private readonly HashSet<long> _answeredSn = new HashSet<long>();
        private readonly Queue<long> _answeredOrder = new Queue<long>();
        private const int AnsweredCap = 128;

        /// <summary>一个候选窗口。</summary>
        private sealed class Window
        {
            public string Kind;
            public long Sn;
            public long SinceMs;
            public long PlayerId;
            public int[] Ids;
            public int[] Prices;
            public bool[] SoldOut;
            public bool[] Free;
            /// <summary>5249: 买这个筹码要花多少星币。</summary>
            public int RelicGold;
            /// <summary>5249: 占卜相关金额(客户端原样展示)。</summary>
            public int DivinationGold;
            /// <summary>商店类窗口: true=PVE(5215), false=PVP(5029)。</summary>
            public bool PveShop;
            /// <summary>5047 战斗询问: 挑战者 id(AskBattleC2S.AskPlayerId)。</summary>
            public long AskPlayerId;
            /// <summary>5039 闪避窗口: true = 这一击不能闪避。</summary>
            public bool NoDodge;
            /// <summary>战斗用牌候选的消耗(与 Ids 同序; 可能为 null)。</summary>
            public int[] Costs;
            /// <summary>5035: 我方剩余战斗点数(-1 = 没读到)。</summary>
            public int ResidueCost = -1;
            /// <summary>5035: 我是攻击方(true)还是防守方。</summary>
            public bool IsAttacker;
            /// <summary>5213 追击窗口: 本地算出的候选怪物 playerId(应答时填 SelectId; 0 = 不追)。</summary>
            public long[] MonsterIds;
            /// <summary>5077 窗口: 我站在什么地块上("born"/"fillingStation"/"other"; 纯展示)。</summary>
            public string Land;
            /// <summary>5323 商人买卡: 要买的卡牌配置 id。</summary>
            public long VendorCardId;
            /// <summary>5323 商人买卡: 价格(星币)。</summary>
            public int VendorPrice;
            /// <summary>5067 控制移动卡: 可选点数上限(1..MaxPoint)。</summary>
            public int MaxPoint;
            /// <summary>5063 炮台选目标: 最多能选几个英雄(1..TargetNum)。</summary>
            public int TargetNum;
            /// <summary>5063 炮台选目标: 候选英雄 playerId(本地按客户端同口径过滤; null = 没算出来)。</summary>
            public long[] TargetIds;
            /// <summary>5081/5083 赌场: 客户端按钮可不可点(false = 已死/星币不足, 被置灰)。</summary>
            public bool CanAct = true;
            /// <summary>5081 赌场押注: 这一注多少星币(纯展示)。</summary>
            public int BetGold;
            /// <summary>5081 赌场押注: offer 的 IsExec(应答时要原样回传)。</summary>
            public bool EnableJoin;
        }

        public string WindowKind { get { lock (_lock) { return _window == null ? AgentPendingKind.None : _window.Kind; } } }


        public void Reset()
        {
            lock (_lock)
            {
                _window = null;
                _moveLands = null;
                _usableCards = null;
                _cardSn = 0;
                _throwDiceSn = 0;
                _canThrowDice = false;
                _answeredSn.Clear();
                _answeredOrder.Clear();
            }
        }

        /// <summary>记下"这个 sn 的窗口已经被我们应答过了"(由 CommandRunner 在真正发出请求后调用)。</summary>
        public void NoteAnswered(long sn)
        {
            if (sn == 0) return;
            lock (_lock)
            {
                if (_answeredSn.Add(sn)) _answeredOrder.Enqueue(sn);
                while (_answeredOrder.Count > AnsweredCap) _answeredSn.Remove(_answeredOrder.Dequeue());
            }
        }

        private bool IsAnswered(long sn)
        {
            if (sn == 0) return false;
            lock (_lock) { return _answeredSn.Contains(sn); }
        }


        /// <summary>刷新运行时提示(每 tick 由 StateProbe 调用)。</summary>
        public void SetRuntimeHints(bool canThrowDice, long throwDiceSn, long cardSn, int[] usableCards)
        {
            lock (_lock)
            {
                _canThrowDice = canThrowDice;
                _throwDiceSn = throwDiceSn;
                _cardSn = cardSn;
                _usableCards = usableCards;
            }
        }

        /// <summary>移动候选地块(本地棋盘拓扑算出来的)。</summary>
        public void SetMoveCandidates(int[] lands)
        {
            lock (_lock) { _moveLands = lands; }
        }

        /// <summary>
        /// 我方 playerId(主线程读到后喂一次即可)。
        ///
        /// 用途: 服务器经常**同时**给 4 个玩家各推一条候选动作(反编译证据: 任务奖励的筹码候选是同一时刻
        /// 给四名玩家分别广播的)。只有一个活跃窗口时, "后到的那条"会把"我的那条"顶掉 —— 我的窗口一丢,
        /// agent 就永远看不到该它表态的那件事, 只能等服务器超时代打。所以: 只要我手上还有一个属于我的窗口,
        /// 别人的窗口就不许覆盖它。
        /// </summary>
        public void SetSelf(long selfId)
        {
            lock (_lock) { _selfId = selfId; }
        }

        /// <summary>
        /// 5035 战斗用牌候选(手牌 Guid + 各自战斗消耗 + 我方剩余点数 + 我是攻方还是守方)。
        /// 由主线程每 tick 按 <c>GetVailCard()</c> 的口径算好后喂进来; 直接写在**当前那个 fightCard 窗口**上,
        /// 所以出过一张牌之后候选会自动少一张。
        /// </summary>
        public void SetFightCardCandidates(int[] cardUids, int[] costs, int residueCost, bool isAttacker)
        {
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.FightCard) return;
                _window.Ids = cardUids;
                _window.Costs = costs;
                _window.ResidueCost = residueCost;
                _window.IsAttacker = isAttacker;
            }
        }

        /// <summary>
        /// 5213 追击候选怪物(本地按 <c>GetVailPursuitMonster()</c> 的口径算出), 直接写在当前那个
        /// pursueMonster 窗口上。候选为空数组是合法结果("现在没有可追的怪"), 与"没算出来"不同:
        /// 后者不调用本方法, agent 会看到"候选未知"的提示而不是"没有目标"。
        /// </summary>
        public void SetPursuitMonsters(long[] monsterIds)
        {
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.PursueMonster) return;
                _window.MonsterIds = monsterIds;
            }
        }

        /// <summary>5077 窗口: 把"我站在什么地块上"写到当前窗口(纯展示, 不参与应答)。</summary>
        public void SetStandLand(string land)
        {
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.StopOrContinue) return;
                _window.Land = land;
            }
        }

        // ============================== 状态构建(主线程) ==============================

        private void SetWindow(Window w)
        {
            if (w.PlayerId == 0) return;
            // 没有 sn 的窗口无法应答(SDK 的 Info.Sn 必须填对): 宁可不上报, 也不给 agent 一个点了没用的窗口
            if (w.Sn == 0) return;
            // 已经应答过的 sn 又冒出来 = 服务器在回播我自己的决定, 不是新窗口(见 _answeredSn 的注释)
            if (IsAnswered(w.Sn)) return;
            lock (_lock)
            {
                // 同一个窗口被服务器重复广播(同 Sn): 保留最早的出现时刻, 便于算"已经等了多久"
                if (_window != null && _window.Kind == w.Kind && w.Sn != 0 && _window.Sn == w.Sn) return;
                // 别人的窗口不许顶掉我的窗口(服务器会同时给 4 个玩家各推一条, 见 SetSelf 的注释)
                if (_selfId != 0 && _window != null && _window.PlayerId == _selfId &&
                    w.PlayerId != _selfId)
                {
                    return;
                }
                if (w.Kind != AgentPendingKind.Move) _moveLands = null;
                _window = w;
            }
        }

        private void ClearIf(string kind, long playerId)
        {
            lock (_lock)
            {
                if (_window == null) return;
                if (_window.Kind != kind) return;
                if (playerId != 0 && _window.PlayerId != 0 && _window.PlayerId != playerId) return;
                _window = null;
            }
        }

        private static int[] ToArray(IReadOnlyList<int> src)
        {
            if (src == null) return null;
            var arr = new int[src.Count];
            for (int i = 0; i < src.Count; i++) arr[i] = src[i];
            return arr;
        }
    }
}
