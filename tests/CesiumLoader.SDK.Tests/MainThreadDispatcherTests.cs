using System;
using System.Collections.Generic;
using System.Threading;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 主线程调度队列。mod 由加载器的 boot 线程调用, 而 Unity API 只能在主线程访问,
    /// 所以这段纯托管队列逻辑是整个 SDK 的关键路径, 必须能脱离游戏验证。
    /// </summary>
    public class MainThreadDispatcherTests
    {
        [Fact]
        public void Post_QueuesWithoutExecuting()
        {
            var dispatcher = new MainThreadDispatcher();

            dispatcher.Post(() => { });

            Assert.Equal(1, dispatcher.PendingCount);
            Assert.Equal(1, dispatcher.PostedCount);
            Assert.Equal(0, dispatcher.ExecutedCount);
        }

        [Fact]
        public void Drain_ExecutesInFifoOrderAndEmptiesQueue()
        {
            var dispatcher = new MainThreadDispatcher();
            var order = new List<int>();

            dispatcher.Post(() => order.Add(1));
            dispatcher.Post(() => order.Add(2));
            dispatcher.Post(() => order.Add(3));

            int drained = dispatcher.Drain();

            Assert.Equal(3, drained);
            Assert.Equal(new[] { 1, 2, 3 }, order.ToArray());
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.Equal(3, dispatcher.ExecutedCount);
        }

        [Fact]
        public void Drain_IsSafeOnEmptyQueue()
        {
            var dispatcher = new MainThreadDispatcher();

            Assert.Equal(0, dispatcher.Drain());
            Assert.Equal(0, dispatcher.Drain());
        }

        [Fact]
        public void ThrowingCallback_DoesNotStopTheRest()
        {
            var dispatcher = new MainThreadDispatcher();
            var order = new List<string>();
            var errors = new List<Exception>();
            dispatcher.OnError = e => errors.Add(e);

            dispatcher.Post(() => order.Add("before"));
            dispatcher.Post(() => { throw new InvalidOperationException("boom"); });
            dispatcher.Post(() => order.Add("after"));

            int drained = dispatcher.Drain();

            Assert.Equal(3, drained);
            Assert.Equal(new[] { "before", "after" }, order.ToArray());
            Assert.Single(errors);
            Assert.Equal("boom", errors[0].Message);
            Assert.Equal(1, dispatcher.FailedCount);
            Assert.Equal(2, dispatcher.ExecutedCount);
        }

        [Fact]
        public void ThrowingOnErrorHandler_IsSwallowed()
        {
            var dispatcher = new MainThreadDispatcher();
            dispatcher.OnError = _ => { throw new Exception("handler boom"); };

            dispatcher.Post(() => { throw new Exception("callback boom"); });

            dispatcher.Drain();   // 不应把异常抛回主线程循环
            Assert.Equal(1, dispatcher.FailedCount);
        }

        [Fact]
        public void Post_NullAction_IsIgnored()
        {
            var dispatcher = new MainThreadDispatcher();

            dispatcher.Post(null);

            Assert.Equal(0, dispatcher.PendingCount);
            Assert.Equal(0, dispatcher.PostedCount);
        }

        [Fact]
        public void Clear_DropsPendingWithoutRunningThem()
        {
            var dispatcher = new MainThreadDispatcher();
            bool ran = false;

            dispatcher.Post(() => ran = true);
            dispatcher.Clear();

            Assert.Equal(0, dispatcher.PendingCount);
            Assert.False(ran);
            Assert.Equal(0, dispatcher.ExecutedCount);
        }

        [Fact]
        public void PostAndWait_OnMainThread_ExecutesInline()
        {
            var dispatcher = new MainThreadDispatcher(() => true);
            bool ran = false;

            Assert.True(dispatcher.PostAndWait(() => ran = true));

            Assert.True(ran);
            Assert.Equal(0, dispatcher.PendingCount);   // 内联执行, 不入队
            Assert.Equal(1, dispatcher.ExecutedCount);
        }

        [Fact]
        public void PostAndWait_OnMainThread_ReportsExceptionAsFailure()
        {
            var dispatcher = new MainThreadDispatcher(() => true);
            Exception captured = null;
            dispatcher.OnError = e => captured = e;

            Assert.False(dispatcher.PostAndWait(() => { throw new InvalidOperationException("x"); }));

            Assert.NotNull(captured);
            Assert.Equal(1, dispatcher.FailedCount);
        }

        [Fact]
        public void PostAndWait_NotOnMainThread_BlocksUntilDrained()
        {
            var dispatcher = new MainThreadDispatcher(() => false);
            int value = 0;

            // 模拟主线程: 另一个线程周期性 Drain
            var pump = new Thread(() =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && dispatcher.ExecutedCount == 0)
                {
                    dispatcher.Drain();
                    Thread.Sleep(1);
                }
            });
            pump.IsBackground = true;
            pump.Start();

            Assert.True(dispatcher.PostAndWait(() => value = 42, 4000));
            pump.Join(1000);

            Assert.Equal(42, value);
            Assert.Equal(1, dispatcher.ExecutedCount);
        }

        [Fact]
        public void PostAndWait_Timeout_ReturnsFalseWithoutThrowing()
        {
            var dispatcher = new MainThreadDispatcher(() => false);

            Assert.False(dispatcher.PostAndWait(() => { }, 30));

            Assert.Equal(1, dispatcher.PendingCount);   // 回调仍在队列里, 等下一次 Drain
            Assert.Equal(0, dispatcher.ExecutedCount);
        }

        [Fact]
        public void PostAndWait_NullActionOrNoWait_ReturnsFalse()
        {
            var dispatcher = new MainThreadDispatcher(() => false);

            Assert.False(dispatcher.PostAndWait(null));
            Assert.False(dispatcher.PostAndWait(() => { }, 0));
        }

        [Fact]
        public void Post_IsSafeFromManyThreads()
        {
            var dispatcher = new MainThreadDispatcher();
            int executed = 0;

            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                var thread = new Thread(() =>
                {
                    for (int i = 0; i < 250; i++) dispatcher.Post(() => Interlocked.Increment(ref executed));
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads) thread.Join(5000);

            Assert.Equal(1000, dispatcher.PendingCount);
            Assert.Equal(1000, dispatcher.Drain());
            Assert.Equal(1000, executed);
        }
    }
}
