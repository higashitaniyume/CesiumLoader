using CesiumLoader.SDK.UserInterface;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// WidgetService 的离线行为: 测试宿主没有 uGUI 程序集, 只能验证"解析不到类型时安全退化"
    /// 与句柄/作用域的空值语义; 真正建 Canvas 需要游戏内运行环境。
    /// </summary>
    public class WidgetServiceTests
    {
        [Fact]
        public void Canvas_WithoutUGui_ReturnsNullInsteadOfThrowing()
        {
            if (WidgetService.IsAvailable) return; // 宿主恰好加载了 uGUI 时跳过

            Assert.Null(WidgetService.Canvas("hud"));
            Assert.Null(WidgetService.Panel(null, Color.white));
            Assert.Null(WidgetService.Text(null, "x", 12, Color.white, WidgetAnchor.Center));
            Assert.Null(WidgetService.Slider(null, 0f, 1f, 0.5f));
            Assert.Null(WidgetService.Badge(null, Color.red));
        }

        [Fact]
        public void UnavailableReason_IsReportedWhenMissing()
        {
            if (WidgetService.IsAvailable) return;

            Assert.False(string.IsNullOrEmpty(WidgetService.UnavailableReason));
        }

        [Fact]
        public void Handle_WithNullRect_IsInert()
        {
            var handle = new WidgetHandle(null, null);

            Assert.False(handle.IsAlive);
            Assert.Null(handle.GameObject);
            Assert.False(handle.SetText("x"));
            Assert.False(handle.SetValue(1f));
            Assert.False(handle.SetColor(Color.white));
            Assert.False(handle.SetActive(true));
            Assert.False(handle.SetRaycastTarget(true));
            Assert.False(handle.SetSize(10f, 10f));
            Assert.False(handle.SetAnchoredPosition(1f, 2f));
            Assert.False(handle.Destroy());
        }

        [Fact]
        public void Layout_WithNullRect_ReturnsFalse()
        {
            Assert.False(WidgetService.Anchor(null, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero));
            Assert.False(WidgetService.Stretch(null));
            Assert.False(WidgetService.Place(null, WidgetAnchor.TopLeft, Vector2.zero, Vector2.one));
            Assert.False(WidgetService.Destroy(null));
        }

        [Fact]
        public void DestroyAll_WithoutCanvases_ReturnsZero()
        {
            Assert.Equal(0, WidgetService.DestroyAll());
        }

        [Fact]
        public void Scope_TracksNothingForNull_AndDisposeIsIdempotent()
        {
            var scope = WidgetService.CreateScope("test-scope");

            Assert.Equal("test-scope", scope.Name);
            Assert.False(scope.IsDisposed);
            Assert.Equal(0, scope.Count);

            Assert.Null(scope.Track((RectTransform)null));
            Assert.Null(scope.Track((WidgetHandle)null));
            Assert.Equal(0, scope.Count);

            scope.Dispose();
            Assert.True(scope.IsDisposed);

            scope.Dispose();
            Assert.True(scope.IsDisposed);
            Assert.Equal(0, scope.Count);
        }

        [Fact]
        public void Scope_DefaultName_IsNotEmpty()
        {
            using (var scope = WidgetService.CreateScope())
            {
                Assert.False(string.IsNullOrEmpty(scope.Name));
            }
        }
    }
}
