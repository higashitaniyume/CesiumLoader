# mod-变速 (SpeedHackMod)

> 用热键在**游戏里实时**调整时间流速的内置 mod。变速引擎本身由加载器提供（`version.dll`
> inline hook 系统时间函数），这个 mod 只是把引擎接到键盘上，并记住你调过的倍率。

## 热键

| 热键 | 作用 |
| --- | --- |
| `Delete` | **开关变速**：开 = 用上次的倍率，关 = 回到 `1.0x` |
| `Alt` + `=`（就是 `+`，小键盘 `+` 也行） | **加速**，每次 `speedStep`（默认 0.5）；按住不放会连续加速 |
| `Alt` + `-`（小键盘 `-` 也行） | **减速**，同上 |

- **只读键盘**：不接管鼠标、不做输入独占，也不拦截游戏自己的按键 —— 不会影响正常游玩。
- **按下即生效**，不用重启游戏；屏幕上会弹当前倍率（如 `变速: 2.5x`），
  加载器日志里对应 `[speedhack] 倍率 -> 2.5`，mod 日志里对应 `倍率 2x -> 2.5x`。
- 关着的时候按 `Alt` + `+`/`-` 会**直接开启**并从 `1.0x` 起算
  （否则"想减速"会算成加速，很反直觉）。
- 倍率限制在 `minSpeed` ~ `maxSpeed`（默认 `0.1` ~ `10`），到极限只弹提示、不再变化。

## 配置

`mods\SpeedHackMod\config.json`（首次运行自动生成）：

```json
{
  "speed": 2.0,
  "speedStep": 0.5,
  "minSpeed": 0.1,
  "maxSpeed": 10.0,
  "toggleKey": "Delete",
  "speedUpKey": "Equals",
  "speedDownKey": "Minus",
  "useNumpadKeys": true,
  "notify": true,
  "rememberSpeed": true,
  "repeatInterval": 0.15
}
```

| 键 | 说明 |
| --- | --- |
| `speed` | "开启"时使用的倍率。会被热键调整同步更新（`rememberSpeed=true` 时） |
| `speedStep` | 每次调整的步长（默认 0.5；想更细腻可改 0.25） |
| `minSpeed` / `maxSpeed` | 热键可调范围（非法值会退回 SDK 默认 `0.1` / `10`） |
| `toggleKey` | 开关键（Unity `KeyCode` 名字，如 `Delete` / `Insert` / `F8`） |
| `speedUpKey` / `speedDownKey` | 调整键，**需要按住 Alt**（默认 `Equals` / `Minus`，即 `=` / `-`） |
| `useNumpadKeys` | 是否也认小键盘 `+` / `-`（默认 true） |
| `notify` | 是否在屏幕上弹提示 |
| `rememberSpeed` | 是否把调好的倍率写回 `config.json`（默认 true；停手约 1.2 秒后落盘，退出时补写） |
| `repeatInterval` | 按住连调的间隔（秒）；`0` = 按住只调一次 |

热键名字用 Unity 的 `KeyCode` 枚举名（`Equals`、`Minus`、`Alpha1`、`F8`、`PageUp`…），
写错会退回默认键并在日志里警告。

## 与"加载器基准倍率"的关系

变速**不依赖这个 mod**：加载器启动时就按 `doorstop_config.json` 的 `speedhackBaseSpeed`
应用基础倍率（`2.0` = 全程 2 倍速，`1.0` = 正常）。本 mod 的行为是：

- 启动时若引擎**已经是** 2.0x（说明 doorstop 里配了 2.0），就把它当作"已开启 2.0x"，
  按一次 `Delete` 才回到 `1.0x`；
- 启动时若引擎是 `1.0x`，则读取配置里的 `speed` 作为"下次开启用的倍率"，但**不会**主动变速；
- 也就是说：**mod 不覆盖你在 doorstop / Toys 里设的基准倍率**，只接管热键之后的调整。

## ⚠️ 风险

- 变速会改变游戏感知的**所有**时间：动画、演出、回合计时、**网络超时**。
- **联机对局慎用**：服务器是权威时钟，本地时间戳与服务器不一致容易被判定为异常节奏，
  有断线/封号风险。建议只在单机练习、看回放、本地观感加速时使用。
- 倍率越高越容易出问题（>4x 时 Unity 的物理/动画/协程都会变形）；默认上限故意只给到 `10`。
- 与外部变速工具（speedhack-rs 等）**互斥**：它们同样劫持 `version.dll`，Windows 按模块名
  只加载一个。

## 实现说明

| 层 | 做什么 |
| --- | --- |
| 加载器 `version.dll` | 进程启动时 MinHook inline hook `GetTickCount`/`GetTickCount64`/`timeGetTime`/`QueryPerformanceCounter`，按倍率缩放返回值；导出 `ap_speed_set` / `ap_speed_get` / `ap_speed_active` |
| SDK `SpeedHack` | P/Invoke 上面的导出；并提供纯计算 `StepSpeed` / `ClampSpeed`（热键算倍率用，离线可测） |
| 本 mod | 轮询键盘 → 算倍率 → `SpeedHack.SetSpeed()`；写回 `config.json` |

细节：

- 本 mod 只碰加载器导出与 Unity `Input`，**不碰任何游戏单例**，所以启动延迟只等 10 秒
  （其它 mod 默认 30 秒是为了避开启动早期的单例访问崩溃窗口），加载/开场阶段就能变速。
- `SetSpeed` 会显式挡掉 `NaN`：`NaN` 和任何数比较都是 false，只靠 `<=0 / >100` 拦不住，
  而把 `NaN` 喂给引擎会让虚拟时间彻底坏掉。
- 引擎装不上（旧加载器 / hook 失败）时，`Main()` 只写一行警告就退出，不会假装能变速。
