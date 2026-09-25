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
│   ├── FreeCameraMod\             内置 mod (滚轮缩放)
│   ├── SpeedHackMod\              内置 mod (变速热键)
├── third_party\                    随仓库入库的第三方依赖 (vendored: 不要包管理器, 克隆即可编译, 见其 README)
│   ├── minhook\                    MinHook (inline hook 库, 变速引擎使用, BSD-2-Clause)
│   ├── fmt\                        fmt (字符串格式化, header-only, MIT)
│   ├── spdlog\                     spdlog (日志, header-only, MIT)
│   └── nlohmann_json\              nlohmann/json (JSON/JSONC 解析, 单头文件, MIT)
├── smoke\SpeedCtlSmoke\            冒烟测试宿主 (变速控制文件通道, 不依赖游戏)
├── dist\modloader\                 预编译托管产物 (SDK + 内置 mod + doorstop_config.json,
│                                   必须入库 —— CI 只校验不重建, 见「发布」)
└── tools\
    ├── cesium\                    模组脚手架与包分发 CLI (new/build/package/list/verify)
    ├── builtin-mods.json          内置 mod 清单的唯一来源 (CI 与打包脚本共用)
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
- 游戏内实时调整用内置 mod **变速 (SpeedHackMod)**：`Delete`（键位可在 AstralParty.Toys 的 mod 配置里改，
  支持鼠标侧键）在 `1.0x` 与"刚才的倍率"之间
  切换（再按一次切回），`Alt+=` / `Alt+-` 调倍率（`1.0` 起，默认每次 0.5，按住连续调，调完自动写回配置）。
- **时间连续性**：`speed == 1.0` 同样走虚拟时间公式（此时是精确的整数平移），**不做**"直接返回真实时间"
  的快捷路径 —— 否则从 2x 切回 1.0x 的瞬间游戏看到的时钟会倒退（主线程卡住约十秒，画面冻结但声音还在）。
  设成 `1.0` 时 hook 仍保持安装（`state.txt` 里 `active=1`），只是斜率变成 1。
