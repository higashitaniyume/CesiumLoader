# CesiumLoader

Astral Party 国服 (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod 加载器与 SDK。

GitHub: https://github.com/higashitaniyume/CesiumLoader

全部代码收在一个 Visual Studio 2022 解决方案里：

```
CesiumLoader.sln
├── src\
│   ├── CesiumLoader\              C++ DLL 加载器 (产出 version.dll, Doorstop 式代理 + 变速引擎)
│   │   ├── modmeta.cpp/h          mod 元数据(sidecar)解析 + 依赖拓扑排序 (纯标准库, 可单测)
│   │   └── il2cpp_safe.h          IL2CPP 互操作安全封装 (空检查/异常转译)
│   ├── CesiumLoader.Bootstrap\    C# 托管引导程序 (netstandard2.0, 零引用, 编排 SDK/mods)
│   ├── CesiumLoader.SDK\          C# SDK (netstandard2.0, 供 mod 引用)
│   ├── ActivityLogMod\            C# 示例 mod (行为日志)
│   └── SpeedHackMod\              C# 示例 mod (游戏变速, SpeedHack SDK 接口)
├── third_party\minhook\            MinHook (inline hook 库, 变速引擎使用, MIT)
└── tools\
    ├── cesium\                    模组脚手架与包分发 CLI (new/build/package/list/verify)
    └── smoke\                     冒烟测试 (modmeta/权限/变速, 不依赖游戏)
```

## 原理 (Doorstop 式引导)

UnityPlayer.dll 在进程启动时依赖 version.dll (导入 GetFileVersionInfoSizeA /
GetFileVersionInfoA / VerQueryValueA, 经 dumpbin 确认), 而 version.dll 不在
KnownDLLs 列表——把本 DLL 命名为 `version.dll` 放在游戏 exe 同目录,
Windows 加载器会优先加载它。这正是 BepInEx/Doorstop 生态的事实标准代理方式,
与旧的 winmm.dll 劫持相比: 导出面从 180 个缩到 15 个、更接近行业惯例、
杀软误报概率更低。

分层:

```
version.dll (C++ 薄代理, 15 个导出转发到系统 version.dll)
  └─ DllMain 直接启动引导线程 (开头 Sleep 1.5s 避开 loader lock):
       1. 读取 AstralParty_ModLoader\doorstop_config.json (enabled 开关/超时/控制台)
       2. 等待 GameAssembly.dll (60s) → 解析 il2cpp_* 导出
       3. 等待 il2cpp domain (30s) + thread_attach
       4. 等待 HybridCLR 热更 (AstralParty.Runtime 出现, 60s)
       5. 设置环境变量 CESIUM_* 目录
       6. 编排层:
          - 默认 (useManagedBootstrap=false): 原生加载 sdk\*.dll → mods\*.dll,
            逐个 Assembly.Load(byte[]) + 调用 {文件名}.ModEntry.Main()
          - 实验 (useManagedBootstrap=true): Assembly.Load(byte[]) 加载
            bootstrap\CesiumLoader.Bootstrap.dll 并调用 Bootstrap.Main(),
            由托管代码编排一切
       7. 启动 activity-mod.log → 控制台 转发线程
  └─ CesiumLoader.Bootstrap.dll (C# 托管引导, 零引用, 可脱离游戏单元测试):
       1. 按文件名排序加载 sdk\*.dll (不调入口)
       2. 按文件名排序加载 mods\*.dll 并调用 {文件名}.ModEntry.Main()
       3. 单个 mod 失败不中断其他 mod
```

设计要点:
- **配置驱动**: `doorstop_config.json` 的 `enabled=false` 可完全禁用加载器
  (不弹控制台、不加载任何东西), 游戏原样运行。
- **原生编排是默认路径**: HybridCLR 的 AOT 裁剪会移除游戏未用到的部分反射 API
  (实测 `Assembly.GetType(string,bool)` 抛 MethodNotFind), 因此 SDK/mods 加载
  默认走原生 il2cpp API —— 这些 API 在 GameAssembly.dll 里永远存在, 已验证可靠。
- **托管 Bootstrap 是实验特性**: `useManagedBootstrap=true` 时启用; 依赖游戏恰好
  保留了所需反射 API, 当前已知 `Assembly.GetType` 不可用, 托管编排可能失败。
- **DllMain 直接引导**: 不在 loader lock 下做危险操作, 线程开头 Sleep 1.5s
  避开进程初始化敏感期; 即使游戏从不调用代理导出也能可靠引导。

## 变速引擎 (SpeedHack)

加载器内置 CheatEngine 式变速能力（借鉴 [speedhack-rs](https://github.com/Hirtol/speedhack-rs)）：
- 引导线程启动后用 **MinHook** inline hook 4 个系统时间函数（`GetTickCount` /
  `GetTickCount64` / `timeGetTime` / `QueryPerformanceCounter`），按倍率缩放返回值。
- 通过 `ap_speed_set` / `ap_speed_get` / `ap_speed_active` 导出暴露给 SDK
  （`CesiumLoader.SDK.SpeedHack` 类封装，mod 可直接调用）。
- 缩放算法与 speedhack-rs 的 `TimeState` 等价：切换倍率时重设时间基准，
  保证虚拟时间连续不跳变。
- 附带示例 mod `SpeedHackMod`：热键控制倍率（F1=2x / F2=0.5x / F3=恢复），
  配置在 `configs/SpeedHackMod.json`。

> ⚠️ 变速影响游戏感知的所有时间（动画/回合/网络超时）。联机对局慎用：
> 服务器权威时钟会检测到本地时间戳异常，有断线/封号风险。

## 行业化特性

面向模组生态的工程化能力（对标 BepInEx 等成熟框架）：

| 特性 | 说明 |
|---|---|
| **Mod 权限模型** | 敏感 API (GameActions 服务器操作 / SpeedHack 变速) **默认关闭**，mod 在 `[ModManifest]` 声明请求；可用 `mods\{name}.permissions.json` 逐项覆盖（强制开/关）。未授权调用静默降级并告警 |
| **模组元数据标准 + 依赖解析** | `mods\{name}.json` sidecar（id/版本/权限/SDK 版本/依赖）；加载器按依赖**拓扑排序**加载，缺失依赖/版本不符/循环依赖的 mod 被跳过并报告（`modmeta.cpp` 纯标准库，可单测） |
| **API 版本协商** | SDK 声明版本 `2.0.0`；mod 声明 `SdkVersion`，要求高于当前的 mod 被拒绝加载。`doorstop_config.json` 的 `sdkVersion` 声明当前版本 |
| **事件驱动化** | `GameEvents.StartAutoHook()` 内部每 1 秒维持 RPC 挂钩，mod 无需每秒轮询；`ModBase.Run` 不传 tick 则不空转 |
| **IL2CPP 互操作安全封装** | `il2cpp_safe.h` 收敛全部互操作点：函数指针空检查、参数/返回值校验、托管异常转译成可读错误，防止原生崩溃拖垮游戏 |
| **调试与故障体验** | mod 入口异常写 `logs\mod-errors.log`（SDK `SdkLog.ReportCrash` + 原生 `write_mod_error` 双写）；`cesium verify` 离线预检兼容性 |
| **脚手架与包分发** | `tools\cesium` CLI：`new`（生成项目+程序集级元数据+权限示例）/ `build` / `package`（zip 分发）/ `list` / `verify`（模拟加载器判定） |

## 目录布局

游戏 exe 所在目录 (部署后)：

```
游戏目录\
├── version.dll                  ← 加载器 (本解决方案产物, Doorstop 代理)
└── AstralParty_ModLoader\
    ├── doorstop_config.json     ← 加载器配置 (enabled 总开关等)
    ├── bootstrap\               ← 托管引导程序 (CesiumLoader.Bootstrap.dll)
    ├── sdk\                     ← SDK 依赖 (CesiumLoader.SDK.dll)
    ├── mods\                    ← 用户 mod (每个 DLL 一个 mod)
    └── logs\                    ← cesium-loader.log + activity-mod.log
```

## doorstop_config.json

所有字段可选, 缺失/损坏时用默认值, 不会阻止游戏启动:

```json
{
  "enabled": true,                    // false = 完全禁用加载器, 游戏原样运行
  "useManagedBootstrap": false,       // false=原生编排(默认,可靠); true=托管 Bootstrap 编排(实验)
  "bootstrapAssembly": "CesiumLoader.Bootstrap.dll",
  "bootstrapType": "CesiumLoader.Bootstrap.Bootstrap",
  "bootstrapMethod": "Main",
  "gameAssemblyTimeoutSec": 60,       // GameAssembly.dll 等待超时
  "domainTimeoutSec": 30,             // il2cpp domain 等待超时
  "hybridclrTimeoutSec": 60,          // HybridCLR 热更等待超时
  "consoleEnabled": true,             // 分配控制台窗口
  "consoleTopmost": true,             // 控制台窗口置顶
  "forwardActivityLog": true          // mod 日志转发到控制台
}
```

## 构建

要求: Visual Studio 2022 (v143) + Windows SDK 10.0.26100 + .NET SDK 8/9/10。

```
# 1. NuGet 还原 (首次)
dotnet restore CesiumLoader.sln

# 2. 命令行构建 (或直接用 VS2022 打开 sln 按 F7)
MSBuild CesiumLoader.sln /p:Configuration=Release /p:Platform=x64

# 3. 冒烟测试 (转发 + 引导, 不依赖游戏)
dotnet run --project tools\smoke\host\BootstrapHostTest.csproj -c Release
```

产物:
- `bin\Release\version.dll` — C++ 加载器 (Doorstop 代理 + 变速引擎)
- `src\CesiumLoader.Bootstrap\bin\Release\netstandard2.0\CesiumLoader.Bootstrap.dll`
- `src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll`
- `src\SpeedHackMod\bin\Release\netstandard2.0\SpeedHackMod.dll` — 变速示例 mod
- `src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.dll`

## 开发一个 mod

**推荐用脚手架 CLI** (生成项目 + 程序集级元数据 + 权限示例 + sidecar):

```
dotnet run --project tools\cesium -c Release -- new MyMod --author 小明 --desc "我的第一个mod"
dotnet run --project tools\cesium -c Release -- build MyMod
dotnet run --project tools\cesium -c Release -- package MyMod -o MyMod-1.0.0.zip
```

或手写: 新建 C# 类库 (netstandard2.0)，ProjectReference 到 `CesiumLoader.SDK`，
并引用游戏热更程序集 (见 `ActivityLogMod.csproj` 的 Reference 模式)。

**元数据 (程序集级声明, 权威位置 — AssemblyInfo.cs):**

```csharp
[assembly: CesiumLoader.SDK.ModManifest("我的Mod", "1.0.0", "小明", "描述",
    Permissions = CesiumLoader.SDK.ModPermission.ReadGameState,  // 敏感权限默认关闭!
    SdkVersion = "2.0.0")]                                        // API 版本协商
```

**入口 (纯事件驱动, 无轮询):**

```csharp
public static class ModEntry
{
    public static void Main()
    {
        CesiumLoader.SDK.ModBase.Run(OnInit);   // 不传 tick = 不轮询
    }
    static void OnInit()
    {
        CesiumLoader.SDK.GameEvents.CardUsed += (pid, card, remain) => /* ... */;
        CesiumLoader.SDK.GameEvents.StartAutoHook();   // SDK 内部维持挂钩
    }
}
```

**权限说明**: `ReadGameState` / `FileWrite` 默认授予; `GameActions` (向服务器发操作) /
`SpeedHack` (变速) **默认拒绝**, mod 必须显式声明, 用户还可通过
`mods\{name}.permissions.json` 强制开/关。未授权调用静默失败并告警。

编译出的 DLL + sidecar (`{name}.json`) 放进 `AstralParty_ModLoader\mods\`，重启游戏生效。
发布前用 `cesium verify <mods_dir>` 离线预检依赖与版本兼容性。

SDK API 一览:
- `ModBase.Run(init, tick=null, delayMs=30000, tag)` — 生命周期 (不传 tick 不轮询)
- `GameEvents.*` — 15 个事件 + `StartAutoHook()` (SDK 内部维持挂钩, 事件驱动)
- `Permissions.Has/Require` — 权限门控 (敏感 API 内部自动检查)
- `SdkVersion.Accepts/Current` — API 版本协商
- `SdkLog.ReportCrash/CrashGuard` — 故障报告 (完整堆栈写 mod-errors.log)
- `Players.*` — 全部玩家 / 星币 / 手牌数 / 名字 / 是否自己 / 手牌内容
- `Names.*` — 卡牌 / 遗物 / 技能 / 角色 / 战斗角色 名字解析
- `SdkLog.Write(tag, line)` — 写日志 (转发到 loader 控制台)

## 从旧版 (winmm.dll) 迁移

旧版加载器叫 `winmm.dll`。升级到 Doorstop 式时:

1. **删除游戏目录下的 `winmm.dll`** (旧代理), 放入新的 `version.dll`。
   (两者并存会导致双引导线程 / 重复加载 mod!)
2. 新布局增加 `AstralParty_ModLoader\bootstrap\` 与 `doorstop_config.json`。
3. SDK/mods/logs 目录不变, 现有 mod 无需改动。

## 发布 (GitHub Action)

打 tag `modloader-v*` 自动构建并发布「解压即部署」的压缩包到 GitHub Release：

```
git tag modloader-v1.0.0
git push origin modloader-v1.0.0
```

`release-modloader.yml` 会:
1. windows-latest 上 MSBuild 构建 C++ 加载器 (version.dll, 无游戏依赖)
2. dotnet 现场构建 CesiumLoader.Bootstrap (自包含, 零引用)
3. 用 `dist\modloader\` 里的预编译 SDK / 示例 mod (游戏热更 DLL 不入库, 故用 dist)
4. 打包成部署布局 + 生成 `cesium-loader.json` 清单 (版本 / SHA256 / 布局)

产物 (Release 资产):
- `cesium-loader-v1.0.0.zip` — 带版本号
- `cesium-loader.zip` — 固定名, 供 `releases/latest/download/cesium-loader.zip` 使用

AstralParty.Toys 的 Mod 管理功能从这个 URL 下载安装:
`https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-loader.zip`

更新 SDK / mod 后记得同步 dist (本地构建 → 覆盖 dist → 提交):
```
dotnet build src\CesiumLoader.SDK -c Release
dotnet build src\ActivityLogMod -c Release
copy src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll dist\modloader\AstralParty_ModLoader\sdk\
copy src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.dll dist\modloader\AstralParty_ModLoader\mods\
```
