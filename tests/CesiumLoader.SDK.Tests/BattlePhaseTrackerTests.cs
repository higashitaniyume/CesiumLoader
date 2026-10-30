using CesiumLoader.SDK.Gameplay;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 战斗子阶段推导。服务器把 BattleRole.Point 当普通 proto3 整数下发,
    /// “未投骰”与“投出 0 点”都是 0, 只能由战斗快照与投骰广播的先后顺序区分。
    /// </summary>
    public class BattlePhaseTrackerTests
    {
        private const long BattleId = 9001;
        private const long Attacker = 1001;
        private const long Defender = 1002;

        private static BattlePhaseTracker NewBattle(int point = 0)
        {
            var tracker = new BattlePhaseTracker();
            tracker.ObserveBattle(BattleId, Attacker, Defender, isEnd: false, attackerPoint: point);
            return tracker;
        }

        [Fact]
        public void NewBattle_WaitsForAttackerRoll()
        {
            var tracker = NewBattle();

            Assert.Equal(BattlePhase.AttackerRollPending, tracker.Phase);
            Assert.False(tracker.AttackerHasRolled);
            Assert.Equal(-1, tracker.AttackerPoint);
        }

        [Fact]
        public void DiceZero_IsAValidRolledPoint()
        {
            var tracker = NewBattle();

            bool changed = tracker.ObserveAttackerDice(Attacker, 0);

            Assert.True(changed);
            Assert.Equal(BattlePhase.AttackerRolled, tracker.Phase);
            Assert.True(tracker.AttackerHasRolled);
            Assert.Equal(0, tracker.AttackerPoint);
        }

        [Fact]
        public void DiceAboveZero_IsAValidRolledPoint()
        {
            var tracker = NewBattle();

            Assert.True(tracker.ObserveAttackerDice(Attacker, 4));
            Assert.Equal(4, tracker.AttackerPoint);
        }

        [Fact]
        public void SameDiceTwice_DoesNotSignalAnotherChange()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 0);

            Assert.False(tracker.ObserveAttackerDice(Attacker, 0));
            Assert.Equal(0, tracker.AttackerPoint);
        }

        [Fact]
        public void SnapshotWithPositivePoint_ProvesTheRollHappened()
        {
            // 重连后可能只拿到战斗快照: 骰点 1..6 只可能出现在投骰之后。
            var tracker = NewBattle(point: 5);

            Assert.Equal(BattlePhase.AttackerRolled, tracker.Phase);
            Assert.Equal(5, tracker.AttackerPoint);
        }

        [Fact]
        public void SnapshotWithZeroPointAfterRolledZero_KeepsTheRolledState()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 0);

            tracker.ObserveBattle(BattleId, Attacker, Defender, isEnd: false, attackerPoint: 0);

            Assert.Equal(BattlePhase.AttackerRolled, tracker.Phase);
            Assert.Equal(0, tracker.AttackerPoint);
        }

        [Fact]
        public void DiceFromAnotherPlayer_IsIgnored()
        {
            var tracker = NewBattle();

            Assert.False(tracker.ObserveAttackerDice(Defender, 3));
            Assert.Equal(BattlePhase.AttackerRollPending, tracker.Phase);
            Assert.Equal(-1, tracker.AttackerPoint);
        }

        [Fact]
        public void DiceWithoutBattle_IsIgnored()
        {
            var tracker = new BattlePhaseTracker();

            Assert.False(tracker.ObserveAttackerDice(Attacker, 3));
            Assert.Equal(BattlePhase.None, tracker.Phase);
        }

        [Fact]
        public void BattleEnd_SwitchesToEndedAndKeepsTheRolledPoint()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 2);

            tracker.ObserveBattle(BattleId, Attacker, Defender, isEnd: true, attackerPoint: 2);

            Assert.Equal(BattlePhase.Ended, tracker.Phase);
            Assert.Equal(2, tracker.AttackerPoint);
        }

        [Fact]
        public void NextBattle_ResetsTheRolledState()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 0);
            tracker.ObserveBattle(BattleId, Attacker, Defender, isEnd: true, attackerPoint: 0);

            tracker.ObserveBattle(BattleId + 1, Attacker, Defender, isEnd: false, attackerPoint: 0);

            Assert.Equal(BattlePhase.AttackerRollPending, tracker.Phase);
            Assert.Equal(-1, tracker.AttackerPoint);
        }

        [Fact]
        public void ChangedBattleId_WhileActive_StartsAFreshBattle()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 6);

            tracker.ObserveBattle(BattleId + 1, Attacker, Defender, isEnd: false, attackerPoint: 0);

            Assert.Equal(BattlePhase.AttackerRollPending, tracker.Phase);
            Assert.Equal(-1, tracker.AttackerPoint);
        }

        [Fact]
        public void ChangedAttacker_StartsAFreshBattle()
        {
            var tracker = NewBattle();
            tracker.ObserveAttackerDice(Attacker, 6);

            tracker.ObserveBattle(BattleId, Defender, Attacker, isEnd: false, attackerPoint: 0);

            Assert.Equal(BattlePhase.AttackerRollPending, tracker.Phase);
            Assert.Equal(-1, tracker.AttackerPoint);
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            var tracker = NewBattle(point: 3);

            tracker.Reset();

            Assert.Equal(BattlePhase.None, tracker.Phase);
            Assert.False(tracker.AttackerHasRolled);
            Assert.Equal(-1, tracker.AttackerPoint);
        }
    }
}
