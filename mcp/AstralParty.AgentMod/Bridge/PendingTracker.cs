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
