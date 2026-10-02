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

        // ---------- 战斗三件套(5047 打不打 / 5035 出牌 / 5039 闪避) ----------
        // 反编译依据: 三者的超时代答分别是"不打""不出牌""不闪避"(见 docs/MCP-Agent桥接.md §5)。

        [Fact]
        public void 战斗询问窗口_给出打与不打并说明超时默认()
        {
            var t = new PendingTracker();
            t.OnAskFightOffer(Self, Other, 5047, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.AskFight, p.Kind);
            Assert.Equal(5047, p.Sn);
            Assert.Equal(Other, p.AskPlayerId);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("astral_ask_battle") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("astral_ask_battle") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不打"));
        }

        [Fact]
        public void 战斗询问回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnAskFightOffer(Self, Other, 5047, 1000);
            t.OnAskFightDone(Self, 1500);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1600).Kind);
        }

        [Fact]
        public void 战斗出牌窗口_候选是手牌Guid并带各自消耗()
        {
            var t = new PendingTracker();
            t.OnFightCardOffer(Self, 5035, 1000);
            t.SetFightCardCandidates(new[] { 11, 22 }, new[] { 1, 3 }, 2, true);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.FightCard, p.Kind);
            Assert.Equal(5035, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal(2, p.Candidates.Count);
            Assert.Equal(11, p.Candidates[0].Id);
            Assert.Equal(1, p.Candidates[0].Cost);
            Assert.Equal(3, p.Candidates[1].Cost);
            Assert.True(p.IsAttacker);
            Assert.Equal(2, p.ResidueCost);
            Assert.Contains(p.Options, o => o.Contains("astral_use_card") && o.Contains("pass"));
            Assert.Contains(p.Notes, n => n.Contains("攻击方"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不出牌"));
        }

        [Fact]
        public void 战斗出牌候选_按handCard口径问名字()
        {
            var t = new PendingTracker();
            t.OnFightCardOffer(Self, 5035, 1000);
            t.SetFightCardCandidates(new[] { 77 }, new[] { 2 }, 5, false);

            string kindSeen = null;
            long idSeen = 0;
            var p = t.Build(Self, false, 0, 1200, (k, sn) => -1,
                (k, id) => { kindSeen = k; idSeen = id; return "名甲"; });

            Assert.Equal("handCard", kindSeen);
            Assert.Equal(77, idSeen);
            Assert.Equal("名甲", p.Candidates[0].Name);
            Assert.False(p.IsAttacker);
            Assert.Equal(5, p.ResidueCost);
            Assert.Contains(p.Notes, n => n.Contains("防守方"));
        }

        [Fact]
        public void 战斗出牌候选_没有可出的牌时说明原因()
        {
            var t = new PendingTracker();
            t.OnFightCardOffer(Self, 5035, 1000);
            t.SetFightCardCandidates(new int[0], new int[0], 0, false);

            var p = Build(t, false, 0, 1200);

            Assert.Empty(p.Candidates);
            Assert.Contains(p.Notes, n => n.Contains("没有可出的战斗牌"));
        }

        [Fact]
        public void 战斗出牌回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnFightCardOffer(Self, 5035, 1000);
            t.OnFightCardDone(Self, 1500);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1600).Kind);
        }

        [Fact]
        public void 闪避窗口_noDodge时只给硬吃()
        {
            var t = new PendingTracker();
            t.OnFightChoiceOffer(Self, true, 5039, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.FightChoice, p.Kind);
            Assert.Equal(5039, p.Sn);
            Assert.True(p.NoDodge);
            Assert.DoesNotContain(p.Options, o => o.Contains("dodge\":true"));
            Assert.Contains(p.Options, o => o.Contains("dodge\":false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不闪避"));
        }

        [Fact]
        public void 闪避窗口_可以闪避时给两个选项()
        {
            var t = new PendingTracker();
            t.OnFightChoiceOffer(Self, false, 5039, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.False(p.NoDodge);
            Assert.Contains(p.Options, o => o.Contains("dodge\":true"));
            Assert.Contains(p.Options, o => o.Contains("dodge\":false"));
        }

        [Fact]
        public void 闪避窗口_执行器能查到NoDodge好拦下非法闪避()
        {
            var t = new PendingTracker();
            t.OnFightChoiceOffer(Self, true, 5039, 1000);

            bool noDodge;
            Assert.True(t.TryGetNoDodge(out noDodge));
            Assert.True(noDodge);
        }

        [Fact]
        public void 没有闪避窗口时_查不到NoDodge()
        {
            var t = new PendingTracker();
            t.OnFightCardOffer(Self, 5035, 1000);

            bool noDodge;
            Assert.False(t.TryGetNoDodge(out noDodge));
            Assert.False(noDodge);
        }

        [Fact]
        public void 闪避回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnFightChoiceOffer(Self, false, 5039, 1000);
            t.OnFightChoiceDone(Self, 1500);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1600).Kind);
        }

        [Fact]
        public void 四个人同时被问_别人的窗口既不报给我也不顶掉我的()
        {
            var t = new PendingTracker();
            t.SetSelf(Self);
            t.OnFightCardOffer(Self, 5035, 1000);

            // 其他三个玩家同时各自有窗口(4 人局同一条 5047 会广播给所有人)
            t.OnAskFightOffer(Other, Self, 5047, 1100);
            t.OnFightChoiceOffer(3003, false, 5039, 1100);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.FightCard, p.Kind);
            Assert.Equal(5035, p.Sn);
            Assert.True(p.Actionable);
        }

        [Fact]
        public void 轮到别人时_不报成我的窗口()
        {
            var t = new PendingTracker();
            t.SetSelf(Self);
            t.OnAskFightOffer(Other, Self, 5047, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.None, p.Kind);
            Assert.False(p.Actionable);
            Assert.Equal(0, p.Sn);
        }

        [Fact]
        public void 战斗窗口_剩余时间用该窗口的sn去问()
        {
            var t = new PendingTracker();
            t.OnAskFightOffer(Self, Other, 5047, 1000);

            long askedSn = 0;
            var p = t.Build(Self, false, 0, 1200, (k, sn) => { askedSn = sn; return 12345; },
                (k, id) => "名" + id);

            Assert.Equal(5047, askedSn);
            Assert.Equal(12345, p.RemainingMs);
        }

        // ---------- 加油站/出生点(5077 / 回执 5078) ----------

        [Fact]
        public void 加油站窗口_给停留与继续两个选项()
        {
            var t = new PendingTracker();
            t.OnStopOrContinueOffer(Self, 5077, 1000);
            t.SetStandLand("fillingStation");

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.StopOrContinue, p.Kind);
            Assert.Equal(5077, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal("fillingStation", p.Land);
            Assert.Contains(p.Options, o => o.Contains("astral_stop_or_continue") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("astral_stop_or_continue") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 继续走"));
        }

        [Fact]
        public void 加油站窗口_没读到地块时也照常开窗()
        {
            var t = new PendingTracker();
            t.OnStopOrContinueOffer(Self, 5077, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.StopOrContinue, p.Kind);
            Assert.Contains(p.Notes, n => n.Contains("没读到当前地块"));
        }

        [Fact]
        public void 加油站回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnStopOrContinueOffer(Self, 5077, 1000);
            t.OnStopOrContinueDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        [Fact]
        public void 加油站窗口_应答后的同sn回声不会重开()
        {
            var t = new PendingTracker();
            t.OnStopOrContinueOffer(Self, 5077, 1000);
            t.NoteAnswered(5077);                       // 我答了
            t.OnStopOrContinueDone(Self, 1100);         // 回执关窗
            t.OnStopOrContinueOffer(Self, 5077, 1200);  // 服务器把我自己的答案当动作回播(同 sn)

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1300).Kind);
        }

        // ---------- 复活队友(5233 / 回执 5234) ----------

        [Fact]
        public void 复活队友窗口_给复活与不复活两个选项()
        {
            var t = new PendingTracker();
            t.OnReviveTeammateOffer(Self, 5233, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.ReviveTeammate, p.Kind);
            Assert.Equal(5233, p.Sn);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("astral_revive_teammate") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("astral_revive_teammate") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不复活"));
        }

        [Fact]
        public void 复活队友回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnReviveTeammateOffer(Self, 5233, 1000);
            t.OnReviveTeammateDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        [Fact]
        public void 复活队友窗口_别人的窗口不当作我的()
        {
            var t = new PendingTracker();
            t.OnReviveTeammateOffer(Other, 5233, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.None, p.Kind);
            Assert.Contains(p.Notes, n => n.Contains("其他玩家"));
        }

        // ---------- 机制选择(5259 / 回执 5260) ----------

        [Fact]
        public void 机制选择窗口_给启动与不启动两个选项()
        {
            var t = new PendingTracker();
            t.OnSelectMechanismOffer(Self, 5259, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.SelectMechanism, p.Kind);
            Assert.Equal(5259, p.Sn);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("astral_select_mechanism") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("astral_select_mechanism") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不启动"));
        }

        [Fact]
        public void 机制选择回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnSelectMechanismOffer(Self, 5259, 1000);
            t.OnSelectMechanismDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        // ---------- 医院(5093 / 回执 5094) ----------

        [Fact]
        public void 医院窗口_只有一个检查选项且写明没有拒绝()
        {
            var t = new PendingTracker();
            t.OnHospitalOffer(Self, 5093, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.HospitalCheck, p.Kind);
            Assert.Equal(5093, p.Sn);
            Assert.True(p.Actionable);
            Assert.Single(p.Options);
            Assert.Contains("astral_hospital_check", p.Options[0]);
            Assert.Contains(p.Notes, n => n.Contains("只有\"检查\"一个合法上行"));
        }

        [Fact]
        public void 医院回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnHospitalOffer(Self, 5093, 1000);
            t.OnHospitalDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        // ---------- 炮台选目标(5063 / 回执 5064) ----------

        [Fact]
        public void 炮台窗口_列出候选英雄与选择上限()
        {
            var t = new PendingTracker();
            t.OnBatteryOffer(Self, 2, new long[] { 1001, 2002, 3003 }, 555001, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.BatteryTarget, p.Kind);
            Assert.Equal(555001, p.Sn);
            Assert.Equal(2, p.TargetNum);
            Assert.True(p.Actionable);
            Assert.Equal(3, p.Candidates.Count);
            Assert.Contains(p.Candidates, c => c.Kind == "player" && c.LongId == 1001);
            Assert.Contains(p.Options, o => o.Contains("astral_battery_pick") && o.Contains("targetIds"));
            Assert.Contains(p.Options, o => o.Contains("astral_battery_pick") && o.Contains("leave"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 离开"));
        }

        [Fact]
        public void 炮台窗口_候选未读出来时只能离开且不伪装成没有目标()
        {
            var t = new PendingTracker();
            t.OnBatteryOffer(Self, 1, null, 555002, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.BatteryTarget, p.Kind);
            Assert.Empty(p.Candidates);
            Assert.Contains(p.Notes, n => n.Contains("候选还没读出来"));
            Assert.Contains(p.Options, o => o.Contains("leave"));
        }

        [Fact]
        public void 炮台窗口_候选为空数组是合法结果()
        {
            var t = new PendingTracker();
            t.OnBatteryOffer(Self, 1, new long[0], 555003, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.BatteryTarget, p.Kind);
            Assert.Empty(p.Candidates);
            Assert.Contains(p.Notes, n => n.Contains("候选有 0 个"));
        }

        [Fact]
        public void 炮台回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnBatteryOffer(Self, 1, new long[] { 1001 }, 555004, 1000);
            t.OnBatteryDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        [Fact]
        public void 炮台窗口_别人的窗口不当作我的()
        {
            var t = new PendingTracker();
            t.OnBatteryOffer(Other, 1, new long[] { 1001 }, 555005, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.None, p.Kind);
            Assert.Contains(p.Notes, n => n.Contains("其他玩家"));
        }

        // ---------- 怪物追击(5213 / 回执 5214) ----------

        [Fact]
        public void 追击窗口_候选是怪物playerId且带64位id()
        {
            var t = new PendingTracker();
            t.OnPursueMonsterOffer(Self, 5213, 1000);
            t.SetPursuitMonsters(new[] { 1080857L, 2287364L });

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.PursueMonster, p.Kind);
            Assert.Equal(5213, p.Sn);
            Assert.True(p.Actionable);
            Assert.Equal(2, p.Candidates.Count);
            Assert.Equal("monster", p.Candidates[0].Kind);
            Assert.Equal(1080857L, p.Candidates[0].LongId);
            Assert.Equal(2287364L, p.Candidates[1].LongId);
            Assert.Contains(p.Options, o => o.Contains("astral_pursue_monster") && o.Contains("monsterId"));
            Assert.Contains(p.Options, o => o.Contains("pass"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不追击"));
        }

        [Fact]
        public void 追击候选_名字按monster口径问()
        {
            var t = new PendingTracker();
            t.OnPursueMonsterOffer(Self, 5213, 1000);
            t.SetPursuitMonsters(new[] { 4242L });

            string kindSeen = null;
            var p = t.Build(Self, false, 0, 1200, (k, sn) => -1,
                (k, id) => { kindSeen = k; return "小怪"; });

            Assert.Equal("monster", kindSeen);
            Assert.Equal("小怪", p.Candidates[0].Name);
        }

        [Fact]
        public void 追击窗口_没有可追的怪时说明只能不追()
        {
            var t = new PendingTracker();
            t.OnPursueMonsterOffer(Self, 5213, 1000);
            t.SetPursuitMonsters(new long[0]);

            var p = Build(t, false, 0, 1200);

            Assert.Empty(p.Candidates);
            Assert.Contains(p.Options, o => o.Contains("pass"));
            Assert.Contains(p.Notes, n => n.Contains("没算出可追的怪"));
        }

        [Fact]
        public void 追击窗口_执行器能取到候选怪物用于校验()
        {
            var t = new PendingTracker();
            t.OnPursueMonsterOffer(Self, 5213, 1000);
            t.SetPursuitMonsters(new[] { 7L, 8L });

            long[] ids;
            Assert.True(t.TryGetMonsterIds(out ids));
            Assert.Equal(new[] { 7L, 8L }, ids);
        }

        [Fact]
        public void 追击回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnPursueMonsterOffer(Self, 5213, 1000);
            t.OnPursueMonsterDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        [Fact]
        public void 别人的追击窗口_既不报给我也不顶掉我的()
        {
            var t = new PendingTracker();
            t.SetSelf(Self);

            t.OnStopOrContinueOffer(Self, 5077, 1000);
            t.OnPursueMonsterOffer(Other, 5213, 1100);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.StopOrContinue, p.Kind);
            Assert.Equal(5077, p.Sn);
        }

        // ---------- 商人买卡(5323 / 回执 5324) ----------

        [Fact]
        public void 商人买卡窗口_带上价格与卡牌id()
        {
            var t = new PendingTracker();
            t.OnVendorCardOffer(Self, 21014, 5, 5323, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.VendorCard, p.Kind);
            Assert.Equal(5323, p.Sn);
            Assert.Equal(5, p.VendorPrice);
            Assert.Equal(21014, p.VendorCardId);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("astral_vendor_buy_card") && o.Contains("true"));
            Assert.Contains(p.Options, o => o.Contains("astral_vendor_buy_card") && o.Contains("false"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 不买"));
        }

        [Fact]
        public void 商人买卡窗口_执行器能取到价格()
        {
            var t = new PendingTracker();
            t.OnVendorCardOffer(Self, 21015, 7, 5323, 1000);

            int price;
            Assert.True(t.TryGetVendorPrice(out price));
            Assert.Equal(7, price);
        }

        [Fact]
        public void 商人买卡回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnVendorCardOffer(Self, 21016, 5, 5323, 1000);
            t.OnVendorCardDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }

        // ---------- 控制移动卡选点(5067 / 回执 5068) ----------

        [Fact]
        public void 选点窗口_给出可选上限()
        {
            var t = new PendingTracker();
            t.OnSelectPointOffer(Self, 6, 5067, 1000);

            var p = Build(t, false, 0, 1200);

            Assert.Equal(AgentPendingKind.SelectPoint, p.Kind);
            Assert.Equal(5067, p.Sn);
            Assert.Equal(6, p.MaxPoint);
            Assert.True(p.Actionable);
            Assert.Contains(p.Options, o => o.Contains("astral_select_point") && o.Contains("1..6"));
            Assert.Contains(p.Notes, n => n.Contains("超时不答 = 1 点"));
        }

        [Fact]
        public void 选点窗口_执行器能取到上限()
        {
            var t = new PendingTracker();
            t.OnSelectPointOffer(Self, 6, 5067, 1000);

            int max;
            Assert.True(t.TryGetMaxPoint(out max));
            Assert.Equal(6, max);
        }

        [Fact]
        public void 没有选点窗口时_查不到上限()
        {
            var t = new PendingTracker();
            int max;
            Assert.False(t.TryGetMaxPoint(out max));
        }

        [Fact]
        public void 选点回执_关掉窗口()
        {
            var t = new PendingTracker();
            t.OnSelectPointOffer(Self, 6, 5067, 1000);
            t.OnSelectPointDone(Self, 1100);

            Assert.Equal(AgentPendingKind.None, Build(t, false, 0, 1200).Kind);
        }
    }
}