- 控制文件通道的目录 `speed\` 会镜像一份到 `%LocalAppData%\AstralParty_ModLoader\speed`，两处都监听、都写。

> ⚠️ 变速影响游戏感知的所有时间（动画/演出/回合/网络超时），**别把倍率调太高**（建议 ≤3x）。
> 倍率范围 `[1.0, 100]`：`1.0` = 正常，**低于 1 倍（减速）被硬性禁止** ——
> 请求解析（`parse_speed`）、引擎设置（`speedhack_set_speed`）、
> SDK（`SetSpeed`/`ClampSpeed`/`StepSpeed`）与 AstralParty.Toys 前端逐层拦截，且一律**不改动引擎状态**；
> 把 `config.json` 的 `minSpeed` 写成 `0.1` 也会被 SDK 抬回 `1.0`。

## 模组生态能力

| 特性 | 说明 |
|---|---|
| **Mod 能力声明 + 警告** | mod 在 `[ModManifest(Permissions=...)]` 声明会用到的能力（读对局/操作游戏/写文件），sidecar 同步导出。已取消权限门控：任何 mod 都能调用 SDK 全部 API；声明「操作游戏」的 mod 在加载时与工具列表里显示 ⚠️ 警告（仅提示来源可信，不阻止） |
| **模组元数据标准 + 依赖解析** | `mods\{ModId}\{ModId}.json` sidecar（id/版本/能力/SDK 版本/依赖，与 DLL 同文件夹）；加载器按依赖**拓扑排序**加载，缺失依赖/版本不符/循环依赖的 mod 被跳过并报告（`modmeta.cpp` 纯标准库，可单测） |
| **API 版本协商** | SDK 声明版本 `2.2.1`；mod 声明 `SdkVersion`，要求高于当前的 mod 被拒绝加载。`doorstop_config.json` 的 `sdkVersion` 声明当前版本 |
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
| [SDK-操作](docs/SDK-操作.md) | `GameActions`：像玩家一样发送 C2S 指令（⚠️ 真实影响对局）|
| [SDK-变速](docs/SDK-变速.md) | 变速引擎（倍率下限 1.0、控制文件通道）|
| [SDK-Unity调用与ECall隔离](docs/SDK-Unity调用与ECall隔离.md) | **改 SDK 前必读**：ECall 机制与隔离约定 |
| [平台约束-第三方库与AOT裁剪](docs/平台约束-第三方库与AOT裁剪.md) | **引第三方库前必读**：托管第三方库为何加载不起来、游戏内置 Newtonsoft 的裁剪面、存在性权威判定方法 |
| [steam-bypass](docs/steam-bypass.md) | **脱离 Steam 运行**：加载器原生绕过（6 hook）的四阶段原理、配置全表、已验证测试矩阵、已知损失与回退方法 |
| [mod-FreeCameraMod](docs/mod-FreeCameraMod.md) | 滚轮缩放 mod 的配置与原理（进游戏不接管；F1 恢复原视角）|
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
    ├── logs\                    ← cesium-loader.log + activity-mod.log
    └── speed\                   ← 变速控制文件通道 (request.txt / state.txt)
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
  "speedhackBaseSpeed": 1.0,          // 变速(加载器内置): 1.0=正常, 2.0=全程2倍速; 低于 1.0 按 1.0 处理(不允许减速)
  "speedControlEnabled": true,        // 变速控制文件通道(mod 热键变速用; false=关掉)
  "steamBypassEnabled": true,         // Steam 自杀门绕过(阶段1): 把 AOT 类 SteamManager.Awake() 换成 no-op
  "steamBypassRestartCheck": true,    // 阶段1 附加保险: steam_api64!SteamAPI_RestartAppIfNecessary 恒返回 0
  "steamBypassMatchmaking": true,     // 阶段2 大厅匹配绕过: CreateLobbyAsync/JoinLobbyAsync 恒返回"已完成的空 Task"
                                      // (修 Steam 全关时点创建房间/加入房间毫无反应; 只受本开关控制)
  "steamBypassLobbyHasValue": true,   // 阶段3: Nullable`1<Lobby>::get_HasValue() 恒返回 false
                                      // **实测作废(保留但无效)**: HybridCLR 解释器把 Nullable.HasValue
                                      // 当内建指令内联, 该 hook 从不被命中(hook 无害, 但不是解法)。
                                      // 只在 steamBypassMatchmaking=true 时有意义
  "steamBypassTaskCtorMode": "auto",  // 方案C(主): 造空 Task 时 Task`1..ctor(T) 的调用方式 ——
                                      // "auto"(默认, 先 direct 原生 ABI 直调 ctor 的 methodPointer,
                                      //   探针校验失败自动退回 invoke 并记日志) / "direct" / "invoke"。
                                      // 绕过 il2cpp_runtime_invoke 对 Nullable<Lobby> 的错误编组
                                      // (它把 hasValue 弄成非零 -> val.HasValue 误判 true -> SetPublic NRE)。
                                      // 日志会分别打印 direct / invoke 两条路径的 hasValue 诊断。
  "steamBypassLobbyMethods": false,   // 方案B(备用安全网): Lobby.SetPublic()/SetJoinable(bool) 挂 no-op,
                                      // Lobby.Id 是属性则 get_Id() 恒返回 0(是字段则只记日志)。
                                      // **默认 false, 不要改回 true**: 实机证明 true 会让游戏启动早期
                                      // 崩溃(0xC0000005 / GameAssembly.dll, 崩溃前最后一条日志是方案B
                                      // 的 "Lobby.get_Id 首次被拦截"); 改回 false 后一切正常。
                                      // 现在也不需要它: 方案C 已把 Task 结果构造成真正的空
                                      // Nullable(HasValue=false), SetPublic/SetJoinable 不会被调用。
                                      // 它只是"hasValue 若又变 true"的备用安全网, 启用前需自行实机验证
  "steamBypassLobbyQuery": true,      // 阶段5: Steamworks.Data.LobbyQuery::RequestAsync() 恒返回
                                      // "预建好的、结果为**长度 0 的 Lobby[]** 的已完成 Task<Lobby[]>"。
                                      // 实机实测 6 条 NRE 抛在这个 await 上(栈顶就是 RequestAsync,
                                      // **不是** SteamMatchmaking.LobbyList 取值本身 —— 此前文档标为
                                      // "未实测"的那一点已据此更正); 危害是 **await 之后的代码全部不执行**
                                      // -> 退房/解散/被踢后本地房间状态不清、被踢提示不弹、不返回房间列表页。
                                      // 返回长度 0 的数组后 RoomLogic.cs:385 的 array.Length > 0 为 false
                                      // -> 跳过 Steam 大厅清理 -> :398 UpdateRoomByExit / :401 ClearRoomInfo
                                      // / :567-573 正常执行。全游戏只有这 2 处调用(:384/:554), 影响面为零。
                                      // **独立开关**: 与上面几个 steamBypass* 互不影响。
                                      // false = 恢复原版行为(退房/解散/被踢时 await 之后仍被跳过)
  "sdkVersion": "2.2.1"               // 当前 SDK 版本(校验 mod 的 SdkVersion)
}
```

## 构建

要求: Visual Studio 2022 (v143) + Windows SDK 10.0.26100 + .NET SDK 8/9/10。

```
# 1. NuGet 还原 (首次)
dotnet restore CesiumLoader.sln

