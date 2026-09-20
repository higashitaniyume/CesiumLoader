# SDK 输入子系统（按键 / 鼠标 / 光标 / 输入独占）

> 相关源码：`Services/InputBackend.cs`、`Services/InputService.cs`。

## 1. 三层结构

```
mod 代码
  └─ InputService               ← 门面 + 输入独占仲裁(谁先申请谁独占)
       └─ IInputBackend          ← 可替换接缝
            ├─ UnityInputReflectionBackend  ← 反射读 UnityEngine.Input(首选)
            ├─ Win32InputBackend            ← GetAsyncKeyState/GetCursorPos 兜底
            └─ CompositeInputBackend        ← 主 + 兜底依次尝试
```

**为什么不是直接调 `UnityEngine.Input`**：本作是 IL2CPP + HybridCLR 热更，输入 API 属于
"可能被裁剪"的一类；反射 + Win32 兜底能让 SDK 在两种情况下都可用。

## 2. 读按键

```csharp
if (InputService.IsKeyDown(KeyCode.F1))      { /* 按下这一帧 */ }
if (InputService.IsKeyHeld(KeyCode.W))       { /* 一直按着 */ }
if (InputService.IsKeyUp(KeyCode.F1))        { /* 松开这一帧 */ }
if (InputService.IsKeyPressed(KeyCode.Space)){ /* 同 IsKeyDown, Unity 习惯命名 */ }
if (InputService.IsAnyKeyDown(KeyCode.F1, KeyCode.F2)) { }

float axis   = InputService.GetAxis("Mouse X");   // 失败返回 0
float scroll = InputService.GetMouseScroll();
bool  held   = InputService.IsMouseButtonHeld(0);
bool  click  = InputService.IsMouseButtonPressed(0);
Vector2 pos;
if (InputService.TryGetMousePosition(out pos)) { }
```

> `InputService` 提供的这些读取**不区分按键来自游戏还是 mod**。想避免"按 F1 顺便触发了游戏功能"，
> 请配合下面的输入独占，或让游戏自己处理。

## 3. 每帧读取一次的语义

所有 `IsKeyDown` / `IsKeyUp` / `IsMouseButtonPressed` 都是"本帧"语义，**一帧内重复调用会重复返回 true**，
不会消费掉事件。需要"只处理一次"时请自行在 mod 内按帧号去重：

```csharp
int _lastFrame;
void OnUpdate()
{
    int frame = UnityTime.FrameCount;
    if (frame == _lastFrame) return;
    _lastFrame = frame;
    if (InputService.IsKeyDown(KeyCode.F1)) Toggle();
}
```

## 4. 输入独占（避免和游戏抢键）

```csharp
InputService.CaptureKeyboard(ctx, reason: "自由相机");
InputService.CaptureMouse(ctx, reason: "自由相机");
InputService.Capture(ctx, keyboard: true, mouse: true, force: false, reason: "...");

InputService.ReleaseKeyboard(ctx);
InputService.ReleaseMouse(ctx);
InputService.ReleaseAll(ctx);
InputService.ForceReleaseAll();   // 无视所有权强制释放(出问题时的兜底)
```

- `owner` 传 `ModContext` 时，mod 卸载会自动释放（`ModContext.Cleanup()` 已登记）。
- `force: false` 时**不抢占**别人已经拿到的独占，返回 `false`。
- `CaptureGrantedCount` / `CaptureDeniedCount` 可用于诊断"为什么我的按键没生效"。

本作的实测情况：**游戏没有把按键独占交给任何框架**，SDK 的独占只是给 mod 之间的协商用，
不会真的阻止游戏收到按键。因此自由相机只绑定 F1，不做鼠标接管（见 `docs/mod-FreeCameraMod.md`）。

## 5. 光标（ECall 重灾区）

```csharp
InputService.SetCursorVisible(false);          // 返回是否成功
InputService.SetCursorLockMode(CursorLockMode.Locked);
InputService.SaveAndLockCursor();              // 保存并锁定+隐藏(进入自由视角前调用)
InputService.RestoreCursor();                  // 退出时还原
string s = InputService.DescribeCursor();      // "lock=Locked visible=False"
```

`Cursor.*` 是 ECall，SDK 内部已全部经 `UnityCall` 隔离，离线时：
`SetCursorVisible` 返回 `false`、`DescribeCursor()` 返回默认值字符串，**不会抛异常**。

`SaveAndLockCursor()` 只在第一次保存状态；`RestoreCursor()` 只在确实锁过时才还原，重复调用安全。

## 6. 诊断计数器

| 属性 | 含义 |
| --- | --- |
| `QueryCount` | 累计输入查询次数 |
| `CaptureGrantedCount` | 成功拿到独占的次数 |
| `CaptureDeniedCount` | 被拒（别人持有）的次数 |
| `Backend` | 当前后端实例（可判断是 Unity 反射还是 Win32 在生效） |
