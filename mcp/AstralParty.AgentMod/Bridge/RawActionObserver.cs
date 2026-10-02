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
                // 双保险: 战斗掷骰的窗口也可能只以 RPC 回执(5038)的形式到达 —— 那条回执在 SDK 里已经
                // 包装成强类型事件, 直接订阅它比"赌 5038 一定会出现在 1002 动作流里"更可靠。
                GameEvents.BattleDice += OnBattleDiceEvent;
                _subscribed = true;
            }
            catch { }
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            try { GameEvents.RawAction -= OnRawAction; } catch { }
            try { GameEvents.BattleDice -= OnBattleDiceEvent; } catch { }
            _subscribed = false;
        }

        /// <summary>战斗掷骰回执(强类型事件, Action&lt;playerId, val&gt;): 关掉战斗骰窗口。</summary>
        private void OnBattleDiceEvent(long playerId, int val)
        {
            try { _tracker.OnBattleDiceDone(playerId, AgentBridgeLayout.NowMs()); } catch { }
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
            // 空负载必须先处理: protobuf 会把"全部字段都是默认值"的消息序列化成 **0 字节**。
            // 5027 MoveC2S 的默认值恰好就是 Direction==0, 也就是"服务器在问该你走哪儿了" ——
            // 如果这里直接 return, "该我移动"的窗口会被整个吞掉(agent 永远等不到 astral_move)。
            // 反向是安全的: "某人已经决定走哪"必然带 Direction != 0, 不可能是空负载。
            if (e.Data == null || e.Data.Length == 0)
            {
                if (e.Id == 5027) // 空负载 = 全默认 = Direction==0 = 该我选落点
                {
                    _tracker.OnMoveAction(e.PlayerId, e.Sn, now);
                    return "MoveOffer{空负载}";
                }
                // 5077 加油站/出生点 与 5213 怪物追击: offer **本身就是零负载**(客户端从不读 Data,
                // 3 局真实回放里 Data 长度恒为 0)。所以"空负载 = 服务器在问要不要/要不要追",
                // 而"有负载 = 某人的答案(Info.Sn 非 0)被回播", 两者必须分开处理, 否则要么漏窗口,
                // 要么把自己的答案当成新窗口重开(回声)。
                if (e.Id == 5077)
                {
                    _tracker.OnStopOrContinueOffer(e.PlayerId, e.Sn, now);
                    return "StopOrContinueOffer{空负载}";
                }
                if (e.Id == 5213)
                {
                    _tracker.OnPursueMonsterOffer(e.PlayerId, e.Sn, now);
                    return "PursueMonsterOffer{空负载}";
                }
                // 5093 医院 / 5233 复活队友 / 5259 机制选择: 这三条 offer 同样是**零负载**
                // (对应窗口只读 action.Sn, 从不 ReadObject), 所以"空负载 = 服务器在问",
                // "有负载 = 某人的答案(Info.Sn 非 0)被回播", 两者分开处理。
                if (e.Id == 5093)
                {
                    _tracker.OnHospitalOffer(e.PlayerId, e.Sn, now);
                    return "HospitalOffer{空负载}";
                }
                if (e.Id == 5233)
                {
                    _tracker.OnReviveTeammateOffer(e.PlayerId, e.Sn, now);
                    return "ReviveTeammateOffer{空负载}";
                }
                if (e.Id == 5259)
                {
                    _tracker.OnSelectMechanismOffer(e.PlayerId, e.Sn, now);
                    return "SelectMechanismOffer{空负载}";
                }
                return null; // 其余 id 的空负载无从判断语义, 只记长度(外层已记)
            }
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

                    // ---------- 战斗攻击骰(5037 / 回执 5038) ----------
                    // 反编译 FightLogic.ReadyFightThrowDice: 客户端**不解 Data**, 只用 action.PlayerId/action.Sn 开窗口,
                    // 所以这里也不解码 —— 有负载反而说明是我自己的决定被回播(那个由"已应答 sn"挡板处理)。
                    case 5037:
                        _tracker.OnBattleDiceOffer(e.PlayerId, e.Sn, now);
                        return "BattleDiceOffer{len=" + e.Data.Length + "}";

                    case 5038:
                        {
                            var d = ByteBuf.ReadObject<BattleThrowDiceS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnBattleDiceDone(e.PlayerId, now);
                            return "BattleDiceResult{playerId=" + d.PlayerId + ", val=" + d.Val + "}";
                        }

                    // ---------- 事件选择(5317 / 回执 5318) ----------
                    // 候选 = SelectEventC2S.Events(应答时必须原样回传), sn = action.Sn。
                    case 5317:
                        {
                            var d = ByteBuf.ReadObject<SelectEventC2S>(e.Data);
                            if (d == null) return null;
                            var events = ToList(d.Events);
                            if (events.Count > 0) _tracker.OnEventCandidates(e.PlayerId, events, e.Sn, now);
                            return "EventCandidates{idx=" + d.Idx + ", events=[" + Join(events) + "]}";
                        }

                    case 5318:
                        {
                            var d = ByteBuf.ReadObject<SelectEventS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnEventSelected(e.PlayerId, now);
                            return "EventResult{playerId=" + d.PlayerId + ", eventId=" + d.EventId + "}";
                        }

                    // ---------- 战斗询问(5047 / 回执 5048) ----------
                    // 反编译 FightLogic.AskFight: Data = AskBattleC2S{AskPlayerId, FightBack}。
                    // FightBack=true = 反击成立, 客户端**自己会立刻以 IsBattle=true 自动应答**(真人没机会选),
                    // 所以这时桥接**绝不能开窗口**(会和客户端抢答同一条 sn)。
                    case 5047:
                        {
                            var d = ByteBuf.ReadObject<AskBattleC2S>(e.Data);
                            if (d == null) return null;
                            if (d.FightBack)
                                return "AskFightAutoAccept{askPlayerId=" + d.AskPlayerId + "}";
                            _tracker.OnAskFightOffer(e.PlayerId, d.AskPlayerId, e.Sn, now);
                            return "AskFight{askPlayerId=" + d.AskPlayerId + "}";
                        }

                    case 5048:
                        {
                            var d = ByteBuf.ReadObject<AskBattleS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnAskFightDone(e.PlayerId, now);
                            return "AskFightResult{playerId=" + d.PlayerId + ", isBattle=" + d.IsBattle + "}";
                        }

                    // ---------- 战斗用牌(5035 / 回执 5036) ----------
                    // 反编译 FightWindow.RefreshPKCard: 客户端只用 action.PlayerId + action.Sn 开窗口, 不读 Data。
                    // 候选由客户端本地算(GetVailCard: 手牌里 EffectType 匹配我方角色的牌), 桥接在 StateProbe 里复刻。
                    case 5035:
                        _tracker.OnFightCardOffer(e.PlayerId, e.Sn, now);
                        return "FightCardOffer{len=" + e.Data.Length + "}";

                    case 5036:
                        {
                            var d = ByteBuf.ReadObject<BattleUseCardS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnFightCardDone(e.PlayerId, now);
                            // 注意: 这条消息里名叫 CardId 的字段装的其实是手牌 Guid(CardId==0 表示"没出牌")
                            return "FightCardResult{playerId=" + d.PlayerId + ", cardUid=" + d.CardId + "}";
                        }

                    // ---------- 战斗闪避选择(5039 / 回执 5040) ----------
                    // 反编译 FightWindow.RefreshDefendReadyChoice: Data = BattleChoiceC2S{NoDodge}。
                    case 5039:
                        {
                            var d = ByteBuf.ReadObject<BattleChoiceC2S>(e.Data);
                            if (d == null) return null;
                            _tracker.OnFightChoiceOffer(e.PlayerId, d.NoDodge, e.Sn, now);
                            return "FightChoice{noDodge=" + d.NoDodge + "}";
                        }

                    case 5040:
                        {
                            var d = ByteBuf.ReadObject<BattleChoiceS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnFightChoiceDone(e.PlayerId, now);
                            return "FightChoiceResult{playerId=" + d.PlayerId + ", val=" + d.Val +
                                   ", dodge=" + d.Dodge + ", existFightBack=" + d.ExistFightBack + "}";
                        }

                    // ---------- 加油站/出生点(5077 / 回执 5078) ----------
                    // 空负载的 5077 是 offer(见上面); 走到这里说明有负载 = 某人的答案(Info.Sn 非 0)被回播。
                    // 这条动作没有独立的消息类型可区分双方, 只能用 Info.Sn 判断 —— 有 sn 就不是新窗口。
                    case 5077:
                        {
                            var d = ByteBuf.ReadObject<StopOrContinueC2S>(e.Data);
                            if (d == null) return null;
                            long answerSn = d.Info != null ? d.Info.Sn : 0;
                            if (answerSn != 0) return "StopOrContinueAnswer{sn=" + answerSn + ", stop=" + d.Stop + "}";
                            // 理论上到不了(offer 是空负载), 但真出现"带负载却没有 sn"就按 offer 处理, 免得漏窗口
                            _tracker.OnStopOrContinueOffer(e.PlayerId, e.Sn, now);
                            return "StopOrContinueOffer{len=" + e.Data.Length + "}";
                        }

                    case 5078:
                        {
                            var d = ByteBuf.ReadObject<StopOrContinueS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnStopOrContinueDone(e.PlayerId, now);
                            return "StopOrContinueResult{playerId=" + d.PlayerId + ", stop=" + d.Stop + "}";
                        }

                    // ---------- 医院(5093 / 回执 5094) ----------
                    case 5093:
                        {
                            var d = ByteBuf.ReadObject<TriggerHospitalC2S>(e.Data);
                            if (d == null) return null;
                            long answerSn = d.Info != null ? d.Info.Sn : 0;
                            if (answerSn != 0) return "HospitalAnswer{sn=" + answerSn + "}";
                            _tracker.OnHospitalOffer(e.PlayerId, e.Sn, now);
                            return "HospitalOffer{len=" + e.Data.Length + "}";
                        }

                    case 5094:
                        {
                            var d = ByteBuf.ReadObject<TriggerHospitalS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnHospitalDone(e.PlayerId, now);
                            return "HospitalResult{playerId=" + d.PlayerId + ", inHospital=" + d.InHospital + "}";
                        }

                    // ---------- 复活队友(5233 / 回执 5234) ----------
                    case 5233:
                        {
                            var d = ByteBuf.ReadObject<AskReviveTeammateC2S>(e.Data);
                            if (d == null) return null;
                            long answerSn = d.Info != null ? d.Info.Sn : 0;
                            if (answerSn != 0) return "ReviveTeammateAnswer{sn=" + answerSn + ", revive=" + d.IsRevive + "}";
                            _tracker.OnReviveTeammateOffer(e.PlayerId, e.Sn, now);
                            return "ReviveTeammateOffer{len=" + e.Data.Length + "}";
                        }

                    case 5234:
                        {
                            var d = ByteBuf.ReadObject<AskReviveTeammateS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnReviveTeammateDone(e.PlayerId, now);
                            return "ReviveTeammateResult{playerId=" + d.PlayerId + ", revive=" + d.IsRevive + "}";
                        }

                    // ---------- 机制选择(5259 / 回执 5260) ----------
                    case 5259:
                        {
                            var d = ByteBuf.ReadObject<SelectMechanismC2S>(e.Data);
                            if (d == null) return null;
                            long answerSn = d.Info != null ? d.Info.Sn : 0;
                            if (answerSn != 0) return "SelectMechanismAnswer{sn=" + answerSn + ", select=" + d.Select + "}";
                            _tracker.OnSelectMechanismOffer(e.PlayerId, e.Sn, now);
                            return "SelectMechanismOffer{len=" + e.Data.Length + "}";
                        }

                    case 5260:
                        {
                            var d = ByteBuf.ReadObject<SelectMechanismS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnSelectMechanismDone(e.PlayerId, now);
                            return "SelectMechanismResult{playerId=" + d.PlayerId + ", select=" + d.Select + "}";
                        }

                    // ---------- 怪物追击(5213 / 回执 5214) ----------
                    case 5213:
                        {
                            var d = ByteBuf.ReadObject<MonsterPursuitC2S>(e.Data);
                            if (d == null) return null;
                            long answerSn = d.Info != null ? d.Info.Sn : 0;
                            if (answerSn != 0) return "PursueMonsterAnswer{sn=" + answerSn + ", selectId=" + d.SelectId + "}";
                            _tracker.OnPursueMonsterOffer(e.PlayerId, e.Sn, now);
                            return "PursueMonsterOffer{len=" + e.Data.Length + "}";
                        }

                    case 5214:
                        {
                            var d = ByteBuf.ReadObject<MonsterPursuitS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnPursueMonsterDone(e.PlayerId, now);
                            return "PursueMonsterResult{playerId=" + d.PlayerId + ", nodeId=" + d.NodeId +
                                   ", exit=" + d.Exit + "}";
                        }

                    // ---------- 商人买卡(5323 / 回执 5324) ----------
                    // offer 与答案**是同一个消息类**: offer 只有 CardId+Gold(没有 Info),
                    // 答案是 Info.Sn + IsBuy(不回填 CardId/Gold) —— 所以用 Info.Sn 是否为 0 区分。
                    case 5323:
                        {
                            var d = ByteBuf.ReadObject<VendorBuyCardC2S>(e.Data);
                            if (d == null) return null;
                            long offerSn = d.Info != null ? d.Info.Sn : 0;
                            if (offerSn != 0)
                                return "VendorBuyAnswer{sn=" + offerSn + ", isBuy=" + d.IsBuy + "}";
                            _tracker.OnVendorCardOffer(e.PlayerId, d.CardId, d.Gold, e.Sn, now);
                            return "VendorBuyOffer{cardId=" + d.CardId + ", gold=" + d.Gold + "}";
                        }

                    case 5324:
                        {
                            var d = ByteBuf.ReadObject<VendorBuyCardS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnVendorCardDone(e.PlayerId, now);
                            return "VendorBuyResult{playerId=" + d.PlayerId + ", isBuy=" + d.IsBuy + "}";
                        }

                    // ---------- 控制移动卡选点(5067 / 回执 5068) ----------
                    case 5067:
                        {
                            var d = ByteBuf.ReadObject<ThrowDiceResultC2S>(e.Data);
                            if (d == null) return null;
                            long offerSn = d.Info != null ? d.Info.Sn : 0;
                            if (offerSn != 0)
                                return "SelectPointAnswer{sn=" + offerSn + ", point=" + d.Point + "}";
                            _tracker.OnSelectPointOffer(e.PlayerId, d.MaxPoint, e.Sn, now);
                            return "SelectPointOffer{maxPoint=" + d.MaxPoint + "}";
                        }

                    // ---------- 炮台选目标(5063 / 回执 5064) ----------
                    // offer 与答案**是同一个消息类**(和 5323/5067 一样): offer 有 LandType(=11)+TargetNum+CanTargetIds,
                    // 答案是 Info.Sn + TargetIds(或 Exit=true) —— 用 Info.Sn 是否为 0 区分。
                    // 只有 LandType==11 才是炮台(客户端 DealLand_LandChoiceTarget 里只处理 11)。
                    case 5063:
                        {
                            var d = ByteBuf.ReadObject<LandChoiceTargetC2S>(e.Data);
                            if (d == null) return null;
                            long offerSn = d.Info != null ? d.Info.Sn : 0;
                            if (offerSn != 0)
                                return "BatteryPickAnswer{sn=" + offerSn + ", targets=" + d.TargetIds.Count +
                                       ", exit=" + d.Exit + "}";
                            if (d.LandType != 11)
                                return "LandChoiceTargetOffer{landType=" + d.LandType + "(非炮台, 忽略)}";

                            long[] cands;
                            var map = d.CanTargetIds;
                            if (!GameProbe.TryBatteryTargets(id => map.TryGetValue(id, out bool v) && v, out cands))
                            {
                                // 战斗数据还没就绪: 退回协议里给的 id(至少不丢候选; 客户端也会按 battle 过滤, 只是此刻读不到)
                                var keys = new List<long>();
                                foreach (var kv in map) if (kv.Value) keys.Add(kv.Key);
                                cands = keys.ToArray();
                            }
                            _tracker.OnBatteryOffer(e.PlayerId, d.TargetNum, cands, e.Sn, now);
                            return "BatteryPickOffer{targetNum=" + d.TargetNum + ", candidates=" + cands.Length + "}";
                        }

                    case 5064:
                        {
                            var d = ByteBuf.ReadObject<LandChoiceTargetS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnBatteryDone(e.PlayerId, now);
                            return "BatteryPickResult{playerId=" + d.PlayerId + ", targets=" + d.TargetIds.Count +
                                   ", exit=" + d.Exit + "}";
                        }

                    case 5068:
                        {
                            var d = ByteBuf.ReadObject<ThrowDiceResultS2C>(e.Data);
                            if (d == null) return null;
                            _tracker.OnSelectPointDone(e.PlayerId, now);
                            return "SelectPointResult{playerId=" + d.PlayerId + ", maxPoint=" + d.MaxPoint +
                                   ", point=" + d.Point + "}";
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
