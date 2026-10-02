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
    internal sealed class PendingTracker
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

        /// <summary>取当前窗口的候选 id(仅当窗口类型匹配)。agent 只报 index 时, mod 需要拿回完整候选再发请求。</summary>
        public bool TryGetWindowIds(string kind, out int[] ids)
        {
            ids = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != kind) return false;
                if (_window.Ids == null || _window.Ids.Length == 0) return false;
                ids = (int[])_window.Ids.Clone();
                return true;
            }
        }

        /// <summary>5039 闪避窗口: 取"这一击能不能闪避"。当前没有该窗口时返回 false(调用方应自己判窗口存在)。</summary>
        public bool TryGetNoDodge(out bool noDodge)
        {
            noDodge = false;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.FightChoice) return false;
                noDodge = _window.NoDodge;
                return true;
            }
        }

        /// <summary>5213 追击窗口: 取本地算出的候选怪物 playerId。用于"agent 只能追候选里的怪"这条校验。</summary>
        public bool TryGetMonsterIds(out long[] ids)
        {
            ids = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.PursueMonster) return false;
                if (_window.MonsterIds == null) return false;
                ids = (long[])_window.MonsterIds.Clone();
                return true;
            }
        }

        /// <summary>5067 控制移动卡: 取可选点数上限。没有该窗口时返回 false。</summary>
        public bool TryGetMaxPoint(out int maxPoint)
        {
            maxPoint = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.SelectPoint) return false;
                maxPoint = _window.MaxPoint;
                return true;
            }
        }

        /// <summary>5323 商人买卡: 取价格(星币)。没有该窗口时返回 false。</summary>
        public bool TryGetVendorPrice(out int price)
        {
            price = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.VendorCard) return false;
                price = _window.VendorPrice;
                return true;
            }
        }

        /// <summary>5063 炮台选目标: 取"最多能选几个 + 候选英雄"。用于"agent 只能选候选里的英雄、数量不超上限"的校验。</summary>
        public bool TryGetBatteryTargets(out long[] ids, out int targetNum)
        {
            ids = null;
            targetNum = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.BatteryTarget) return false;
                targetNum = _window.TargetNum;
                ids = _window.TargetIds == null ? null : (long[])_window.TargetIds.Clone();
                return true;
            }
        }

        /// <summary>5081 赌场押注: 取 offer 的 IsExec(要原样回传) / 按钮可不可点 / 本注星币。</summary>
        public bool TryGetGambleGuess(out bool isExec, out bool canAct, out int betGold)
        {
            isExec = false;
            canAct = false;
            betGold = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.GambleGuess) return false;
                isExec = _window.EnableJoin;
                canAct = _window.CanAct;
                betGold = _window.BetGold;
                return true;
            }
        }

        /// <summary>5083 赌场掷骰: 取"按钮可不可点"。没有该窗口时返回 false。</summary>
        public bool TryGetGambleDice(out bool canAct)
        {
            canAct = false;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.GambleDice) return false;
                canAct = _window.CanAct;
                return true;
            }
        }

        /// <summary>取当前窗口的 sn(仅当窗口类型匹配)。各项操作的 Info.Sn 必须用对应窗口的 sn。</summary>
        public bool TryGetWindowSn(string kind, out long sn)
        {
            sn = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != kind || _window.Sn == 0) return false;
                sn = _window.Sn;
                return true;
            }
        }

        /// <summary>
        /// 取当前商店窗口的类型(决定发 ShopBuyC2S 还是 PVEShopBuyC2S: 两者消息类不同)。
        /// </summary>
        public bool TryGetShopWindow(out bool pveShop, out int slotCount)
        {
            pveShop = false;
            slotCount = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.Shop) return false;
                pveShop = _window.PveShop;
                slotCount = _window.Ids == null ? 0 : _window.Ids.Length;
                return true;
            }
        }

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

        // ============================== 事件入口(可能来自网络线程) ==============================

        public void OnRelicCandidates(long playerId, IReadOnlyList<int> relicIds, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.SelectRelic, PlayerId = playerId, Ids = ToArray(relicIds), Sn = sn, SinceMs = nowMs });
        }

        public void OnRewardCandidates(long playerId, IReadOnlyList<int> cardIds, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.RewardCard, PlayerId = playerId, Ids = ToArray(cardIds), Sn = sn, SinceMs = nowMs });
        }

        public void OnShopCandidates(long playerId, int[] ids, int[] prices, bool[] soldOut, bool[] free, bool pve, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.Shop,
                PlayerId = playerId,
                Ids = ids,
                Prices = prices,
                SoldOut = soldOut,
                Free = free,
                PveShop = pve,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>筹码地块购买 offer(5249)。</summary>
        public void OnBuyRelicOffer(long playerId, long sn, int relicGold, int divinationGold, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.BuyRelic,
                PlayerId = playerId,
                Sn = sn,
                RelicGold = relicGold,
                DivinationGold = divinationGold,
                SinceMs = nowMs
            });
        }

        /// <summary>移动动作(5027): 服务器只通知"该你走", 候选地块稍后由主线程算出来。</summary>
        public void OnMoveAction(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.Move, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        public void OnRelicSelected(long playerId, int relicId, long nowMs) { ClearIf(AgentPendingKind.SelectRelic, playerId); }
        public void OnRewardSelected(long playerId, int cardId, long nowMs) { ClearIf(AgentPendingKind.RewardCard, playerId); }

        /// <summary>移动回执(S2C Move): 窗口关闭。</summary>
        public void OnMoved(long playerId, long nowMs)
        {
            ClearIf(AgentPendingKind.Move, playerId);
            lock (_lock) { _moveLands = null; }
        }

        /// <summary>商店购买回执 / 进店未买: 窗口关闭。</summary>
        public void OnShopDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.Shop, playerId); }

        /// <summary>筹码地块回执(5250): 窗口关闭。</summary>
        public void OnBuyRelicDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BuyRelic, playerId); }

        // ---------- 战斗攻击骰窗口(5037 / 回执 5038) ----------

        /// <summary>
        /// 战斗攻击骰窗口(5037 = ReadyFightThrowDice)。
        ///
        /// 依据(反编译 FightLogic.ReadyFightThrowDice / RequestBattleThrowDiceC2S):
        /// 窗口归属 = action.PlayerId, 要回传的 sn = action.Sn, 应答消息 = BattleThrowDiceC2S。
        /// 这条动作**没有可解码的业务负载**(客户端自己也不解 Data), 所以判"是不是新窗口"只能靠 sn —— 交给
        /// <see cref="SetWindow"/> 的"已应答 sn"挡板处理(服务器会把我自己的决定回播成同 sn 的动作)。
        /// </summary>
        public void OnBattleDiceOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.BattleDice, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>战斗掷骰回执(5038 = BattleThrowDiceS2C): 窗口关闭。</summary>
        public void OnBattleDiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BattleDice, playerId); }

        // ---------- 事件选择窗口(5317 / 回执 5318) ----------

        /// <summary>
        /// 事件选择候选(5317)。候选 = <c>SelectEventC2S.Events</c>, sn = action.Sn。
        ///
        /// 依据(反编译 UI.LandEventWindow.ShowSkill10202): 客户端把服务器那条消息**原样**改
        /// <c>Idx</c> 与 <c>Info.Sn</c> 后发回, 所以候选 id 列表必须留着(应答时要回传)。
        /// 该窗口超时会被服务器代选第 0 项。
        /// </summary>
        public void OnEventCandidates(long playerId, IReadOnlyList<int> eventIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.SelectEvent,
                PlayerId = playerId,
                Ids = ToArray(eventIds),
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>事件选择回执(5318 = SelectEventS2C): 窗口关闭。</summary>
        public void OnEventSelected(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectEvent, playerId); }

        // ---------- 战斗询问窗口(5047 / 回执 5048) ----------

        /// <summary>
        /// "要不要打这一场"(5047 = <c>FightLogic.AskFight</c>)。offer = <c>AskBattleC2S</c>,
        /// 其中 <c>AskPlayerId</c> = 挑战者, <c>FightBack</c> = 服务器已经知道答案(客户端会自己以
        /// <c>IsBattle=true</c> 自动应答, 真人玩家根本没有选择机会) —— **FightBack=true 时不要开这个窗口**,
        /// 否则桥接会跟客户端抢答同一条 sn。
        ///
        /// 依据(反编译 <c>UI.FightWindow.OpenChallengeWin/SureLaunch/RequestClosePKWin</c>):
        /// 归属 = <c>action.PlayerId</c>(只对本人注册超时); 超时回调点的是 <c>btn_Leave</c> →
        /// <c>IsBattle=false</c>, 即**不答 = 不打**。
        /// </summary>
        public void OnAskFightOffer(long playerId, long askPlayerId, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.AskFight,
                PlayerId = playerId,
                AskPlayerId = askPlayerId,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>战斗询问回执(5048 = AskBattleS2C): 窗口关闭。</summary>
        public void OnAskFightDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.AskFight, playerId); }

        // ---------- 战斗用牌窗口(5035 / 回执 5036) ----------

        /// <summary>
        /// 战斗准备阶段轮到我出牌(5035 = <c>FightLogic.ReadyFightUseCard</c>)。
        ///
        /// 这条动作**没有可解码的负载**(反编译 <c>UI.FightWindow.RefreshPKCard</c> 只用了 action.PlayerId 与 action.Sn)。
        /// 候选牌由客户端本地算: <c>GetVailCard()</c> = 我方手牌里 <c>Config.EffectType</c> 匹配我方角色的那些
        /// (我是攻击方 → <c>EffectType.Attack</c>, 我是防守方 → <c>EffectType.Defense</c>),
        /// 再由 <c>RefreshCardUsability</c> 按剩余战斗点数把买不起的牌置灰 —— 这两步由
        /// <see cref="SetFightCardCandidates"/> 从主线程喂进来。
        ///
        /// 超时回调点的是 <c>btn_FinishPkCard</c> → <c>CardUid = 0</c>, 即**不答 = 不出牌**。
        /// </summary>
        public void OnFightCardOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.FightCard, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>战斗用牌回执(5036 = BattleUseCardS2C): 窗口关闭。</summary>
        public void OnFightCardDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.FightCard, playerId); }

        // ---------- 战斗闪避窗口(5039 / 回执 5040) ----------

        /// <summary>
        /// 防守方选"闪避 / 硬吃"(5039 = <c>FightLogic.ReadyFightChoice</c>)。offer = <c>BattleChoiceC2S</c>,
        /// 只需要读它的 <c>NoDodge</c>。
        ///
        /// 依据(反编译 <c>UI.FightWindow.RefreshDefendReadyChoice/ChooseActive</c>):
        /// 归属 = <c>action.PlayerId</c>; <c>NoDodge=true</c> 时客户端**直接拒绝闪避请求**(只弹个提示、不发包),
        /// 所以这时 agent 只能回 <c>dodge=false</c>; 超时回调点的是 <c>btn_Defend</c> → <c>Dodge=false</c>,
        /// 即**不答 = 不闪避**。
        /// </summary>
        public void OnFightChoiceOffer(long playerId, bool noDodge, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.FightChoice,
                PlayerId = playerId,
                NoDodge = noDodge,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>闪避选择回执(5040 = BattleChoiceS2C): 窗口关闭。</summary>
        public void OnFightChoiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.FightChoice, playerId); }

        // ---------- 加油站/出生点窗口(5077 / 回执 5078) ----------

        /// <summary>
        /// 服务器问"走到这里要停下还是继续走"(5077 = <c>UI.LandFillingStationWindow.DealLand_StopOrContinue</c>)。
        ///
        /// 依据(反编译 + decomp/四窗口契约.md): 这条动作**完全没有业务负载** —— 客户端从来不
        /// <c>ReadObject</c>, 3 局真实回放里 <c>Data</c> 长度恒为 0; 窗口要显示的东西全在本地玩家状态里
        /// (<c>standLand.LandType</c>、星币、等级…), 由 <see cref="SetStandLand"/> 从主线程喂进来。
        /// 只有本人弹双按钮窗口; 超时回调点的是"继续走" → **不答 = 继续走**(<c>Stop=false</c>)。
        /// </summary>
        public void OnStopOrContinueOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.StopOrContinue, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>加油站/出生点回执(5078 = StopOrContinueS2C): 窗口关闭。</summary>
        public void OnStopOrContinueDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.StopOrContinue, playerId); }

        // ---------- 复活队友窗口(5233 / 回执 5234) ----------

        /// <summary>
        /// 服务器问"要不要复活倒下的队友"(5233 = <c>LandLogic.DealAskReviveTeammate</c>)。
        ///
        /// 依据(反编译): 这条动作**没有业务负载** —— 窗口 <c>ShowAskReviveTeammate(action)</c> 只读
        /// <c>action.Sn</c> 与 <c>action.PlayerId</c>, 从不 <c>ReadObject</c>; 该显示什么(谁倒下了、
        /// 要花多少星币)全在本地玩家状态里。窗口只在本人这边弹出, 其他人只看到"思考中"(11000)。
        /// 两个按钮: <c>btn_Stop</c> → <c>IsRevive=true</c>、<c>btn_Continue</c> → <c>IsRevive=false</c>;
        /// 超时回调点的是 <c>btn_Continue</c> → **不答 = 不复活**(<c>IsRevive=false</c>)。
        /// </summary>
        public void OnReviveTeammateOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.ReviveTeammate, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>复活队友回执(5234 = AskReviveTeammateS2C): 窗口关闭。</summary>
        public void OnReviveTeammateDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.ReviveTeammate, playerId); }

        // ---------- 机制选择窗口(5259 / 回执 5260) ----------

        /// <summary>
        /// 服务器问"要不要启动这个地块机制"(5259 = <c>LandLogic.DealAskSelectMechanism</c>)。
        ///
        /// 依据(反编译): **没有业务负载** —— 窗口 <c>ShowSelectMechanism(action)</c> 只读 <c>action.Sn</c>。
        /// <c>btn_Stop</c>("启动") → <c>Select=true</c>、<c>btn_Continue</c> → <c>Select=false</c>;
        /// 超时回调点的是 <c>btn_Continue</c> → **不答 = 不启动**(<c>Select=false</c>)。
        /// </summary>
        public void OnSelectMechanismOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.SelectMechanism, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>机制选择回执(5260 = SelectMechanismS2C): 窗口关闭。</summary>
        public void OnSelectMechanismDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectMechanism, playerId); }

        // ---------- 医院窗口(5093 / 回执 5094) ----------

        /// <summary>
        /// 服务器问"要不要接受医院检查"(5093 = <c>UI.LandHospitalWindow.DealLand_TriggerHospital</c>)。
        ///
        /// 依据(反编译): **没有业务负载**(窗口只读 <c>_action.Sn</c>)。窗口里两个按钮, 但只有
        /// <c>btn_check</c> 会发包(<c>RequestTriggerHospitalC2S(sn)</c> → <c>TriggerHospitalC2S{Info}</c>),
        /// <c>btn_noSick</c> 只切本地视图、不上行。倒计时结束点的是 <c>btn_check</c> →
        /// **超时同样发"检查"**, 所以这条窗口没有"拒绝"这个语义。
        /// </summary>
        public void OnHospitalOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.HospitalCheck, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>医院回执(5094 = TriggerHospitalS2C): 窗口关闭。</summary>
        public void OnHospitalDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.HospitalCheck, playerId); }

        // ---------- 怪物追击窗口(5213 / 回执 5214) ----------

        /// <summary>
        /// 服务器问"要不要追击怪物、追哪一只"(5213 = <c>LandLogic.DealMonsterPursuit</c>)。
        ///
        /// 依据(反编译): 候选怪物**不在协议里**(<c>Data</c> 恒为 0 字节), 由客户端本地按
        /// <c>CharacterType.Monster &amp;&amp; !NotSelect &amp;&amp; HP&gt;0 &amp;&amp; 非医院地块 &amp;&amp; 不同队伍</c> 过滤 —— 桥接在
        /// <see cref="SetPursuitMonsters"/> 里复刻同一口径(来自 <c>GameProbe.TrySelfPursuitMonsters</c>)。
        /// 超时回调点的是"不追击" → **不答 = 不追击**(<c>SelectId=0</c>)。
        /// </summary>
        public void OnPursueMonsterOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.PursueMonster, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>怪物追击回执(5214 = MonsterPursuitS2C): 窗口关闭。</summary>
        public void OnPursueMonsterDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.PursueMonster, playerId); }

        // ---------- 商人买卡窗口(5323 / 回执 5324) ----------

        /// <summary>
        /// 服务器问"要不要花 N 星币买下商人这张卡"(5323 = <c>LandLogic.DealAskVendorBuyCard</c>)。
        /// offer = <c>VendorBuyCardC2S{CardId, Gold}</c>(**没有 Info 字段**, 所以"Info.Sn==0"是用来区分
        /// offer 与"我的答案被回播"的依据)。客户端在星币不足时只弹提示、不发包。
        /// 超时回调点的是"取消" → **不答 = 不买**(<c>IsBuy=false</c>)。
        /// </summary>
        public void OnVendorCardOffer(long playerId, long cardId, int gold, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.VendorCard,
                PlayerId = playerId,
                VendorCardId = cardId,
                VendorPrice = gold,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>商人买卡回执(5324 = VendorBuyCardS2C): 窗口关闭。</summary>
        public void OnVendorCardDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.VendorCard, playerId); }

        // ---------- 控制移动卡选点窗口(5067 / 回执 5068) ----------

        /// <summary>
        /// 服务器问"这张控制移动卡要用几点移动力"(5067 = <c>CardWindow.RefreshCardInfo_ControlMoveCard</c>)。
        /// offer = <c>ThrowDiceResultC2S{MaxPoint}</c>(没有 Info), 应答 <c>Point ∈ 1..MaxPoint</c>。
        /// 只有本人开窗(<c>ActionListener</c> 里先判 <c>IsSelf</c>); 超时会把点数兜成 <c>1</c> → **不答 = 1 点**。
        /// </summary>
        public void OnSelectPointOffer(long playerId, int maxPoint, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.SelectPoint,
                PlayerId = playerId,
                MaxPoint = maxPoint,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>选点回执(5068 = ThrowDiceResultS2C): 窗口关闭。</summary>
        public void OnSelectPointDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectPoint, playerId); }

        // ---------- 炮台选目标窗口(5063 / 回执 5064) ----------

        /// <summary>
        /// 服务器问"炮台要打哪几个英雄"(5063 = <c>UI.LandBatteryWindow.DealLand_LandChoiceTarget</c>, 仅 <c>LandType==11</c>)。
        /// offer 与答案**是同一个消息类** <c>LandChoiceTargetC2S</c>: offer 有 <c>LandType</c>/<c>TargetNum</c>/<c>CanTargetIds</c>,
        /// 答案是 <c>Info.Sn</c> + <c>TargetIds</c>(或 <c>Exit=true</c>) —— 所以用 <c>Info.Sn</c> 是否为 0 区分。
        /// 候选英雄由本地按 <c>characterType==Hero &amp;&amp; CanTargetIds[id]</c> 过滤(见 <c>GameProbe.TryBatteryTargets</c>);
        /// <paramref name="candidateIds"/> = null 表示"没算出来"(不要伪装成"没有目标")。
        /// 超时回调点的是"离开" → **不答 = 离开**(<c>Exit=true</c>)。
        /// </summary>
        public void OnBatteryOffer(long playerId, int targetNum, long[] candidateIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.BatteryTarget,
                PlayerId = playerId,
                TargetNum = targetNum,
                TargetIds = candidateIds,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>炮台选目标回执(5064 = LandChoiceTargetS2C): 窗口关闭。</summary>
        public void OnBatteryDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BatteryTarget, playerId); }

        // ---------- 占卜窗口(5069 / 回执 5070) ----------

        /// <summary>
        /// 服务器问"占卜翻哪一张"(5069 = <c>UI.LandDivinationWindow.DealLand_Divination</c>)。
        /// offer = <c>TriggerDivinationC2S{CanChoiceIds}</c>(恰好两张; 同一消息类的答案则是 <c>Info</c> + <c>Id</c>,
        /// 靠 <c>Info.Sn==0</c> 区分, 同 5323/5067/5063)。只有本人能点, 其他人只看到"思考中"。
        /// 超时回调点的是 <c>btn_Divination_1</c> → **不答 = 选第 1 张**(<c>CanChoiceIds[0]</c>)。
        /// </summary>
        public void OnDivinationOffer(long playerId, IReadOnlyList<int> divinationIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.Divination,
                PlayerId = playerId,
                Ids = ToArray(divinationIds),
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>占卜回执(5070 = TriggerDivinationS2C): 窗口关闭。</summary>
        public void OnDivinationDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.Divination, playerId); }

        // ---------- 赌场窗口(5081 押注 / 5083 掷骰; 回执 5082/5084, 状态 1022) ----------

        /// <summary>
        /// 服务器问"赌场押奇数还是偶数"(5081 = <c>UI.LandGambleWindow.DealLand_Gamble</c>)。
        /// offer = <c>StartGambleC2S{Hall, IsExec}</c>(<c>IsExec</c> = 我能不能参与), 答案是
        /// <c>Info</c> + <c>IsExec</c> + <c>GuessCode</c> —— 靠 <c>Info.Sn</c> 区分(同 5323/5067/5063/5069)。
        /// 倒计时点的是 <c>btn_odd</c> → **不答 = 押奇数**(<c>GuessCode=1</c>)。
        /// 客户端在 <c>IsDie || GoldLack</c> 时把两个按钮都置灰(<c>touchable=false</c>), 真人点不动 ——
        /// 桥接记下 <paramref name="canAct"/>=false 并拒答(客户端自己的超时仍会押奇数)。
        /// </summary>
        public void OnGambleGuessOffer(long playerId, long sn, bool enableJoin, bool canAct, int betGold, long nowMs)
        {
            if (!enableJoin) return;   // 客户端 OnClick* 第一件事就是判 _enableJoinGamble: 不参与就没有任何上行
            SetWindow(new Window
            {
                Kind = AgentPendingKind.GambleGuess,
                PlayerId = playerId,
                Sn = sn,
                EnableJoin = enableJoin,
                CanAct = canAct,
                BetGold = betGold,
                SinceMs = nowMs
            });
        }

        /// <summary>
        /// 赌场掷骰窗口(5083 = <c>LandGambleWindow.DealLand_GambleDice</c>): **唯一合法上行就是掷骰**, 没有可选参数;
        /// 倒计时点的也是 <c>btn_Dice</c> → "不答"与"答"在服务器看来一样(同 5093 医院)。
        /// <paramref name="canAct"/>=false 时客户端把按钮置灰, 桥接拒答。
        /// </summary>
        public void OnGambleDiceOffer(long playerId, long sn, bool canAct, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.GambleDice,
                PlayerId = playerId,
                Sn = sn,
                CanAct = canAct,
                SinceMs = nowMs
            });
        }

        /// <summary>押注回执(5082 = StartGambleS2C, **该消息没有任何字段**): 关窗。</summary>
        public void OnGambleGuessDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.GambleGuess, playerId); }

        /// <summary>掷骰回执(5084 = GambleThrowDicS2C{PlayerId, Point}): 关窗。</summary>
        public void OnGambleDiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.GambleDice, playerId); }

        /// <summary>
        /// 赌场状态变化(1022 = <c>GambleChangeS2C{Hall}</c>, 由观察者按 <c>Hall</c> 算好两个窗口是否还开着):
        /// 赌场窗口没有"id+1 回执"式的关窗信号, 真正决定按钮还在不在的是 <c>Hall.S</c> 与我的
        /// <c>GuessCode</c>/<c>Point</c>, 所以这条必须处理, 否则窗口会一直挂着。
        /// 只处理"关" —— 窗口的**开**只能由 5081/5083 的 offer 触发(否则会给 agent 一个没有 sn 的窗口)。
        /// </summary>
        public void OnGambleState(bool guessOpen, bool diceOpen)
        {
            lock (_lock)
            {
                if (_window == null) return;
                if (!guessOpen && _window.Kind == AgentPendingKind.GambleGuess) _window = null;
                else if (!diceOpen && _window.Kind == AgentPendingKind.GambleDice) _window = null;
            }
        }

        // ============================== 主线程提示 ==============================

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

        public AgentPending Build(
            long selfId,
            bool canThrowDice,
            long currentSn,
            long nowMs,
            Func<string, long, long> remainingOf,
            Func<string, int, string> nameOf)
        {
            var p = new AgentPending();

            Window w;
            int[] moveLands;
            long cardSn;
            int[] usable;
            lock (_lock)
            {
                w = _window;
                moveLands = _moveLands;
                cardSn = _cardSn;
                usable = _usableCards;
            }

            bool windowIsMine = w != null && (selfId == 0 || w.PlayerId == selfId);

            if (windowIsMine)
            {
                p.Kind = w.Kind;
                p.Sn = w.Sn;
                p.Source = "event";
                p.SinceMs = w.SinceMs;
                p.AskPlayerId = w.AskPlayerId;
                p.NoDodge = w.NoDodge;
                p.ResidueCost = w.ResidueCost;
                p.IsAttacker = w.IsAttacker;
                p.Land = w.Land;
                p.VendorCardId = w.VendorCardId;
                p.VendorPrice = w.VendorPrice;
                p.MaxPoint = w.MaxPoint;
                p.TargetNum = w.TargetNum;
                p.TargetIds = w.TargetIds;
                p.GambleCanAct = w.CanAct;
                p.BetGold = w.BetGold;
                FillCandidates(p, w, nameOf);
                FillOptions(p, w, moveLands, usable, currentSn);
                FillDeadline(p, nowMs, remainingOf, w.Sn);
                return p;
            }

            if (canThrowDice)
            {
                p.Kind = AgentPendingKind.ThrowDice;
                p.Sn = currentSn;
                p.Source = "sdk";
                p.Actionable = true;
                p.SinceMs = nowMs;
                p.Options.Add("astral_throw_dice {}                        普通投骰");
                p.Options.Add("astral_throw_dice {\"noOper\":true}           无牌可出直接过");
                p.Options.Add("astral_throw_dice {\"moveNow\":true}          投完立即移动");
                if (usable != null && usable.Length > 0) AddUsableCardNote(p, usable, nameOf);
                FillDeadline(p, nowMs, remainingOf, currentSn);
                return p;
            }

            // 服务器给了可用牌列表 + 一个用牌 sn → 等我对效果牌/快速卡表态
            if (cardSn > 0 && usable != null && usable.Length > 0)
            {
                p.Kind = AgentPendingKind.CardChoice;
                p.Sn = cardSn;
                p.Source = "sdk";
                p.Actionable = true;
                p.SinceMs = nowMs;
                foreach (int id in usable)
                {
                    if (id == 0) continue;
                    p.Candidates.Add(new AgentCandidate { Id = id, Kind = "card", Name = nameOf != null ? nameOf("card", id) : null });
                }
                p.Options.Add("astral_use_effect_card {\"cardId\":<候选里的 id>}   当效果牌打出去");
                p.Options.Add("astral_use_quick_card {\"cardId\":<候选里的 id>, \"targetId\":<被跟玩家>}   跟牌");
                p.Options.Add("astral_abandon_card {\"cardId\":<候选里的 id>}      弃掉");
                p.Notes.Add("这是服务器给的\"可出牌\"列表: 只列了能用的牌, 不代表必须出。");
                FillDeadline(p, nowMs, remainingOf, cardSn);
                return p;
            }

            if (w != null)
            {
                p.Source = "event";
                p.Notes.Add("有候选窗口正在等其他玩家(playerId=" + w.PlayerId + ", kind=" + w.Kind + ")");
            }
            return p;
        }

        private void FillCandidates(AgentPending p, Window w, Func<string, int, string> nameOf)
        {
            // 移动: 候选是本地算出来的地块
            if (w.Kind == AgentPendingKind.Move)
            {
                int[] lands;
                lock (_lock) { lands = _moveLands; }
                if (lands != null)
                {
                    foreach (int id in lands)
                    {
                        if (id == 0) continue;
                        p.Candidates.Add(new AgentCandidate { Id = id, Kind = "land", Name = nameOf != null ? nameOf("land", id) : null });
                    }
                }
                return;
            }

            if (w.Kind == AgentPendingKind.FightCard)
            {
                if (w.Ids == null) return;
                for (int i = 0; i < w.Ids.Length; i++)
                {
                    if (w.Ids[i] == 0) continue;
                    var fc = new AgentCandidate { Id = w.Ids[i], Kind = "card" };
                    if (nameOf != null) fc.Name = nameOf("handCard", fc.Id);
                    if (w.Costs != null && i < w.Costs.Length) fc.Cost = w.Costs[i];
                    p.Candidates.Add(fc);
                }
                return;
            }

            if (w.Kind == AgentPendingKind.PursueMonster)
            {
                if (w.MonsterIds == null) return;
                foreach (long id in w.MonsterIds)
                {
                    if (id == 0) continue;
                    // 怪物 id 是 playerId(64 位), 所以两个字段都填: LongId 给工具用, Id 只作显示/兼容
                    p.Candidates.Add(new AgentCandidate
                    {
                        Id = (int)id,
                        LongId = id,
                        Kind = "monster",
                        Name = nameOf != null ? nameOf("monster", (int)id) : null
                    });
                }
                return;
            }

            if (w.Kind == AgentPendingKind.BatteryTarget)
            {
                if (w.TargetIds == null) return;
                foreach (long id in w.TargetIds)
                {
                    if (id == 0) continue;
                    // 英雄 id 是 playerId(64 位): 两个字段都填(LongId 给工具用)
                    p.Candidates.Add(new AgentCandidate
                    {
                        Id = (int)id,
                        LongId = id,
                        Kind = "player",
                        Name = nameOf != null ? nameOf("player", (int)id) : null
                    });
                }
                return;
            }

            if (w.Ids == null) return;
            string kind = CandidateKindOf(w.Kind);
            for (int i = 0; i < w.Ids.Length; i++)
            {
                int id = w.Ids[i];
                if (id == 0) continue;
                var c = new AgentCandidate
                {
                    Id = id,
                    Name = nameOf != null ? nameOf(kind, id) : null,
                    Kind = kind
                };
                if (w.Prices != null && i < w.Prices.Length) c.Price = w.Prices[i];
                if (w.SoldOut != null && i < w.SoldOut.Length) c.SoldOut = w.SoldOut[i];
                if (w.Free != null && i < w.Free.Length) c.Free = w.Free[i];
                p.Candidates.Add(c);
            }
        }

        private void FillOptions(AgentPending p, Window w, int[] moveLands, int[] usable, long currentSn)
        {
            switch (w.Kind)
            {
                case AgentPendingKind.SelectRelic:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_relic {\"index\":0..N-1}          选第 index 个候选");
                    p.Options.Add("astral_select_relic {}                     默认选第 0 个");
                    p.Options.Add("astral_select_relic {\"reroll\":true}        重摇这组候选(不结束窗口, 服务器会推新的一组)");
                    p.Notes.Add("重摇后窗口不会关闭: 会来一组新的候选(新 sn), 届时重新决策。");
                    break;

                case AgentPendingKind.RewardCard:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_reward_card {\"index\":0..N-1}    选第 index 个候选");
                    break;

                case AgentPendingKind.Move:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_move {\"landId\":<候选里的 id>}         走到候选地块");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("没算出目标地块(可能不在棋盘上/只有唯一方向由客户端自动走)。");
                    else if (p.Candidates.Count == 1)
                        p.Notes.Add("只有一个方向时客户端通常已经自动走掉, 若仍看到本窗口就直接应答它。");
                    break;

                case AgentPendingKind.Shop:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_shop_buy {\"indexes\":[0,2]}           买第 0、2 格(null 表示空手离店)");
                    p.Options.Add("astral_shop_buy {}                          空手离店");
                    if (w.PveShop) p.Options.Add("astral_atm_transfer {\"targetId\":<队友 playerId>}   给队友转 " + 5 + " 星币(PVE 商店的 ATM)");
                    if (!w.PveShop) p.Notes.Add("这是 PVP 商店(5029): 只支持买/离店。");
                    else p.Notes.Add("这是 PVE 商店(5215): Star 币不足时别硬买。");
                    break;

                case AgentPendingKind.BuyRelic:
                    p.Actionable = true;
                    p.Options.Add("astral_buy_relic {\"confirm\":true}           花 " + w.RelicGold + " 星币买下这个筹码");
                    p.Options.Add("astral_buy_relic {\"confirm\":false}          不买, 离开");
                    p.Notes.Add("筹码价格 RelicGold=" + w.RelicGold + (w.DivinationGold != 0 ? ", DivinationGold=" + w.DivinationGold : ""));
                    p.Notes.Add("★ 判定依据是 Select(2=买 / 0=离开), 不是 Exit —— 客户端确认购买时 Exit 也是 true。");
                    break;

                case AgentPendingKind.CardChoice:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_use_effect_card {\"cardId\":<候选里的 id>}");
                    p.Options.Add("astral_use_quick_card {\"cardId\":<候选里的 id>, \"targetId\":<被跟玩家>}");
                    p.Options.Add("astral_abandon_card {\"cardId\":<候选里的 id>}");
                    break;

                case AgentPendingKind.BattleDice:
                    p.Actionable = true;
                    p.Options.Add("astral_throw_dice {\"battle\":true}        投战斗攻击骰(用本窗口的 sn)");
                    p.Notes.Add("战斗攻击判定(5037)。sn 用本窗口的, 不能用普通回合的投骰 sn。");
                    break;

                case AgentPendingKind.SelectEvent:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_event {\"index\":0..N-1}     选第 index 个事件");
                    p.Options.Add("astral_select_event {\"eventId\":<候选里的 id>}   按事件 id 选");
                    p.Options.Add("astral_select_event {}                    默认选第 0 个");
                    p.Notes.Add("超时服务器会代选第 0 项; 候选 id 会原样回传给服务器。");
                    break;

                case AgentPendingKind.AskFight:
                    p.Actionable = true;
                    p.Options.Add("astral_ask_battle {\"accept\":true}          接受这场战斗");
                    p.Options.Add("astral_ask_battle {\"accept\":false}         不打");
                    if (w.AskPlayerId != 0)
                        p.Notes.Add("挑战者(AskPlayerId)=" + w.AskPlayerId + "; 要不要打由你判断(可参考 astral_state 里双方 HP/ATK/DEF)。");
                    p.Notes.Add("★ 超时不答 = 不打(客户端超时回调点的是\"离开\"按钮), 所以想打就得主动答。");
                    break;

                case AgentPendingKind.FightCard:
                    p.Actionable = true;
                    p.Options.Add("astral_use_card {\"cardId\":<候选里的 id>}     出这张战斗牌(候选 id 就是手牌 Guid)");
                    p.Options.Add("astral_use_card {\"pass\":true}              不出牌, 直接过");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("没有可出的战斗牌(手牌里没有匹配我方角色的牌, 或都买不起)。");
                    p.Notes.Add("我方角色: " + (w.IsAttacker ? "攻击方(只能用攻击类牌)" : "防守方(只能用防御类牌)") +
                                (w.ResidueCost >= 0 ? "; 剩余战斗点数=" + w.ResidueCost : "") +
                                "; 候选的 Cost 就是这张牌要花的点数, 超过剩余点数服务器会拒。");
                    p.Notes.Add("★ 超时不答 = 不出牌(CardUid=0)。");
                    break;

                case AgentPendingKind.FightChoice:
                    p.Actionable = true;
                    if (w.NoDodge)
                    {
                        p.Options.Add("astral_battle_choice {\"dodge\":false}      硬吃(本回合不能闪避)");
                        p.Notes.Add("★ NoDodge=true: 客户端会直接拒绝闪避请求, 只能回 dodge=false。");
                    }
                    else
                    {
                        p.Options.Add("astral_battle_choice {\"dodge\":true}       闪避");
                        p.Options.Add("astral_battle_choice {\"dodge\":false}      硬吃");
                    }
                    p.Notes.Add("★ 超时不答 = 不闪避。");
                    break;

                case AgentPendingKind.StopOrContinue:
                    p.Actionable = true;
                    p.Options.Add("astral_stop_or_continue {\"stop\":false}      继续走");
                    p.Options.Add("astral_stop_or_continue {\"stop\":true}       就地停留");
                    p.Notes.Add("加油站/出生点(5077)。信息全在本地: " +
                                (string.IsNullOrEmpty(w.Land) ? "没读到当前地块" : "当前地块=" + w.Land) +
                                "; 其余看 astral_state 里的星币/等级/分数。");
                    p.Notes.Add("★ 超时不答 = 继续走(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.ReviveTeammate:
                    p.Actionable = true;
                    p.Options.Add("astral_revive_teammate {\"revive\":true}     复活队友(花星币)");
                    p.Options.Add("astral_revive_teammate {\"revive\":false}    不复活");
                    p.Notes.Add("复活队友(5233)。要花多少星币、谁倒下了看 astral_state 的 Gold 与全体单位血量。");
                    p.Notes.Add("★ 超时不答 = 不复活(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.SelectMechanism:
                    p.Actionable = true;
                    p.Options.Add("astral_select_mechanism {\"select\":true}    启动");
                    p.Options.Add("astral_select_mechanism {\"select\":false}   不启动");
                    p.Notes.Add("机制选择(5259)。");
                    p.Notes.Add("★ 超时不答 = 不启动(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.HospitalCheck:
                    p.Actionable = true;
                    p.Options.Add("astral_hospital_check {}                    接受检查");
                    p.Notes.Add("医院(5093): 这条窗口**只有\"检查\"一个合法上行** —— 客户端另一个按钮(\"没病\")" +
                                "只切本地视图、不发包; 倒计时结束点的是\"检查\"。");
                    p.Notes.Add("★ 超时不答 = 客户端自己发\"检查\", 所以答与不答的效果一样。");
                    break;

                case AgentPendingKind.PursueMonster:
                    p.Actionable = true;
                    if (p.Candidates.Count > 0)
                        p.Options.Add("astral_pursue_monster {\"monsterId\":<候选里的 LongId>}   追击这只怪");
                    p.Options.Add("astral_pursue_monster {\"pass\":true}                         不追击");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("本地没算出可追的怪(怪物要么已被选走、要么在医院地块、要么同队)。此时只能不追。");
                    p.Notes.Add("★ 超时不答 = 不追击(SelectId=0)。");
                    break;

                case AgentPendingKind.VendorCard:
                    p.Actionable = true;
                    p.Options.Add("astral_vendor_buy_card {\"buy\":true}      花 " + w.VendorPrice + " 星币买下");
                    p.Options.Add("astral_vendor_buy_card {\"buy\":false}     不买");
                    p.Notes.Add("商人买卡(5323): 价格=" + w.VendorPrice + " 星币" +
                                (w.VendorCardId != 0 ? ", 卡牌 id=" + w.VendorCardId : "") +
                                "; 星币不足时客户端会拒绝购买请求。");
                    p.Notes.Add("★ 超时不答 = 不买(客户端超时回调点的是\"取消\"按钮)。");
                    break;

                case AgentPendingKind.SelectPoint:
                    p.Actionable = true;
                    p.Options.Add("astral_select_point {\"point\":1.." + w.MaxPoint + "}     用几点移动力");
                    p.Notes.Add("控制移动卡(5067): 可选点数 1.." + w.MaxPoint + "。");
                    p.Notes.Add("★ 超时不答 = 1 点(客户端超时会把点数兜成 1 再确定)。");
                    break;

                case AgentPendingKind.BatteryTarget:
                    p.Actionable = true;
                    {
                        int candCount = w.TargetIds == null ? -1 : w.TargetIds.Length;
                        p.Options.Add("astral_battery_pick {\"targetIds\":[id,...]}    选 1.." + w.TargetNum +
                                      " 个英雄(用候选里的 playerId/LongId)");
                        p.Options.Add("astral_battery_pick {\"leave\":true}              不选目标, 直接离开");
                        if (candCount >= 0)
                            p.Notes.Add("炮台选目标(5063): 最多选 " + w.TargetNum + " 个英雄, 候选有 " + candCount +
                                        " 个(见 pending 候选, kind=player)。");
                        else
                            p.Notes.Add("炮台选目标(5063): 最多选 " + w.TargetNum +
                                        " 个英雄; 候选还没读出来(战斗数据未就绪), 此时只能 leave=true。");
                        p.Notes.Add("★ 超时不答 = 离开(客户端超时回调点的是\"离开\"按钮, Exit=true)。");
                    }
                    break;

                case AgentPendingKind.Divination:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_divination_pick {\"index\":0..N-1}     选第 index 张占卜牌");
                    p.Options.Add("astral_divination_pick {\"divinationId\":<候选里的 id>}   按占卜卡 id 选");
                    p.Options.Add("astral_divination_pick {}                    默认选第 0 张");
                    p.Notes.Add("占卜(5069): 两张里选一张(候选见 pending, kind=divination)。");
                    p.Notes.Add("★ 超时不答 = 选第 1 张(客户端超时回调点的是第 1 张牌)。");
                    break;

                case AgentPendingKind.GambleGuess:
                    p.Actionable = w.CanAct;
                    p.Options.Add("astral_gamble_guess {\"guessCode\":1}   押奇数");
                    p.Options.Add("astral_gamble_guess {\"guessCode\":2}   押偶数");
                    p.Notes.Add("赌场押注(5081): 本注 " + w.BetGold + " 星币。");
                    if (!w.CanAct)
                        p.Notes.Add("**你这边按钮是灰的(已死/星币不足), 桥接会拒答** —— 客户端自己的超时仍会替你押奇数。");
                    p.Notes.Add("★ 超时不答 = 押奇数(GuessCode=1)。");
                    break;

                case AgentPendingKind.GambleDice:
                    p.Actionable = w.CanAct;
                    p.Options.Add("astral_gamble_dice {}                掷骰(唯一合法上行)");
                    if (!w.CanAct)
                        p.Notes.Add("**你这边按钮是灰的(已死/星币不足), 桥接会拒答** —— 客户端自己的超时仍会替你掷。");
                    p.Notes.Add("★ 超时不答 = 也发掷骰(客户端超时点的就是这个按钮)。");
                    break;
            }

            if (usable != null && usable.Length > 0 && w.Kind != AgentPendingKind.CardChoice)
                AddUsableCardNote(p, usable, null);
        }

        private static void AddUsableCardNote(AgentPending p, int[] usable, Func<string, int, string> nameOf)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < usable.Length; i++)
            {
                if (usable[i] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(usable[i]);
                if (nameOf != null)
                {
                    string n = nameOf("card", usable[i]);
                    if (!string.IsNullOrEmpty(n)) sb.Append('(').Append(n).Append(')');
                }
            }
            if (sb.Length > 0) p.Notes.Add("服务器标记为\"当前可用\"的牌: [" + sb + "]");
        }

        private static void FillDeadline(AgentPending p, long nowMs, Func<string, long, long> remainingOf, long sn)
        {
            p.DeadlineMs = 0;
            p.RemainingMs = -1;
            if (remainingOf == null) return;
            long remaining;
            try { remaining = remainingOf(p.Kind, sn); }
            catch { return; }
            if (remaining < 0) return;
            p.RemainingMs = remaining;
            p.DeadlineMs = nowMs + remaining;
        }

        private static string CandidateKindOf(string windowKind)
        {
            switch (windowKind)
            {
                case AgentPendingKind.SelectRelic: return "relic";
                case AgentPendingKind.RewardCard: return "rewardCard";
                case AgentPendingKind.Shop: return "shopCard";
                case AgentPendingKind.Move: return "land";
                case AgentPendingKind.SelectEvent: return "event";
                case AgentPendingKind.PursueMonster: return "monster";
                case AgentPendingKind.Divination: return "divination";
                default: return "unknown";
            }
        }

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
