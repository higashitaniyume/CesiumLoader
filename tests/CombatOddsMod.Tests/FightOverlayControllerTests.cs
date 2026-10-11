using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// FightWindow 覆盖层状态机测试。真实 FairyGUI 绑定要进游戏目视确认,
    /// 但"何时建/复用/换父/隐藏"的编排在此用假反射器完全覆盖。
    /// </summary>
    public class FightOverlayControllerTests
    {
        // 假反射器: 记录调用, 可编排"窗口是否显示 / 容器对象 / 建标签是否成功"。
        private sealed class FakeReflector : IFightOverlayReflector
        {
            public bool Showing = true;
            public object Pane = new object();
            public bool CreateReturnsNull = false;

            public int CreateCalls;
            public int AddCalls;
            public string LastText;
            public bool? LastVisible;

            // 记录每个 label 当前挂在哪个 pane(模拟 FairyGUI parent)。
            public readonly Dictionary<object, object> Parent = new Dictionary<object, object>();

            public bool IsFightShowing() { return Showing; }
            public object GetFightPane() { return Pane; }

            public object CreateLabel()
            {
                CreateCalls++;
                if (CreateReturnsNull) return null;
                return new object();
            }

            public bool LabelInPane(object label, object pane)
            {
                return Parent.TryGetValue(label, out var p) && ReferenceEquals(p, pane);
            }

            public void AddChildToPane(object pane, object label) { AddCalls++; Parent[label] = pane; }
            public void SetText(object label, string text) { LastText = text; }
            public void SetVisible(object label, bool visible) { LastVisible = visible; }
        }

        [Fact]
        public void Disabled_HidesAndNeverCreates()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(enabled: false, text: "x");
            Assert.Equal(0, f.CreateCalls);
            Assert.False(c.HasLabel);
        }

        [Fact]
        public void NotShowing_HidesAndNeverCreates()
        {
            var f = new FakeReflector { Showing = false };
            var c = new FightOverlayController(f);
            c.Update(true, "x");
            Assert.Equal(0, f.CreateCalls);
            Assert.False(c.HasLabel);
        }

        [Fact]
        public void PaneNull_HidesAndNeverCreates()
        {
            var f = new FakeReflector { Pane = null };
            var c = new FightOverlayController(f);
            c.Update(true, "x");
            Assert.Equal(0, f.CreateCalls);
        }

        [Fact]
        public void FirstShow_CreatesAddsSetsTextAndVisible()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "期望伤害 3.5");
            Assert.Equal(1, f.CreateCalls);
            Assert.Equal(1, f.AddCalls);
            Assert.Equal("期望伤害 3.5", f.LastText);
            Assert.True(f.LastVisible);
            Assert.True(c.HasLabel);
        }

        [Fact]
        public void SecondUpdate_SamePane_ReusesLabelNoReAdd()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            c.Update(true, "b");
            Assert.Equal(1, f.CreateCalls);   // 不重建
            Assert.Equal(1, f.AddCalls);      // 不重挂
            Assert.Equal("b", f.LastText);    // 文本更新
        }

        [Fact]
        public void PaneChanged_RecreatesFreshLabel()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            f.Pane = new object();            // 重开窗口 → 新容器(旧标签可能已随旧容器销毁)
            c.Update(true, "a");
            Assert.Equal(2, f.CreateCalls);   // 丢弃旧引用, 重建新标签
            Assert.Equal(2, f.AddCalls);      // 挂到新容器
        }

        [Fact]
        public void LabelRemovedByGame_RecreatesFreshLabel()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            f.Parent.Clear();                 // 模拟游戏在战斗子阶段之间把我们的标签移除/销毁
            c.Update(true, "b");
            Assert.Equal(2, f.CreateCalls);   // 检测到脱离容器 → 重建
            Assert.Equal(2, f.AddCalls);
            Assert.Equal("b", f.LastText);    // 新标签仍更新文本
            Assert.True(f.LastVisible);
        }

        [Fact]
        public void CreateReturnsNull_NoThrow_RetriesNextTime()
        {
            var f = new FakeReflector { CreateReturnsNull = true };
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            Assert.Equal(1, f.CreateCalls);
            Assert.False(c.HasLabel);
            f.CreateReturnsNull = false;      // 下次能建成功
            c.Update(true, "a");
            Assert.Equal(2, f.CreateCalls);
            Assert.True(c.HasLabel);
            Assert.Equal(1, f.AddCalls);
        }

        [Fact]
        public void Hide_SetsInvisibleWhenLabelExists()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            c.Hide();
            Assert.False(f.LastVisible);
        }

        [Fact]
        public void ShowThenNotShowing_HidesLabel()
        {
            var f = new FakeReflector();
            var c = new FightOverlayController(f);
            c.Update(true, "a");
            Assert.True(f.LastVisible);
            f.Showing = false;
            c.Update(true, "a");
            Assert.False(f.LastVisible);      // 隐藏
            Assert.Equal(1, f.CreateCalls);   // 不销毁, 只隐藏
        }
    }
}
