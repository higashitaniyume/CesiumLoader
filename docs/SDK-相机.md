# SDK 相机子系统

> 相关源码：`Services/CameraBackend.cs`、`Services/CameraService.cs`、`Services/CameraState.cs`、
> `Services/CinemachineService.cs`、`Services/FreeCameraMath.cs`。
> 自由相机 mod 见 `docs/mod-FreeCameraMod.md`。

## 1. 分层

```
mod 代码
  └─ CameraService            ← 门面：解析/缓存/读写/状态存取, 全部空安全
       ├─ ICameraBackend      ← 可替换接缝(测试替身 / 未来换实现)
       │    └─ UnityCameraBackend  ← 唯一真正摸 Unity Camera 的地方, 全部经 UnityCall
       ├─ CameraState         ← 可 JSON 序列化的相机快照
       └─ CinemachineService  ← 反射式接入 Cinemachine(3.x), 玩家没装也能安全降级
```

`CameraService.SetBackend(null)` 会恢复默认的 `UnityCameraBackend`；测试里可以用
`SetBackend(new FakeCameraBackend())` 把整条链路与 Unity 解耦。

## 2. 解析相机

```csharp
Camera main = CameraService.GetMainCamera();          // 可能为 null(菜单/加载中是占位相机)
Camera[] all = CameraService.GetAllCameras(true);     // includeDisabled
Camera cam  = CameraService.FindCamera("PlayerCam");  // 名字 / 路径 / 包含匹配
Camera cam2 = CameraService.FindCamera("/Root/PlayerCam");
bool alive  = CameraService.IsCameraAlive(main);
string desc = CameraService.DescribeCamera(main);      // 一行摘要, 日志用
```

`FindCameraHandle` 的匹配顺序：`main` 别名 → 精确名字（大小写不敏感）→ **路径最后一段** →
路径后缀 → 包含匹配。路径最后一段那一级是为了兼容读不出层级路径的对象（替身、刚创建的相机）。

解析结果会缓存，换场景/换相机请调用 `CameraService.InvalidateCaches()`。

解析顺序（实测本作后细化）：`Camera.main` → tag=`MainCamera`（**只在启用中的相机里找**）→
名字含 `main`（启用中）→ tag=`MainCamera`（含失活）→ 名字含 `main`（含失活）→ depth 最高的启用相机。

之所以"先挑启用的、再兜底允许失活"：本作在菜单/场景过渡期会把 Main Camera 临时
`enabled=false` 并停到极高处，只认启用相机的话过渡期会退化成"随便一台启用的相机"（可能是 UI 相机）；
而完全不兜底又会在读不到 `enabled` 时解析不出相机。**能不能接管由调用方判断**（自由相机有就绪校验），
SDK 只负责给最合适的候选。

## 3. 读写相机

```csharp
CameraService.SetPosition(cam, new Vector3(0, 160, -57));
CameraService.SetRotation(cam, UnityCall.Euler(70f, 0f, 0f));   // 建议经 UnityCall
CameraService.SetEulerAngles(cam, new Vector3(70f, 0f, 0f));    // 内部已隔离

float fov = CameraService.GetFieldOfView(cam);
CameraService.SetFieldOfView(cam, 80f);
CameraService.SetNearClipPlane(cam, 0.05f);
CameraService.SetFarClipPlane(cam, 2000f);
CameraService.SetDepth(cam, -1f);
CameraService.SetCullingMask(cam, mask);
CameraService.SetBackgroundColor(cam, new Color(0.05f, 0.06f, 0.09f, 1f));
CameraService.SetAspect(cam, 1.777f);
```

`SomeGet()` 在不可用时返回默认值（0 / false），`SomeSet()` 返回 `bool` 表示是否成功。
**mod 不应该直接用 `cam.fieldOfView = x`**：那是 ECall，见 `docs/SDK-Unity调用与ECall隔离.md`。

## 4. 快照与还原

进入接管前先存快照，退出时还原，是"不破坏玩家游戏体验"的关键。

