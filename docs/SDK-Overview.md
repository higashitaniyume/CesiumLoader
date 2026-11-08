# CesiumLoader SDK 概览

> Astral Party 国服 (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod SDK。
> 加载器把 mod DLL 注入 HybridCLR 解释器，SDK 提供与游戏类型强类型绑定的 API。

## 命名空间结构

SDK 类型全部位于 `CesiumLoader.SDK` 根命名空间之下的子命名空间中，按职责组织：

| 命名空间 | 主要类型 |
|---|---|
| `CesiumLoader.SDK.Mods` | `ModBase`、`ModHost`、`ModContext`、`IMod`、`StartupGate` |
| `CesiumLoader.SDK.Manifests` | `ModManifestAttribute`、`ModPermission`、`SdkManifest`、`SdkVersion` |
| `CesiumLoader.SDK.Configuration` | `ModConfig`、`SdkConfig`、`CesiumJson` |
| `CesiumLoader.SDK.Logging` | `SdkLog`、`ModLogger` |
| `CesiumLoader.SDK.Gameplay` | `GameEvents`、`ShopSettlement`、`ShopOffer`、`RelicOffer`、`GameCommands`、`BattlePhase`、`GameActions`、`Players`、`Names`、`ConfigReads` |
| `CesiumLoader.SDK.Speed` | `SpeedHack` |
| `CesiumLoader.SDK.Windowing` | `WindowFocus` |
| `CesiumLoader.SDK.Cameras` | `CameraService`、`CameraState`、`FreeCameraMath` |
| `CesiumLoader.SDK.Scenes` | `SceneService` |
| `CesiumLoader.SDK.Inputs` | `InputService`、input backends |
| `CesiumLoader.SDK.Scheduling` | `CoroutineService`、`UpdateService` |
| `CesiumLoader.SDK.UserInterface` | `UiService`、UI data types |
| `CesiumLoader.SDK.Events` | UI, scene, camera and update events |
| `CesiumLoader.SDK.Diagnostics` | diagnostic snapshots and collectors |
| `CesiumLoader.SDK.Engine` | Unity object, transform, time and frame-rate helpers |
| `CesiumLoader.SDK.Runtime` | main-thread and runtime assembly services |
| `CesiumLoader.SDK.Audio` | `AudioService`、`AudioEvents`、`AudioReplacementRule`、`WavUtility` |
| `CesiumLoader.SDK.IO` | `Path`、`Paths`（各级路径发现、模组与数据子路径拼接、安全文件操作） |
| `CesiumLoader.SDK.Internals` | loader-only implementation details |

> 根命名空间额外提供 `SdkPath` 门面类，方便在已有 `using System.IO;` 的代码中直接调用路径 API 而无类名冲突。其余 SDK 类型请按模块导入对应子命名空间。

## 架构

```
游戏进程
├── version.dll (原生加载器, Doorstop 代理)
│   ├── 等 GameAssembly → il2cpp 运行时 → HybridCLR 热更 (AstralParty.Runtime)
│   ├── 变速引擎 speedhack: MinHook inline hook 4 个系统时间函数
│   ├── 窗口置前通道: 监听 mod 请求并调用 SetForegroundWindow
│   └── 原生加载 sdk\*.dll → mods\{ModId}\{ModId}.dll → 调用 {文件名}.ModEntry.Main()
└── CesiumLoader.SDK.dll (netstandard2.0, 供 mod 编译期引用)
    ├── ModBase      mod 生命周期基座
    ├── StartupGate  启动门 (游戏就绪即初始化, delayMs 只是上限)
    ├── GameEvents   28 个游戏事件 (RPC 回调按需 hook, 含商店成交/筹码刷新/血量击杀与带 sn 的 RawAction)
    ├── Players      玩家数据访问
    ├── Names        名字解析 (卡牌/遗物/技能/角色)
    ├── GameActions  向服务器发送操作 (投骰/移动/用牌/选人等)
    ├── SpeedHack    游戏变速 (文件通道, 加载器执行时间缩放)
    ├── WindowFocus  把游戏窗口切到前台 (文件通道, 加载器执行激活)
    ├── SdkConfig    mod 配置 (JSON)
    ├── SdkLog       日志
    ├── ModManifest  mod 元数据声明
    └── SdkManifest  sidecar 导出 (供外部工具读取)
```

关键点：
- **编译期绑定**：SDK 直接引用游戏热更程序集类型（`GameLogic`、`party.model`、`party.protocol`、`Core.Net`、`Cysharp.Threading.Tasks`），mod 在编译时引用 SDK + 游戏热更 DLL。
- **XML 文档**：SDK 附带同名 `CesiumLoader.SDK.xml`，与 dll 放在同一目录，IDE 里悬停、补全和跳转都能看到类型与成员注释。
- **HybridCLR 适配**：加载器等 HybridCLR 热更就绪后才加载 mod，避免"热更程序集被替换导致类型过期"。
- **启动门**：游戏启动早期访问 `NetManager`/`UIManager` 等单例会触发崩溃，SDK 因此设了启动上限（默认 30 秒）。实际初始化在**游戏就绪的那一刻**发生——已经进入可玩场景且有面板打开——而不是固定等满 30 秒；上限只在判定不到就绪时兜底。

## 快速上手

### 1. 项目配置

C# 类库 (netstandard2.0)，引用 SDK 和游戏热更程序集（参考 `ActivityLogMod.csproj` 的引用模式）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CesiumLoader.SDK\CesiumLoader.SDK.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- 游戏热更 DLL: 从游戏目录 extracted 或本地副本引用 -->
    <Reference Include="AstralParty.Runtime">
      <HintPath>..\refs\AstralParty.Runtime.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>..\refs\UnityEngine.CoreModule.dll</HintPath>
    </Reference>
    <!-- 其余依赖同上 -->
  </ItemGroup>
</Project>
```

### 2. 入口

```csharp
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;

public static class ModEntry
{
    public static void Main()
    {
        ModBase.Run(OnInit, OnTick, tag: "MyMod");
    }

    static void OnInit()
    {
        // 订阅事件 / 读配置 (游戏就绪后执行, 最晚 30 秒)
        GameEvents.CardUsed += (pid, cardId, remain) =>
            SdkLog.Info("MyMod", $"玩家 {pid} 出牌 {Names.Card(cardId)} 剩{remain}张");
    }

    static void OnTick()
    {
        // 每秒轮询: 保持事件挂钩 (游戏每场战斗会重置 RPC 回调)
        GameEvents.EnsureHooked();
    }
}
```

### 3. 部署

编译出的 DLL 放入游戏目录 `AstralParty_ModLoader\mods\`，重启游戏生效。

## 文档索引

| 文档 | 内容 |
|---|---|
| [SDK-LifecycleAndLogging.md](SDK-LifecycleAndLogging.md) | ModBase.Run / SdkLog 日志分级 |
| [SDK-Events.md](SDK-Events.md) | GameEvents 32 个事件 + 商店成交/报价 + 筹码结果与刷新 + 血量击杀 + downSn + 战斗子阶段 + StartAutoHook (事件驱动) |
| [SDK-PlayersAndNames.md](SDK-PlayersAndNames.md) | Players 玩家数据 / Names 名字解析 |
| [SDK-Actions.md](SDK-Actions.md) | GameActions 投骰/移动/用牌/选择 (⚠ 声明后可操作, 工具/加载器会警告) |
| [SDK-SpeedHack.md](SDK-SpeedHack.md) | SpeedHack 游戏变速 (加载器内置功能, 也可 mod 编程控制) |
| [SDK-FrameRate.md](SDK-FrameRate.md) | UnityFrameRate 帧率与垂直同步 (运行时修改, 不写游戏存档) |
| [SDK-WindowFocus.md](SDK-WindowFocus.md) | WindowFocus 把游戏窗口切到前台 (本地 UDP 通道) |
| [SDK-CapabilitiesAndMetadata.md](SDK-CapabilitiesAndMetadata.md) | 能力声明(仅警告) / sidecar 元数据 / 依赖解析 / 版本协商 |
| [SDK-ConfigAndMetadata.md](SDK-ConfigAndMetadata.md) | SdkConfig JSON 配置 / ModManifest 元数据 / SdkManifest sidecar |
| [SDK-Paths.md](SDK-Paths.md) | SdkPath / Path 路径服务 (各级目录发现、子路径定位与安全文件操作) |