# 2. 命令行构建 (或直接用 VS2022 打开 sln 按 F7)
MSBuild CesiumLoader.sln /p:Configuration=Release /p:Platform=x64

# 3. 单元测试 (SDK, 不依赖游戏; 覆盖控制文件通道 / 倍率下限 / 时间连续性, 当前 313 个用例)
dotnet test tests\CesiumLoader.SDK.Tests\CesiumLoader.SDK.Tests.csproj -c Release

# 4. 冒烟测试: 变速控制文件通道 (真实 version.dll + 宿主进程, 不依赖游戏 ——
#    控制文件通道在"等 GameAssembly.dll"之前就已就绪, 所以不需要游戏)
pwsh -NoProfile -File tools\smoke-speedctl.ps1

# 5. 构建 + 组装 dist + 打包 Release 压缩包 (托管部分走 dotnet build,
#    内置 mod 清单读 tools\builtin-mods.json; 产物在 dist\release\)
pwsh -NoProfile -File tools\package-modloader.ps1
```

产物:
- `bin\Release\version.dll` — C++ 加载器 (Doorstop 代理 + 变速引擎)
- `src\CesiumLoader.Bootstrap\bin\Release\netstandard2.0\CesiumLoader.Bootstrap.dll`
- `src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll`
- `src\ActivityLogMod\bin\Release\netstandard2.0\ActivityLogMod.dll` — 内置 mod (行为日志)
- `src\FreeCameraMod\bin\Release\netstandard2.0\FreeCameraMod.dll` — 内置 mod (自由相机: 滚轮缩放)
- `src\SpeedHackMod\bin\Release\netstandard2.0\SpeedHackMod.dll` — 内置 mod (变速热键)
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
    SdkVersion = "2.2.1")]                                        // API 版本协商
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

打 tag `modloader-<版本>` 自动构建并发布「解压即部署」的压缩包到 GitHub Release。
**版本号取自 tag**（CI 把 `modloader-` 前缀去掉），所以打 tag 前不需要改任何版本文件，
但要保证 SDK / mods / dist / 文档里的 `2.x.y` 字符串彼此一致：

```
git tag modloader-2.2.1
git push origin modloader-2.2.1
```

`release-modloader.yml` 会:
1. windows-latest 上 MSBuild **只构建原生加载器**（`src\CesiumLoader\CesiumLoader.vcxproj`
   `/p:Configuration=Release /p:Platform=x64`，不是整个 sln），无游戏依赖
2. dotnet 现场构建 CesiumLoader.Bootstrap (自包含, 零引用) + cesium CLI (SDK 工具包)
3. 用 `dist\modloader\` 里的预编译 SDK / 内置 mod (游戏热更 DLL 不入库, 故用 dist)；
   内置 mod 清单读 `tools\builtin-mods.json`（与本地打包脚本同一来源），并逐项校验产物存在
4. 打包成部署布局 + 生成 `cesium-loader.json` 清单 (版本 / SHA256 / 布局)
5. 组装 SDK 工具包 (cesium.exe + SDK DLL + 文档 + 示例源码)

产物 (Release 资产):
- `cesium-loader-2.2.1.zip` — 加载器部署包 (带版本号)
- `cesium-loader.zip` — 固定名, 供 `releases/latest/download/cesium-loader.zip` 使用
- `cesium-sdk-tools-2.2.1.zip` — SDK 工具包 (带版本号)
- `cesium-sdk-tools.zip` — 固定名, 供 `releases/latest/download/cesium-sdk-tools.zip` 使用

AstralParty.Toys 的 Mod 管理功能从这个 URL 下载安装:
`https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-loader.zip`

mod 开发者从这个 URL 下载 SDK 工具包:
`https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-sdk-tools.zip`

> 📌 **发布顺序：先发加载器，再发 Toys。** AstralParty.Toys 的发布流水线固定从
> `releases/latest/download/cesium-loader.zip` 拉取并内嵌（覆盖 `Resources\ModLoader\`，
> 含 `loader-version.json`，但不含 `doorstop_config.json`），所以加载器还没发布就先打 Toys 的 tag，
> 会把上一版加载器打进去。

更新 SDK / mod 后同步 dist（`dist\` 里预编译的托管产物必须入库，CI 只校验不重建）:

```
pwsh -NoProfile -File tools\package-modloader.ps1
```

它会重建 SDK 与 `tools\builtin-mods.json` 里列出的每个内置 mod，组装
`dist\modloader\AstralParty_ModLoader\`（sdk + mods + doorstop_config.json），
再产出 `dist\release\*.zip` 与清单。跑完 `git status` 看一眼 dist 的改动一起提交即可。
