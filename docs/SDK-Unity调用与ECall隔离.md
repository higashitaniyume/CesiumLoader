# Unity 调用与 ECall 隔离

> 本文是 SDK 的**底层铁律**。任何新增/修改 Unity 调用的代码都必须遵守，否则"出错不崩溃、返回默认值"的契约不成立。

## 1. 问题：为什么普通的 try/catch 防不住 Unity

Unity 的引擎 API 在 IL2CPP 里大量是 `[MethodImpl(InternalCall)]`（程序内实现调用，ECall）。ECall 的失败
**不发生在执行那一行代码时，而发生在该方法被 JIT 时**：

```
System.Security.SecurityException: ECall methods must be packaged into a system module.
```

后果是，下面这种写法**完全没有防御作用**，异常会直接从方法里逃出去：

```csharp
// ✗ 错误: ECall 与 try/catch 在同一个方法里
public static float GetDeltaTime()
{
    try { return Time.deltaTime; }        // JIT 这个方法时就已经失败了
    catch { return 0f; }                  // 永远执行不到
}
```

实测中还发现了第二个坑：**内联**。把 ECall 写进 lambda 或小方法，如果它被调用方内联，失败点会"上移"到
调用方，调用方自己的 try/catch 同样白写。所以隔离方法必须显式禁止内联。

## 2. 解法：`UnityCall` 两层结构

`src/CesiumLoader.SDK/Unity/UnityCall.cs` 是**整个 SDK 里唯一允许直接触碰 Unity ECall 的文件**。

```csharp
// 第一层: Raw —— 方法体里只有一个 Unity ECall, 必须 NoInlining, 不能有 try/catch
[MethodImpl(MethodImplOptions.NoInlining)]
internal static float RawDeltaTime() { return Time.deltaTime; }

// 第二层: 安全方法 —— 方法体里只有 try/catch 和默认值, 绝不直接碰 Unity
internal static float DeltaTime() { try { return RawDeltaTime(); } catch { return 0f; } }
```

于是 ECall 的失败发生在"被调用处"，正好落在安全方法的 try 里，可以被捕获。

## 3. 写新代码时怎么用

| 要用的 Unity API | 正确写法 |
| --- | --- |
| 已在 `UnityCall` 里 | 直接 `UnityCall.FieldOfView(cam)` |
| 还没有 | 按上文两层结构往 `UnityCall` 里加一对 `RawXxx` / `Xxx` |

**绝对不要**在 `Services/`、`UI/`、`Runtime/`、`Diagnostics/` 或 mod 代码里直接写 `Time.xxx`、
`Camera.main`、`transform.position`、`GameObject.Find`、`Quaternion.Euler`、`SceneManager.xxx`、
`Cursor.xxx`、`Screen.xxx`、`Application.xxx`、`Resources.FindObjectsOfTypeAll`、`Debug.Log` 等。

原因分两类：

- **同方法 try/catch 失效**：异常逃逸，SDK 契约破裂（用户表现为游戏崩溃）。
- **lambda 内联失效**：`SafeString(() => scene.name)` 这种写法看着安全，实际仍会炸。

## 4. 已知 ECall 清单（离线实测，不是推断）

**是 ECall —— 必须走 `UnityCall`：**

- `Time.*` 全部（deltaTime / unscaledDeltaTime / fixedDeltaTime / time / realtimeSinceStartup / frameCount / timeScale / smoothDeltaTime）
- `Cursor.*`（visible / lockState）
- `Screen.width` / `Screen.height`
- `Camera.main` / `Camera.allCameras` / `Camera.allCamerasCount`
- 相机镜头属性：`fieldOfView`、`nearClipPlane`、`farClipPlane`、`depth`、`orthographic`、`orthographicSize`、`enabled`、`cullingMask`、`clearFlags`、`backgroundColor`、`aspect`、`tag`
- `Transform` 全部：`position` / `rotation` / `eulerAngles` / `localPosition` / `localRotation` / `localEulerAngles` / `localScale` / `forward` / `right` / `up` / `parent` / `childCount` / `GetChild` / `Find` / `LookAt` / `Rotate` / `Translate` / `SetParent` / `TransformPoint` / `InverseTransformPoint`
- `UnityEngine.Object.name`、`GetInstanceID()`、`Destroy` / `DestroyImmediate` / `DontDestroyOnLoad`、`gameObject`、`transform`、`SetActive`、`activeSelf`、`activeInHierarchy`
- `GameObject.Find` / `FindWithTag` / `FindGameObjectsWithTag`、`GetComponent` / `AddComponent`
- `Resources.FindObjectsOfTypeAll`
- `Quaternion.Euler` / `Quaternion.LookRotation`、`Quaternion.eulerAngles`
- `Application.*`（unityVersion / version / productName / platform / persistentDataPath / isPlaying）
- `SceneManager.*`（GetActiveScene / GetSceneAt / GetSceneByName / sceneCount / 事件订阅）、`Scene` 的 `name` / `path` / `handle` / `buildIndex` / `IsValid()` / `isLoaded` / `GetRootGameObjects()`
- `Debug.Log`

**是托管代码 —— 可以随便用（离线也不炸）：**

- `Quaternion.identity`、`new Vector3(...)`、`new Quaternion(...)`、`new Color(...)`
- `Vector3.Distance`、`Mathf.Clamp`
- `UnityEngine.Object` 的 `== null` / `!= null`（走托管比较）
- `System.Type` 反射（`cursor.Name` / `BaseType` / `GetField` 等，是 .NET 自己的）

## 5. 离线降级的默认值约定

| 类别 | 失败时返回 |
| --- | --- |
| 数值（float/int） | `0`（时间缩放用 `1`，缩放向量用 `1`） |
| 布尔 | `false` |
| 引用（Camera / Transform / GameObject） | `null` |
| 数组 | 空数组（不是 null，调用方可以放心 `for`） |
| `Quaternion` | `Quaternion.identity` |
| `Vector3` | `Vector3.zero`（方向类用 `forward` / `right` / `up`） |
| `Scene` | `default(Scene)`（句柄 0、无效） |

## 6. 平台判定这类"降级也要有答案"的例外

`UnityCall.IsWindows()` 在 Unity 平台 API 不可用时退回 `Environment.OSVersion.Platform`。
诊断信息不该因为 Unity 还没起来就显示"未知平台"——降级值应当尽量有意义，而不是永远为 0。

## 7. 回归测试

`tests/CesiumLoader.SDK.Tests` 在**没有 Unity 运行时的 .NET 进程**里运行，正因为如此它天然会踩到所有
ECall。测试全绿（235/235）等价于"Sdk 的所有公开 API 在 Unity 不可用时都能降级返回默认值而不抛异常"。

> 编写测试时也有一条约束：**测试方法自己不能直接调用 ECall**（例如 `Quaternion.Euler(...)`、
> `Scene.IsValid()`）。ECall 在测试方法里失败会直接让该方法 JIT 失败，表现为莫名其妙的
> `SecurityException`，且 xunit 取不到堆栈。测试需要通过 SDK 的 API 或纯托管值来断言。
