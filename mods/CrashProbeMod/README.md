# 崩溃探针（CrashProbeMod）

验证**加载器崩溃转储通路**的测试模组：打开游戏设置页后，故意制造一次原生访问冲突，让游戏以 `0xC0000005` 退出，用来确认 `AstralParty_ModLoader\logs\crash\` 下的 `.dmp` / `.txt` 能正常产生。

> ⚠ 它**真的会让游戏崩溃**。只在测试环境用，验证完就删掉 `mods\CrashProbeMod` 目录。
>
> 本模组**不随加载器发布包分发**，也不在 `tools\builtin-mods.json` 的内置清单里；它只是仓库里的一个测试工程，需要时手动部署。

## 行为

1. 模组加载后进入待机，日志里有一条 `崩溃探针: 已就绪...`。
2. **打开设置页**（或按 `F8`）后，等 `delayMs`（默认 1500ms），模组写一次空指针。
3. 进程以访问冲突退出，加载器的 VEH 落盘：

```text
AstralParty_ModLoader\logs\crash\<yyyyMMdd>-<HHmmss>-<pid>.dmp
AstralParty_ModLoader\logs\crash\<yyyyMMdd>-<HHmmss>-<pid>.txt
```

- `.dmp`：`MDMP` 格式，可直接用 Visual Studio 打开看线程栈与已加载模块。
- `.txt`：异常摘要（码/名、地址、线程、读写类型）+ 崩溃时的加载器环境（版本、路径、命令行、`GameAssembly.dll` 指纹、完整配置、模组加载结果）。

崩溃前模组会先写一行 `正在触发崩溃: 写空指针(预期 0xC0000005)`，这行在 `activity-mod-<会话>.log` 里，可用来确认"崩之前模组确实跑到了"。

## 为什么用裸指针

热更程序集不能 P/Invoke，托管异常又会被 SDK 的回调护栏接住（不会让进程退出）。所以这里用 `unsafe` 写地址 0：IL 是 `stind.i4`，HybridCLR 解释执行时就是一次原生写，内核抛 `0xC0000005`，加载器的 VEH 才能拿到现场。

如果某种写法被解释器拦成托管异常，模组会自动退化：空指针 → 未映射地址（`0xDEADBEE0`）→ 无限递归（栈溢出）。三条路都没让进程退出时，日志里会留一行 `三种方式都没能让进程退出`。

## 配置

`mods\CrashProbeMod\config.json`（保存后约 1 秒热重载）：

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 总开关；关掉后完全不做任何事 |
| `triggerOnSettings` | `true` | 打开设置页时触发 |
| `delayMs` | `1500` | 触发延迟（毫秒，0–60000） |
| `hotkey` | `"F8"` | 手动触发热键；`"none"` 表示不绑定 |

## 验证步骤

1. 确认 `doorstop_config.json` 里 `crashDumpEnabled` 不是 `false`（默认 `true`）。模组启动时也会自己核对，关了会在日志里报错。
2. 启动游戏，等模组加载完（控制台有 `mod load report`）。
3. 打开设置页；约 1.5 秒后游戏退出（Windows 可能弹"程序已停止工作"，正常）。
4. 检查转储：

```powershell
pwsh -NoProfile -File tools\verify-crash-dump.ps1
```

脚本会检查 `MDMP` 签名、配套 `.txt`、环境信息段是否齐全，并把摘要打出来。

## 排查

| 现象 | 原因 / 处理 |
|---|---|
| 设置页打开后什么都没发生 | 看 `activity-mod-<会话>.log` 里有没有 `已武装(设置页)`；若没有，说明 `UIManager._cachedSettingWindow` 判定失效（游戏更新改了字段），用热键 `F8` 或把 `hotkey` 换成别的键 |
| 崩了但 `logs\crash` 是空的 | `crashDumpEnabled=false`，或崩溃发生在加载器装 VEH 之前（启动最开始的 2 秒），或程序不是被访问冲突终止的 |
| 日志里出现 `三种方式都没能让进程退出` | 三种崩法都被拦住，把日志反馈给加载器维护者 |
| 想确认"崩溃后进程真的没了" | 任务管理器里 `AstralParty_CN.exe` 应消失；`logs\crash` 里多一对同名 dmp/txt |

## 卸载

删掉 `AstralParty_ModLoader\mods\CrashProbeMod\` 整个目录即可，下次启动不再加载。
