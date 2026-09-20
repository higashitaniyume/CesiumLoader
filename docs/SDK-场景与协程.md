# SDK 场景与协程

> 相关源码：`Services/SceneService.cs`、`Services/CoroutineService.cs`。

## 1. 场景查询

```csharp
string name = SceneService.GetActiveSceneName();     // 带缓存, 换场景自动失效; 离线返回 null
int    handle = SceneService.GetActiveSceneHandle(); // 0 = 无效场景
int    count = SceneService.GetSceneCount();
Scene  s      = SceneService.GetSceneAt(0);
bool   loaded = SceneService.IsSceneLoaded("Battle");
List<string> names = SceneService.GetLoadedSceneNames();
GameObject[] roots = SceneService.GetRootGameObjects();
GameObject go = SceneService.FindInScene("Main Camera");
```

所有查询在 Unity 不可用时返回默认值（null / 0 / 空列表 / `default(Scene)`），不抛异常。

> `Scene.IsValid()`、`scene.name`、`scene.handle` 都是 ECall，SDK 内部已隔离；mod 里请用上面的 API，
> 不要直接读 `scene.name`。

## 2. 订阅场景事件

```csharp
var sub1 = SceneService.SubscribeSceneLoaded((scene, mode) => { }, ctx);   // 原始签名
var sub2 = SceneService.SubscribeSceneLoaded(evt => { }, ctx);              // 事件参数版
var sub3 = SceneService.SubscribeSceneUnloaded(scene => { }, ctx);
var sub4 = SceneService.SubscribeActiveSceneChanged((prev, cur) => { }, ctx);

sub1.Dispose();                     // 单条退订
SceneService.UnsubscribeAll(ctx);   // mod 卸载时一次性退订(返回移除条数)
```

`ctx` 不为空时会登记进 mod 生命周期，`ModContext.Cleanup()` 自动退订。

等待场景：

```csharp
SceneService.WaitForScene("Battle", scene => { }, timeoutMs: 15000, owner: ctx);
SceneService.RunWhenSceneLoaded("Battle", scene => { }, onlyOnce: true, owner: ctx);
```

超时会计入 `WaitTimeoutCount`，并且**不会**抛异常。

## 3. 兜底检测（为什么需要）

本作的场景事件**有时不触发**（HybridCLR 裁剪 + 加载流程特殊）。SDK 因此有两层保障：

1. `HookSceneEvents()`：尝试挂 `SceneManager.sceneLoaded/sceneUnloaded/activeSceneChanged`；
2. 每帧 `CheckActiveSceneFallback()`：比较活动场景 handle，发现变化就自己补发一次事件。

mod 侧不需要区分事件是"真的来自 Unity"还是"兜底补发的"。诊断计数：

| 属性 | 含义 |
| --- | --- |
| `SceneLoadedSubscriberCount` 等 | 三个事件各自的订阅数 |
| `ForwardedLoadCount` | 向 mod 转发过的加载次数 |
| `ForwardedActiveChangeCount` | 转发过的活动场景切换次数 |
| `WaitTimeoutCount` | 等待超时次数 |

## 4. 协程

协程在 SDK 的主线程泵（UniTask PlayerLoop）里推进，**不需要 GameObject**。

```csharp
CoroutineHandle h = CoroutineService.Start(MyRoutine(), ctx, "我的流程");

IEnumerator MyRoutine()
{
    yield return new RoutineWaitSeconds(1.5f);
    yield return new RoutineWaitFrames(30);
    yield return new RoutineWaitUntil(() => SceneService.IsSceneLoaded("Battle"), 15f);
    yield return new RoutineWaitWhile(() => InputService.IsKeyHeld(KeyCode.LeftShift), 10f);
}
```

`RoutineWaitSeconds` 使用真实时间（不受 `timeScale` 影响），所以变速时等待时长仍然正确。

```csharp
CoroutineService.Stop(h);
CoroutineService.StopAllCoroutines(ctx);   // mod 卸载时自动调用
CoroutineService.ActiveCount;
```

单个协程抛异常会被记录 (`ExceptionCount`) 并停止该协程，不影响其它协程或游戏。
