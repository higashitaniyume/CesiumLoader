using Xunit;

namespace PveWaveForecastMod.Tests
{
    public class WaveModelTests
    {
        [Fact]
        public void CategoryTakesHighestMonsterType()
        {
            var wave = new WaveInfo();
            wave.Monsters.Add(new MonsterTag { Name = "a", Category = WaveCategory.Normal });
            wave.Monsters.Add(new MonsterTag { Name = "b", Category = WaveCategory.Elite });
            Assert.Equal(WaveCategory.Elite, wave.Category);
            wave.Monsters.Add(new MonsterTag { Name = "c", Category = WaveCategory.Boss });
            Assert.Equal(WaveCategory.Boss, wave.Category);
        }

        [Fact]
        public void MonsterLineUsesCategoryPrefixes()
        {
            var wave = new WaveInfo();
            wave.Monsters.Add(new MonsterTag { Name = "孩子王嘎呜", Category = WaveCategory.Boss });
            wave.Monsters.Add(new MonsterTag { Name = "采集小熊", Category = WaveCategory.Elite });
            wave.Monsters.Add(new MonsterTag { Name = "闹钟猫头鹰", Category = WaveCategory.Normal });
            Assert.Equal("首领·孩子王嘎呜 / 精英·采集小熊 / 普通·闹钟猫头鹰", wave.MonsterLine);
        }

        [Fact]
        public void PerLandLineOnlyShowsAssignedMonster()
        {
            var wave = new WaveInfo { ExactLands = true };
            wave.Monsters.Add(new MonsterTag { Id = 1, Name = "玩具恐龙", Category = WaveCategory.Elite });
            wave.Monsters.Add(new MonsterTag { Id = 2, Name = "闹钟猫头鹰", Category = WaveCategory.Normal, Count = 3 });
            wave.MonstersByLand[10] = new System.Collections.Generic.List<int> { 1 };
            wave.MonstersByLand[11] = new System.Collections.Generic.List<int> { 2 };
            Assert.Equal("精英·玩具恐龙", wave.MonsterLineForLand(10));
            Assert.Equal("普通·闹钟猫头鹰", wave.MonsterLineForLand(11));
            Assert.Equal("", wave.MonsterLineForLand(12));
        }

        [Fact]
        public void ShortLineFallsBackToEventId()
        {
            var wave = new WaveInfo { EventId = 32131, Progress = 3, Title = "" };
            Assert.Equal("R3 波次 32131", wave.ShortLine);
        }
    }
}
