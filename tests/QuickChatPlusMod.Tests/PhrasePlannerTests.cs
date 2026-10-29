using System.Collections.Generic;
using QuickChatPlusMod;
using Xunit;

namespace QuickChatPlusMod.Tests
{
    public sealed class PhrasePlannerTests
    {
        private static PhraseEntry Entry(int chatId, int[] modes, bool inGame = false, bool room = false, bool result = false)
        {
            return new PhraseEntry
            {
                ChatId = chatId,
                InGame = inGame,
                Room = room,
                Result = result,
                Modes = modes,
            };
        }

        [Fact]
        public void AddsPhraseOnlyForModeThatDoesNotHaveIt()
        {
            var entries = new List<PhraseEntry> { Entry(1, new[] { 4 }, inGame: true) };

            var plan = PhrasePlanner.Plan(entries, new[] { 4, 12 }, includeRoom: false, includeResult: false);

            var addition = Assert.Single(plan);
            Assert.Equal(12, addition.Mode);
            Assert.Equal(1, addition.ChatId);
            Assert.Equal(0, addition.EntryIndex);
        }

        [Fact]
        public void DoesNotRepeatPhraseAlreadyVisibleInMode()
        {
            var entries = new List<PhraseEntry> { Entry(1, new[] { 4 }, inGame: true) };

            var plan = PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: true);

            Assert.Empty(plan);
        }

        [Fact]
        public void SkipsRoomPhrasesWhenDisabled()
        {
            var entries = new List<PhraseEntry> { Entry(10001, new int[0], room: true) };

            Assert.Empty(PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: false, includeResult: true));
            var addition = Assert.Single(PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: true));
            Assert.Equal(10001, addition.ChatId);
        }

        [Fact]
        public void SkipsResultPhrasesWhenDisabled()
        {
            var entries = new List<PhraseEntry> { Entry(30001, new int[0], result: true) };

            Assert.Empty(PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: false));
            var addition = Assert.Single(PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: true));
            Assert.Equal(30001, addition.ChatId);
        }

        [Fact]
        public void SharedChatIdIsAddedOncePerMode()
        {
            // 20012 这类 ID 同时出现在房间与结算类别里。
            var entries = new List<PhraseEntry>
            {
                Entry(20012, new int[0], room: true),
                Entry(20012, new int[0], result: true),
            };

            var plan = PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: true);

            var addition = Assert.Single(plan);
            Assert.Equal(20012, addition.ChatId);
        }

        [Fact]
        public void AddsOnePhrasePerMode()
        {
            var entries = new List<PhraseEntry> { Entry(7, new int[0], result: true) };

            var plan = PhrasePlanner.Plan(entries, new[] { 4, 6, 9 }, includeRoom: false, includeResult: true);

            Assert.Equal(3, plan.Count);
            Assert.Equal(new[] { 4, 6, 9 }, new[] { plan[0].Mode, plan[1].Mode, plan[2].Mode });
        }

        [Fact]
        public void DeduplicatesRepeatedEntries()
        {
            var entries = new List<PhraseEntry>
            {
                Entry(5, new int[0], room: true),
                Entry(5, new int[0], room: true),
            };

            var plan = PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: false);

            Assert.Single(plan);
        }

        [Fact]
        public void KeepsEntryOrder()
        {
            var entries = new List<PhraseEntry>
            {
                Entry(30, new int[0], room: true),
                Entry(10, new int[0], room: true),
                Entry(20, new int[0], room: true),
            };

            var plan = PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: false);

            Assert.Equal(new[] { 30, 10, 20 }, new[] { plan[0].ChatId, plan[1].ChatId, plan[2].ChatId });
        }

        [Fact]
        public void EmptyTargetModesProducesNothing()
        {
            var entries = new List<PhraseEntry> { Entry(1, new int[0], room: true) };

            Assert.Empty(PhrasePlanner.Plan(entries, new int[0], includeRoom: true, includeResult: true));
        }

        [Fact]
        public void IgnoresEntriesWithoutChatCategories()
        {
            var entries = new List<PhraseEntry> { Entry(1, new[] { 4 }) };

            Assert.Empty(PhrasePlanner.Plan(entries, new[] { 4 }, includeRoom: true, includeResult: true));
        }
    }
}
