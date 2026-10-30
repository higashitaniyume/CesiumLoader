<div align="center">

# CesiumLoader

## 感谢您选择并使用 CesiumLoader！

**此仓库是游戏 《Astral Party》的模组加载器与开发 SDK**

原生引导 · 托管模组 · 事件 API · 开发工具

[![Release](https://img.shields.io/github/v/release/higashitaniyume/CesiumLoader?style=flat-square&label=release)](https://github.com/higashitaniyume/CesiumLoader/releases)
[![Release CI](https://img.shields.io/github/actions/workflow/status/higashitaniyume/CesiumLoader/release-modloader.yml?style=flat-square&label=Release%20CI)](https://github.com/higashitaniyume/CesiumLoader/actions/workflows/release-modloader.yml)
[![Loader License](https://img.shields.io/badge/loader-AGPL--3.0-555?style=flat-square)](LICENSE)
[![SDK License](https://img.shields.io/badge/SDK-MIT-555?style=flat-square)](loader/CesiumLoader.SDK/LICENSE)

![C++](https://img.shields.io/badge/C%2B%2B-00599C?style=flat-square&logo=cplusplus&logoColor=white)
![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Unity](https://img.shields.io/badge/Unity-222?style=flat-square&logo=unity&logoColor=white)
![HybridCLR](https://img.shields.io/badge/HybridCLR-555?style=flat-square)
![MSVC v145](https://img.shields.io/badge/VS%202026-MSVC%20v145-555?style=flat-square)

[下载加载器](https://github.com/higashitaniyume/CesiumLoader/releases/latest) · [SDK 工具包](https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-sdk-tools.zip) · [SDK 文档](docs/SDK-概览.md) · [AstralParty.Toys](https://github.com/higashitaniyume/AstralParty.Toys)

</div>

---

为 Astral Party 的 **Unity IL2CPP + HybridCLR** 运行环境提供模组加载、依赖解析、日志与变速能力。原生 `version.dll` 代理在游戏启动时引导，待热更程序集就绪后加载 SDK 与托管模组。

目标环境：Windows x64，Astral Party（Steam App ID `2622000`，Unity `2021.3.45f2`）。版本变更见 [Release 记录](https://github.com/higashitaniyume/CesiumLoader/releases)。

## 安装

推荐通过配套桌面工具 [AstralParty.Toys](https://github.com/higashitaniyume/AstralParty.Toys) 安装、更新和配置：

```powershell
winget install Hiynet.AstralParty.Toys
```

手动安装：退出游戏，将 [cesium-loader.zip](https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-loader.zip) 解压到游戏主程序所在目录。默认卡牌皮肤可另行下载 [cesium-default-skins.zip](https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-default-skins.zip)，同样解压到游戏目录。

```text
游戏目录/
├── version.dll
└── AstralParty_ModLoader/
    ├── doorstop_config.json     加载器配置
    ├── sdk/                    SDK 程序集
    ├── bootstrap/              托管引导程序
    ├── mods/{ModId}/           模组 DLL、元数据与配置
    ├── logs/                   每次启动独立的引导、行为与错误日志
    ├── speed/                  变速控制文件通道
    └── window/                 窗口置前控制文件通道
```

新增模组解压至 `mods/`，保持 `{ModId}/{ModId}.dll` 布局；新增模组或修改加载器配置后重启游戏。模组自身的 `config.json` 在支持热重载的模组中保存后自动生效，具体见各模组文档。旧版 `winmm.dll` 加载器需先移除，避免重复引导。兼容旧版直接放在 `mods/` 根目录的 DLL。

## 内置模组

| 模组 | 功能 |
| --- | --- |
| [AutoThanksMod](docs/mod-AutoThanksMod.md) | 队友治疗、给牌、转星币后自动感谢，结算逐人点赞 |
| [CardSkinMod](mods/CardSkinMod/README.md) | 自定义卡面、多方案与 PNG 序列帧动态卡面 |
| [CharacterSkinMod](docs/mod-CharacterSkinMod.md) | 角色外观、多人物皮肤方案与精灵动画 |
| [CombatOddsMod](docs/mod-CombatOddsMod.md) | 战斗胜率分析、头顶攻防数字与悬停详情，怪物名字/血量旁显示技能 CD，梅加斯技能按钮上方显示轨道轰炸理论伤害（回放可见），标记与摩西精密技巧闪避显示 |
| [FreeCameraMod](docs/mod-FreeCameraMod.md) | 滚轮沿视线缩放，默认 `F1` 恢复视角；锁定镜头默认关闭，需手动设置 `autoFollow=false` |
| [SpeedHackMod](docs/mod-SpeedHackMod.md) | 游戏内调整倍率，切换正常速度与上次倍率 |
| [TurnClockMod](docs/mod-TurnClockMod.md) | 变速时只把自己的操作倒计时按真实时间走，动画、战斗与其他玩家回合照常加速 |
| ActivityLogMod | 记录出牌、投骰、回合等行为并转发到控制台 |
| [ForkDirectionMod](mods/ForkDirectionMod/README.md) | PVE 岔路三个相邻地块按顺序显示红 A、黄 B、蓝 C 与地块 ID |
| [HideHudMod](docs/mod-HideHudMod.md) | 长按反引号键（Tab 上方）隐藏战斗 HUD，可改为按一下切换；手牌、怪物信息、头像与其他 HUD 可分别开关 |
| [TurnFocusMod](mods/TurnFocusMod/README.md) | 轮到自己回合、选择筹码、战斗出牌、攻击或防御、匹配成功，或开房后有人发准备消息时把游戏窗口切到前台，可选播放提示音 |
| [QuickChatPlusMod](mods/QuickChatPlusMod/README.md) | 战斗内快捷短语面板补入房间等待、结算与其他模式的既有短语，可按类别与模式开关 |
| [HomeBottomBarMod](mods/HomeBottomBarMod/README.md) | 主页底栏隐藏公告，设置左移一位，最右侧增加退出按钮 |
| [UnlockFrameRateMod](mods/UnlockFrameRateMod/README.md) | 在游戏帧率列表追加 144/165/240/360/不限帧 选项，选中后由游戏自己应用 |
| [LanguageUnlockMod](mods/LanguageUnlockMod/README.md) | 国服启用设置里已有的英文 / 日文 / 繁中语言选项，切换后重登生效 |

内置模组的 `config.json` 带简中、繁中、英文、日文配置说明，Toys 会按界面语言显示；第三方模组可按 [配置与元数据](docs/SDK-配置与元数据.md) 用 `ConfigDoc` 提供说明。


## 配置与运行

通过 Toys 编辑加载器与模组配置，或直接修改 `AstralParty_ModLoader/doorstop_config.json`。常用配置：

| 字段 | 用途 |
| --- | --- |
| `enabled` | 加载器总开关 |
| `consoleEnabled` / `consoleTopmost` | 控制台显示与置顶 |
| `debugShell` | CesiumLoader Debug Shell 的命令输入（默认开） |
| `forwardActivityLog` | 将模组行为日志转发到控制台 |
| `speedhackBaseSpeed` | 启动时的基础倍率，`1.0` 为正常速度 |
| `speedControlEnabled` | 允许通过文件通道调整倍率 |
| `windowControlEnabled` | 允许模组通过文件通道把游戏窗口切到前台并播放提示音(WAV) |
| `useManagedBootstrap` | 实验性托管编排；默认使用原生编排 |

**变速范围为 1–100 倍，建议不超过 3 倍。** 变速影响动画、演出、回合与网络超时；切换倍率时保持虚拟时间连续。默认 `Delete` 切换正常速度与上次倍率，`Alt` + `=` / `Alt` + `-` 调整倍率，热键可在 Toys 中修改。

Steam 绕过涉及多个独立开关，推荐通过 Toys 选择启动方式。原理、配置与回退方法见 [Steam 绕过说明](docs/steam-bypass.md)。

控制台窗口由加载器通过 `AllocConsole` 分配，始终由传统控制台承载：Windows 的默认终端应用设置只作用于控制台程序启动时创建的控制台，不会接管这里后分配的控制台。`consoleTopmost` 作用于这个控制台窗口。

## CesiumLoader Debug Shell

控制台窗口里可以直接输入命令查看环境、改配置、看日志（配置项 `debugShell`，默认开启；设为 `false` 则只显示日志）：

```text
cesium> ls TurnFocus
cesium> set TurnFocusMod.cooldownSeconds 2
cesium> set loader.speedhackBaseSpeed 3
cesium> exit
```

配置键为 `loader.<字段>` 或 `<ModId>.<键>`，`ls` 列出全部可配置项，`get` / `set` 读写单个键，`help` 查看全部命令。修改会立即写盘并保留文件原有格式。完整说明见 [调试 shell](docs/工具-调试Shell.md)。

日志按启动会话分别保存，文件名包含时间与进程 ID；排障时保留对应会话的日志及同次构建的 DLL/PDB。

## 开发模组

下载 [SDK 工具包](https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/cesium-sdk-tools.zip)，内含自包含 `cesium.exe`、SDK 程序集与符号、文档和示例。编译模组仍需本机安装 .NET SDK。

```powershell
cesium new MyMod --author YourName --desc "My first mod"
cesium build MyMod
cesium package MyMod -o MyMod-1.0.0.zip
cesium verify <mods_dir>
```

模组使用 `netstandard2.0`，以 `{ModId}.ModEntry.Main()` 为入口。脚手架自动生成 SDK 引用、程序集元数据与分发布局；直接使用游戏类型时，将所需热更程序集放入项目 `refs/`。

SDK 提供生命周期、游戏事件、玩家数据、相机、输入、UI、场景、协程、诊断、帧率与游戏操作 API。加载器依据元数据检查 SDK 版本与依赖，并按依赖顺序加载；单个模组失败会记录日志，不中断其他模组。能力声明用于展示提示，不限制 API 调用。

**HybridCLR 热更侧受 AOT 裁剪约束，无法任意使用 P/Invoke 或完整 .NET BCL。** 开发前请阅读平台约束与 Unity 调用说明。

| 文档 | 内容 |
| --- | --- |
| [SDK 概览](docs/SDK-概览.md) · [生命周期与日志](docs/SDK-生命周期与日志.md) | 开始开发、初始化与清理 |
| [配置与元数据](docs/SDK-配置与元数据.md) · [能力声明](docs/SDK-能力声明与元数据.md) | 配置、版本、依赖与能力描述 |
| [事件](docs/SDK-事件.md) · [玩家与名字](docs/SDK-玩家与名字.md) | 对局事件与数据查询 |
| [相机](docs/SDK-相机.md) · [输入与光标](docs/SDK-输入与光标.md) · [UI](docs/SDK-UI.md) | 交互与覆盖层 |
| [场景与协程](docs/SDK-场景与协程.md) · [变速](docs/SDK-变速.md) · [帧率](docs/SDK-帧率.md) | 场景、调度、时间与画面帧率控制 |
| [窗口焦点](docs/SDK-窗口焦点.md) | 把游戏窗口切到前台并播放提示音的文件通道 |
| [操作](docs/SDK-操作.md) · [诊断](docs/SDK-诊断.md) | 游戏指令与排障 |
| [Unity 调用与 ECall 隔离](docs/SDK-Unity调用与ECall隔离.md) · [平台约束](docs/平台约束-第三方库与AOT裁剪.md) | 运行时兼容要求 |
| [cesium CLI](docs/工具-cesium-CLI.md) | 脚手架、打包与离线预检 |
| [调试 shell](docs/工具-调试Shell.md) | 控制台命令：查环境、改配置、看日志 |
| [MCP 桥接](mcp/README.md) · [桥接设计](docs/MCP-Agent桥接.md) | 可选 AI 接管组件，默认发布包不包含 |

## 构建与发布

需要 Visual Studio 2026（MSVC v145 工具链）、Windows 10/11 SDK 与 .NET SDK 8 或更高版本。在本仓库根目录、VS 2026 开发者终端中执行：

```powershell
dotnet restore CesiumLoader.sln
MSBuild CesiumLoader.sln /p:Configuration=Release /p:Platform=x64
dotnet test tests/CesiumLoader.SDK.Tests/CesiumLoader.SDK.Tests.csproj -c Release
dotnet test tests/TurnFocusMod.Tests/TurnFocusMod.Tests.csproj -c Release
pwsh -NoProfile -File tools/smoke-speedctl.ps1
pwsh -NoProfile -File tools/package-modloader.ps1
```

| 目录 | 职责 |
| --- | --- |
| `loader/` | C++ 原生加载器、C# Bootstrap 与 SDK |
| `mods/` | 内置模组与可选实验组件 |
| `mcp/` | 游戏内桥接模组与 MCP server |
| `tools/` | CLI、打包与部署脚本 |
| `tests/` / `smoke/` | 单元测试与变速冒烟测试 |
| `refs/game/` | 编译期引用的游戏程序集（随仓库提供，见 [说明](refs/game/README.md)） |
| `dist/modloader/` | 打包时生成的分发布局；其中的程序集不入库 |
| `third_party/` | 随仓库提供的第三方依赖 |

仓库自带全部构建输入：SDK、内置模组与测试在编译期需要的游戏程序集放在 `refs/game/`，因此 `git clone` 之后无需本地提取目录、也无需任何预编译产物即可构建。游戏更新后运行 `tools/fetch-game-refs.ps1` 可整组刷新这些程序集。

发布由 `modloader-<版本>` 标签触发。CI 从源码构建原生加载器、Bootstrap、SDK、全部内置模组与 CLI，运行测试，再用 `tools/stage-modloader-dist.ps1` 组装 `dist/modloader/` 分发布局并打包；内置清单统一读取 `tools/builtin-mods.json`。`tools/package-modloader.ps1` 复刻同一套步骤，打 tag 前可先在本地验证。

Release 提供加载器包、SDK 工具包及独立默认皮肤包，加载器与工具包均有版本化名称和固定下载名称。**先发布 CesiumLoader，再发布 Toys**，确保 Toys 内嵌最新加载器。部署包细节见 [发布包布局](docs/发布包布局.md)。

## 许可

Copyright © 2026 Yume Higashitani。

- 加载器、Bootstrap、内置模组、MCP 与工具的原创代码采用 [GNU AGPL-3.0](LICENSE)。
- `loader/CesiumLoader.SDK/` 中的 SDK 采用 [MIT License](loader/CesiumLoader.SDK/LICENSE)。第三方模组仅引用 SDK 时，可自行选择开源或闭源及分发协议；复制 AGPL 代码仍需遵守其条款。
- SDK 工具包中的其他组件及随仓库分发的第三方库，各自遵循其许可证。

软件按许可证约定提供，不附带担保。
