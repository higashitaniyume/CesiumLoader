# CesiumLoader

Astral Party 国服 (Steam appid 2622000, Unity 2021.3.45f2 IL2CPP + HybridCLR) 的 Mod 加载器与 SDK。

全部代码收在一个 Visual Studio 2022 解决方案里：

```
CesiumLoader.sln
├── src\
│   ├── CesiumLoader\          C++ DLL 加载器 (产出 winmm.dll, DLL 劫持)
│   ├── CesiumLoader.SDK\      C# SDK (netstandard2.0, 供 mod 引用)
│   └── ActivityLogMod\        C# 示例 mod (行为日志)
└── tools\
    └── gen_winmm_exports.ps1  从系统 winmm.dll 生成 C++ 转发桩
```

## 原理

UnityPlayer.dll 在进程启动时依赖 winmm.dll (导入 22 个函数)，而 winmm.dll
不在 KnownDLLs 列表——把本 DLL 命名为 `winmm.dll` 放在游戏 exe 同目录，
Windows 加载器会优先加载它。DLL 的 182 个导出 (180 个系统 winmm 函数 +
`ap_console_write` / `ap_console_write_w`) 全部转发到
`C:\Windows\System32\winmm.dll`，保证 AkSoundEngine / CRI 初始化正常。

首个 winmm 导出被调用时 (loader lock 已释放) 启动引导线程：

1. `AllocConsole` 分配控制台窗口 ("CesiumLoader Console")
2. 等待 `GameAssembly.dll` 加载 (60s 超时)
3. 解析 `il2cpp_*` 导出 (18 个函数指针)
4. 等待 il2cpp domain 就绪 (30s) + `il2cpp_thread_attach`
5. 等待 HybridCLR 热更 (`AstralParty.Runtime` 出现, 60s)
6. 设置环境变量 `CESIUM_MODS_DIR` / `CESIUM_LOG_DIR` / `CESIUM_SDK_DIR`
7. 先 `Assembly.Load(byte[])` 加载 `sdk\*.dll` (按文件名排序, 不调入口)
8. 再加载 `mods\*.dll` 并调用入口 `{文件名}.ModEntry.Main()`
9. 启动线程监控 `activity-mod.log`, 新增行转发到控制台

## 目录布局

游戏 exe 所在目录 (部署后)：

```
游戏目录\
├── winmm.dll                  ← 加载器 (本解决方案产物)
└── AstralParty_ModLoader\
    ├── sdk\                   ← SDK 依赖 (CesiumLoader.SDK.dll)
    ├── mods\                  ← 用户 mod (每个 DLL 一个 mod)
    └── logs\                  ← cesium-loader.log + activity-mod.log
```

## 构建

要求: Visual Studio 2022 (v143) + Windows SDK 10.0.26100 + .NET SDK 8/9。

```
# 1. NuGet 还原 (首次)
dotnet restore CesiumLoader.sln

# 2. 命令行构建 (或直接用 VS2022 打开 sln 按 F7)
MSBuild CesiumLoader.sln /p:Configuration=Release /p:Platform=x64

# 3. 重新生成 winmm 转发桩 (修改 gen 脚本后)
pwsh tools\gen_winmm_exports.ps1
```

产物:
- `bin\Release\winmm.dll` — C++ 加载器
- `src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll`
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
