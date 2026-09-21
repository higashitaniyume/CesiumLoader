# CesiumLoader

Astral Party (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod 加载器与 SDK。

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
│   ├── FreeCameraMod\             内置 mod (俯瞰视角)
│   ├── SpeedHackMod\              内置 mod (变速热键)
├── third_party\minhook\            MinHook (inline hook 库, 变速引擎使用, MIT)
├── smoke\SpeedCtlSmoke\            冒烟测试宿主 (变速控制文件通道, 不依赖游戏)
└── tools\
    ├── cesium\                    模组脚手架与包分发 CLI (new/build/package/list/verify)
    ├── package-modloader.ps1      构建 + 组装 + 打包 Release 压缩包
    └── smoke-speedctl.ps1         跑变速控制文件通道冒烟测试
```

## 原理 (Doorstop 式引导)

UnityPlayer.dll 在进程启动时依赖 version.dll (导入 GetFileVersionInfoSizeA /
GetFileVersionInfoA / VerQueryValueA, 经 dumpbin 确认), 而 version.dll 不在
KnownDLLs 列表——把本 DLL 命名为 `version.dll` 放在游戏 exe 同目录,
Windows 加载器会优先加载它(BepInEx/Doorstop 生态通用的代理方式)。
与旧的 winmm.dll 劫持相比: 导出面从 180 个缩到 15 个、更接近系统 DLL 的
标准接口、杀软误报概率更低。

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
          - 默认 (useManagedBootstrap=false): 原生加载 sdk\*.dll → mods\ 下
            每 mod 文件夹的 {ModId}.dll, 逐个 Assembly.Load(byte[]) +
            调用 {文件名}.ModEntry.Main(), 控制台按行业标准分级彩色输出
            (先列出要加载的 mod, 再逐个 ✔ 成功 / ✘ 失败, 末尾总结)
          - 实验 (useManagedBootstrap=true): Assembly.Load(byte[]) 加载
            bootstrap\CesiumLoader.Bootstrap.dll 并调用 Bootstrap.Main(),
            由托管代码编排一切
       7. 启动 activity-mod.log → 控制台 转发线程
  └─ CesiumLoader.Bootstrap.dll (C# 托管引导, 零引用, 可脱离游戏单元测试):
       1. 按文件名排序加载 sdk\*.dll (不调入口)
       2. 按文件名排序加载 mods\ 下的 {ModId}.dll 并调用 {文件名}.ModEntry.Main()
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
- 通过 `ap_speed_set` / `ap_speed_get` / `ap_speed_active` 导出暴露（原生调用方可用），
  以及**变速控制文件通道**（`<speed>\request.txt` / `state.txt`）暴露给 mod ——
  本作的热更程序集无法 P/Invoke，所以 SDK 的 `CesiumLoader.SDK.SpeedHack` 走文件。
- 缩放算法与 speedhack-rs 的 `TimeState` 等价：切换倍率时重设时间基准，
  保证虚拟时间连续不跳变。
- 变速是加载器内置功能（不是 mod）：`doorstop_config.json` 的
  `speedhackBaseSpeed` 控制（1.0=正常，2.0=全程 2 倍速），游戏启动即应用；
  AstralParty.Toys 的模组页面提供可视化开关。
- 游戏内实时调整用内置 mod **变速 (SpeedHackMod)**：`Delete` 设为 `1.0x`，
  `Alt+=` / `Alt+-` 调倍率（`1.0` 起，默认每次 0.5，按住连续调，调完自动写回配置）。

> ⚠️ 变速影响游戏感知的所有时间（动画/演出/回合/网络超时），**别把倍率调太高**（建议 ≤3x）。
> 倍率范围 `[1.0, 100]`：`1.0` = 正常，**低于 1 倍（减速）被硬性禁止**，请求在加载器层就会被忽略。

## 模组生态能力

| 特性 | 说明 |
|---|---|
| **Mod 能力声明 + 警告** | mod 在 `[ModManifest(Permissions=...)]` 声明会用到的能力（读对局/操作游戏/写文件），sidecar 同步导出。已取消权限门控：任何 mod 都能调用 SDK 全部 API；声明「操作游戏」的 mod 在加载时与工具列表里显示 ⚠️ 警告（仅提示来源可信，不阻止） |
| **模组元数据标准 + 依赖解析** | `mods\{ModId}\{ModId}.json` sidecar（id/版本/能力/SDK 版本/依赖，与 DLL 同文件夹）；加载器按依赖**拓扑排序**加载，缺失依赖/版本不符/循环依赖的 mod 被跳过并报告（`modmeta.cpp` 纯标准库，可单测） |
| **API 版本协商** | SDK 声明版本 `2.1.5`；mod 声明 `SdkVersion`，要求高于当前的 mod 被拒绝加载。`doorstop_config.json` 的 `sdkVersion` 声明当前版本 |
| **事件驱动化** | `GameEvents.StartAutoHook()` 内部每 1 秒维持 RPC 挂钩，mod 无需每秒轮询；`ModBase.Run` 不传 tick 则不空转 |
| **IL2CPP 互操作安全封装** | `il2cpp_safe.h` 收敛全部互操作点：函数指针空检查、参数/返回值校验、托管异常转译成可读错误，防止原生崩溃拖垮游戏 |
| **调试与故障体验** | mod 入口异常写 `logs\mod-errors.log`（SDK `SdkLog.ReportCrash` + 原生 `write_mod_error` 双写）；`cesium verify` 离线预检兼容性 |
| **脚手架与包分发** | `tools\cesium` CLI：`new`（生成项目+程序集级元数据）/ `build` / `package`（zip 分发）/ `list` / `verify`（模拟加载器判定） |
| **相机接管** | `CameraService` + `ICameraBackend` 接缝：解析/读写/快照还原；`CameraState` 可 JSON 持久化；`CinemachineService` 反射接入 Cinemachine（玩家没装也安全降级） |
| **输入与光标** | `InputService`：按键/鼠标查询 + mod 间输入独占仲裁 + 光标锁定还原；后端为「Unity 反射 → Win32」两级兜底 |
| **UI 登记** | `UiService`：通知 / 窗口 / 覆盖层 / OnGUI 回调，注册与渲染分离（无渲染后端时登记仍然可见）；mod 卸载自动全部移除 |
| **场景与协程** | `SceneService`（查询 + 事件订阅 + **事件不触发时的每帧兜底补发**）；`CoroutineService`（不依赖 GameObject 的协程，按真实时间等待） |
| **运行时反射** | `RuntimeAssemblyService`：按名找程序集/类型/成员，`SafeInvoke*` / `SafeGet*` 失败返回 `null`、`false` 并记日志 |
| **诊断转储** | `SdkDiagnostics.Dump()` 一次性落盘运行时+相机+场景+UI+计数器；`DumpJson()` 给工具读；IL2CPP/HybridCLR 不可用时也报告原因而非留空 |
| **ECall 隔离** | `UnityCall`：Unity 引擎调用全部两层隔离（`RawXxx` 只含 ECall + 安全方法只含 try/catch），保证"Unity 不可用时降级返回默认值"的契约真正成立。详见 `docs\SDK-Unity调用与ECall隔离.md` |

## SDK 文档

| 文档 | 内容 |
|---|---|
| [SDK-概览](docs/SDK-概览.md) | 整体分层、快速上手 |
| [SDK-生命周期与日志](docs/SDK-生命周期与日志.md) | `ModBase` / `ModContext` / 清理顺序 / 日志分级 |
| [SDK-配置与元数据](docs/SDK-配置与元数据.md) | `ModConfig`、sidecar、依赖解析 |
| [SDK-能力声明与元数据](docs/SDK-能力声明与元数据.md) | `Permissions` 声明与警告 |
| [SDK-事件](docs/SDK-事件.md) | `GameEvents` / `CameraEvents` / `UpdateService` |
| [SDK-玩家与名字](docs/SDK-玩家与名字.md) | `Players.*` / `Names.*` |
| [SDK-相机](docs/SDK-相机.md) | 相机解析、读写、快照还原、Cinemachine |
| [SDK-输入与光标](docs/SDK-输入与光标.md) | 按键/鼠标、输入独占、光标锁定 |
| [SDK-UI](docs/SDK-UI.md) | 通知 / 窗口 / 覆盖层 / OnGUI |
| [SDK-场景与协程](docs/SDK-场景与协程.md) | 场景查询订阅、兜底补发、协程 |
| [SDK-诊断](docs/SDK-诊断.md) | 三类 dump、落盘、反射工具、排查清单 |
| [SDK-变速](docs/SDK-变速.md) | 变速引擎 |
| [SDK-Unity调用与ECall隔离](docs/SDK-Unity调用与ECall隔离.md) | **改 SDK 前必读**：ECall 机制与隔离约定 |
| [mod-FreeCameraMod](docs/mod-FreeCameraMod.md) | 俯瞰视角 mod 的配置与原理 |
| [mod-SpeedHackMod](docs/mod-SpeedHackMod.md) | 变速 mod 的热键、配置与风险 |
| [工具-cesium-CLI](docs/工具-cesium-CLI.md) | 脚手架 CLI |

## 目录布局

游戏 exe 所在目录 (部署后)：

```
游戏目录\
├── version.dll                  ← 加载器 (本解决方案产物, Doorstop 代理)
└── AstralParty_ModLoader\
    ├── doorstop_config.json     ← 加载器配置 (enabled 总开关等)
    ├── bootstrap\               ← 托管引导程序 (CesiumLoader.Bootstrap.dll)
    ├── sdk\                     ← SDK 依赖 (CesiumLoader.SDK.dll)
    ├── mods\                    ← 用户 mod (每 mod 一个文件夹: mods\{ModId}\{ModId}.dll)
    │   └── ActivityLogMod\      ← 示例 mod 文件夹 (DLL + sidecar 同文件夹)
    │       ├── ActivityLogMod.dll
    │       └── ActivityLogMod.json   ← sidecar (id/版本/权限/enabled/依赖)
    └── logs\                    ← cesium-loader.log + activity-mod.log
```

> 兼容旧布局: 直接放在 `mods\` 根下的 `.dll`（旧版平铺）仍会被加载, 平滑升级无需迁移。

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
  "consoleTopmost": false,            // 控制台窗口置顶(默认 false)
  "forwardActivityLog": true,         // mod 日志转发到控制台
  "speedhackBaseSpeed": 1.0,          // 变速(加载器内置): 1.0=正常, 2.0=全程2倍速
  "speedControlEnabled": true,        // 变速控制文件通道(mod 热键变速用; false=关掉)
  "sdkVersion": "2.1.5"               // 当前 SDK 版本(校验 mod 的 SdkVersion)
}
```

## 构建

要求: Visual Studio 2022 (v143) + Windows SDK 10.0.26100 + .NET SDK 8/9/10。

```
# 1. NuGet 还原 (首次)
dotnet restore CesiumLoader.sln

# 2. 命令行构建 (或直接用 VS2022 打开 sln 按 F7)
MSBuild CesiumLoader.sln /p:Configuration=Release /p:Platform=x64

# 3. 单元测试 (SDK, 不依赖游戏)
dotnet test tests\CesiumLoader.SDK.Tests\CesiumLoader.SDK.Tests.csproj -c Release

# 4. 冒烟测试: 变速控制文件通道 (真实 version.dll + 宿主进程, 不依赖游戏)
pwsh -NoProfile -File tools\smoke-speedctl.ps1
```

产物:
- `bin\Release\version.dll` — C++ 加载器 (Doorstop 代理 + 变速引擎)
- `src\CesiumLoader.Bootstrap\bin\Release\netstandard2.0\CesiumLoader.Bootstrap.dll`
- `src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll`
- `src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.dll`
- `tools\cesium\bin\Release\net8.0\cesium.exe` — mod 脚手架与包分发 CLI

## SDK 工具包下载

mod 开发者无需克隆仓库——直接从 Release 下载 **SDK 工具包**（`cesium-sdk-tools.zip`，
win-x64 自包含，开箱即用）：

```
https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-sdk-tools.zip
```

包含:
- `cesium.exe` — mod 脚手架与包分发 CLI (自包含, 无需本机 .NET)
- `CesiumLoader.SDK.dll` — mod 开发引用 (编译期绑定, 需配合游戏热更程序集)
- `docs\` — SDK 文档 (概览/事件/玩家/操作/变速/生命周期/配置/能力声明)
- `examples\ActivityLogMod\` — 示例 mod 源码 (行为日志)

快速开始:

```
cesium new MyMod --author 你 --desc "第一个mod"    # 生成项目模板
cesium build MyMod                                  # 构建 (需本机 dotnet SDK)
cesium package MyMod -o MyMod-1.0.0.zip             # 打包分发
cesium verify <mods_dir>                            # 离线预检依赖/版本兼容
cesium --help                                       # 全部命令与帮助
```

> 提示: `cesium new` 生成的项目自动附带 `refs\CesiumLoader.SDK.dll`（从工具包同目录复制），
> 开箱即构建。mod 若直接用游戏类型（如 `RoomInfo`/`Battle`），把游戏热更 DLL 放进
> 项目 `refs\` 目录即可自动引用（csproj 已配 `Condition`）。

## 开发一个 mod

**推荐用脚手架 CLI** (生成项目 + 程序集级元数据 + sidecar):

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
    Permissions = CesiumLoader.SDK.ModPermission.ReadGameState,  // 声明会用到的能力(仅展示/警告)
    SdkVersion = "2.1.5")]                                        // API 版本协商
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

**能力声明**: `Permissions` 只是声明 mod 会用到的能力（读对局 / 操作游戏 / 写文件），
供工具和加载器展示警告——已取消权限门控，任何 mod 都能调用 SDK 全部 API。
声明「操作游戏」的 mod 在加载时与工具列表会显示 ⚠️ 警告，仅提示来源可信，不阻止。

编译出的 mod 放进 `AstralParty_ModLoader\mods\`（每 mod 一个文件夹：`mods\{ModId}\{ModId}.dll` + 可选同名 `.json` sidecar），重启游戏生效。
`cesium package` 打出的 zip 已是该布局，解压到 `mods\` 即完成安装。
发布前用 `cesium verify <mods_dir>` 离线预检依赖与版本兼容性。

SDK API 一览:
- `ModBase.Run(init, tick=null, delayMs=30000, tag)` — 生命周期 (不传 tick 不轮询)
- `GameEvents.*` — 15 个事件 + `StartAutoHook()` (SDK 内部维持挂钩, 事件驱动)
- `Permissions.Has/Require` — 恒返回 true (权限门控已取消, 仅保留 API 兼容)
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

打 tag `modloader-<版本>` 自动构建并发布「解压即部署」的压缩包到 GitHub Release：

```
git tag modloader-0.2.0
git push origin modloader-0.2.0
```

`release-modloader.yml` 会:
1. windows-latest 上 MSBuild 构建 C++ 加载器 (version.dll, 无游戏依赖)
2. dotnet 现场构建 CesiumLoader.Bootstrap (自包含, 零引用) + cesium CLI (SDK 工具包)
3. 用 `dist\modloader\` 里的预编译 SDK / 示例 mod (游戏热更 DLL 不入库, 故用 dist)
4. 打包成部署布局 + 生成 `cesium-loader.json` 清单 (版本 / SHA256 / 布局)
5. 组装 SDK 工具包 (cesium.exe + SDK DLL + 文档 + 示例源码)

产物 (Release 资产):
- `cesium-loader-v1.0.0.zip` — 加载器部署包 (带版本号)
- `cesium-loader.zip` — 固定名, 供 `releases/latest/download/cesium-loader.zip` 使用
- `cesium-sdk-tools-v1.0.0.zip` — SDK 工具包 (带版本号)
- `cesium-sdk-tools.zip` — 固定名, 供 `releases/latest/download/cesium-sdk-tools.zip` 使用

AstralParty.Toys 的 Mod 管理功能从这个 URL 下载安装:
`https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-loader.zip`

mod 开发者从这个 URL 下载 SDK 工具包:
`https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-sdk-tools.zip`

更新 SDK / mod 后记得同步 dist (本地构建 → 覆盖 dist → 提交):
```
dotnet build src\CesiumLoader.SDK -c Release
dotnet build src\ActivityLogMod -c Release
copy src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll dist\modloader\AstralParty_ModLoader\sdk\
mkdir dist\modloader\AstralParty_ModLoader\mods\ActivityLogMod   (若不存在)
copy src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.dll  dist\modloader\AstralParty_ModLoader\mods\ActivityLogMod\
copy src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.json dist\modloader\AstralParty_ModLoader\mods\ActivityLogMod\   (若存在)
```
