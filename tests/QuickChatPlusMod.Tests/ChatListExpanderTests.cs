using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using QuickChatPlusMod;
using Xunit;

namespace QuickChatPlusMod.Tests
{
    public sealed class ChatListExpanderTests
    {
        [Fact]
        public void ExpandMakesRoomPhraseVisibleInBattleList()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(10001, new[] { ChatType.Room }, new MapModeType[0]));
            chat.Infos.Add(Info(20001, new[] { ChatType.InGame }, new[] { MapModeType.Pve }));

            var mutation = ChatListExpander.Expand(chat,
                new[] { (int)MapModeType.Pve, (int)MapModeType.MutatorPve }, includeRoom: true, includeResult: true);

            Assert.Equal(3, mutation.Added);
            Assert.Equal(5, chat.Infos.Count);
            Assert.Equal(2, chat.GetBattleChats(MapModeType.Pve, 0).Count);
            Assert.Equal(2, chat.GetBattleChats(MapModeType.MutatorPve, 0).Count);
        }

        [Fact]
        public void ExpandedCloneUsesInGameTypeAndTargetMode()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(30001, new[] { ChatType.Result }, new MapModeType[0]));

            var mutation = ChatListExpander.Expand(chat, new[] { (int)MapModeType.CampaignPve },
                includeRoom: true, includeResult: true);

            var clone = Assert.IsType<ChatInfoConfigure>(Assert.Single(mutation.Clones));
            Assert.Contains(ChatType.InGame, clone.ChatType);
            Assert.DoesNotContain(ChatType.Result, clone.ChatType);
            Assert.Contains(MapModeType.CampaignPve, clone.MapModeType);
            Assert.Empty(clone.MapLimit);
        }

        [Fact]
        public void RestoreRemovesClonesAndRestoresMapLimits()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(10001, new[] { ChatType.Room }, new MapModeType[0]));
            chat.Infos.Add(Info(20001, new[] { ChatType.InGame }, new[] { MapModeType.Pve }));
            chat.Infos.Add(Info(23001, new[] { ChatType.InGame }, new[] { MapModeType.Pve }, new[] { 82010 }));

            var mutation = ChatListExpander.Expand(chat, new[] { (int)MapModeType.Pve }, true, true);
            ChatListExpander.Restore(chat, mutation);

            Assert.Equal(3, chat.Infos.Count);
            Assert.Single(chat.GetBattleChats(MapModeType.Pve, 0));
            Assert.Equal(2, chat.GetBattleChats(MapModeType.Pve, 82010).Count);
        }

        [Fact]
        public void MapLimitedPhraseAppearsOnEveryMapWithoutDuplicates()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(23001, new[] { ChatType.InGame }, new[] { MapModeType.Pve }, new[] { 82010 }));

            Assert.Empty(chat.GetBattleChats(MapModeType.Pve, 0));
            Assert.Single(chat.GetBattleChats(MapModeType.Pve, 82010));

            var mutation = ChatListExpander.Expand(chat, new[] { (int)MapModeType.Pve },
                includeRoom: false, includeResult: false);

            Assert.Equal(1, mutation.Unlimited);
            Assert.Single(chat.GetBattleChats(MapModeType.Pve, 0));
            Assert.Single(chat.GetBattleChats(MapModeType.Pve, 82010));
        }

        [Fact]
        public void ExpandIsIdempotentWhileClonesPresent()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(10001, new[] { ChatType.Room }, new MapModeType[0]));

            ChatListExpander.Expand(chat, new[] { (int)MapModeType.Pve }, true, true);
            int afterFirst = chat.Infos.Count;
            var second = ChatListExpander.Expand(chat, new[] { (int)MapModeType.Pve }, true, true);

            Assert.Equal(0, second.Added);
            Assert.Equal(afterFirst, chat.Infos.Count);
        }

        [Fact]
        public void IncludeRoomFalseLeavesRoomPhrasesOut()
        {
            var chat = new ChatConfigure();
            chat.Infos.Add(Info(10001, new[] { ChatType.Room }, new MapModeType[0]));
            chat.Infos.Add(Info(20001, new[] { ChatType.InGame }, new[] { MapModeType.Pve }));

            ChatListExpander.Expand(chat, new[] { (int)MapModeType.MutatorPve }, includeRoom: false, includeResult: true);

            var list = chat.GetBattleChats(MapModeType.MutatorPve, 0);
            var only = Assert.Single(list);
            Assert.Equal(20001, only.ChatID);
        }

        [Fact]
        public void EmptyConfigLeavesNothingToExpand()
        {
            var chat = new ChatConfigure();

            var mutation = ChatListExpander.Expand(chat, new[] { (int)MapModeType.Pve }, true, true);

            Assert.True(mutation.IsEmpty);
            Assert.Empty(chat.Infos);
        }

        /// <summary>
        /// 配置类型由游戏热更程序集提供，字段 setter 是私有的；测试里直接反射写 backing field，
        /// 避免经过 protobuf 的 Parser（其 netstandard 重载在 .NET 8 测试宿主里类型转发不完整）。
        /// </summary>
        private static ChatInfoConfigure Info(int chatId, ChatType[] types, MapModeType[] modes, int[] limits = null)
        {
            var info = new ChatInfoConfigure();
            SetField(info, "iD_", chatId);
            SetField(info, "chatID_", chatId);
            AddAll(info.ChatType, types);
            AddAll(info.MapModeType, modes);
            if (limits != null) AddAll(info.MapLimit, limits);
            return info;
        }

        private static void SetField(object target, string name, int value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("ChatInfoConfigure 缺少字段: " + name);
            field.SetValue(target, value);
        }

        private static void AddAll(object target, IList values)
        {
            var list = (IList)target;
            for (int i = 0; i < values.Count; i++) list.Add(values[i]);
        }
    }
}
