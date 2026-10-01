using System;
using System.Collections.Generic;
using AstralParty.Agent;
using AstralParty.AgentMod.Bridge;
using Xunit;

namespace AstralParty.AgentMod.Tests
{
    /// <summary>
    /// "现在服务器在等我做什么"的状态机。桥接里最容易出错的一块(漏报会让 agent 干等,
    /// 误报会让 agent 发出被服务器拒绝的请求), 所以逐种窗口都钉一遍。
    /// </summary>
    public class PendingTrackerTests
    {
        private const long Self = 1001;
        private const long Other = 2002;

        private static AgentPending Build(PendingTracker t, bool canThrowDice, long currentSn, long now,
            Func<string, long, long> remaining = null)
        {
            return t.Build(Self, canThrowDice, currentSn, now, remaining ?? ((k, sn) => -1), (k, id) => "名" + id);
        }

        [Fact]
        public void 没有窗口也没有投骰时_pending是none()
        {
            var t = new PendingTracker();
            var p = Build(t, false, 0, 1000);

            Assert.Equal(AgentPendingKind.None, p.Kind);
            Assert.False(p.Actionable);
            Assert.Equal(0, p.Sn);
            Assert.Empty(p.Candidates);
        }

        [Fact]
        public void 筹码三选一窗口_带候选与可选操作()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 50001, 50042, 50093 }, 5211, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.SelectRelic, p.Kind);
            Assert.Equal(5211, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal(3, p.Candidates.Count);
            Assert.Equal(50001, p.Candidates[0].Id);
            Assert.Equal("relic", p.Candidates[0].Kind);
            Assert.Equal("名50042", p.Candidates[1].Name);
            Assert.Contains(p.Options, o => o.Contains("astral_select_relic"));
            Assert.Equal(1000, p.SinceMs);
        }

        [Fact]
        public void 同一个sn重复广播_不重置等待起点()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2, 3 }, 5211, 1000);
            t.OnRelicCandidates(Self, new List<int> { 1, 2, 3 }, 5211, 5000);

            var p = Build(t, false, 0, 9000);
            Assert.Equal(1000, p.SinceMs);
        }

        [Fact]
        public void 没有sn的窗口一律不上报_避免给出点了没用的窗口()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2, 3 }, 0, 1000);

            var p = Build(t, false, 0, 1200);
            Assert.Equal(AgentPendingKind.None, p.Kind);
        }

        [Fact]
        public void 别人的窗口_报none并说明在等别人()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Other, new List<int> { 1, 2, 3 }, 5211, 1000);

            var p = Build(t, false, 0, 1200);
            Assert.Equal(AgentPendingKind.None, p.Kind);
            Assert.Contains(p.Notes, n => n.Contains("其他玩家"));
        }

        [Fact]
        public void 没窗口但可以投骰_报throwDice并给出三种变体()
        {
            var t = new PendingTracker();
            var p = Build(t, true, 777, 1000);

            Assert.Equal(AgentPendingKind.ThrowDice, p.Kind);
            Assert.Equal(777, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal("sdk", p.Source);
            Assert.Contains(p.Options, o => o.Contains("noOper"));
            Assert.Contains(p.Options, o => o.Contains("moveNow"));
        }

        [Fact]
        public void 窗口优先于投骰_不会让agent误以为该投骰()
        {
            var t = new PendingTracker();
            t.OnRewardCandidates(Self, new List<int> { 111, 222 }, 5377, 1000);

            var p = Build(t, true, 777, 1200);
            Assert.Equal(AgentPendingKind.RewardCard, p.Kind);
        }

        [Fact]
        public void 可用牌加CardSN_报cardChoice并列出可出的牌()
        {
            var t = new PendingTracker();
            t.SetRuntimeHints(false, 0, 5073, new[] { 90001, 90002 });

            var p = Build(t, false, 0, 1000);

            Assert.Equal(AgentPendingKind.CardChoice, p.Kind);
            Assert.Equal(5073, p.Sn);
            Assert.Equal(2, p.Candidates.Count);
            Assert.Equal("card", p.Candidates[0].Kind);
            Assert.Contains(p.Options, o => o.Contains("astral_use_quick_card"));
        }

        [Fact]
        public void 投骰窗口里如果服务器还标了可用牌_作为备注写出来()
        {
            var t = new PendingTracker();
            t.SetRuntimeHints(true, 777, 0, new[] { 90001 });

            var p = Build(t, true, 777, 1000);

            Assert.Equal(AgentPendingKind.ThrowDice, p.Kind);
            Assert.Contains(p.Notes, n => n.Contains("90001"));
        }

        [Fact]
        public void 移动窗口_候选地块由主线程算出后填入()
        {
            var t = new PendingTracker();
            t.OnMoveAction(Self, 5027, 1000);
            t.SetMoveCandidates(new[] { 31, 32 });

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.Move, p.Kind);
            Assert.True(p.Actionable);
            Assert.Equal(2, p.Candidates.Count);
            Assert.Equal(31, p.Candidates[0].Id);
            Assert.Equal("land", p.Candidates[0].Kind);
            Assert.Contains(p.Options, o => o.Contains("astral_move"));
        }

        [Fact]
        public void 移动窗口_候选还没算出来时不可应答且说明原因()
        {
            var t = new PendingTracker();
            t.OnMoveAction(Self, 5027, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.Move, p.Kind);
            Assert.False(p.Actionable);
            Assert.Contains(p.Notes, n => n.Contains("没算出目标地块"));
        }

        [Fact]
        public void 移动回执_关掉移动窗口()
        {
            var t = new PendingTracker();
            t.OnMoveAction(Self, 5027, 1000);
            t.SetMoveCandidates(new[] { 31 });
            t.OnMoved(Self, 2000);

            var p = Build(t, false, 0, 2500);
            Assert.Equal(AgentPendingKind.None, p.Kind);
        }

        [Fact]
        public void 商店窗口_候选带价格售罄与免费标记()
        {
            var t = new PendingTracker();
            t.OnShopCandidates(Self, new[] { 3001, 3002, 3003 }, new[] { 3, 3, 0 },
                new[] { false, true, false }, new[] { false, false, true }, true, 5215, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.Shop, p.Kind);
            Assert.Equal(3, p.Candidates.Count);
            Assert.Equal(3, p.Candidates[0].Price);
            Assert.True(p.Candidates[1].SoldOut);
            Assert.True(p.Candidates[2].Free);
            Assert.Contains(p.Options, o => o.Contains("astral_shop_buy"));
            Assert.Contains(p.Options, o => o.Contains("astral_atm_transfer")); // PVE 才有 ATM
            Assert.Contains(p.Notes, n => n.Contains("PVE"));
        }

        [Fact]
        public void PVP商店_不提供ATM转账()
        {
            var t = new PendingTracker();
            t.OnShopCandidates(Self, new[] { 3001 }, new[] { 2 }, new[] { false }, new[] { false }, false, 5029, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.DoesNotContain(p.Options, o => o.Contains("astral_atm_transfer"));
            Assert.Contains(p.Notes, n => n.Contains("PVP"));
        }

        [Fact]
        public void 筹码地块购买窗口_给出买与不买两个选项并带价格()
        {
            var t = new PendingTracker();
            t.OnBuyRelicOffer(Self, 5249, 7, 0, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.BuyRelic, p.Kind);
            Assert.True(p.Actionable);
            Assert.Equal(5249, p.Sn);
            Assert.Contains(p.Options, o => o.Contains("confirm") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("confirm") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("7"));
            // 这条是反编译纠正过的坑: 判别依据是 Select 而不是 Exit
            Assert.Contains(p.Notes, n => n.Contains("Select"));
        }

        [Fact]
        public void 筹码地块回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnBuyRelicOffer(Self, 5249, 7, 0, 1000);
            t.OnBuyRelicDone(Self, 1500);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1600).Kind);
        }

        [Fact]
        public void 奖励卡窗口_用5377的sn()
        {
            var t = new PendingTracker();
            t.OnRewardCandidates(Self, new List<int> { 41001, 41002 }, 5377, 1000);

            var p = Build(t, false, 0, 1100);
            Assert.Equal(AgentPendingKind.RewardCard, p.Kind);
            Assert.Equal(5377, p.Sn);
            Assert.Contains(p.Options, o => o.Contains("astral_select_reward_card"));
        }

        [Fact]
        public void 选择回执_关掉对应窗口()
        {
            var t = new PendingTracker();
            t.OnRewardCandidates(Self, new List<int> { 1, 2 }, 5377, 1000);
            t.OnRewardSelected(Self, 1, 1200);
            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1300).Kind);

            var t2 = new PendingTracker();
            t2.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);
            t2.OnRelicSelected(Self, 1, 1200);
            Assert.Equal(AgentPendingKind.None, Build(t2, false, 0, 1300).Kind);
        }

        [Fact]
        public void 别人选了牌_不会关掉我的窗口()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);
            t.OnRelicSelected(Other, 1, 1200);

            Assert.Equal(AgentPendingKind.SelectRelic, Build(t, false, 0, 1300).Kind);
        }

        [Fact]
        public void 倒计时可读时_填RemainingMs与DeadlineMs()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);

            var p = Build(t, false, 0, 1000, (kind, sn) => 4500);

            Assert.Equal(4500, p.RemainingMs);
            Assert.Equal(5500, p.DeadlineMs);
        }

        [Fact]
        public void 倒计时读不到时_RemainingMs是负一_不编造deadline()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);

            var p = Build(t, false, 0, 1000, (kind, sn) => -1);

            Assert.Equal(-1, p.RemainingMs);
            Assert.Equal(0, p.DeadlineMs);
        }

        [Fact]
        public void TryGetWindowIds_只在类型匹配时给候选()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 11, 22, 33 }, 5211, 1000);

            int[] ids;
            Assert.True(t.TryGetWindowIds(AgentPendingKind.SelectRelic, out ids));
            Assert.Equal(new[] { 11, 22, 33 }, ids);

            int[] notMine;
            Assert.False(t.TryGetWindowIds(AgentPendingKind.RewardCard, out notMine));
            Assert.Null(notMine);

            // 拿到的必须是副本: 改动不能污染状态机
            ids[0] = 999;
            int[] again;
            t.TryGetWindowIds(AgentPendingKind.SelectRelic, out again);
            Assert.Equal(11, again[0]);
        }

        [Fact]
        public void TryGetWindowSn_只在类型匹配时给sn()
        {
            var t = new PendingTracker();
            t.OnBuyRelicOffer(Self, 5249, 7, 0, 1000);

            long sn;
            Assert.True(t.TryGetWindowSn(AgentPendingKind.BuyRelic, out sn));
            Assert.Equal(5249, sn);
            Assert.False(t.TryGetWindowSn(AgentPendingKind.Shop, out sn));
        }

        [Fact]
        public void TryGetShopWindow_区分PVE与PVP()
        {
            var t = new PendingTracker();
            t.OnShopCandidates(Self, new[] { 1, 2 }, new[] { 3, 3 }, new[] { false, false }, new[] { false, false }, true, 5215, 1000);

            bool pve;
            int slots;
            Assert.True(t.TryGetShopWindow(out pve, out slots));
            Assert.True(pve);
            Assert.Equal(2, slots);
        }

        [Fact]
        public void Reset_清掉窗口与运行时提示()
        {
            var t = new PendingTracker();
            t.OnMoveAction(Self, 5027, 1000);
            t.SetMoveCandidates(new[] { 31 });
            t.SetRuntimeHints(true, 777, 5073, new[] { 90001 });

            t.Reset();

            var p = Build(t, false, 0, 1100);
            Assert.Equal(AgentPendingKind.None, p.Kind);
        }

        [Fact]
        public void 新窗口顶掉旧窗口_一次只报一件事()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);
            t.OnRewardCandidates(Self, new List<int> { 3, 4 }, 5377, 1100);

            var p = Build(t, false, 0, 1200);
            Assert.Equal(AgentPendingKind.RewardCard, p.Kind);
        }

        // ---------- 事件选择(5317 / 回执 5318) ----------
        // 依据: 反编译 UI.LandEventWindow.ShowSkill10202 —— 候选 = SelectEventC2S.Events, sn = action.Sn。

        [Fact]
        public void 事件选择窗口_列出候选与可用操作()
        {
            var t = new PendingTracker();
            t.OnEventCandidates(Self, new List<int> { 7001, 7002, 7003 }, 5317, 1000);

            var p = Build(t, false, 0, 1200);
            Assert.Equal(AgentPendingKind.SelectEvent, p.Kind);
            Assert.Equal(5317, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal(3, p.Candidates.Count);
            Assert.Equal(7001, p.Candidates[0].Id);
            Assert.Equal("event", p.Candidates[0].Kind);
            Assert.Contains(p.Options, o => o.Contains("astral_select_event"));
        }

        [Fact]
        public void 事件选择回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnEventCandidates(Self, new List<int> { 7001 }, 5317, 1000);
            t.OnEventSelected(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        [Fact]
        public void 事件候选窗口_能把候选原样取出来应答()
        {
            var t = new PendingTracker();
            t.OnEventCandidates(Self, new List<int> { 7001, 7002 }, 5317, 1000);

            int[] ids;
            Assert.True(t.TryGetWindowIds(AgentPendingKind.SelectEvent, out ids));
            Assert.Equal(new[] { 7001, 7002 }, ids);
        }

        // ---------- 战斗攻击骰(5037 / 回执 5038) ----------
        // 依据: 反编译 FightLogic.ReadyFightThrowDice(窗口归属 action.PlayerId, sn = action.Sn)。

        [Fact]
        public void 战斗掷骰窗口_给出battle投骰选项()
        {
            var t = new PendingTracker();
            t.OnBattleDiceOffer(Self, 5037, 1000);

            var p = Build(t, false, 0, 1200);
            Assert.Equal(AgentPendingKind.BattleDice, p.Kind);
            Assert.Equal(5037, p.Sn);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("battle"));
        }

        [Fact]
        public void 战斗掷骰回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnBattleDiceOffer(Self, 5037, 1000);
            t.OnBattleDiceDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        // ---------- 回声防护 ----------
        // 服务器会把我自己的决定当成一条同 sn 的动作广播回来; 若不管, 刚回完的窗口会被自己的回声重新打开。

        [Fact]
        public void 已应答的sn回声_不会把窗口重新打开()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);
            t.NoteAnswered(5211);            // 我们应答了这个窗口
            t.OnRelicSelected(Self, 1, 1100); // 回执关窗
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1200); // 服务器回播我自己的决定

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1300).Kind);
        }

        [Fact]
        public void 应答过旧sn之后_新的sn照常开窗()
        {
            var t = new PendingTracker();
            t.NoteAnswered(5211);
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5212, 1200);

            var p = Build(t, false, 0, 1300);
            Assert.Equal(AgentPendingKind.SelectRelic, p.Kind);
            Assert.Equal(5212, p.Sn);
        }

        [Fact]
        public void 筹码窗口_列出重摇选项()
        {
            var t = new PendingTracker();
            t.OnRelicCandidates(Self, new List<int> { 1, 2 }, 5211, 1000);

            var p = Build(t, false, 0, 1200);
            Assert.Contains(p.Options, o => o.Contains("reroll"));
            Assert.Contains(p.Notes, n => n.Contains("窗口不会关闭") || n.Contains("新的候选"));
        }
    }
}
