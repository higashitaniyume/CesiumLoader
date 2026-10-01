using System;
using System.Collections.Generic;
using AstralParty.Agent;
using CesiumLoader.SDK;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 局面观测: 把"游戏里现在是什么情况"采集成本可序列化的 <see cref="AgentState"/>。
    ///
    /// 全部字段都走 SDK 的只读访问器(内部都做了空保护和异常吞噬), 所以这里只负责"组装",
    /// 不在游戏里裸访问 Unity/游戏对象。必须在**主线程**调用。
    /// </summary>
    internal sealed class StateProbe
    {
        private readonly PendingTracker _tracker;

        /// <summary>我方 playerId(主线程最近一次采集的结果, 供网络线程只读)。</summary>
        public long SelfId { get; private set; }

        public StateProbe(PendingTracker tracker)
        {
            _tracker = tracker;
        }

        /// <summary>采集一次局面。</summary>
        public AgentState Capture(string modVersion, long stateSeq, long now)
        {
            var st = new AgentState();
            st.Schema = AgentBridgeLayout.SchemaVersion;
            st.ModVersion = modVersion;
            st.SdkVersion = SdkVersionText();
            st.UpdatedAtMs = now;
            st.UpdatedAtUtc = AgentBridgeLayout.NowUtcIso();
            st.StateSeq = stateSeq;

            CaptureScene(st);
            CaptureUnits(st);
            CaptureSelf(st);
            CaptureHand(st);
            CaptureBuffs(st);
            if (SelfId != 0) _tracker.SetSelf(SelfId);
            CaptureTurn(st, now);
            return st;
        }

        private void CaptureScene(AgentState st)
        {
            try { st.Scene = SceneService.GetActiveSceneName(); } catch { }
        }

        private void CaptureUnits(AgentState st)
        {
            try
            {
                var roster = Players.Roster();
                if (roster == null) return;
                for (int i = 0; i < roster.Count; i++)
                {
                    var r = roster[i];
                    if (r.Id == 0) continue;
                    var u = new AgentUnit
                    {
                        Id = r.Id,
                        Name = r.Name,
                        IsSelf = r.IsSelf,
                        IsMonster = r.IsMonster,
                        IsBot = r.IsBot,
                        Atk = r.Atk,
                        Def = r.Def,
                        Hp = r.Hp,
                        MaxHp = r.MaxHp
                    };
                    if (!r.IsMonster)
                    {
                        u.Gold = Players.Gold(r.Id);
                        u.HandCount = Players.HandCount(r.Id);
                    }
                    st.Units.Add(u);
                    if (r.IsSelf && SelfId == 0) SelfId = r.Id;
                }
                st.InRoom = roster.Count > 0;
            }
            catch { }

            // 战斗中(玩家数据表非空)。Roster 读的是房间, 战斗数据在另一处, 两个都报。
            try { st.InBattle = Players.All().Count > 0; } catch { }
        }

        private void CaptureSelf(AgentState st)
        {
            // 备用路径: 不在房间但在战斗里(BattlePlayerData.player 是 RoomPlayer, id 在 player.Id 上)
            if (SelfId == 0)
            {
                try
                {
                    var all = Players.All();
                    for (int i = 0; i < all.Count; i++)
                    {
                        long pid = 0;
                        try { pid = all[i].player.Id; } catch { }
                        if (pid != 0 && Players.IsSelf(pid)) { SelfId = pid; break; }
                    }
                }
                catch { }
            }

            if (SelfId == 0) return;
            try
            {
                st.Self = new AgentSelf
                {
                    PlayerId = SelfId,
                    Nick = Players.Nick(SelfId),
                    Gold = Players.Gold(SelfId),
                    HandCount = Players.HandCount(SelfId)
                };
            }
            catch { }
        }

        private void CaptureHand(AgentState st)
        {
            try
            {
                var hand = Players.MyHandCards();
                if (hand == null) return;
                for (int i = 0; i < hand.Count; i++)
                {
                    var c = hand[i];
                    if (c == null) continue;
                    string name = null;
                    try { name = Names.Card(c.CardId); } catch { }
                    st.Hand.Add(new AgentCard
                    {
                        Guid = c.Guid,
                        CardId = c.CardId,
                        Name = name,
                        IsTemp = c.IsTemp,
                        PurifyNum = c.PurifyNum,
                        BattleCost = c.BattleCost
                    });
                }
            }
            catch { }
        }

        private void CaptureBuffs(AgentState st)
        {
            if (SelfId == 0) return;
            try
            {
                var buffs = Players.BuffsOf(SelfId);
                if (buffs == null) return;
                for (int i = 0; i < buffs.Count; i++)
                {
                    var b = buffs[i];
                    st.SelfBuffs.Add(new AgentBuff { BuffId = b.BuffId, Layers = b.Layers, KeepRound = b.KeepRound });
                }
            }
            catch { }
        }

        private void CaptureTurn(AgentState st, long now)
        {
            bool canThrowDice = false;
            long sn = 0;
            try { canThrowDice = GameActions.CanThrowDice; } catch { }
            try { sn = GameActions.CurrentSn; } catch { }

            st.IsMyTurn = canThrowDice;
            st.CurrentSn = sn;

            // 用牌窗口 / 可用牌: 服务器给过我"可出的牌"时, ActionLogic 会填 UsableCards + CardSN
            long cardSn = 0;
            int[] usable = null;
            bool notMove = false;
            if (GameProbe.TryReadCardWindow(out cardSn, out usable, out notMove))
                _tracker.SetRuntimeHints(canThrowDice, sn, cardSn, usable);

            // 轮到我移动时, 本地按棋盘拓扑算出可走的地块(与客户端建箭头用的是同一个方法)
            if (_tracker.WindowKind == AgentPendingKind.Move)
            {
                var lands = GameProbe.SelfMoveTargets();
                if (lands != null) _tracker.SetMoveCandidates(lands);
            }

            // 战斗出牌窗口(5035): 候选牌过滤口径与客户端 GetVailCard() 一致
            // (手牌里 EffectType 匹配我方角色的牌; 我是攻击方用 Attack 类, 防守方用 Defense 类)。
            // 只要还挂着这个窗口就刷新一次, 这样出过一张牌之后候选会自动少一张。
            if (_tracker.WindowKind == AgentPendingKind.FightCard)
            {
                int[] uids, costs;
                int residue;
                bool attacker;
                if (GameProbe.TrySelfFightCards(out uids, out costs, out residue, out attacker))
                    _tracker.SetFightCardCandidates(uids, costs, residue, attacker);
            }

            // 追击窗口(5213): 候选怪物**不在协议里**, 只有本地过滤出来的那一份(GetVailPursuitMonster 同口径)
            if (_tracker.WindowKind == AgentPendingKind.PursueMonster)
            {
                long[] monsters;
                if (GameProbe.TrySelfPursuitMonsters(out monsters))
                    _tracker.SetPursuitMonsters(monsters);
            }

            // 加油站/出生点窗口(5077): offer 零负载, 窗口内容(站在哪种地块)只能从本地读, 供 agent 参考
            if (_tracker.WindowKind == AgentPendingKind.StopOrContinue)
                _tracker.SetStandLand(GameProbe.SelfStandLand());

            st.Pending = _tracker.Build(SelfId, canThrowDice, sn, now, DeadlineProbe.RemainingMs, ResolveName);
            st.NotMove = notMove;
        }

        /// <summary>候选项显示名解析(纯展示用, 读不到就退回 #id)。</summary>
        public static string ResolveName(string kind, int id)
        {
            try
            {
                switch (kind)
                {
                    case "relic": return Fallback(Names.Relic(id), id);
                    case "shopCard":
                    case "rewardCard":
                    case "card": return Fallback(Names.Card(id), id);
                    // 战斗用牌的候选 id 是手牌 Guid(不是卡牌配置 id), 名字要拿我方手牌反查 CardId
                    case "handCard":
                        {
                            try
                            {
                                var hand = Players.MyHandCards();
                                if (hand != null)
                                {
                                    for (int i = 0; i < hand.Count; i++)
                                    {
                                        if (hand[i] != null && hand[i].Guid == id)
                                        {
                                            string n = Names.Card(hand[i].CardId);
                                            return string.IsNullOrEmpty(n) ? "手牌#" + id : n;
                                        }
                                    }
                                }
                            }
                            catch { }
                            return "手牌#" + id;
                        }
                    case "land": return "#" + id;
                    // 追击候选的 id 是怪物(也是玩家)的 playerId, 名字从房间花名册里反查
                    case "monster":
                        {
                            try
                            {
                                var roster = Players.Roster();
                                if (roster != null)
                                {
                                    for (int i = 0; i < roster.Count; i++)
                                    {
                                        var r = roster[i];
                                        if (r.Id == id && !string.IsNullOrEmpty(r.Name)) return r.Name;
                                    }
                                }
                            }
                            catch { }
                            return "#" + id;
                        }
                    default: return "#" + id;
                }
            }
            catch { return "#" + id; }
        }

        private static string Fallback(string name, int id)
        {
            return string.IsNullOrEmpty(name) ? "#" + id : name;
        }

        private static string SdkVersionText()
        {
            try { return typeof(GameActions).Assembly.GetName().Version.ToString(); }
            catch { return "unknown"; }
        }
    }

    /// <summary>
    /// 读"这个操作还剩多少毫秒"。
    ///
    /// 实现见 <see cref="GameProbe.RemainingMs"/>: 先用 OperationTimer.GetOperateTimer(sn) 拿 Timer
    /// 的 GetTimeRemaining(), 兜底用 OperationTimer 的 operationTime - downtime(秒)。
    /// 读不到(战役图没有倒计时 / 反射失败)时返回 -1, 外部工具应改用 Pending.SinceMs 判断。
    /// </summary>
    internal static class DeadlineProbe
    {
        public static long RemainingMs(string kind, long sn)
        {
            try { return GameProbe.RemainingMs(sn); }
            catch { return -1; }
        }
    }
}
