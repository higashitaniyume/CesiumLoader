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
        // Phase 1 纯新增: 单价公式 / 座位兜底 / 名字重载
        // =====================================================================

        [Fact]
        public void ShopUnitPrice_NonTalentSubtractsDiscount()
        {
            Assert.Equal(20, GameEvents.ShopUnitPrice(gold: 50, discount: 30, talent: false, special: 0));
            Assert.Equal(50, GameEvents.ShopUnitPrice(gold: 50, discount: 0, talent: false, special: 0));
            Assert.Equal(-1, GameEvents.ShopUnitPrice(gold: 10, discount: 30, talent: false, special: 0));
            Assert.Equal(-1, GameEvents.ShopUnitPrice(gold: -1, discount: 0, talent: false, special: 0));
        }

        [Fact]
        public void ShopUnitPrice_TalentUsesSpecialAndClampsToZero()
        {
            Assert.Equal(20, GameEvents.ShopUnitPrice(gold: 50, discount: 10, talent: true, special: 30));
            Assert.Equal(0, GameEvents.ShopUnitPrice(gold: 50, discount: 40, talent: true, special: 30));
            Assert.Equal(-1, GameEvents.ShopUnitPrice(gold: 50, discount: 0, talent: true, special: -1));
        }

        [Fact]
        public void ResolveSlot_UsesSeatWhenValidElseFallback()
        {
            Assert.Equal(2, Players.ResolveSlot(2, 7));
            Assert.Equal(0, Players.ResolveSlot(0, 7));
            Assert.Equal(15, Players.ResolveSlot(15, 7));
            Assert.Equal(7, Players.ResolveSlot(-1, 7));
            Assert.Equal(7, Players.ResolveSlot(16, 7));
        }

        // =====================================================================
        // Phase 2 纯新增: 步数确认 / 结算与追击数据类 / 事件数
        // =====================================================================

        [Fact]
        public void EventCount_IncludesNewEvents()
        {
            Assert.Equal(32, GameEvents.EventCount);
        }

        [Fact]
        public void NewPayloadTypes_DescribeThemselvesWithoutThrowing()
        {
            Assert.False(string.IsNullOrEmpty(new HeroHpChangeInfo { PlayerId = 1, MaxHp = 10 }.ToString()));
            Assert.False(string.IsNullOrEmpty(new PlayerFinalStats { PlayerId = 1 }.ToString()));
            Assert.False(string.IsNullOrEmpty(new FightSnapshot { BattleId = 1 }.ToString()));
        }

        [Fact]
        public void Movement_ObserveAndPumpConfirmSteps()
        {
            var totals = new Dictionary<long, long> { { 11L, 0L } };
            var pending = new List<GameEvents.MovementPending>();
            bool incomplete = false;
            var actor = new object();
            var before = new GameEvents.MovementState { Actor = actor, Land = 100, Steps = 5, Next = 101, Valid = true };

            // 观察一次目标 101 应排队; 重复观察应去重。
            Assert.True(GameEvents.MovementObserveCore(totals, pending, ref incomplete, 11L, 101, before, 1000L));
            Assert.False(GameEvents.MovementObserveCore(totals, pending, ref incomplete, 11L, 101, before, 1000L));
            Assert.Single(pending);

            // 确认: 到达目标且步数 -1, 且非最后一步, 应 +1 并清空。
            var after = new GameEvents.MovementState { Actor = actor, Land = 101, Steps = 4, Next = 102, Valid = true };
            var confirmed = new List<int>();
            bool any = GameEvents.MovementPumpCore(totals, pending, ref incomplete, _ => after, 1001L,
                (pid, total) => confirmed.Add(total));
            Assert.True(any);
            Assert.Equal(1L, totals[11L]);
            Assert.Equal(new[] { 1 }, confirmed);
            Assert.Empty(pending);
            Assert.False(incomplete);
        }

        [Fact]
        public void Movement_PumpDiscardsTimeout()
        {
            var totals = new Dictionary<long, long> { { 11L, 0L } };
            var pending = new List<GameEvents.MovementPending>();
            bool incomplete = false;
            var actor = new object();
            var before = new GameEvents.MovementState { Actor = actor, Land = 100, Steps = 5, Next = 101, Valid = true };
            Assert.True(GameEvents.MovementObserveCore(totals, pending, ref incomplete, 11L, 101, before, 1000L));

            // 读不到确认(仍在旧地块)且超时 10 秒后应丢弃并标不完整, 不加步数。
            var stale = new GameEvents.MovementState { Actor = actor, Land = 100, Steps = 5, Next = 101, Valid = true };
            GameEvents.MovementPumpCore(totals, pending, ref incomplete, _ => stale, 11000L, null);
            Assert.Equal(0L, totals[11L]);
            Assert.Empty(pending);
            Assert.True(incomplete);
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
