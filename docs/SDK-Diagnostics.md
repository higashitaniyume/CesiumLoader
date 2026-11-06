# SDK 诊断子系统

> 相关源码：`Diagnostics/Diagnostics.cs`。用于"游戏里出问题但没法挂调试器"时的唯一观测手段。

## 1. 为什么需要它

本作是 IL2CPP + HybridCLR：

- **托管调试器挂不上**（热更程序集不在 Mono 调试链上）；
- **P/Invoke（托管→原生）不受支持**，所以不能靠注入式调试工具；
- 崩溃往往只留一行 `Player.log`。

因此 SDK 把所有可观测信息做成"可序列化快照 + 文件转储"。

## 2. 三类采集

```csharp
RuntimeDump rt = RuntimeDiagnostics.Collect();   // SDK/Unity 版本、平台、程序集、IL2CPP/HybridCLR、主线程、计数器
CameraDump  cd = CameraDiagnostics.Collect();    // 相机数量、主相机名/层级/状态、vcam
SceneDump   sd = SceneDiagnostics.Collect();     // 场景数、活动场景、根对象列表、订阅数
```

每个 dump 都有 `ToText()`（给人看）和 `ToJson()`（给工具看）。

`RuntimeDump` 中与 IL2CPP 相关的字段（`Il2CppAvailable` / `Il2CppAttached` / `Il2CppDetail` /
`RuntimeAssemblyLoaded`）在**没有 GameAssembly.dll 的进程里也必须被填充**，报告"不可用"及原因，
而不是留空——这样日志里能一眼区分"没加载"和"忘了采集"。

## 3. 落盘转储

```csharp
string path = SdkDiagnostics.Dump("战斗卡住了");   // 运行时+相机+场景+UI+计数器, 追加写入
string json = SdkDiagnostics.DumpJson();           // 完整 JSON 包, 给脚本/CI 读
SdkDiagnostics.DumpCameraInfo();                   // 只要相机段
SdkDiagnostics.DumpSceneInfo();
SdkDiagnostics.DumpRuntimeInfo();
string oneLine = SdkDiagnostics.Summary();         // 一行摘要, 适合直接打日志
string file = SdkDiagnostics.GetDumpFilePath();
```

路径规则：`CESIUM_LOG_FILE`（加载器给出的**本次启动**的引导日志完整路径）→ 否则
`CESIUM_LOG_DIR\cesium-loader.log` → 再否则 `%LOCALAPPDATA%\AstralParty_ModLoader\logs\cesium-loader.log`。
每次启动一套日志文件，同一次启动内的多次转储会追加到**同一个**文件（能看到时间线），
但不会落进上一次启动的文件里；没有加载器（脱离游戏跑）时仍然是固定名 + 追加。

游戏内触发方式（示例 mod）：

```csharp
public override void OnUpdate()
{
    if (InputService.IsKeyDown(KeyCode.F12))
        SdkLog.Info("DIAG", "已转储 -> " + SdkDiagnostics.Dump("手动转储"));
}
```

## 4. 反射工具（runtime）

`RuntimeAssemblyService` 解决"热更程序集里的类型我在编译期看不到"的问题：

```csharp
Type t = RuntimeAssemblyService.FindType("AstralParty.Battle.PlayerController");
List<Type> all = RuntimeAssemblyService.FindTypesByBaseName("CinemachineVirtualCameraBase");
Assembly asm = RuntimeAssemblyService.FindAssembly("AstralParty.Runtime");
bool loaded  = RuntimeAssemblyService.IsAssemblyLoaded("AstralParty.Runtime");

MethodInfo m = RuntimeAssemblyService.FindMethod(t, "TakeDamage");     // 静态+实例都找
FieldInfo  f = RuntimeAssemblyService.FindField(t, "hp");
PropertyInfo p = RuntimeAssemblyService.FindProperty(t, "IsAlive");

object r = RuntimeAssemblyService.SafeInvokeStatic(t, "GetInstance", null, "战斗");
object v = RuntimeAssemblyService.SafeGetField(obj, "hp", "战斗");
RuntimeAssemblyService.SafeSetProperty(obj, "IsAlive", true);
```

- 所有 `Safe*` 失败返回 `null` / `false`，并把异常记进日志（带 `tag` 便于定位）。
- 查找结果有缓存，换场景后调用 `InvalidateCaches()`。
- `FindField` 用于字段（含 `const`），`FindProperty` 用于属性 —— `SdkVersion.Current` 是
  `const` 字段，用 `FindProperty` 找它必然返回 null。
- 计数：`TypeLookupCount` / `TypeCacheHits` / `TypeMissCount`。

## 5. 日志

```csharp
SdkLog.Info("MYMOD", "启动完成");
SdkLog.Warn("MYMOD", "配置项缺失, 使用默认值");
SdkLog.Error("MYMOD", "下载失败: " + ex.Message);
SdkLog.Debug("MYMOD", "每帧信息(默认不显示)");
```

日志分级由 `doorstop_config.json` / 环境变量控制；`Debug` 级默认不输出，排查时再打开。

## 6. 排查清单

| 现象 | 先看 |
| --- | --- |
| mod 完全没加载 | `logs\cesium-loader-<会话>.log`（引导阶段；取最新一个） |
| mod 加载了但没反应 | `logs\activity-mod-<会话>.log`、`logs\mod-errors-<会话>.log` |
| 游戏内部报错 | `%LOCALAPPDATA%Low\feimo\AstralParty_CN\Player.log` |
| 相机/场景状态不对 | `SdkDiagnostics.Dump()` 的转储文件 |
| 按键没生效 | `InputService.CaptureDeniedCount`、`DescribeCursor()` |
| 类型反射失败 | `TypeMissCount` + `Safe*` 的 tag 日志 |
