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
bool block = UiService.ShouldBlockGameInput();     // 是否应该吃掉游戏输入
```

> 实测修正：早期版本把"有通知"也算作挡住输入，导致提示一出现就吞掉玩家的点击。
> 现在**通知不算挡住输入**，只有真正可见的窗口/覆盖层才算，并且必须配合输入独占实际生效。

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
