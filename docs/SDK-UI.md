# SDK UI 子系统（通知 / 窗口 / 覆盖层 / OnGUI 回调）

> 相关源码：`UI/UiService.cs`。渲染本身交给可替换的 `IUiBackend`（IMGUI / 无渲染环境都能降级）。

## 1. 设计原则

**注册与渲染分离**：mod 只负责"登记"，SDK 负责"什么时候画、画成什么样"。
即使没有任何渲染后端（例如单元测试进程），登记信息仍然完整可见（`NotificationTotal`、
`WindowCount`、`OverlayCount` 都在涨），只是不画出来。

## 2. 通知

```csharp
UiService.Notify("加载完成", UiNotificationLevel.Success, 3f, ctx);
UiService.Notify("出了问题", UiNotificationLevel.Warning);
UiService.Notify("信息", UiNotificationLevel.Info, 2.5f);

int alive = UiService.NotificationCount;      // 当前还在显示的
long total = UiService.NotificationTotal;     // 历史累计
int cleared = UiService.ClearNotifications(ctx);   // 只清自己的; 不传 owner 清全部
```

空文本会被忽略，不会占一个通知位。

## 3. 窗口

```csharp
ModWindowInfo w = UiService.OpenWindow("my-window", "标题", ctx);
bool open  = UiService.IsWindowOpen("my-window", ctx);
bool close = UiService.CloseWindow("my-window", ctx);   // 只能关自己的(owner 不匹配则失败)
int n      = UiService.RemoveWindows(ctx);              // 批量移除自己的
```

## 4. 覆盖层

```csharp
UiService.RegisterOverlay("hud", visible: true, owner: ctx);
UiService.SetOverlayVisible("hud", false, ctx);
int removed = UiService.RemoveOverlays(ctx);
```

## 5. OnGUI 回调

```csharp
UiService.RegisterGuiCallback(() => { GUI.Label(new Rect(10, 10, 300, 20), "hello"); }, ctx, "hud");
UiService.UnregisterGuiCallback(handler);
UiService.InvokeGuiCallbacks();     // 由 SDK 的 GUI 管线调用
```

回调在 Unity 的 `OnGUI` 时机执行，**不要在回调里做重活**（每帧可能多次调用）。

## 6. 输入冲突与"是否挡住游戏"

```csharp
bool any = UiService.IsAnyWindowOrOverlayOpen();   // 有窗口/覆盖层可见
bool mod = UiService.IsAnyModUiOpen();
bool window = UiService.IsAnyWindowOpen();         // 只有打开的窗口
bool block = UiService.ShouldBlockGameInput();     // 是否应该吃掉游戏输入
```

> 实测修正：早期版本把"有通知"也算作挡住输入，导致提示一出现就吞掉玩家的点击。
> 现在**通知不算挡住输入**；**HUD/标签类覆盖层也不算**——它们本身不接收输入，
> 若算进去会让自由相机的滚轮/热键被常驻 HUD 永久屏蔽。只有真正打开的 mod 窗口才屏蔽输入。

## 7. 生命周期：mod 卸载自动清理

`ModContext.Cleanup()` 会调用 `UiService.RemoveAllForMod(this)`，一次性移除该 mod 的
**窗口 + 覆盖层 + 通知 + OnGUI 回调**。这是兜底，mod 自己也在 `RegisterCleanup` 里主动关更好。

```csharp
ctx.RegisterCleanup(() => UiService.CloseWindow("my-window", ctx));
```

## 8. 诊断

```csharp
UiState s = UiService.Describe();
// s.WindowCount / s.OpenWindowCount / s.OverlayCount / s.VisibleOverlayCount
// s.NotificationCount / s.OpenWindows(列表)
UiService.ClearAll();   // 测试/重载用: 清空全部登记
```

## 9. uGUI 控件（WidgetService，可选）

`UiService` 只做状态与生命周期；需要真正往屏幕上放 Text / Slider 时用 `WidgetService`。
它用运行时反射创建 uGUI 组件（SDK 编译期只引用 `UnityEngine.CoreModule`），
解析不到 uGUI 时 `IsAvailable == false`，所有创建方法返回 `null`。

```csharp
var scope = WidgetService.CreateScope("hud");
ctx.RegisterCleanup(scope.Dispose);            // mod 卸载自动销毁

var canvas = scope.Track(WidgetService.Canvas("my-hud", sortingOrder: 1200));
var panel  = WidgetService.Panel(canvas.Rect, new Color(0f, 0f, 0f, 0.6f), rounded: true);
WidgetService.Place(panel.Rect, WidgetAnchor.TopLeft, new Vector2(16, -16), new Vector2(320, 120));

var title = WidgetService.Text(panel.Rect, "对局统计", 20, Color.white, WidgetAnchor.TopLeft);
WidgetService.Place(title.Rect, WidgetAnchor.TopLeft, new Vector2(12, -8), new Vector2(296, 28));

var bar = WidgetService.Slider(panel.Rect, 0f, 100f, 42f);
WidgetService.Place(bar.Rect, WidgetAnchor.Bottom, new Vector2(0, 12), new Vector2(296, 18));
WidgetService.EnsureEventSystem();             // Slider 拖动需要
```

| API | 说明 |
|---|---|
| `Canvas(name, sortingOrder = 1000, persist = true)` | 独立 Canvas（ScreenSpaceOverlay + CanvasScaler + GraphicRaycaster），默认跨场景保留 |
| `Panel(parent, color, rounded)` | 纯色 Image，`rounded` 使用运行时生成的九宫格贴图 |
| `Text(parent, text, size, color, anchor)` | 文本，默认 `raycastTarget = false`、自动换行 |
| `Slider(parent, min, max, value)` | 背景 + 填充，数值用 `SetValue` 修改 |
| `Badge(parent, tint, rounded)` | 带底色的 Image |
| `Anchor` / `Stretch` / `Place` | 设置锚点 / 铺满父节点 / 贴角定位 |
| `Destroy(rt)` / `DestroyAll()` | 销毁根节点（子节点递归销毁）/ 销毁本 SDK 创建的全部 Canvas |
| `EnsureEventSystem()` | 复用已有 EventSystem，没有才创建 |

返回值是 `WidgetHandle`（`Rect` 是 CoreModule 的 `RectTransform`，`Component` 是底层 uGUI 组件）：

```csharp
title.SetText("第 3 回合");
bar.SetValue(75f);
panel.SetColor(Color.black);
panel.SetActive(false);
```

约定：

- 所有 API 自动切主线程，可以直接在 RPC 回调里调用。
- 默认不接收鼠标命中（HUD 不挡游戏点击）；需要交互时显式 `SetRaycastTarget(true)`。
- 只做最小封装：展开 / 折叠、拖拽、换行估算、配色映射等业务逻辑由 mod 自己实现。
- `WidgetScope` 记录根节点，`Dispose()` 逆序销毁；配合 `ctx.RegisterCleanup` 满足卸载清理。
