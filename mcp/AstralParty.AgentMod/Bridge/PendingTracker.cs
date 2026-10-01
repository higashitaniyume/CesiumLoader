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
            }
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
                default: return "unknown";
            }
        }

        private void SetWindow(Window w)
        {
            if (w.PlayerId == 0) return;
            // 没有 sn 的窗口无法应答(SDK 的 Info.Sn 必须填对): 宁可不上报, 也不给 agent 一个点了没用的窗口
            if (w.Sn == 0) return;
            lock (_lock)
            {
                // 同一个窗口被服务器重复广播(同 Sn): 保留最早的出现时刻, 便于算"已经等了多久"
                if (_window != null && _window.Kind == w.Kind && w.Sn != 0 && _window.Sn == w.Sn) return;
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
