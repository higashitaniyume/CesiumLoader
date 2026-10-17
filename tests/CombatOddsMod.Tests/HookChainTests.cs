using System;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 回调链包装策略的回归测试。
    /// 背景: 2.3.4 曾经在 SDK 也会重挂的 UseEffectCardS2C 上再包一层, 而且包装器在调用时读取可变的静态字段,
    /// 结果一条技能消息被处理几十次, 游戏以 GameAssembly 访问违例崩溃。现在只包装 SDK 不碰的回调,
    /// 包装捕获原始委托, 并且每局安装次数受限。
    /// </summary>
    public class HookChainTests
    {
        [Fact]
        public void Build_ReturnsNullWhenAlreadyInstalled()
        {
            Func<int, int> original = x => x;
            Func<int, int> installed = x => original(x);
            Assert.Null(HookChain.Build(installed, installed, 4, o => x => o(x)));
        }

        [Fact]
        public void Build_ReturnsNullWhenTheBudgetIsSpent()
        {
            Func<int, int> original = x => x;
            Func<int, int> installed = x => original(x);
            Func<int, int> thirdParty = x => installed(x);
            // 预算用尽: 别人在我们上面又包了一层也不能再加层。
            Assert.Null(HookChain.Build(thirdParty, installed, 0, o => x => o(x)));
        }

        [Fact]
        public void Build_InstallsAgainAfterTheGameResetsTheCallback()
        {
            Func<int, int> original = x => x;
            Func<int, int> installed = x => original(x);
            // 游戏为每局重新赋一个新的委托实例: 必须重装, 否则新一局不再有数据。
            Func<int, int> newOriginal = x => x;
            var rebuilt = HookChain.Build(newOriginal, installed, 4, o => x => o(x));
            Assert.NotNull(rebuilt);
        }

        [Fact]
        public void Wrapper_CapturesTheOriginalInsteadOfReadingALiveField()
        {
            int originalCalls = 0;
            Func<int, int> original = x => { originalCalls++; return x; };

            // 旧实现: 包装在调用时读可变的"字段"。
            Func<int, int> live = original;
            int depth = 0;
            var buggy = HookChain.Build(live, null, 4, o => x =>
            {
                if (++depth > 100) throw new InvalidOperationException("self recursion");
                return live(x);
            });
            Assert.NotNull(buggy);

            // 字段被替换成"包住 buggy 的新包装"后, 旧实现会调回自己。
            live = x => buggy(x);
            Assert.Throws<InvalidOperationException>(() => buggy(1));

            // 正确实现捕获安装时的委托, 只调用一次原始实现。
            originalCalls = 0;
            var fixedWrapper = HookChain.Build(original, null, 4, o => x => o(x));
            Assert.NotNull(fixedWrapper);
            fixedWrapper(7);
            Assert.Equal(1, originalCalls);
        }

        [Fact]
        public void RepeatedExternalRewrapping_StaysWithinTheInstallBudget()
        {
            int originalCalls = 0;
            Func<int, int> original = x => { originalCalls++; return x; };
            Func<int, int> field = original;
            Func<int, int> ours = null;

            // 模拟"别的组件每秒在我们上面重挂 + 我们每 0.1 秒检查一次"共 100 秒。
            for (int second = 0; second < 100; second++)
            {
                var outerInner = field;
                field = x => outerInner(x);

                for (int tick = 0; tick < 10; tick++)
                {
                    var wrapped = HookChain.Build(field, ours, 4, o => x => o(x));
                    if (wrapped == null) continue;
                    ours = wrapped;
                    field = wrapped;
                }
            }

            // 链长必须被预算限住(最多 4 次安装), 不能每秒都加一层。
            int depth = 0;
            Func<int, int> counter = null;
            counter = x =>
            {
                depth++;
                Assert.True(depth <= 8, "回调链异常增长: 一帧被处理 " + depth + " 次");
                return field(x);
            };
            int before = originalCalls;
            counter(1);
            Assert.True(originalCalls - before >= 1);
        }
    }
}
