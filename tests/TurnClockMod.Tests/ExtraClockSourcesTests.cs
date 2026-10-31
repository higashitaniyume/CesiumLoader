using System;
using System.Collections.Generic;
using System.Reflection;
using TurnClockMod;
using Xunit;

namespace TurnClockMod.Tests
{
    /// <summary>
    /// 界面倒计时登记表的离线用例: 用与游戏同名的假界面类型验证
    /// "按类型名 + 字段名找计时器"和"字段改名后的兜底"。
    /// </summary>
    public class ExtraClockSourcesTests
    {
        /// <summary>成员名与游戏里的 UnityTimer.Timer 一致(登记表只认类型名 Timer)。</summary>
        private sealed class Timer
        {
            public float duration = 20f;
        }

        // 嵌套类型的 Type.Name 就是这两个名字, 正好与游戏里的面板同名 —— 但成员各不相同。

        private static class NamedFields
        {
            public sealed class RoomHeroPanel
            {
                public Timer timeLimiter = new Timer();
            }

            public sealed class RoomWaitPanel
            {
                public Timer masterKickTimer = new Timer();
                public Timer roomTimer = new Timer();
            }

            public sealed class BattleSettlementPanel
            {
                public Timer operateTimer = new Timer();
            }

            public sealed class StoryWindow
            {
                public Timer AutoTimer = new Timer();
            }
        }

        private static class RenamedFields
        {
            public sealed class RoomHeroPanel
            {
                public Timer renamedClock = new Timer();
                public Timer secondClock = new Timer();
            }

            public sealed class RoomWaitPanel
            {
                public float roomTimer = 1f;
            }
        }

        private static ClockSourceTable Table()
        {
            return new ClockSourceTable();
        }

        // ---------- 登记表本身 ----------

        [Fact]
        public void Default_RegistersTheFourScreens()
        {
            var keys = new List<string>();
            for (int i = 0; i < ClockSourceTable.Default.Length; i++) keys.Add(ClockSourceTable.Default[i].Key);

            Assert.Equal(new[] { "heroSelect", "settlement", "roomWait", "storyAuto" }, keys.ToArray());
        }

        [Fact]
        public void Default_KeepsTheStoryTimerQuiet()
        {
            ClockSource story = Table().Match("StoryWindow");

            Assert.NotNull(story);
            Assert.False(story.Trace);
            Assert.Equal("AutoTimer", story.Fields[0]);
        }

        [Fact]
        public void Match_FindsSourceByRuntimeTypeName()
        {
            ClockSourceTable table = Table();

            ClockSource hero = table.Match("RoomHeroPanel");
            ClockSource room = table.Match("RoomWaitPanel");

            Assert.NotNull(hero);
            Assert.Equal("heroSelect", hero.Key);
            Assert.NotNull(room);
            Assert.Equal(2, room.Fields.Length);
        }

        [Fact]
        public void Match_ReturnsNullForUnknownType()
        {
            Assert.Null(Table().Match("SomeOtherPanel"));
            Assert.Null(Table().Match(null));
            Assert.Null(Table().Match(""));
        }

        // ---------- 按字段名取计时器 ----------

        [Fact]
        public void Resolve_UsesRegisteredFieldName()
        {
            List<TimerSlot> slots = Table().Resolve(typeof(NamedFields.RoomHeroPanel));

            Assert.Single(slots);
            Assert.Equal("timeLimiter", slots[0].FieldName);
            Assert.False(slots[0].FromFallback);
            Assert.Equal("heroSelect", slots[0].Key);

            var host = new NamedFields.RoomHeroPanel();
            Assert.Same(host.timeLimiter, slots[0].Read(host));
        }

        [Fact]
        public void Resolve_FindsEveryRegisteredFieldOnOneHost()
        {
            List<TimerSlot> slots = Table().Resolve(typeof(NamedFields.RoomWaitPanel));

            Assert.Equal(2, slots.Count);
            Assert.Equal("roomTimer", slots[0].FieldName);
            Assert.Equal("masterKickTimer", slots[1].FieldName);
        }

        [Fact]
        public void Resolve_FallsBackToAnyTimerFieldWhenNamesChanged()
        {
            List<TimerSlot> slots = Table().Resolve(typeof(RenamedFields.RoomHeroPanel));

            Assert.Equal(2, slots.Count);
            Assert.All(slots, slot => Assert.True(slot.FromFallback));

            // 兜底结果按字段名排序, 保证同一次运行里两次探测一致
            Assert.Equal("renamedClock", slots[0].FieldName);
            Assert.Equal("secondClock", slots[1].FieldName);
        }

        [Fact]
        public void Resolve_IgnoresFieldWithRegisteredNameThatIsNotATimer()
        {
            // 字段名对上了但类型不是 Timer(比如被改成 float 进度): 不认领, 也不兜底到别的东西
            List<TimerSlot> slots = Table().Resolve(typeof(RenamedFields.RoomWaitPanel));

            Assert.Empty(slots);
        }

        [Fact]
        public void Resolve_ReturnsEmptyForUnregisteredType()
        {
            Assert.Empty(Table().Resolve(typeof(string)));
            Assert.Empty(Table().Resolve(null));
        }

        [Fact]
        public void Resolve_CachesPerType()
        {
            ClockSourceTable table = Table();

            List<TimerSlot> first = table.Resolve(typeof(NamedFields.BattleSettlementPanel));
            List<TimerSlot> second = table.Resolve(typeof(NamedFields.BattleSettlementPanel));

            Assert.Same(first, second);
        }

        [Fact]
        public void TimerSlot_ReadHandlesMissingHost()
        {
            List<TimerSlot> slots = Table().Resolve(typeof(NamedFields.StoryWindow));

            Assert.Single(slots);
            Assert.Null(slots[0].Read(null));

            // 字段还没赋值(界面刚创建)时读出来是 null, 调用方据此跳过
            var host = new NamedFields.StoryWindow();
            host.AutoTimer = null;
            Assert.Null(slots[0].Read(host));
        }

        [Fact]
        public void IsTimerField_OnlyAcceptsTheGameTimerType()
        {
            FieldInfo timerField = typeof(NamedFields.StoryWindow).GetField("AutoTimer");
            FieldInfo floatField = typeof(RenamedFields.RoomWaitPanel).GetField("roomTimer", BindingFlags.Instance | BindingFlags.Public);

            Assert.True(ClockSourceTable.IsTimerField(timerField));
            Assert.False(ClockSourceTable.IsTimerField(floatField));
            Assert.False(ClockSourceTable.IsTimerField(null));
        }
    }
}
