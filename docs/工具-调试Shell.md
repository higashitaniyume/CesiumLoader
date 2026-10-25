# CesiumLoader Debug Shell

控制台窗口里直接输入命令，查看加载器环境、改配置、看 mod 状态和日志，不用改文件、不用重启。

在 `doorstop_config.json` 里配置 `"debugShell": true`（或在运行中的 shell 里 `set loader.debugShell true`）后启用；默认关闭，关闭时控制台只显示日志。

启用后，控制台最后一行是提示符：

```text
cesium>
```

输入命令回车执行。`↑` / `↓` 翻历史命令，`Ctrl+V`（或 `Shift+Insert`）粘贴，`Ctrl+C` 取消当前输入（不会结束游戏），`Ctrl+L` 清屏，`Esc` 清空当前行。

鼠标：

- 滚轮上下滚动回看日志；一开始打字，视口自动回到最底部（与 cmd 一致）。
- 按住左键拖选文本，右键或 `Enter` 复制；有选区时 `Ctrl+C` 也是复制，不会结束游戏。
- 有选区时控制台输出会暂停（conhost 的 QuickEdit 固有行为，和 cmd 里选中文本一样），按 `Esc` 或复制完成即恢复。

## 命令

| 命令 | 作用 |
| --- | --- |
| `help [命令]` | 命令列表；`help set` 看单条说明 |
| `status` | 加载器、会话、进程摘要 |
| `env` | 版本、目录、环境变量 |
| `paths` | 全部目录与日志文件（标注是否存在） |
| `mods [--all]` | 模组加载结果；`--all` 连禁用的也列出 |
| `hooks` | 变速 hook、控制台、日志转发状态 |
| `log [行数\|path]` | 本次启动的加载器日志尾部，默认 20 行 |
| `proc` | 进程内存、句柄、线程 |
| `ls [过滤]` | 列出全部可配置项（同 `set --list`） |
| `get <键>` | 显示某个配置项的值、类型、生效时机 |
| `set <键> <值>` | 设置配置项 |
| `reload` | 重新读取 `doorstop_config.json` |
| `clear` | 清屏 |
| `detach` | 关闭 shell，游戏继续运行 |
| `exit [代码]` | **结束游戏进程** |

任意命令加 `--help`（如 `set --help`）显示该命令的说明。

## 配置键

配置键分两类：

```text
loader.<字段>       加载器配置
<ModId>.<键>        模组的 config.json
```

例如：

```text
cesium> set TurnFocusMod.cooldownSeconds 2
TurnFocusMod.cooldownSeconds = 2   原值 1
  ModConfig 模组约 1 秒内热重载; 旧式 SdkConfig 需重启

cesium> set loader.speedhackBaseSpeed 3
loader.speedhackBaseSpeed = 3   原值 2
  已应用: 当前倍率 3.0x
```

`ls` 可以看到所有可用键（`loader.*` 与各模组当前的配置项），带过滤：

```text
cesium> ls TurnFocus
配置项 4 个（loader.* 与 <ModId>.<键>; set 用法: set --help）
  TurnFocusMod.alwaysPlaySound  = false            bool
  TurnFocusMod.cooldownSeconds  = 1                int
  TurnFocusMod.enabled          = true             bool
  TurnFocusMod.soundFile        = C:\tmp\ding.wav  string
```

字符串值里有空格时用引号包住：

```text
cesium> set TurnFocusMod.soundFile "C:\sounds\your turn.wav"
```

## 生效时机

每个 `loader.*` 字段都带一个生效时机，`get` 会显示：

| 时机 | 含义 |
| --- | --- |
| 立即生效 | 改完马上应用（变速、控制台置顶、日志转发、shell 开关） |
| 只能开启 | 文件通道线程是常驻的，可从关到开，关闭需要重启 |
| 重启后生效 | 只写进配置，下次启动游戏时生效（引导流程、超时、Steam 绕过等） |

模组配置写盘后由模组自己决定：使用 `ModConfig` 的模组约 1 秒内热重载，旧式 `SdkConfig` 模组需要重启游戏。

## 写盘行为

- 修改 `loader.*` 会立即写入 `doorstop_config.json`：只替换该键的值，**注释与键顺序保留**，原文件备份为 `doorstop_config.json.bak`。
- 修改模组键会写入对应的 `config.json`（`mods/<ModId>/config.json`；旧式模组为 `configs/<ModId>.json`），同样保留原有格式并备份 `.bak`。
- 改写后的内容会先重新解析校验，解析失败就不写盘。
- 值会按字段类型校验：布尔接受 `true/false/1/0/on/off`，数字有范围限制（例如倍率不能低于 1、超时 1–3600）。

## 关闭 shell

- 只想关掉命令行、保留日志窗口：`detach`，或配置 `set loader.debugShell false`（下次启动也不启用）。
- 想恢复：`set loader.debugShell true`，或把 `doorstop_config.json` 的 `debugShell` 改成 `true` 后重启游戏。

## 说明

- 控制台窗口由加载器用 `AllocConsole` 分配。Windows 的“默认终端应用”设置只对控制台程序启动时创建的控制台生效，不会接管这种后分配的控制台，所以窗口始终是传统控制台（conhost）。
- shell 运行在独立线程，只读写加载器自己的状态与配置文件，不介入游戏逻辑。
- `exit` 会结束游戏进程，和任务管理器结束进程等价；对局中使用会直接掉线。
