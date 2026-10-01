using System;
using System.Collections.Generic;
using System.Text;
using AstralParty.Agent;
using CesiumLoader.SDK;
using Core.Net;
using party.protocol;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 原始动作流的观察者: 订阅 SDK 的 <see cref="GameEvents.RawAction"/>, 把每条动作
    /// 记进 actions.jsonl, 并把其中与"待响应窗口"有关的动作喂给 <see cref="PendingTracker"/>。
    ///
    /// 为什么用 SDK 的事件而不是自己包装 RPC 回调: GameEvents 每秒会重挂回调,
    /// 两个 mod 各自包装同一条回调链会让包装层层套娃(历史上踩过), 所以原始动作流
    /// 由 SDK 统一提供 —— 见 <c>loader\CesiumLoader.SDK\GameEvents.cs</c> 的 RawAction。
    ///
    /// 解码坐标见 docs/MCP-Agent桥接.md(命令号 → 消息类 → 字段), 全部来自反编译实证。
    /// 线程: 回调在**网络线程**。这里只做纯计算 + 入队(加锁), 不碰 Unity API。
    /// </summary>
    internal sealed class RawActionObserver
    {
        private readonly BridgeJournal _journal;
        private readonly PendingTracker _tracker;
        private readonly bool _logActions;

        /// <summary>我方 playerId 的读取器(不在战斗/房间时可能为 0)。</summary>
        private readonly Func<long> _selfId;

        private bool _subscribed;

        public RawActionObserver(BridgeJournal journal, PendingTracker tracker, Func<long> selfId, bool logActions)
        {
            _journal = journal;
            _tracker = tracker;
            _selfId = selfId;
            _logActions = logActions;
        }

        public void Subscribe()
        {
            if (_subscribed) return;
            try
            {
                GameEvents.RawAction += OnRawAction;
                _subscribed = true;
            }
            catch { }
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            try { GameEvents.RawAction -= OnRawAction; } catch { }
            _subscribed = false;
        }

        private void OnRawAction(RawActionEvent e)
        {
            if (e == null) return;
            try
            {
                long now = AgentBridgeLayout.NowMs();
                long self = 0;
                try { self = _selfId != null ? _selfId() : 0; } catch { }
                bool isSelf = self != 0 && e.PlayerId == self;

                string decoded = Decode(e, isSelf, now);

                if (_logActions)
                {
                    _journal.AddAction(new AgentActionRecord
                    {
                        AtMs = now,
                        Id = e.Id,
                        Sn = e.Sn,
                        PlayerId = e.PlayerId,
                        IsSelf = isSelf,
                        Len = e.Data == null ? 0 : e.Data.Length,
                        Decoded = decoded
                    });
                }
            }
            catch { }
        }

        /// <summary>
        /// 按动作 id 解码(已知类型才解, 其余留空只记长度)。
        /// 解出来的信息同时喂给 pending 状态机 —— 这是"服务器在等我做什么"的主要来源之一。
        /// </summary>
        private string Decode(RawActionEvent e, bool isSelf, long now)
        {
            if (e.Data == null || e.Data.Length == 0) return null;
            try
            {
                switch (e.Id)
                {
                    case 5021: // 掷骰
                        {
                            var d = ByteBuf.ReadObject<ThrowDiceC2S>(e.Data);
                            if (d == null) return null;
                            return "ThrowDice{dev=" + d.DevPoint + ", noOper=" + d.IsNoOper +
                                   ", moveNow=" + d.IsMoveNow + "}";
                        }

                    case 5027: // 移动: Direction != 0 = 某人已经决定走哪(含我自己发出去的); == 0 = 服务器问"该你走了"
                        {
                            var d = ByteBuf.ReadObject<MoveC2S>(e.Data);
                            if (d == null) return null;
                            if (d.Direction != 0)
                            {
                                _tracker.OnMoved(e.PlayerId, now);
                                return "Move{landId=" + d.Direction + ", force=" + d.ForceDir + "}";
                            }
                            _tracker.OnMoveAction(e.PlayerId, e.Sn, now);
                            return "MoveOffer{force=" + d.ForceDir + ", landEffect=" + d.LandEffect + "}";
                        }

                    case 5029: // PVP 商店(offer 与"我的回复"共用同一个 Action.Id)
                        {
                            var d = ByteBuf.ReadObject<ShopBuyC2S>(e.Data);
                            if (d == null) return null;
                            var cards = ToList(d.Cards);
                            var already = ToBoolList(d.Alreadys);
                            var buys = ToList(d.BuyCards);
                            if (cards.Count == 0 && buys.Count > 0)
                                return "ShopBuy{buyIndexes=[" + Join(buys) + "]}"; // 我/别人的购买动作, 不改窗口
                            var prices = new int[cards.Count];
                            var soldOut = new bool[cards.Count];
                            var free = new bool[cards.Count];
                            for (int i = 0; i < cards.Count; i++)
                            {
                                prices[i] = d.Gold;
                                soldOut[i] = i < already.Count && already[i];
                                free[i] = d.FreeCardNum > 0 && i < d.FreeCardNum;
                            }
                            _tracker.OnShopCandidates(e.PlayerId, cards.ToArray(), prices, soldOut, free, false, e.Sn, now);
                            return "PVPShop{gold=" + d.Gold + ", freeNum=" + d.FreeCardNum +
                                   ", cards=[" + Join(cards) + "], soldOut=[" + JoinFlags(already) + "]}";
                        }

                    case 5215: // PVE 商店 / ATM
                        {
                            var d = ByteBuf.ReadObject<PVEShopBuyC2S>(e.Data);
                            if (d == null) return null;
                            var cards = ToList(d.Cards);
                            var already = ToBoolList(d.Alreadys);
                            var buys = ToList(d.BuyCards);
                            if (cards.Count == 0 && (buys.Count > 0 || d.AssistPlayer != 0 || d.IsClose))
                                return "PVEShopBuy{buyIndexes=[" + Join(buys) + "], assistPlayer=" + d.AssistPlayer +
                                       ", close=" + d.IsClose + "}";
                            var prices = new int[cards.Count];
                            var soldOut = new bool[cards.Count];
                            var free = new bool[cards.Count];
                            var talentFree = ToBoolList(d.TalentSkillFreeCard);
                            for (int i = 0; i < cards.Count; i++)
                            {
                                int price = d.Gold - d.DisCountGold;
                                if (price < 0) price = 0;
                                prices[i] = price;
                                soldOut[i] = i < already.Count && already[i];
                                free[i] = i < talentFree.Count && talentFree[i];
                            }
                            _tracker.OnShopCandidates(e.PlayerId, cards.ToArray(), prices, soldOut, free, true, e.Sn, now);
                            return "PVEShop{gold=" + d.Gold + ", discount=" + d.DisCountGold +
                                   ", assistGold=" + d.AssistGold + ", cards=[" + Join(cards) + "]}" ;
                        }

                    case 5211: // 筹码三选一候选(Relics 非空 = 服务器给候选)
                        {
                            var d = ByteBuf.ReadObject<SelectRelicC2S>(e.Data);
                            if (d == null) return null;
                            var relics = ToList(d.Relics);
                            if (relics.Count > 0) _tracker.OnRelicCandidates(e.PlayerId, relics, e.Sn, now);
                            return "RelicCandidates{lv=" + d.Lv + ", supLv=" + d.SupLv +
                                   ", reroll=" + d.IsReroll + ", idx=" + d.Idx + ", relics=[" + Join(relics) + "]}";
                        }

                    case 5377: // 奖励卡候选(注意: 不是 5211!)
                        {
                            var d = ByteBuf.ReadObject<SelectRewardCardC2S>(e.Data);
                            if (d == null) return null;
                            var cardIds = ToList(d.CardIds);
                            if (cardIds.Count > 0) _tracker.OnRewardCandidates(e.PlayerId, cardIds, e.Sn, now);
                            return "RewardCandidates{idx=" + d.Idx + ", cardIds=[" + Join(cardIds) + "]}";
                        }

                    case 5249: // 筹码地块购买 offer(Data 带价格)
                        {
                            var d = ByteBuf.ReadObject<BuyRelicC2S>(e.Data);
                            if (d == null) return null;
                            _tracker.OnBuyRelicOffer(e.PlayerId, e.Sn, d.RelicGold, d.DivinationGold, now);
                            return "BuyRelicOffer{relicGold=" + d.RelicGold + ", divinationGold=" + d.DivinationGold +
                                   ", select=" + d.Select + "}";
                        }

                    case 5250: // 筹码地块回执: 窗口关闭(Select 2=买了 / 0=没买)
                        {
                            var d = ByteBuf.ReadObject<BuyRelicS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnBuyRelicDone(e.PlayerId, now);
                            return "BuyRelicResult{select=" + d.Select + ", exit=" + d.Exit + "}";
                        }

                    case 5030: // PVP 商店回执(注意: ShopBuyS2C 没有 IsClose 字段)
                        {
                            var d = ByteBuf.ReadObject<ShopBuyS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnShopDone(e.PlayerId, now);
                            return "PVPShopResult{buyIndexes=[" + Join(ToList(d.BuyCards)) + "]}";
                        }

                    case 5216: // PVE 商店回执
                        {
                            var d = ByteBuf.ReadObject<PVEShopBuyS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnShopDone(e.PlayerId, now);
                            return "PVEShopResult{buyIndexes=[" + Join(ToList(d.BuyCards)) + "], close=" + d.IsClose + "}";
                        }

                    default:
                        return null; // 未知动作: 只记 id/sn/pid/长度, 保留原始可见性
                }
            }
            catch
            {
                return null; // 解码失败不影响记录(仍然有 id/sn/pid/len)
            }
        }

        private static List<int> ToList(IEnumerable<int> src)
        {
            var list = new List<int>();
            if (src == null) return list;
            foreach (var v in src) list.Add(v);
            return list;
        }

        private static List<bool> ToBoolList(IEnumerable<bool> src)
        {
            var list = new List<bool>();
            if (src == null) return list;
            foreach (var v in src) list.Add(v);
            return list;
        }

        private static string Join(List<int> ids)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.ToString();
        }

        private static string JoinFlags(List<bool> flags)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < flags.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(flags[i] ? "1" : "0");
            }
            return sb.ToString();
        }
    }
}
