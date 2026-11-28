using System;
using System.Reflection;
using CesiumLoader.SDK.Networking;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// RpcGate 的纯逻辑内核测试。
    ///
    /// 这里<b>故意</b>只测 <see cref="RpcGateCore"/> 与权限位：RpcGate 的公开成员引用游戏类型
    /// (RPCMsgManager / NetManager / RPCAsyncResult)，而测试进程不加载 AstralParty.Runtime.dll，
    /// 任何触达这些类型的成员都会在 JIT 时抛 "could not load file or assembly"。
    /// 真正需要游戏类型的部分(字段替换、延迟完成)只能进游戏里验证。
    /// </summary>
    public class RpcGateTests
    {
        // ---------- 假容器: 形状与游戏的 RPCMsgManager 一致(public 实例字段 + 每 RPC 一个类) ----------

        public class FakeChooseSkinRpc
        {
            public virtual bool Call(int useAdorn) => true;
        }

        public class FakeRoleCardRpc
        {
            public virtual bool Call(int useAdorn) => true;
        }

        /// <summary>只出现在私有/静态字段里的类型: 不该被字段发现选中。</summary>
        public class OnlyPrivateRpc
        {
            public virtual bool Call() => true;
        }

        public class FakeManager
        {
            public FakeChooseSkinRpc ChooseSkinC2S = new FakeChooseSkinRpc();
            public FakeRoleCardRpc RoleCardChoiceResC2S = new FakeRoleCardRpc();
            private FakeRoleCardRpc _privateField = new FakeRoleCardRpc();
            private OnlyPrivateRpc _privateOnly = new OnlyPrivateRpc();
            public static FakeRoleCardRpc StaticField = new FakeRoleCardRpc();
            public static OnlyPrivateRpc StaticOnly = new OnlyPrivateRpc();
            public string NotAnRpc;
        }

        public class AmbiguousManager
        {
            public FakeChooseSkinRpc A = new FakeChooseSkinRpc();
            public FakeChooseSkinRpc B = new FakeChooseSkinRpc();
        }

        // ---------- 字段发现 ----------

        [Fact]
        public void FindField_ResolvesUniquePublicInstanceField()
        {
            FieldInfo f = RpcGateCore.FindField(typeof(FakeManager), typeof(FakeChooseSkinRpc));

            Assert.NotNull(f);
            Assert.Equal("ChooseSkinC2S", f.Name);
            Assert.Equal(typeof(FakeChooseSkinRpc), f.FieldType);
        }

        [Fact]
        public void FindField_IgnoresPrivateAndStaticFields()
        {
            // 只在私有/静态字段里出现的类型必须查不到 —— 换错东西比不换危险得多。
            // (FakeRoleCardRpc 同时有 public 字段, 所以它能找到, 且只能找到那个 public 的)
            Assert.Null(RpcGateCore.FindField(typeof(FakeManager), typeof(OnlyPrivateRpc)));
            FieldInfo f = RpcGateCore.FindField(typeof(FakeManager), typeof(FakeRoleCardRpc));
            Assert.NotNull(f);
            Assert.Equal("RoleCardChoiceResC2S", f.Name);
            Assert.True(f.IsPublic);
            Assert.False(f.IsStatic);
        }

        [Fact]
        public void FindField_ReturnsNullWhenTargetTypeAbsent()
        {
            // 游戏更新把字段改名/删掉时的路径: 必须返回 null(fail-safe 放行), 不能抛
            Assert.Null(RpcGateCore.FindField(typeof(FakeManager), typeof(Version)));
        }

        [Fact]
        public void FindField_ReturnsNullOnAmbiguity()
        {
            // 两个同类型字段无法判断该换哪个 —— 拦错字段比不拦危险得多, 必须放弃
            Assert.Null(RpcGateCore.FindField(typeof(AmbiguousManager), typeof(FakeChooseSkinRpc)));
        }

        [Fact]
        public void FindField_NullArguments_AreSafe()
        {
            Assert.Null(RpcGateCore.FindField(null, typeof(FakeChooseSkinRpc)));
            Assert.Null(RpcGateCore.FindField(typeof(FakeManager), null));
        }

        // ---------- 判定护栏 ----------

        [Fact]
        public void ShouldIntercept_NullPredicateNeverBlocks()
        {
            Assert.False(RpcGateCore.ShouldIntercept("req", (Func<string, bool>)null, "test"));
        }

        [Fact]
        public void ShouldIntercept_NullRequestNeverBlocks()
        {
            Assert.False(RpcGateCore.ShouldIntercept<string>(null, _ => true, "test"));
        }

        [Fact]
        public void ShouldIntercept_ReturnsPredicateResult()
        {
            Assert.True(RpcGateCore.ShouldIntercept("keep", s => s == "keep", "test"));
            Assert.False(RpcGateCore.ShouldIntercept("skip", s => s == "keep", "test"));
        }

        [Fact]
        public void ShouldIntercept_ThrowingPredicateFallsBackToSending()
        {
            // 判定体跑在游戏自己的调用栈里, 抛异常绝不能外泄;
            // 判不出来就必须让原版请求照发(返回 false), 而不是拦下或崩溃。
            bool blocked = RpcGateCore.ShouldIntercept<string>(
                "req", _ => throw new InvalidOperationException("boom"), "test-throw");

            Assert.False(blocked);
        }

        // ---------- 延迟完成队列 ----------

        [Fact]
        public void Deferred_IsDrainedInOrder()
        {
            RpcGateCore.ResetForTest();
            string order = null;

            RpcGateCore.Enqueue(() => order += "a");
            RpcGateCore.Enqueue(() => order += "b");
            int done = RpcGateCore.DrainDeferred();

            Assert.Equal(2, done);
            Assert.Equal("ab", order);
            Assert.Equal(0, RpcGateCore.PendingDeferred);
        }

        [Fact]
        public void Deferred_ThrowingActionDoesNotStopOthers()
        {
            RpcGateCore.ResetForTest();
            bool after = false;

            RpcGateCore.Enqueue(() => throw new InvalidOperationException("boom"));
            RpcGateCore.Enqueue(() => after = true);
            int done = RpcGateCore.DrainDeferred();

            Assert.True(after);
            Assert.Equal(2, done);
            Assert.Equal(0, RpcGateCore.PendingDeferred);
        }

        [Fact]
        public void Deferred_DrainOnEmptyQueueIsNoOp()
        {
            RpcGateCore.ResetForTest();
            Assert.Equal(0, RpcGateCore.DrainDeferred());
        }

        [Fact]
        public void Deferred_NullIsIgnored()
        {
            RpcGateCore.ResetForTest();
            RpcGateCore.Enqueue(null);
            Assert.Equal(0, RpcGateCore.PendingDeferred);
        }

        // ---------- 换/还原的语义 ----------

        [Fact]
        public void NeedsSwap_OnlyWhenFieldDoesNotHoldOurInstance()
        {
            var original = new FakeChooseSkinRpc();
            var mine = new FakeChooseSkinRpc();

            Assert.True(RpcGateCore.NeedsSwap(original, mine));
            Assert.False(RpcGateCore.NeedsSwap(mine, mine));
            Assert.True(RpcGateCore.NeedsSwap(null, mine));    // 空字段也要装上(启动早期可能还没赋值)
        }

        [Fact]
        public void NeedsRestore_OnlyWhenFieldStillHoldsOurInstance()
        {
            var mine = new FakeChooseSkinRpc();
            var someoneElse = new FakeChooseSkinRpc();

            Assert.True(RpcGateCore.NeedsRestore(mine, mine));
            // 别人(或游戏自己)已经改过 —— 不覆盖非我们造成的状态
            Assert.False(RpcGateCore.NeedsRestore(someoneElse, mine));
            Assert.False(RpcGateCore.NeedsRestore(null, mine));
        }
    }
}
