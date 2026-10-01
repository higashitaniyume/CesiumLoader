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

        private BridgeResult UseCard(BridgeCommand cmd, long started)
        {
            int cardId;
            if (!ResolveCard(cmd, out cardId)) return BadArgs(cmd, "缺少 cardId(手牌 CardId; 也可给 cardGuid)");

            long? sn = ResolveSn(cmd, null);
            if (!GameActions.UseCard(cardId, sn)) return NotSent(cmd, "战斗用牌");
            OnSent(sn);
            return Done(cmd, started, "已发送 战斗用牌 UseCard(cardId=" + cardId + ", sn=" + Show(sn) + ")");
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
