# CesiumLoader SDK 概览

> Astral Party 国服 (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod SDK。
> 加载器把 mod DLL 注入 HybridCLR 解释器，SDK 提供与游戏类型强类型绑定的 API。

## 架构

```
游戏进程
├── version.dll (原生加载器, Doorstop 代理)
│   ├── 等 GameAssembly → il2cpp 运行时 → HybridCLR 热更 (AstralParty.Runtime)
│   ├── 变速引擎 speedhack: MinHook inline hook 4 个系统时间函数
│   └── 原生加载 sdk\*.dll → mods\*.dll → 调用 {文件名}.ModEntry.Main()
└── CesiumLoader.SDK.dll (netstandard2.0, 供 mod 编译期引用)
    ├── ModBase      mod 生命周期基座
    ├── GameEvents   15 个游戏事件 (RPC 回调 hook)
    ├── Players      玩家数据访问
    ├── Names        名字解析 (卡牌/遗物/技能/角色)
    ├── GameActions  向服务器发送操作 (投骰/移动/用牌等)
    ├── SpeedHack    游戏变速 (P/Invoke 调加载器 ap_speed_* 导出)
    ├── SdkConfig    mod 配置 (JSON)
    ├── SdkLog       日志
    ├── ModManifest  mod 元数据声明
    └── SdkManifest  sidecar 导出 (供外部工具读取)
```

关键点：
- **编译期绑定**：SDK 直接引用游戏热更程序集类型（`GameLogic`、`party.model`、`party.protocol`、`Core.Net`、`Cysharp.Threading.Tasks`），mod 在编译时引用 SDK + 游戏热更 DLL。
- **HybridCLR 适配**：加载器等 HybridCLR 热更就绪后才加载 mod，避免"热更程序集被替换导致类型过期"。
- **30 秒安全延迟**：游戏启动早期访问 `NetManager`/`UIManager` 等单例会触发崩溃，SDK 默认等 30 秒再初始化。

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
public static class ModEntry
{
    public static void Main()
    {
        CesiumLoader.SDK.ModBase.Run(OnInit, OnTick, tag: "MyMod");
    }

    static void OnInit()
    {
        // 订阅事件 / 读配置 (启动 30 秒后执行)
        CesiumLoader.SDK.GameEvents.CardUsed += (pid, cardId, remain) =>
            CesiumLoader.SDK.SdkLog.Info("MyMod", $"玩家 {pid} 出牌 {CesiumLoader.SDK.Names.Card(cardId)} 剩{remain}张");
    }

    static void OnTick()
    {
        // 每秒轮询: 保持事件挂钩 (游戏每场战斗会重置 RPC 回调)
        CesiumLoader.SDK.GameEvents.EnsureHooked();
    }
}
```

### 3. 部署

编译出的 DLL 放入游戏目录 `AstralParty_ModLoader\mods\`，重启游戏生效。

## 文档索引

| 文档 | 内容 |
|---|---|
| [SDK-生命周期与日志.md](SDK-生命周期与日志.md) | ModBase.Run / SdkLog 日志分级 |
| [SDK-事件.md](SDK-事件.md) | GameEvents 15 个事件 + StartAutoHook (事件驱动) |
| [SDK-玩家与名字.md](SDK-玩家与名字.md) | Players 玩家数据 / Names 名字解析 |
| [SDK-操作.md](SDK-操作.md) | GameActions 投骰/移动/用牌/选择 (⚠ 声明后可操作, 工具/加载器会警告) |
| [SDK-变速.md](SDK-变速.md) | SpeedHack 游戏变速 (加载器内置功能, 也可 mod 编程控制) |
| [SDK-能力声明与元数据.md](SDK-能力声明与元数据.md) | 能力声明(仅警告) / sidecar 元数据 / 依赖解析 / 版本协商 |
| [SDK-配置与元数据.md](SDK-配置与元数据.md) | SdkConfig JSON 配置 / ModManifest 元数据 / SdkManifest sidecar |
| [工具-cesium-CLI.md](工具-cesium-CLI.md) | 脚手架与包分发 CLI (new/build/package/list/verify) |
