using System.Collections.Generic;
using Xunit;

namespace UnlockFrameRateMod.Tests
{
    /// <summary>
    /// 用真实 protobuf 类型跑一遍补表逻辑: 覆盖 private set 的反射写入、
    /// RepeatedField 追加、MapField 文案写入与去重/移除。
    /// </summary>
    public class FrameRateTableTests
    {
        private static SettingsConfigure NewSettings()
        {
            var settings = new SettingsConfigure();
            settings.RefreshRates.Add(FrameRateTable.CreateRate(1, 120, 1204, null));
            return settings;
        }

        [Fact]
        public void Ensure_AppendsEveryOptionWithLocalizedText()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();

            int added = FrameRateTable.Ensure(settings, strings, null);

            Assert.Equal(FrameRateOptions.Values.Length, added);
            Assert.Equal(1 + FrameRateOptions.Values.Length, settings.RefreshRates.Count);

            var first = settings.RefreshRates[1];
            Assert.Equal(144, first.Value);
            Assert.True(FrameRateOptions.IsExtraDescriptionId(first.DescriptionID));

            var label = strings.LocalDict[first.DescriptionID];
            Assert.Equal("144帧", label.Simplified);
            Assert.Equal("144幀", label.Traditional);
            Assert.Equal("144 FPS", label.English);
            Assert.Equal("144 FPS", label.Japanese);
            Assert.Equal("144 FPS", label.Korean);

            var last = settings.RefreshRates[settings.RefreshRates.Count - 1];
            Assert.Equal(FrameRateOptions.Unlimited, last.Value);
            Assert.Equal("不限帧", strings.LocalDict[last.DescriptionID].Simplified);
        }

        [Fact]
        public void Ensure_AssignsDistinctIdsInsideReservedRange()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();

            FrameRateTable.Ensure(settings, strings, null);

            var ids = new HashSet<int>();
            for (int i = 1; i < settings.RefreshRates.Count; i++)
            {
                int id = settings.RefreshRates[i].DescriptionID;
                Assert.InRange(id, FrameRateOptions.DescriptionIdBase, FrameRateOptions.DescriptionIdBase + FrameRateOptions.DescriptionIdSpan - 1);
                Assert.True(ids.Add(id), "文案 Id 重复: " + id);
                Assert.True(strings.LocalDict.ContainsKey(id));
            }
        }

        [Fact]
        public void Ensure_IsIdempotent()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();

            Assert.Equal(FrameRateOptions.Values.Length, FrameRateTable.Ensure(settings, strings, null));
            int before = settings.RefreshRates.Count;

            Assert.Equal(0, FrameRateTable.Ensure(settings, strings, null));
            Assert.Equal(before, settings.RefreshRates.Count);
        }

        [Fact]
        public void Ensure_SkipsValuesTheGameAlreadyHas()
        {
            var settings = NewSettings();
            settings.RefreshRates.Add(FrameRateTable.CreateRate(2, 144, 1205, null));
            var strings = new STRSettingsConfigure();

            Assert.Equal(FrameRateOptions.Values.Length - 1, FrameRateTable.Ensure(settings, strings, null));

            int count144 = 0;
            for (int i = 0; i < settings.RefreshRates.Count; i++)
            {
                if (settings.RefreshRates[i].Value == 144) count144++;
            }
            Assert.Equal(1, count144);
        }

        [Fact]
        public void Remove_OnlyDropsOurEntries()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();
            FrameRateTable.Ensure(settings, strings, null);

            int removed = FrameRateTable.Remove(settings, strings);

            Assert.Equal(FrameRateOptions.Values.Length, removed);
            Assert.Single(settings.RefreshRates);
            Assert.Equal(120, settings.RefreshRates[0].Value);
            Assert.Empty(strings.LocalDict);
        }

        [Fact]
        public void Ensure_RestoresLabelsWhenStringTableWasRebuilt()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();
            FrameRateTable.Ensure(settings, strings, null);
            int before = settings.RefreshRates.Count;

            // 模拟文案表被重建: 条目还在, 文案没了
            strings.LocalDict.Clear();

            Assert.Equal(0, FrameRateTable.Ensure(settings, strings, null));
            Assert.Equal(before, settings.RefreshRates.Count);
            Assert.Equal(FrameRateOptions.Values.Length, strings.LocalDict.Count);
            Assert.Equal("144帧", strings.LocalDict[settings.RefreshRates[1].DescriptionID].Simplified);
        }

        [Fact]
        public void Remove_WhenNothingWasAdded_KeepsGameEntries()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();

            Assert.Equal(0, FrameRateTable.Remove(settings, strings));
            Assert.Single(settings.RefreshRates);
        }

        [Fact]
        public void Ensure_MovesToAnotherRangeWhenBaseIsTaken()
        {
            var settings = NewSettings();
            var strings = new STRSettingsConfigure();
            strings.LocalDict[FrameRateOptions.DescriptionIdBase] = FrameRateTable.CreateString(FrameRateOptions.DescriptionIdBase, 60, null);

            FrameRateTable.Ensure(settings, strings, null);

            var first = settings.RefreshRates[1];
            Assert.NotEqual(FrameRateOptions.DescriptionIdBase, first.DescriptionID);
            Assert.True(FrameRateOptions.IsExtraDescriptionId(first.DescriptionID));
            // 原有文案不能被覆盖
            Assert.Equal("60帧", strings.LocalDict[FrameRateOptions.DescriptionIdBase].Simplified);
        }
    }
}
