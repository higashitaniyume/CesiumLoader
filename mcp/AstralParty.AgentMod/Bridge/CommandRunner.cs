using System;
using System.Collections.Generic;
using System.Text;
using AstralParty.Agent;
using CesiumLoader.SDK;
using Core.Net;
using GameLogic;
using party.model;
using party.protocol;
using Tools;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 命令执行: 把外部 MCP server 下发的命令翻译成 SDK 的 <see cref="GameActions"/> 调用,
    /// 以及 GameActions 没封装的那几个窗口(商店 / ATM / 筹码地块)的直接 RPC 调用。
    ///
    /// 位置很关键: 全部方法都必须在**主线程**调用(由 mod 的 OnUpdate 驱动),
    /// 与玩家手动点 UI 走同一条 C2S 链路, 服务器按正常规则处理。
    ///
    /// sn 的解析(重点): SDK 的 GameActions 在不给 sn 时只认 <c>ActionLogic.throwDiceSn</c>,
    /// 而移动(5027)/商店(5029/5215)/筹码三选一(5211)/奖励卡(5377)/买筹码(5249) 的 sn 在别处。
    /// 所以每个工具都按**它自己的窗口类型**去 <see cref="PendingTracker"/> 取 sn, agent 也可以显式给 sn 覆盖。
    ///
    /// 发送成功后会取消该 sn 的操作倒计时: 游戏给每个窗口都挂了"超时自动代打"回调
    /// (选第一项 / 空购买离店 / 不买离开), 不取消的话到点会自动再发一次。
    ///
    /// 开关语义(谁也别绕开):
    ///   enableActions=false → 一切动作命令 rejected(只读模式);
    ///   pauseActions=true   → 一切动作命令 paused(急停);
    ///   dryRun=true         → 走完解析但不发送(dry_run)。
    /// </summary>
    internal sealed class CommandRunner
    {
        private readonly BridgeSettings _settings;
        private readonly PendingTracker _tracker;
        private readonly Func<long> _selfId;

        public CommandRunner(BridgeSettings settings, PendingTracker tracker, Func<long> selfId)
        {
            _settings = settings;
            _tracker = tracker;
            _selfId = selfId;
        }

        public BridgeResult Execute(BridgeCommand cmd)
        {
            long started = AgentBridgeLayout.NowMs();

            // 只读/急停/演练: 一律不发送
            if (!_settings.EnableActions)
                return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.Rejected,
                    "桥接处于只读模式 (mods\\AstralParty.AgentMod\\config.json 里 enableActions=false)");
            if (_settings.PauseActions)
                return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.Paused,
                    "急停生效中 (agent\\control.json 的 PauseActions=true); 解除后用 astral_resume");
            if (_settings.DryRun)
                return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.DryRun,
                    "演练模式 (DryRun=true): 命令已解析但未发送");

            try
            {
                switch (cmd.Tool)
                {
                    case AgentBridgeLayout.Tool.Ping:
                        return Done(cmd, started, "pong");

                    case AgentBridgeLayout.Tool.ThrowDice:
                        return ThrowDice(cmd, started);

                    case AgentBridgeLayout.Tool.Move:
                        return Move(cmd, started);

                    case AgentBridgeLayout.Tool.UseCard:
                        return UseCard(cmd, started);

                    case AgentBridgeLayout.Tool.AskBattle:
                        return AskBattle(cmd, started);

                    case AgentBridgeLayout.Tool.BattleChoice:
                        return BattleChoice(cmd, started);

                    case AgentBridgeLayout.Tool.UseEffectCard:
                        return UseEffectCard(cmd, started);

                    case AgentBridgeLayout.Tool.UseQuickCard:
                        return UseQuickCard(cmd, started);

                    case AgentBridgeLayout.Tool.AbandonCard:
                        return AbandonCard(cmd, started);

                    case AgentBridgeLayout.Tool.SelectRelic:
                        return SelectRelic(cmd, started);

                    case AgentBridgeLayout.Tool.SelectRewardCard:
                        return SelectRewardCard(cmd, started);
                    case AgentBridgeLayout.Tool.SelectEvent:
                        return SelectEvent(cmd, started);

                    case AgentBridgeLayout.Tool.ShopBuy:
                        return ShopBuy(cmd, started);

                    case AgentBridgeLayout.Tool.AtmTransfer:
                        return AtmTransfer(cmd, started);

                    case AgentBridgeLayout.Tool.BuyRelic:
                        return BuyRelic(cmd, started);

                    case AgentBridgeLayout.Tool.Speed:
                        return Speed(cmd, started);

                    case AgentBridgeLayout.Tool.StopOrContinue:
                        return StopOrContinue(cmd, started);

                    case AgentBridgeLayout.Tool.PursueMonster:
                        return PursueMonster(cmd, started);

                    case AgentBridgeLayout.Tool.VendorBuyCard:
                        return VendorBuyCard(cmd, started);

                    case AgentBridgeLayout.Tool.SelectPoint:
                        return SelectPoint(cmd, started);

                    case AgentBridgeLayout.Tool.ReviveTeammate:
                        return ReviveTeammate(cmd, started);

                    case AgentBridgeLayout.Tool.SelectMechanism:
                        return SelectMechanism(cmd, started);

                    case AgentBridgeLayout.Tool.HospitalCheck:
                        return HospitalCheck(cmd, started);

                    case AgentBridgeLayout.Tool.BatteryPick:
                        return BatteryPick(cmd, started);

                    case AgentBridgeLayout.Tool.DivinationPick:
                        return DivinationPick(cmd, started);

                    case AgentBridgeLayout.Tool.GambleGuess:
                        return GambleGuess(cmd, started);

                    case AgentBridgeLayout.Tool.GambleDice:
                        return GambleDice(cmd, started);

                    default:
                        return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.UnknownTool,
                            "未知工具: " + cmd.Tool);
                }
            }
            catch (Exception e)
            {
                return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.Exception,
                    e.GetType().Name + ": " + e.Message);
            }
        }

        // ============================== 各工具 ==============================

        private BridgeResult ThrowDice(BridgeCommand cmd, long started)
        {
            bool battle = cmd.GetBool("battle", false);
            bool noOper = cmd.GetBool("noOper", false);
            bool moveNow = cmd.GetBool("moveNow", false);

            // 战斗攻击骰(5037)与普通回合投骰(5021)的 sn **不是同一个来源**:
            //   普通投骰 → SDK 默认的 ActionLogic.throwDiceSn
            //   战斗攻击 → 5037 那条 action 自己的 Sn(桥接里的 battleDice 窗口)
            // 拿错会被服务器当成过期/非法 sn 拒掉, 所以这里按窗口类型分开解析。
            long? sn = battle ? ResolveSn(cmd, AgentPendingKind.BattleDice) : ResolveSn(cmd, null);
            if (battle && !sn.HasValue)
                return BadArgs(cmd, "现在没有战斗掷骰窗口(5037): 战斗攻击骰的 sn 只有服务器推 5037 时才有。" +
                                    "先用 astral_pending 看有没有 kind=battleDice");

            long usedSn = sn.HasValue ? sn.Value : SafeCurrentSn();

            bool ok = battle
                ? GameActions.BattleThrowDice(sn)
                : GameActions.ThrowDice(noOper, moveNow, sn);

            if (!ok) return NotSent(cmd, battle ? "战斗投骰" : "投骰子");
            OnSent(usedSn);
            return Done(cmd, started, battle
                ? "已发送 战斗投骰 BattleThrowDice(sn=" + usedSn + ")"
                : "已发送 投骰 ThrowDice(noOper=" + noOper + ", moveNow=" + moveNow + ", sn=" + usedSn + ")");
        }

        private BridgeResult Move(BridgeCommand cmd, long started)
        {
            int landId = cmd.GetInt("landId", 0);
            if (landId == 0) return BadArgs(cmd, "缺少 landId(目标地块 id; 从 pending 的候选里取)");

            long? sn = ResolveSn(cmd, AgentPendingKind.Move);
            if (!GameActions.Move(landId, sn)) return NotSent(cmd, "移动");
            OnSent(sn);
            return Done(cmd, started, "已发送 移动 Move(landId=" + landId + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 战斗出牌(5035 窗口)。两种用法:
        ///   cardId = astral_pending 里 kind=fightCard 的候选 id(**就是手牌 Guid**), 或
        ///   pass:true = 这一轮不出牌(与客户端点"结束出牌"/超时同一条路径, CardUid=0)。
        /// 反编译证据: 客户端拖牌出的是 <c>RequestBattleUseCardC2S(sn, item.CardData.Guid)</c>,
        /// 而 <c>BattleUseCardC2S.CardUid</c> 收的就是它 —— 填卡牌配置 CardId 无效。
        /// </summary>
        private BridgeResult UseCard(BridgeCommand cmd, long started)
        {
            long? sn = ResolveSn(cmd, AgentPendingKind.FightCard);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有战斗出牌窗口(5035): sn 只有服务器推 5035 时才有。" +
                                    "先用 astral_pending 看有没有 kind=fightCard");

            if (cmd.GetBool("pass", false))
            {
                if (!GameActions.UseCard(0, sn)) return NotSent(cmd, "战斗出牌(不出牌)");
                OnSent(sn);
                return Done(cmd, started, "已发送 战斗出牌 UseCard(cardUid=0 不出牌, sn=" + Show(sn) + ")");
            }

            int cardUid;
            if (!ResolveCardUid(cmd, out cardUid))
                return BadArgs(cmd, "缺少 cardId(战斗出牌要的是**手牌 Guid**, 也就是 astral_pending 候选里的 id," +
                                    "不是卡牌配置 id; 想不出牌就给 pass:true)");

            if (!GameActions.UseCard(cardUid, sn)) return NotSent(cmd, "战斗出牌");
            OnSent(sn);
            return Done(cmd, started, "已发送 战斗出牌 UseCard(cardUid=" + cardUid + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 战斗询问(5047 窗口): accept=true 打, false 不打。
        /// 反编译证据: 客户端"打"= btn_PK → IsBattle=true, "不打"= btn_Leave → IsBattle=false,
        /// 且超时回调点的就是 btn_Leave —— 所以**不答 = 不打**。
        /// </summary>
        private BridgeResult AskBattle(BridgeCommand cmd, long started)
        {
            if (!cmd.Has("accept"))
                return BadArgs(cmd, "缺少 accept(true=接受战斗 / false=不打)");

            long? sn = ResolveSn(cmd, AgentPendingKind.AskFight);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有战斗询问窗口(5047): sn 只有服务器推 5047 时才有。" +
                                    "先用 astral_pending 看有没有 kind=askFight");

            bool accept = cmd.GetBool("accept", true);
            if (!GameActions.AskBattle(accept, sn)) return NotSent(cmd, "战斗询问应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 战斗询问 AskBattle(isBattle=" + accept + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 战斗闪避选择(5039 窗口): dodge=true 闪避, false 硬吃。
        /// <c>NoDodge=true</c> 时客户端会**直接拒绝**闪避请求(只弹提示、不发包), 所以这里也挡住,
        /// 免得 agent 以为闪了其实什么都没发生。
        /// </summary>
        private BridgeResult BattleChoice(BridgeCommand cmd, long started)
        {
            bool dodge = cmd.GetBool("dodge", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.FightChoice);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有闪避选择窗口(5039): sn 只有服务器推 5039 时才有。" +
                                    "先用 astral_pending 看有没有 kind=fightChoice");

            bool noDodge;
            if (dodge && _tracker.TryGetNoDodge(out noDodge) && noDodge)
                return BadArgs(cmd, "这一击不能闪避(服务器下发 NoDodge=true, 客户端会直接拒绝闪避请求); 只能 dodge=false");

            if (!GameActions.BattleChoice(dodge, sn)) return NotSent(cmd, "闪避选择");
            OnSent(sn);
            return Done(cmd, started, "已发送 闪避选择 BattleChoice(dodge=" + dodge + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 战斗出牌的目标值是**手牌 Guid**。允许三种给法:
        ///   cardUid/guid/cardGuid = 直接就是 Guid; cardId = 候选里的 id(先按 Guid 认, 认不出再当配置 id 反查第一张同配置手牌)。
        /// </summary>
        private bool ResolveCardUid(BridgeCommand cmd, out int cardUid)
        {
            cardUid = 0;
            if (cmd.Has("cardUid")) { cardUid = cmd.GetInt("cardUid", 0); if (cardUid != 0) return true; }
            if (cmd.Has("guid")) { cardUid = cmd.GetInt("guid", 0); if (cardUid != 0) return true; }
            if (cmd.Has("cardGuid")) { cardUid = cmd.GetInt("cardGuid", 0); if (cardUid != 0) return true; }

            int given = cmd.GetInt("cardId", 0);
            if (given == 0) return false;

            long self = 0;
            try { self = _selfId != null ? _selfId() : 0; } catch { }

            // 候选里的 id 就是 Guid: 只要这张牌确实在我手上, 直接用它
            try { if (Players.HandHasGuid(self, given)) { cardUid = given; return true; } } catch { }

            // 退路: 给的是卡牌配置 id → 反查第一张同配置手牌
            int guid = 0;
            try { guid = Players.ResolveCardIdToGuid(self, given); } catch { }
            if (guid != 0) { cardUid = guid; return true; }
            return false;
        }

        private BridgeResult UseEffectCard(BridgeCommand cmd, long started)
        {
            int cardId;
            if (!ResolveCard(cmd, out cardId)) return BadArgs(cmd, "缺少 cardId(手牌 CardId; 也可给 cardGuid)");

            List<long> targetIds = null;
            if (cmd.Has("targetIds"))
            {
                targetIds = new List<long>();
                foreach (int v in cmd.GetIntList("targetIds")) targetIds.Add(v);
            }
            List<int> landIds = cmd.Has("landIds") ? cmd.GetIntList("landIds") : null;
            int effectIndex = cmd.GetInt("effectIndex", 0);

            long? sn = ResolveSn(cmd, AgentPendingKind.CardChoice);
            if (!GameActions.UseEffectCard(cardId, targetIds, landIds, effectIndex, sn)) return NotSent(cmd, "效果牌");
            OnSent(sn);
            return Done(cmd, started, "已发送 效果牌 UseEffectCard(cardId=" + cardId +
                ", targets=[" + Join(targetIds) + "], lands=[" + Join(landIds) + "], effect=" + effectIndex +
                ", sn=" + Show(sn) + ")");
        }

        private BridgeResult UseQuickCard(BridgeCommand cmd, long started)
        {
            int cardId;
            if (!ResolveCard(cmd, out cardId)) return BadArgs(cmd, "缺少 cardId(手牌 CardId; 也可给 cardGuid)");
            long targetId = cmd.GetLong("targetId", 0);
            if (targetId == 0) return BadArgs(cmd, "缺少 targetId(被跟的玩家 id)");

            long? sn = ResolveSn(cmd, AgentPendingKind.CardChoice);
            if (!GameActions.UseQuickCard(cardId, targetId, sn)) return NotSent(cmd, "跟牌");
            OnSent(sn);
            return Done(cmd, started, "已发送 跟牌 UseQuickCard(cardId=" + cardId +
                ", targetId=" + targetId + ", sn=" + Show(sn) + ")");
        }

        private BridgeResult AbandonCard(BridgeCommand cmd, long started)
        {
            var cardIds = cmd.GetIntList("cardIds");
            if (cardIds.Count == 0)
            {
                int one;
                if (!ResolveCard(cmd, out one)) return BadArgs(cmd, "缺少 cardIds(要弃的 CardId 列表)或 cardId");
                cardIds.Add(one);
            }

            long? sn = ResolveSn(cmd, AgentPendingKind.CardChoice);
            if (!GameActions.AbandonCards(cardIds, sn)) return NotSent(cmd, "弃牌");
            OnSent(sn);
            return Done(cmd, started, "已发送 弃牌 AbandonCards([" + Join(cardIds) + "], sn=" + Show(sn) + ")");
        }

        private BridgeResult SelectRelic(BridgeCommand cmd, long started)
        {
            var ids = cmd.GetIntList("relicIds");
            int index = cmd.GetInt("index", -1);
            bool reroll = cmd.GetBool("reroll", false);

            // 重摇: 与选择同一条消息(SelectRelicC2S.IsReroll=true), 不需要候选, 也不该带 index。
            // 反编译 RelicLogic.RequestResetRelic 证实客户端只填 Info + IsReroll。
            // 注意窗口**不会**因此关闭: 服务器随后会推一组新候选(新 sn)。
            if (reroll)
            {
                long? rerollSn = ResolveSn(cmd, AgentPendingKind.SelectRelic);
                if (!GameActions.RerollRelic(rerollSn)) return NotSent(cmd, "重摇筹码");
                OnSent(rerollSn);
                return Done(cmd, started, "已发送 重摇筹码 RerollRelic(IsReroll=true, sn=" + Show(rerollSn) + "); " +
                    "窗口未结束, 等服务器推新的一组候选");
            }

            if (ids.Count == 0 && cmd.Has("relicId"))
            {
                ids.Add(cmd.GetInt("relicId", 0));
                if (index < 0) index = 0;
            }

            if (ids.Count == 0)
            {
                // 没给候选: 用当前窗口的候选(agent 只报 index 也能选)
                int[] window;
                if (_tracker.TryGetWindowIds(AgentPendingKind.SelectRelic, out window) && window != null && window.Length > 0)
                {
                    foreach (int v in window) ids.Add(v);
                    if (index < 0) index = 0;
                }
            }

            if (ids.Count == 0) return BadArgs(cmd, "没有候选筹码: 请带 relicIds, 或等 RelicCandidates 事件后再用 index");
            if (index < 0) index = 0;
            if (index >= ids.Count) return BadArgs(cmd, "index=" + index + " 超出候选范围(共 " + ids.Count + " 个)");

            long? sn = ResolveSn(cmd, AgentPendingKind.SelectRelic);
            if (!GameActions.SelectRelic(ids, index, sn)) return NotSent(cmd, "选筹码");
            OnSent(sn);
            return Done(cmd, started, "已发送 选筹码 SelectRelic(relics=[" + Join(ids) + "], index=" + index +
                ", relicId=" + ids[index] + ", sn=" + Show(sn) + ")");
        }

        private BridgeResult SelectRewardCard(BridgeCommand cmd, long started)
        {
            var ids = cmd.GetIntList("cardIds");
            int index = cmd.GetInt("index", -1);

            if (ids.Count == 0)
            {
                int[] window;
                if (_tracker.TryGetWindowIds(AgentPendingKind.RewardCard, out window) && window != null && window.Length > 0)
                {
                    foreach (int v in window) ids.Add(v);
                    if (index < 0) index = 0;
                }
            }

            if (ids.Count == 0) return BadArgs(cmd, "没有候选奖励卡: 请带 cardIds, 或等奖励卡候选事件后再用 index");
            if (index < 0) index = 0;
            if (index >= ids.Count) return BadArgs(cmd, "index=" + index + " 超出候选范围(共 " + ids.Count + " 个)");

            long? sn = ResolveSn(cmd, AgentPendingKind.RewardCard);
            if (!GameActions.SelectRewardCard(ids, index, sn)) return NotSent(cmd, "选奖励卡");
            OnSent(sn);
            return Done(cmd, started, "已发送 选奖励卡 SelectRewardCard(cards=[" + Join(ids) + "], index=" + index +
                ", cardId=" + ids[index] + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 棋盘事件选择(cmd 5317)。候选来自服务器那条 SelectEventC2S.Events —— 反编译
        /// UI.LandEventWindow.ShowSkill10202 证实客户端会把候选列表**原样回传**, 只改 Idx 与 Info.Sn,
        /// 所以这里必须带完整候选(不能只发下标)。
        /// 不选的话服务器超时会代选第 0 项。
        /// </summary>
        private BridgeResult SelectEvent(BridgeCommand cmd, long started)
        {
            int index = cmd.GetInt("index", -1);
            int eventId = cmd.GetInt("eventId", 0);

            int[] window;
            if (!_tracker.TryGetWindowIds(AgentPendingKind.SelectEvent, out window) || window == null || window.Length == 0)
                return BadArgs(cmd, "当前没有事件选择窗口(5317): 候选只有服务器推 5317 时才有。" +
                                    "先用 astral_pending 看有没有 kind=selectEvent");

            if (index < 0 && eventId != 0)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    if (window[i] == eventId) { index = i; break; }
                }
                if (index < 0)
                    return BadArgs(cmd, "eventId=" + eventId + " 不在本次候选里: [" + Join(window) + "]");
            }
            if (index < 0) index = 0; // 与游戏一致: 没选就是第 0 项
            if (index >= window.Length)
                return BadArgs(cmd, "index=" + index + " 超出候选范围(共 " + window.Length + " 个)");

            long? sn = ResolveSn(cmd, AgentPendingKind.SelectEvent);
            if (!GameActions.SelectEvent(window, index, sn)) return NotSent(cmd, "事件选择");
            OnSent(sn);
            return Done(cmd, started, "已发送 事件选择 SelectEvent(events=[" + Join(window) + "], index=" + index +
                ", eventId=" + window[index] + ", sn=" + Show(sn) + ")");
        }

        // ============================== 商店 / ATM / 筹码地块(GameActions 未封装, 直接走 RPC) ==============================

        /// <summary>
        /// 卡牌商店购买 / 离店。indexes 是**槽位下标**(不是卡牌 id), 空 = 空手离店。
        /// PVP(5029) 与 PVE(5215) 用的消息类不同, 类型由当前商店窗口决定。
        /// </summary>
        private BridgeResult ShopBuy(BridgeCommand cmd, long started)
        {
            bool pve;
            int slots;
            if (!_tracker.TryGetShopWindow(out pve, out slots))
                return BadArgs(cmd, "当前没有商店窗口(服务器还没把商店推给我, 或用 astral_pending 看一下)");

            var indexes = cmd.GetIntList("indexes");
            if (indexes.Count == 0 && cmd.Has("index")) indexes.Add(cmd.GetInt("index", 0));
            foreach (int i in indexes)
            {
                if (i < 0 || (slots > 0 && i >= slots))
                    return BadArgs(cmd, "槽位下标 " + i + " 越界(本店共 " + slots + " 格)");
            }

            long? sn = ResolveSn(cmd, AgentPendingKind.Shop);
            long useSn = sn.HasValue ? sn.Value : 0;
            bool close = indexes.Count == 0;

            var net = MonoSingletonProvider<NetManager>.inst;
            if (net == null || net.RPC == null) return NotSent(cmd, "商店购买(网络未就绪)");

            if (pve)
            {
                var req = new PVEShopBuyC2S { Info = MakeInfo(useSn), AssistPlayer = 0, IsClose = close };
                foreach (int i in indexes) req.BuyCards.Add(i);
                net.RPC.PVEShopBuyC2S.PVEShopBuyC2SCall(req);
            }
            else
            {
                var req = new ShopBuyC2S { Info = MakeInfo(useSn) };
                foreach (int i in indexes) req.BuyCards.Add(i);
                net.RPC.ShopBuyC2S.ShopBuyC2SCall(req);
            }

            OnSent(sn);
            var mode = pve ? "PVE" : "PVP";
            return Done(cmd, started, close
                ? "已发送 " + mode + " 商店离店(空手), sn=" + useSn
                : "已发送 " + mode + " 商店购买 BuyCards=[" + Join(indexes) + "], sn=" + useSn);
        }

        /// <summary>ATM 转账: PVE 商店里的"给队友转 5 星币", 复用 PVEShopBuyC2S(AssistPlayer=目标)。</summary>
        private BridgeResult AtmTransfer(BridgeCommand cmd, long started)
        {
            long targetId = cmd.GetLong("targetId", 0);
            if (targetId == 0) return BadArgs(cmd, "缺少 targetId(要转给哪个队友)");

            bool pve;
            int slots;
            if (!_tracker.TryGetShopWindow(out pve, out slots))
                return BadArgs(cmd, "当前没有商店窗口, ATM 转账只能在 PVE 商店里做");
            if (!pve) return BadArgs(cmd, "这是 PVP 商店(5029): 没有 ATM 转账, 只能买/离店");

            long? sn = ResolveSn(cmd, AgentPendingKind.Shop);
            long useSn = sn.HasValue ? sn.Value : 0;

            var net = MonoSingletonProvider<NetManager>.inst;
            if (net == null || net.RPC == null) return NotSent(cmd, "ATM 转账(网络未就绪)");

            var req = new PVEShopBuyC2S { Info = MakeInfo(useSn), AssistPlayer = targetId, IsClose = false };
            net.RPC.PVEShopBuyC2S.PVEShopBuyC2SCall(req);

            // 转账不结束商店窗口(还能继续买), 所以不取消倒计时
            return Done(cmd, started, "已发送 ATM 转账 AssistPlayer=" + targetId + ", sn=" + useSn);
        }

        /// <summary>
        /// 筹码地块: 花星币买下这个筹码, 或放弃离开。
        /// 判别字段是 <c>Select</c>(2=买 / 0=离开) —— **不是** Exit(客户端确认购买时 Exit 也是 true)。
        /// </summary>
        private BridgeResult BuyRelic(BridgeCommand cmd, long started)
        {
            bool confirm = cmd.GetBool("confirm", true);

            long windowSn;
            if (!_tracker.TryGetWindowSn(AgentPendingKind.BuyRelic, out windowSn))
                return BadArgs(cmd, "当前没有筹码地块购买窗口(5249 还没推给我)");

            long? sn = ResolveSn(cmd, AgentPendingKind.BuyRelic);
            long useSn = sn.HasValue ? sn.Value : windowSn;

            var net = MonoSingletonProvider<NetManager>.inst;
            if (net == null || net.RPC == null) return NotSent(cmd, "买筹码(网络未就绪)");

            var req = new BuyRelicC2S
            {
                Info = MakeInfo(useSn),
                Select = confirm ? 2 : 0,
                Exit = true
            };
            net.RPC.BuyRelicC2S.BuyRelicC2SCall(req);

            OnSent(sn);
            return Done(cmd, started, confirm
                ? "已发送 买下筹码 BuyRelic(Select=2), sn=" + useSn
                : "已发送 放弃筹码(离开) BuyRelic(Select=0), sn=" + useSn);
        }

        /// <summary>
        /// 加油站/出生点(5077 窗口): stop=true 就地停留, false 继续走。
        /// 反编译证据: 客户端"继续走"= <c>btn_Continue</c> → <c>Stop=false</c>, "停留"= <c>btn_Stop</c> → <c>Stop=true</c>,
        /// 且超时回调点的就是 <c>btn_Continue</c> —— 所以**不答 = 继续走**。
        /// </summary>
        private BridgeResult StopOrContinue(BridgeCommand cmd, long started)
        {
            bool stop = cmd.GetBool("stop", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.StopOrContinue);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有加油站/出生点窗口(5077): sn 只有服务器推 5077 时才有。" +
                                    "先用 astral_pending 看有没有 kind=stopOrContinue");

            if (!GameActions.StopOrContinue(stop, sn)) return NotSent(cmd, "停留/继续走应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 停留/继续走 StopOrContinue(stop=" + stop + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 复活队友(5233 窗口): revive=true 复活(花星币), false 不复活。
        /// 反编译证据: <c>btn_Stop</c> → <c>IsRevive=true</c>, <c>btn_Continue</c> → <c>IsRevive=false</c>,
        /// 超时回调点的是 <c>btn_Continue</c> —— 所以**不答 = 不复活**。
        /// </summary>
        private BridgeResult ReviveTeammate(BridgeCommand cmd, long started)
        {
            bool revive = cmd.GetBool("revive", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.ReviveTeammate);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有复活队友窗口(5233): sn 只有服务器推 5233 时才有。" +
                                    "先用 astral_pending 看有没有 kind=reviveTeammate");

            if (!GameActions.ReviveTeammate(revive, sn)) return NotSent(cmd, "复活队友应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 复活队友 AskReviveTeammate(revive=" + revive + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 机制选择(5259 窗口): select=true 启动, false 不启动。
        /// 反编译证据: <c>btn_Stop</c>("启动") → <c>Select=true</c>, <c>btn_Continue</c> → <c>Select=false</c>,
        /// 超时回调点的是 <c>btn_Continue</c> —— 所以**不答 = 不启动**。
        /// </summary>
        private BridgeResult SelectMechanism(BridgeCommand cmd, long started)
        {
            bool select = cmd.GetBool("select", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.SelectMechanism);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有机制选择窗口(5259): sn 只有服务器推 5259 时才有。" +
                                    "先用 astral_pending 看有没有 kind=selectMechanism");

            if (!GameActions.SelectMechanism(select, sn)) return NotSent(cmd, "机制选择应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 机制选择 SelectMechanism(select=" + select + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 医院(5093 窗口): 接受检查 —— 这条窗口只有<b>一个</b>合法上行
        /// (<c>TriggerHospitalC2S</c> 里没有"拒绝"字段), 所以工具不带选项。
        /// 反编译证据: 倒计时结束时点的是 <c>btn_check</c> → 服务器超时代答也是"检查"。
        /// </summary>
        private BridgeResult HospitalCheck(BridgeCommand cmd, long started)
        {
            long? sn = ResolveSn(cmd, AgentPendingKind.HospitalCheck);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有医院窗口(5093): sn 只有服务器推 5093 时才有。" +
                                    "先用 astral_pending 看有没有 kind=hospitalCheck");

            if (!GameActions.HospitalCheck(sn)) return NotSent(cmd, "医院检查应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 医院检查 TriggerHospital(sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 怪物追击(5213 窗口): monsterId = 追这只怪(候选里的 playerId), 或 pass=true = 不追。
        /// 候选**不在协议里**(本地过滤出来的), 所以这里要挡住"追一只不在候选里的怪" ——
        /// 那种请求服务器不会接受, agent 却会以为已经追了。
        /// </summary>
        private BridgeResult PursueMonster(BridgeCommand cmd, long started)
        {
            long monsterId = cmd.GetLong("monsterId", 0);
            if (monsterId == 0) monsterId = cmd.GetLong("id", 0);
            bool pass = cmd.GetBool("pass", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.PursueMonster);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有追击窗口(5213): sn 只有服务器推 5213 时才有。" +
                                    "先用 astral_pending 看有没有 kind=pursueMonster");

            if (!pass && monsterId != 0)
            {
                long[] cands;
                if (!_tracker.TryGetMonsterIds(out cands))
                    return BadArgs(cmd, "本地还没算出可追的怪物(候选不在协议里, 是本地按 GetVailPursuitMonster 过滤的); " +
                                        "现在只能 pass=true(不追)");
                bool found = false;
                if (cands != null)
                    for (int i = 0; i < cands.Length; i++) if (cands[i] == monsterId) { found = true; break; }
                if (!found)
                    return BadArgs(cmd, "monsterId=" + monsterId + " 不在候选里(只能追 astral_pending 列出的怪; 候选数=" +
                                        (cands == null ? 0 : cands.Length) + ")");
            }

            long selectId = pass ? 0 : monsterId;
            if (!GameActions.PursueMonster(selectId, sn)) return NotSent(cmd, "怪物追击应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 怪物追击 PursueMonster(selectId=" + selectId + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 商人买卡(5323 窗口): buy=true 花星币买下, false 不买。
        /// 客户端在星币不足时**只弹提示、不发包**, 所以这里也挡住 —— 否则 agent 会以为买到了。
        /// </summary>
        private BridgeResult VendorBuyCard(BridgeCommand cmd, long started)
        {
            bool buy = cmd.GetBool("buy", false);

            long? sn = ResolveSn(cmd, AgentPendingKind.VendorCard);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有商人买卡窗口(5323): sn 只有服务器推 5323 时才有。" +
                                    "先用 astral_pending 看有没有 kind=vendorCard");

            if (buy)
            {
                int price;
                if (_tracker.TryGetVendorPrice(out price) && price > 0)
                {
                    int gold = 0;
                    try
                    {
                        long self = _selfId != null ? _selfId() : 0;
                        if (self != 0) gold = Players.Gold(self);
                    }
                    catch { }
                    if (gold < price)
                        return BadArgs(cmd, "星币不够(有 " + gold + ", 需要 " + price + "): " +
                                            "客户端在这种情况会直接拒绝购买请求, 桥接也不发。");
                }
            }

            if (!GameActions.VendorBuyCard(buy, sn)) return NotSent(cmd, "商人买卡应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 商人买卡 VendorBuyCard(isBuy=" + buy + ", sn=" + Show(sn) + ")");
        }

        /// <summary>控制移动卡选点(5067 窗口): point 必须落在服务器给的 1..MaxPoint。</summary>
        private BridgeResult SelectPoint(BridgeCommand cmd, long started)
        {
            int point = cmd.GetInt("point", 1);

            long? sn = ResolveSn(cmd, AgentPendingKind.SelectPoint);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有控移卡选点窗口(5067): sn 只有服务器推 5067 时才有。" +
                                    "先用 astral_pending 看有没有 kind=selectPoint");

            int max;
            if (_tracker.TryGetMaxPoint(out max) && max > 0)
            {
                if (point < 1 || point > max)
                    return BadArgs(cmd, "point 必须落在 1.." + max + "(服务器给的 MaxPoint), 收到 " + point);
            }
            else if (point < 1)
            {
                return BadArgs(cmd, "point 必须 >= 1");
            }

            if (!GameActions.SelectPoint(point, sn)) return NotSent(cmd, "选点应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 选点 SelectPoint(point=" + point + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 炮台选目标(5063 窗口): targetIds = 选中 1..TargetNum 个英雄(候选里的 playerId), 或 leave=true = 离开。
        /// 候选**在协议里**(offer 的 CanTargetIds), 但客户端还会按 <c>characterType==Hero</c> 与本地 battle 过滤,
        /// 所以这里只接受 tracker 记下的候选 —— 否则 agent 会以为选上了服务器不认的目标。
        /// </summary>
        private BridgeResult BatteryPick(BridgeCommand cmd, long started)
        {
            long? sn = ResolveSn(cmd, AgentPendingKind.BatteryTarget);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有炮台选目标窗口(5063): sn 只有服务器推 5063(LandType=11)时才有。" +
                                    "先用 astral_pending 看有没有 kind=batteryTarget");

            if (cmd.GetBool("leave", false) || cmd.GetBool("exit", false))
            {
                if (!GameActions.BatteryLeave(sn)) return NotSent(cmd, "炮台离开");
                OnSent(sn);
                return Done(cmd, started, "已发送 炮台离开 BatteryLeave(sn=" + Show(sn) + ")");
            }

            long[] picks = cmd.GetLongArray("targetIds");
            if ((picks == null || picks.Length == 0) && cmd.Has("targetId"))
            {
                long one = cmd.GetLong("targetId", 0);
                if (one != 0) picks = new long[] { one };
            }
            if (picks == null || picks.Length == 0)
                return BadArgs(cmd, "需要 targetIds:[英雄playerId,...](1..TargetNum 个)或 leave=true(离开); " +
                                    "候选见 astral_pending(kind=batteryTarget)");

            long[] cands;
            int targetNum;
            if (!_tracker.TryGetBatteryTargets(out cands, out targetNum))
                return BadArgs(cmd, "本地还没拿到炮台候选英雄; 先用 astral_pending 看有没有 kind=batteryTarget");

            if (targetNum > 0 && picks.Length > targetNum)
                return BadArgs(cmd, "最多只能选 " + targetNum + " 个英雄, 收到 " + picks.Length + " 个");

            if (cands == null)
                return BadArgs(cmd, "炮台候选还没读出来(战斗数据未就绪), 此时只能 leave=true");

            for (int i = 0; i < picks.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < cands.Length; j++) if (cands[j] == picks[i]) { found = true; break; }
                if (!found)
                    return BadArgs(cmd, "targetId=" + picks[i] + " 不在候选里(只能选 astral_pending 列出的玩家; 候选数=" +
                                        cands.Length + ")");
            }

            if (!GameActions.LandChoiceTarget(picks, sn)) return NotSent(cmd, "炮台选目标应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 炮台选目标 LandChoiceTarget(targets=" + picks.Length +
                                      ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 占卜(5069 窗口): 两张牌选一张。index = 候选下标, divinationId = 占卜卡配置 id(候选里的)。
        /// 客户端超时点的是第 1 张牌 → 不答 = 选第 0 项, 所以这里 index 也默认 0。
        /// </summary>
        private BridgeResult DivinationPick(BridgeCommand cmd, long started)
        {
            int index = cmd.GetInt("index", -1);
            int divinationId = cmd.GetInt("divinationId", 0);
            if (divinationId == 0) divinationId = cmd.GetInt("id", 0);

            int[] window;
            if (!_tracker.TryGetWindowIds(AgentPendingKind.Divination, out window) || window == null || window.Length == 0)
                return BadArgs(cmd, "当前没有占卜窗口(5069): 候选只有服务器推 5069 时才有。" +
                                    "先用 astral_pending 看有没有 kind=divination");

            if (index < 0 && divinationId != 0)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    if (window[i] == divinationId) { index = i; break; }
                }
                if (index < 0)
                    return BadArgs(cmd, "divinationId=" + divinationId + " 不在本次候选里: [" + Join(window) + "]");
            }
            if (index < 0) index = 0; // 与游戏一致: 超时点的是第 1 张
            if (index >= window.Length)
                return BadArgs(cmd, "index=" + index + " 超出候选范围(共 " + window.Length + " 个)");

            long? sn = ResolveSn(cmd, AgentPendingKind.Divination);
            if (!GameActions.TriggerDivination(window[index], sn)) return NotSent(cmd, "占卜选择");
            OnSent(sn);
            return Done(cmd, started, "已发送 占卜 TriggerDivination(divinationId=" + window[index] +
                                      ", index=" + index + ", sn=" + Show(sn) + ")");
        }

        /// <summary>
        /// 赌场押注(5081 窗口): guessCode 1=奇数 / 2=偶数(可写 guess="odd"/"even")。
        /// **必须明确选** —— 这一注要花星币, 不像选点那样给默认值; 不答也没关系(客户端超时会押奇数)。
        /// 客户端在已死/星币不足时把按钮置灰, 这里同样拒答。
        /// </summary>
        private BridgeResult GambleGuess(BridgeCommand cmd, long started)
        {
            int guess = cmd.GetInt("guessCode", 0);
            if (guess == 0)
            {
                string g = cmd.GetString("guess", null);
                if (!string.IsNullOrEmpty(g))
                {
                    if (g == "1" || string.Equals(g, "odd", StringComparison.OrdinalIgnoreCase)) guess = 1;
                    else if (g == "2" || string.Equals(g, "even", StringComparison.OrdinalIgnoreCase)) guess = 2;
                }
            }
            if (guess != 1 && guess != 2)
                return BadArgs(cmd, "需要 guessCode(1=奇数, 2=偶数)或 guess(\"odd\"/\"even\"); " +
                                    "要花星币所以不替你默认 —— 不答也行, 客户端自己的超时会押奇数");

            long? sn = ResolveSn(cmd, AgentPendingKind.GambleGuess);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有赌场押注窗口(5081): 先用 astral_pending 看有没有 kind=gambleGuess");

            bool isExec, canAct;
            int bet;
            if (!_tracker.TryGetGambleGuess(out isExec, out canAct, out bet))
                return BadArgs(cmd, "本地还没拿到赌场押注窗口的字段, 先用 astral_pending 确认窗口还在");

            if (!canAct)
                return BadArgs(cmd, "客户端这边两个按钮都是灰的(已死/星币不足), 真人点不动, 所以桥接不发; " +
                                    "客户端自己的超时仍会替你押奇数");

            if (!GameActions.StartGamble(isExec, guess, sn)) return NotSent(cmd, "赌场押注应答");
            OnSent(sn);
            return Done(cmd, started, "已发送 赌场押注 StartGamble(guess=" + guess + ", isExec=" + isExec +
                                      ", bet=" + bet + ", sn=" + Show(sn) + ")");
        }

        /// <summary>赌场掷骰(5083 窗口): 唯一合法上行, 没有可选参数; 按钮被置灰时拒答。</summary>
        private BridgeResult GambleDice(BridgeCommand cmd, long started)
        {
            long? sn = ResolveSn(cmd, AgentPendingKind.GambleDice);
            if (!sn.HasValue || sn.Value <= 0)
                return BadArgs(cmd, "现在没有赌场掷骰窗口(5083): 先用 astral_pending 看有没有 kind=gambleDice");

            bool canAct;
            if (_tracker.TryGetGambleDice(out canAct) && !canAct)
                return BadArgs(cmd, "客户端这边掷骰按钮是灰的(已死/星币不足), 真人点不动, 所以桥接不发; " +
                                    "客户端自己的超时仍会替你掷");

            if (!GameActions.GambleThrowDice(sn)) return NotSent(cmd, "赌场掷骰");
            OnSent(sn);
            return Done(cmd, started, "已发送 赌场掷骰 GambleThrowDice(sn=" + Show(sn) + ")");
        }

        private BridgeResult Speed(BridgeCommand cmd, long started)
        {
            double speed = cmd.GetDouble("speed", 0);
            if (speed <= 0) return BadArgs(cmd, "缺少 speed(倍率; 下限 1.0, 上限 100)");

            double clamped = SpeedHack.ClampSpeed(speed);
            if (!SpeedHack.SetSpeed(clamped)) return NotSent(cmd, "变速");
            return Done(cmd, started, "已设置倍率 " + clamped.ToString("0.###") +
                (Math.Abs(clamped - speed) > 0.0001 ? "(请求 " + speed.ToString("0.###") + " 被夹取)" : "") +
                ", 当前倍率 " + SpeedHack.Speed.ToString("0.###"));
        }

        // ============================== 辅助 ==============================

        /// <summary>与 SDK 的 GameActions.MakeInfo 完全一致(它是 private, 这里复制一份)。</summary>
        private static ActionInfo MakeInfo(long sn)
        {
            return new ActionInfo
            {
                Sn = sn,
                UseTime = OperationTimer.GetExtraTime()
            };
        }

        /// <summary>
        /// 解析这条命令该用哪个 sn: agent 显式给的优先, 否则按窗口类型查 PendingTracker。
        /// 查不到就返回 null, 让 SDK 用它自己的默认(ActionLogic.throwDiceSn)。
        /// </summary>
        private long? ResolveSn(BridgeCommand cmd, string windowKind)
        {
            long explicitSn = cmd.GetLong("sn", 0);
            if (explicitSn > 0) return explicitSn;

            if (windowKind != null)
            {
                long windowSn;
                if (_tracker.TryGetWindowSn(windowKind, out windowSn) && windowSn > 0) return windowSn;
            }
            return null;
        }

        /// <summary>
        /// 动作已经发出去了 → 取消该 sn 的倒计时(免得超时回调再自动代打一次), 并把这个 sn 记成"已应答":
        /// 服务器会把我自己的决定也当动作回播, 不记的话刚回完的窗口会被自己的回声重新打开。
        /// </summary>
        private void OnSent(long? sn)
        {
            if (!sn.HasValue || sn.Value <= 0) return;
            GameProbe.CancelOperationTimer(sn.Value);
            _tracker.NoteAnswered(sn.Value);
        }

        private static long SafeCurrentSn()
        {
            try { return GameActions.CurrentSn; } catch { return 0; }
        }

        private bool ResolveCard(BridgeCommand cmd, out int cardId)
        {
            cardId = 0;
            if (cmd.Has("cardId") || cmd.Has("cardIds"))
            {
                if (cmd.TryGetIntFlexible("cardId", "cardIds", out cardId)) return true;
            }

            // 允许直接给手牌 Guid(战斗事件里的 "CardId" 其实是 Guid): 反查成真实 CardId
            int guid = cmd.GetInt("cardGuid", 0);
            if (guid == 0) guid = cmd.GetInt("guid", 0);
            if (guid != 0)
            {
                long self = 0;
                try { self = _selfId != null ? _selfId() : 0; } catch { }
                int resolved = guid;
                try { resolved = Players.ResolveCardGuid(self, guid); } catch { }
                if (resolved != 0)
                {
                    cardId = resolved;
                    return true;
                }
            }
            return false;
        }

        private static string Show(long? sn)
        {
            return sn.HasValue ? sn.Value.ToString() : "自动(当前待响应)";
        }

        private BridgeResult Done(BridgeCommand cmd, long started, string detail)
        {
            return BridgeResult.Success(cmd.Id, cmd.Tool, detail, AgentBridgeLayout.NowMs() - started);
        }

        private BridgeResult BadArgs(BridgeCommand cmd, string error)
        {
            return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.BadArgs, error);
        }

        /// <summary>SDK 返回 false 的统一解释: 这几种原因最常见, 直接写给 agent 看。</summary>
        private BridgeResult NotSent(BridgeCommand cmd, string what)
        {
            return BridgeResult.Failure(cmd.Id, cmd.Tool, AgentBridgeLayout.Code.Rejected,
                what + " 未发出。可能原因: 不在对局/不在该窗口(没有待响应 sn)、当前不是你可操作、或权限未授予");
        }

        private static string Join<T>(IEnumerable<T> items)
        {
            if (items == null) return string.Empty;
            var sb = new StringBuilder();
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(',');
                sb.Append(it);
                first = false;
            }
            return sb.ToString();
        }
    }
}
