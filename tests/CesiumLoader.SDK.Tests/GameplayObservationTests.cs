using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 对局观测类事件的数据结构与时序读取。
    /// 说明: GameEvents 的 RPC 包装只能在游戏内验证, 这里覆盖可以离线确定的部分 ——
    /// 下行序号读取(队列形状/命令匹配/已消费/异常保守失败)与成交/报价的数据换算。
    /// </summary>
    public class GameplayObservationTests
    {
        // =====================================================================
        // 下行序号
        // =====================================================================

        [Fact]
        public void DownSn_ReadsHeadOfQueueWhenCommandMatches()
        {
            var rpc = new FakeRpc().Enqueue(new FakeMessage(5216, 9001L, consumed: false));

            Assert.Equal(9001L, DownSnReader.Read(rpc, 5216));
        }

        [Fact]
        public void DownSn_RejectsConsumedMessage()
        {
            var rpc = new FakeRpc().Enqueue(new FakeMessage(5216, 9001L, consumed: true));

            Assert.Equal(0L, DownSnReader.Read(rpc, 5216));
        }

        [Fact]
        public void DownSn_RejectsDifferentCommand()
        {
            var rpc = new FakeRpc().Enqueue(new FakeMessage(5212, 9001L, consumed: false));

            Assert.Equal(0L, DownSnReader.Read(rpc, 5216));
        }

        [Fact]
        public void DownSn_OnlyLooksAtHead()
        {
            var rpc = new FakeRpc()
                .Enqueue(new FakeMessage(5212, 111L, consumed: false))
                .Enqueue(new FakeMessage(5216, 222L, consumed: false));

            Assert.Equal(0L, DownSnReader.Read(rpc, 5216));
        }

        [Fact]
        public void DownSn_EmptyQueueReturnsZero()
        {
            Assert.Equal(0L, DownSnReader.Read(new FakeRpc(), 5216));
        }

        [Fact]
        public void DownSn_NullOrInvalidInputsReturnZero()
        {
            Assert.Equal(0L, DownSnReader.Read(null, 5216));
            Assert.Equal(0L, DownSnReader.Read(new FakeRpc(), 0));
            Assert.Equal(0L, DownSnReader.Read(new object(), 5216));
        }

        [Fact]
        public void DownSn_SupportsMessagesCarryingFieldsDirectly()
        {
            var rpc = new FlatRpc().Enqueue(new FlatMessage { CMDID = 5030, DOWNSN = 777L, IsConsumed = false });

            Assert.Equal(777L, DownSnReader.Read(rpc, 5030));
        }

        [Fact]
        public void DownSn_FlatShapeAlsoHonoursConsumed()
        {
            var rpc = new FlatRpc().Enqueue(new FlatMessage { CMDID = 5030, DOWNSN = 777L, IsConsumed = true });

            Assert.Equal(0L, DownSnReader.Read(rpc, 5030));
        }

        // =====================================================================
        // 成交数据换算
        // =====================================================================

        [Fact]
        public void ShopSettlement_ResolvesBuySlotsToCardIds()
        {
            var settlement = new ShopSettlement
            {
                Cards = new[] { 1001, 1002, 1003 },
                BuyCards = new[] { 0, 2 },
                IsPve = true
            };

            Assert.Equal(new[] { 1001, 1003 }, settlement.ResolveBoughtCardIds());
        }

        [Fact]
        public void ShopSettlement_OutOfRangeSlotsBecomeZero()
        {
            var settlement = new ShopSettlement
            {
                Cards = new[] { 1001 },
                BuyCards = new[] { -1, 0, 5 }
            };

            Assert.Equal(new[] { 0, 1001, 0 }, settlement.ResolveBoughtCardIds());
        }

        [Fact]
        public void ShopSettlement_EmptyBuysReturnEmptyArray()
        {
            Assert.Empty(new ShopSettlement().ResolveBoughtCardIds());
            Assert.Empty(new ShopSettlement { Cards = new[] { 1 } }.ResolveBoughtCardIds());
        }

        // =====================================================================
        // 报价数据
        // =====================================================================

        [Fact]
        public void ShopOffer_TotalGoldPrefersDiscount()
        {
            Assert.Equal(30, new ShopOffer { Gold = 50, DisCountGold = 30 }.TotalGold);
            Assert.Equal(50, new ShopOffer { Gold = 50 }.TotalGold);
            Assert.Equal(50, new ShopOffer { Gold = 50, DisCountGold = 0 }.TotalGold);
        }

        [Fact]
        public void PayloadTypes_DescribeThemselvesWithoutThrowing()
        {
            Assert.False(string.IsNullOrEmpty(new ShopOffer { CardIds = new int[0] }.ToString()));
            Assert.False(string.IsNullOrEmpty(new RelicOffer { RelicIds = new int[0] }.ToString()));
            Assert.False(string.IsNullOrEmpty(new ShopSettlement().ToString()));
        }

        // =====================================================================
        // 与游戏同形的假对象
        // =====================================================================

        private sealed class FakeRpc
        {
            // 字段名与游戏 RPCMsgManager 一致, DownSnReader 按名字反射。
            private readonly object _lockObj = new object();
            private readonly List<FakeMessage> _OnPushRPCMsg = new List<FakeMessage>();

            public FakeRpc Enqueue(FakeMessage message)
            {
                _OnPushRPCMsg.Add(message);
                return this;
            }
        }

        private sealed class FakeMessage
        {
            public FakeMessage(int command, long downSn, bool consumed)
            {
                frame = new FakeFrame { CMDID = command, DOWNSN = downSn, IsConsumed = consumed };
            }

            public FakeFrame frame { get; private set; }
        }

        private sealed class FakeFrame
        {
            public int CMDID;
            public long DOWNSN;
            public bool IsConsumed;
        }

        private sealed class FlatRpc
        {
            private readonly object _lockObj = new object();
            private readonly List<FlatMessage> _OnPushRPCMsg = new List<FlatMessage>();

            public FlatRpc Enqueue(FlatMessage message)
            {
                _OnPushRPCMsg.Add(message);
                return this;
            }
        }

        private sealed class FlatMessage
        {
            public int CMDID { get; set; }
            public long DOWNSN { get; set; }
            public bool IsConsumed { get; set; }
        }
    }
}
