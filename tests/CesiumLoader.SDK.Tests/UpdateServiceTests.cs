using System;
using System.Collections.Generic;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 每帧回调服务: 不要求 mod 创建 MonoBehaviour, 由 SDK 主线程泵驱动。
    /// 重点验证: 订阅/退订、mod 卸载自动退订、单个回调异常隔离、遍历期间增删安全。
    /// </summary>
    public class UpdateServiceTests : IDisposable
    {
        private readonly ModContext _owner;

        public UpdateServiceTests()
        {
            UpdateService.Clear();
            ModRegistry.Unregister("UpdateSvcMod");
            _owner = ModRegistry.Register("UpdateSvcMod", "1.0.0", "", "", typeof(UpdateServiceTests).Assembly);
        }

        public void Dispose()
        {
            UpdateService.Clear();
            ModRegistry.Unregister("UpdateSvcMod");
        }

        [Fact]
        public void SubscribeUpdate_IncrementsCountAndRaisesTick()
        {
            int calls = 0;
            UpdateService.SubscribeUpdate(() => calls++, _owner);

            Assert.Equal(1, UpdateService.UpdateSubscriberCount);
            Assert.Equal(0, UpdateService.LateUpdateSubscriberCount);

            long before = UpdateService.UpdateTicks;
            UpdateService.RaiseUpdate();

            Assert.Equal(1, calls);
            Assert.Equal(before + 1, UpdateService.UpdateTicks);
        }

        [Fact]
        public void SubscribeLateUpdate_IsIndependentFromUpdate()
        {
            int updates = 0, lates = 0;
            UpdateService.SubscribeUpdate(() => updates++, _owner);
            UpdateService.SubscribeLateUpdate(() => lates++, _owner);

            UpdateService.RaiseLateUpdate();

            Assert.Equal(0, updates);
            Assert.Equal(1, lates);
        }

        [Fact]
        public void MultipleSubscribers_AllRunInOrder()
        {
            var order = new List<int>();
            UpdateService.SubscribeUpdate(() => order.Add(1), _owner);
            UpdateService.SubscribeUpdate(() => order.Add(2), _owner);
            UpdateService.SubscribeUpdate(() => order.Add(3), _owner);

            UpdateService.RaiseUpdate();

            Assert.Equal(new[] { 1, 2, 3 }, order.ToArray());
        }

        [Fact]
        public void UnsubscribeViaHandle_RemovesSubscription()
        {
            int calls = 0;
            var handle = UpdateService.SubscribeUpdate(() => calls++, _owner);

            Assert.False(handle.IsDisposed);
            handle.Dispose();
            Assert.True(handle.IsDisposed);

            UpdateService.RaiseUpdate();
            Assert.Equal(0, calls);
            Assert.Equal(0, UpdateService.UpdateSubscriberCount);
        }

        [Fact]
        public void DisposingTwice_IsSafe()
        {
            var handle = UpdateService.SubscribeUpdate(() => { }, _owner);

            handle.Dispose();
            handle.Dispose();

            Assert.Equal(0, UpdateService.UpdateSubscriberCount);
        }

        [Fact]
        public void UnsubscribeByDelegate_RemovesAllMatching()
        {
            int calls = 0;
            Action callback = () => calls++;
            UpdateService.SubscribeUpdate(callback, _owner);
            UpdateService.SubscribeUpdate(callback, _owner);

            Assert.True(UpdateService.UnsubscribeUpdate(callback));
            Assert.False(UpdateService.UnsubscribeUpdate(callback));

            UpdateService.RaiseUpdate();
            Assert.Equal(0, calls);
        }

        [Fact]
        public void RemoveAllUpdateCallbacks_RemovesOnlyThatOwner()
        {
            int mine = 0, other = 0;
            ModRegistry.Unregister("UpdateSvcOther");
            var otherCtx = ModRegistry.Register("UpdateSvcOther", "1.0.0");

            UpdateService.SubscribeUpdate(() => mine++, _owner);
            UpdateService.SubscribeUpdate(() => other++, otherCtx);
            UpdateService.SubscribeLateUpdate(() => mine++, _owner);

            int removed = UpdateService.RemoveAllUpdateCallbacks(_owner);

            Assert.Equal(2, removed);
            UpdateService.RaiseUpdate();
            UpdateService.RaiseLateUpdate();
            Assert.Equal(0, mine);
            Assert.Equal(1, other);
            Assert.Equal(0, UpdateService.RemoveAllUpdateCallbacks(null));

            ModRegistry.Unregister("UpdateSvcOther");
        }

        [Fact]
        public void ThrowingSubscriber_DoesNotBreakOthers()
        {
            int calls = 0;
            UpdateService.SubscribeUpdate(() => { throw new InvalidOperationException("boom"); }, _owner);
            UpdateService.SubscribeUpdate(() => calls++, _owner);

            long before = UpdateService.ExceptionCount;
            UpdateService.RaiseUpdate();

            Assert.Equal(1, calls);
            Assert.Equal(before + 1, UpdateService.ExceptionCount);
        }

        [Fact]
        public void SubscribeDuringCallback_DoesNotBreakIteration()
        {
            int late = 0;
            int calls = 0;

            UpdateService.SubscribeUpdate(() =>
            {
                calls++;
                if (calls == 1) UpdateService.SubscribeUpdate(() => late++, _owner);
            }, _owner);

            UpdateService.RaiseUpdate();   // 快照语义: 新订阅下一帧才生效
            Assert.Equal(1, calls);
            Assert.Equal(0, late);

            UpdateService.RaiseUpdate();
            Assert.Equal(2, calls);
            Assert.Equal(1, late);
        }

        [Fact]
        public void UnsubscribeDuringCallback_StillSafeForThisFrame()
        {
            int calls = 0;
            UpdateSubscription self = null;

            // 回调里删除自己: 本帧仍按快照走完, 下一帧起不再收到
            self = UpdateService.SubscribeUpdate(() => { calls++; if (self != null) self.Dispose(); }, _owner);
            UpdateService.SubscribeUpdate(() => calls++, _owner);

            UpdateService.RaiseUpdate();

            Assert.Equal(2, calls);                                   // 快照语义: 本帧两个都跑
            Assert.Equal(1, UpdateService.UpdateSubscriberCount);      // 自删的那个已移除

            UpdateService.RaiseUpdate();
            Assert.Equal(3, calls);                                   // 下一帧只剩一个

            UpdateService.RemoveAllUpdateCallbacks(_owner);
            Assert.Equal(0, UpdateService.UpdateSubscriberCount);
        }

        [Fact]
        public void NullCallbackOrOwner_IsSafe()
        {
            Assert.NotNull(UpdateService.SubscribeUpdate(null, _owner));
            Assert.Equal(0, UpdateService.UpdateSubscriberCount);

            int calls = 0;
            UpdateService.SubscribeUpdate(() => calls++, null);   // 显式无归属
            UpdateService.RaiseUpdate();
            Assert.Equal(1, calls);
        }

        [Fact]
        public void DuplicateSubscriptions_AreTrackedSeparately()
        {
            int calls = 0;
            Action callback = () => calls++;
            var a = UpdateService.SubscribeUpdate(callback, _owner);
            var b = UpdateService.SubscribeUpdate(callback, _owner);

            Assert.NotEqual(a.Id, b.Id);
            a.Dispose();

            UpdateService.RaiseUpdate();
            Assert.Equal(1, calls);
        }
    }
}