```csharp
CameraState saved = CameraService.CaptureState(cam);   // 位置/旋转/欧拉角/FOV/裁剪面/正交/深度/遮罩/清屏/背景/aspect/enabled
// ... 自由相机随便改 ...
CameraService.RestoreState(cam, saved);                // 默认不还原 enabled
CameraService.RestoreState(cam, saved, restoreEnabled: true);   // 确实禁用过相机时才用这个
```

> ⚠️ **`RestoreState` 默认不还原 `enabled`**，这是刻意的（别改回去）。
> 本作在菜单/场景过渡期会把 Main Camera 临时失活；如果快照恰好采到这一刻，"完整还原"就会把
> 游戏当前正在渲染的相机禁用掉 —— 画面变黑，而 ScreenSpaceOverlay 的 HUD 还在、声音照旧。
> 绝大多数接管方案（改位姿/镜头参数）从未碰过 `enabled`，所以默认不碰它最安全；
> 自己的 mod 若禁用过相机，请显式传 `restoreEnabled: true`。

持久化／跨进程传参：

```csharp
string json = CameraService.SerializeState(state);
CameraState parsed;
CameraService.TryDeserializeState(json, out parsed);
CameraService.SaveState(path, state);
CameraService.TryLoadState(path, out state);
```

`CameraState.TryParse` 要求 JSON 根是**对象**：`"[1,2,3]"`、`"123"` 这类会返回 `false`，而不是
给一个"全默认值的假状态"。

## 5. 自己创建相机

```csharp
Camera mine = CameraService.CreateOwnedCamera("CesiumFreeCamera", parent, fov: 60f, depth: -5f);
CameraService.DestroyCamera(mine);   // 只销毁自己创建的
```

`CreateOwnedCamera` 登记的相机会在 mod 卸载时随 `ModContext.Cleanup()` 一起销毁。
不要动游戏自己的相机（不要 `Destroy(Camera.main)`）。

## 6. 屏幕中心射线

```csharp
Vector3 origin, dir;
if (CameraService.TryGetCenterRay(cam, out origin, out dir))
    Physics.Raycast(...);   // 你的 mod 里做拾取
```

## 7. Cinemachine

本作使用 Cinemachine。SDK 用反射接入，**玩家没装/被裁剪时全部安全返回 false/null**。

```csharp
object brain = CinemachineService.FindBrain();
bool blending = CinemachineService.IsBlending(brain);
object vcam = CinemachineService.CreateVirtualCamera("CesiumFreeCamera", parent, priority: 100);
CinemachineService.SetLensFieldOfView(vcam, 70f);
CinemachineService.SetPriority(vcam, 200);        // 抢过控制权
CinemachineService.DestroyVirtualCamera(vcam);
```

> 实测结论：本作的 CinemachineBrain 位于一个**未激活**的对象上，因此自由相机采用
> "每帧直接写 Main Camera 位姿"的方式接管，而不是靠 vcam 抢优先级。Cinemachine API 保留，
> 供 mod 在确认 Brain 激活的场景里使用。

## 8. FreeCameraMath

纯托管数学，离线可测：`ForwardVector(yaw, pitch)`、`ClampPitch`（±89°）、`OrbitPosition`、
`ApplyScrollToFieldOfView` / `ApplyScrollToDistance`、`ApplyHeightStep`。
约定：yaw=0 指向 +Z；`pitch` 为正表示低头（与 `Quaternion.Euler` 一致）。

`ApplyHeightStep(height, delta, step)` 是"游戏内实时调相机高度"用的纯函数：只做
"方向 × 步进 + 夹紧到 `MinCameraHeight`(5) ~ `MaxCameraHeight`(2000)"，
`step` 非法（≤0 / NaN / Inf）时退回 `DefaultHeightStep`(10)，返回值与入参相同即表示已到极限
—— 于是调用方（mod）不用自己处理边界与脏配置，也不会有"按键没反应但看不出为什么"的情况。
