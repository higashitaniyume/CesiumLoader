using Xunit;

namespace CombatOddsMod.Tests
{
    public class CombatOddsPerspectiveTests
    {
        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, false, false)]
        [InlineData(false, true, true)]
        [InlineData(false, true, false)]
        public void MonsterDefender_NeverShowsDefenseOrDodgeRecommendation(bool iAmDefender, bool iAmAttacker, bool showForOthers)
        {
            // 防守方是怪物时，怪物无法闪避，必须绝不显示防守方（防/闪提示）视角
            bool show = CombatOddsPerspective.ShouldShowDefender(iAmDefender, iAmAttacker, showForOthers, defenderIsMonster: true);
            Assert.False(show);
        }

        [Fact]
        public void PlayerDefender_SelfDefending_AlwaysShowsDefender()
        {
            // 自己作为防守方（无论是否开启观战他人），必须显示防守提示
            Assert.True(CombatOddsPerspective.ShouldShowDefender(iAmDefender: true, iAmAttacker: false, showForOthers: false, defenderIsMonster: false));
            Assert.True(CombatOddsPerspective.ShouldShowDefender(iAmDefender: true, iAmAttacker: false, showForOthers: true, defenderIsMonster: false));
        }

        [Fact]
        public void PlayerDefender_SelfAttacking_HidesDefenderPerspective()
        {
            // 我是攻击方打其他玩家时，我自己的界面只看攻击击杀率，不看防守方提示
            Assert.False(CombatOddsPerspective.ShouldShowDefender(iAmDefender: false, iAmAttacker: true, showForOthers: true, defenderIsMonster: false));
            Assert.False(CombatOddsPerspective.ShouldShowDefender(iAmDefender: false, iAmAttacker: true, showForOthers: false, defenderIsMonster: false));
        }

        [Fact]
        public void PlayerDefender_SpectatingOthers_RespectsShowForOthers()
        {
            // 观战两名其他玩家战斗
            Assert.True(CombatOddsPerspective.ShouldShowDefender(iAmDefender: false, iAmAttacker: false, showForOthers: true, defenderIsMonster: false));
            Assert.False(CombatOddsPerspective.ShouldShowDefender(iAmDefender: false, iAmAttacker: false, showForOthers: false, defenderIsMonster: false));
        }

        [Fact]
        public void AttackerPerspective_AttackingMonsterOrPlayer_ShowsAttacker()
        {
            // 我作为攻击方打怪或打人，必须显示攻击击杀率
            Assert.True(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: false, iAmAttacker: true, showForOthers: false));
            Assert.True(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: false, iAmAttacker: true, showForOthers: true));
        }

        [Fact]
        public void AttackerPerspective_SelfDefending_HidesAttackerPerspective()
        {
            // 我是被攻击方时，在场只看自己的防御提示，不看攻击方击杀率
            Assert.False(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: true, iAmAttacker: false, showForOthers: true));
            Assert.False(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: true, iAmAttacker: false, showForOthers: false));
        }

        [Fact]
        public void AttackerPerspective_SpectatingOthers_RespectsShowForOthers()
        {
            // 观战队友打怪或其他人打怪，开启 ShowForOthers 时显示攻击方击杀率
            Assert.True(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: false, iAmAttacker: false, showForOthers: true));
            Assert.False(CombatOddsPerspective.ShouldShowAttacker(iAmDefender: false, iAmAttacker: false, showForOthers: false));
        }
    }
}
