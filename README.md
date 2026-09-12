# CesiumLoader

Astral Party 国服 (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod 加载器与 SDK。

GitHub: https://github.com/higashitaniyume/CesiumLoader

全部代码收在一个 Visual Studio 2022 解决方案里：

```
CesiumLoader.sln
├── src\
│   ├── CesiumLoader\              C++ DLL 加载器 (产出 version.dll, Doorstop 式代理 + 变速引擎)
│   ├── CesiumLoader.Bootstrap\    C# 托管引导程序 (netstandard2.0, 零引用, 编排 SDK/mods)
│   ├── CesiumLoader.SDK\          C# SDK (netstandard2.0, 供 mod 引用)
│   ├── ActivityLogMod\            C# 示例 mod (行为日志)
│   └── SpeedHackMod\              C# 示例 mod (游戏变速, SpeedHack SDK 接口)
├── third_party\minhook\            MinHook (inline hook 库, 变速引擎使用, MIT)
└── tools\
    └── smoke\                     转发/引导冒烟测试 (普通 .NET 可跑, 不依赖游戏)
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

1. 新建 C# 类库 (netstandard2.0)，ProjectReference 到 `CesiumLoader.SDK`，
   并引用游戏热更程序集 (见 `ActivityLogMod.csproj` 的 Reference 模式)。
2. 实现静态入口:

```csharp
public static class ModEntry
{
    public static void Main()
    {
        CesiumLoader.SDK.ModBase.Run(OnInit, OnTick, tag: "MyMod");
    }
    static void OnInit() { /* 订阅 GameEvents.* / 读取 Players.* / Names.* */ }
    static void OnTick() { /* 每 1s 轮询 */ }
}
```

3. 编译出的 DLL 放进游戏目录 `AstralParty_ModLoader\mods\`，重启游戏生效。

SDK API 一览:
- `ModBase.Run(init, tick, delayMs=30000, tag)` — 生命周期 (自动 30s 延迟防启动崩溃)
- `GameEvents.*` — 15 个事件: CardUsed / NoCard / EffectCardUsed / SkillUsed /
  QuickCardUsed / DiceResult / Move / BattleUpdate / BattleDice /
  RewardCardSelected / ShopCandidates / RelicCandidates / RelicSelected /
  RelicsSynced / HandChanged
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
