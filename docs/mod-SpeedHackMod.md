# mod-变速 (SpeedHackMod)

> 用热键在**游戏里实时**调整时间流速的内置 mod。变速引擎本身由加载器提供（`version.dll`
> inline hook 系统时间函数），这个 mod 只是把引擎接到键盘上，并记住你调过的倍率。

## 热键

| 热键 | 作用 |
| --- | --- |
| `Delete`（可改） | **在 `1.0x` 和"刚才的倍率"之间来回切**：第一次按回正常速度，再按一次切回刚才的倍率（引擎保持开启，不卸载 hook） |
| `Alt` + `=`（就是 `+`，小键盘 `+` 也行） | **加速**，每次 `speedStep`（默认 0.5）；按住不放会连续加速 |
| `Alt` + `-`（小键盘 `-` 也行） | **减速**，同上 |

- **只读键盘**：不接管鼠标、不做输入独占，也不拦截游戏自己的按键 —— 不会影响正常游玩。
  （把热键设成鼠标键时也只是"读"那个键，同样不独占鼠标。）
- **热键可改**：`toggleKey` / `speedUpKey` / `speedDownKey` 都是配置项，见下面「改键位」。
- **按下即生效**，不用重启游戏；屏幕上会弹当前倍率（如 `变速: 2.5x`），
  加载器日志里对应 `[speedhack] 倍率 -> 2.5`，mod 日志里对应 `倍率 2x -> 2.5x`。
- `Delete` **不是"关掉变速"**：加载器的变速引擎一旦装上 hook 就常驻，`1.0x` 时 hook
  原样返回真实时间（等价于不变速）。两次切换都是普通的倍率写入，和
  `Alt` 调整**走完全相同的代码路径** —— 没有"卸载/重装 hook""切换引擎状态"这类额外动作。
- `Delete` 是**双向开关**：在 `2.0x` 时按一下 → `1.0x`（同时记住 `2.0x`），再按一下 → 回到 `2.0x`，
  如此往复。记住的倍率只在**本次运行内**有效，并且每次"**离开非 `1.0x`**"都会用引擎当时的
  真实倍率覆盖它 —— 所以先用 `Alt` 调到 `2.5x` 再按 `Delete`，能切回的就是 `2.5x`。
  不想要回切（`Delete` 永远只设为 `1.0x`）就把配置里的 `toggleRestoresSpeed` 设成 `false`。
- 按 `Alt` + `+`/`-` 永远从**引擎当前真实倍率**起算：`Delete` 调到 `1.0x` 后按 `Alt`+`=` 得到
  `1.5x`，而不是跳到记忆里的旧倍率。
- 倍率限制在 `minSpeed` ~ `maxSpeed`（默认 `1.0` ~ `10`，`1.0` 是硬下限，调不下去），
  到极限只弹提示、不再变化。
- `Delete` 的两次切换都**不会**写回配置：`1.0x` 只是临时恢复正常，切回的倍率又恰好等于记忆倍率，
  都不该覆盖你习惯的倍率。

## 配置

`mods\SpeedHackMod\config.json`（首次运行自动生成）：

```json
{
  "speed": 2.0,
  "speedStep": 0.5,
  "minSpeed": 1.0,
  "maxSpeed": 10.0,
  "toggleKey": "Delete",
  "toggleRestoresSpeed": true,
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
| `speed` | `Alt` 调整时的**记忆倍率**（`rememberSpeed=true` 时随调整更新；`Delete` 的 `1.0x` 不写回） |
| `speedStep` | 每次调整的步长（默认 0.5；想更细腻可改 0.25） |
| `minSpeed` / `maxSpeed` | 热键可调范围（非法值会退回 SDK 默认 `1.0` / `10`；`minSpeed` **写小于 1.0 也会被抬到 1.0**，不允许减速） |
| `toggleKey` | 倍率开关键（Unity `KeyCode` 名字，如 `Delete` / `Insert` / `F8`）：在 `1.0x` 与刚才的倍率之间切换 |
| `toggleRestoresSpeed` | `toggleKey` 再按一次时是否切回刚才的倍率（默认 `true`）；设 `false` = 老行为，永远只设为 `1.0x` |
| `speedUpKey` / `speedDownKey` | 调整键，**需要按住 Alt**（默认 `Equals` / `Minus`，即 `=` / `-`） |
| `useNumpadKeys` | 是否也认小键盘 `+` / `-`（默认 true） |
| `notify` | 是否在屏幕上弹提示 |
| `rememberSpeed` | 是否把调好的倍率写回 `config.json`（默认 true；停手约 1.2 秒后落盘，退出时补写） |
| `repeatInterval` | 按住连调的间隔（秒）；`0` = 按住只调一次 |

热键名字用 Unity 的 `KeyCode` 枚举名（`Equals`、`Minus`、`Alpha1`、`F8`、`PageUp`…），
写错会退回默认键并在日志里警告。

### 改键位

两种方式，改完都**重启游戏**才生效（配置只在 mod 初始化时读一次）：

1. **在 AstralParty.Toys 里点**（推荐）：`模组 → 变速 → ⚙` 打开配置，字段名以 `Key` 结尾的
   （`toggleKey` / `speedUpKey` / `speedDownKey`）会显示成**按键按钮** —— 点一下按钮，再按下
   想绑的键就捕获好了：
   - 键盘任意键（`A`~`Z`、`F1`~`F12`、小键盘 `+`/`-`、`Delete`、方向键…）；
   - **鼠标左/右/中键与侧键**（侧键在 Unity 里叫 `Mouse3` = 后退键、`Mouse4` = 前进键，
     所以 `toggleKey` 设成 `Mouse3` 就是"按一下鼠标侧键切变速"）；
   - `Esc` 取消本次捕获；点右边的 `×` 清空该字段 = 退回 mod 内置默认键；
   - 认不出来的键（媒体键等）会提示而不写入，不会偷偷存一个 mod 读不懂的名字。
2. **直接改上面的 `config.json`**：值就是 Unity `KeyCode` 名字，规则与第 1 条本一致。

## 与"加载器基准倍率"的关系

变速**不依赖这个 mod**：加载器启动时就按 `doorstop_config.json` 的 `speedhackBaseSpeed`
应用基础倍率（`2.0` = 全程 2 倍速，`1.0` = 正常）。本 mod 的行为是：

- 启动时若引擎**已经是** 2.0x（说明 doorstop 里配了 2.0），mod 就把它当作当前倍率，
  按一次 `Delete` 回到 `1.0x`（再按一次又切回 `2.0x`），之后按 `Alt` + `+`/`-`
  从这个真实倍率继续调；
- 启动时若引擎是 `1.0x`，则读取配置里的 `speed` 作为记忆倍率，但**不会**主动变速；
- 也就是说：**mod 不覆盖你在 doorstop / Toys 里设的基准倍率**，只接管热键之后的调整。

## ⚠️ 注意

- 变速会改变游戏感知的**所有**时间：动画、演出、回合计时、**网络超时**。
- **别把倍率调太高**：倍率越高，动画/物理/协程越容易变形，演出与网络节奏也越难对齐 ——
  实际使用建议不超过 `3.0`；默认上限故意只给到 `10`（>4x 时 Unity 的物理/动画/协程就会明显走形）。
- **不能减速**：`1.0` 是硬下限，低于 1 倍的请求会被加载器直接忽略（`minSpeed` 写更小也没用）。
- 与外部变速工具（speedhack-rs 等）**互斥**：它们同样劫持 `version.dll`，Windows 按模块名
  只加载一个。

## 实现说明

| 层 | 做什么 |
| --- | --- |
| 加载器 `version.dll` | 进程启动时 MinHook inline hook `GetTickCount`/`GetTickCount64`/`timeGetTime`/`QueryPerformanceCounter`，按倍率缩放返回值；监听 `<speed>\request.txt` 应用倍率并回写 `state.txt` |
| SDK `SpeedHack` | 读写上面的控制文件（热更程序集**无法 P/Invoke**，详见 [SDK-变速](SDK-变速.md)）；并提供纯计算 `StepSpeed` / `ClampSpeed`（热键算倍率用，离线可测） |
| 本 mod | 轮询键盘 → 算倍率 → `SpeedHack.SetSpeed()`；写回 `config.json` |

细节：

- 本 mod 只碰加载器控制文件与 Unity `Input`，**不碰任何游戏单例**，所以启动延迟只等 10 秒
  （其它 mod 默认 30 秒是为了避开启动早期的单例访问崩溃窗口），加载/开场阶段就能变速。
- 需要加载器 ≥ 2.1.5（更早的版本没有控制文件通道），且 `speedControlEnabled` 不为 `false`。
- 倍率请求落盘后由加载器在 ~100ms 内应用，所以"按一下"到"手感变化"几乎无感。
- `SetSpeed` 会显式挡掉 `NaN` / `±Inf`：`NaN` 和任何数比较都是 false，只靠 `<=0 / >100` 拦不住，
  而把 `NaN` 喂给引擎会让虚拟时间彻底坏掉。
- 引擎装不上（旧加载器 / hook 失败）时，`Main()` 会打印一行警告并**每秒重试**：
  加载器稍后才就绪时热键会自动生效，期间按键只提示"引擎暂不可用"，绝不假装能变速。
- 引擎的可用性判定与读写路径全部按"热更程序集 BCL 残缺"的假设写：只用最原始的
  `File.Exists` / `File.Open` / 手写 ASCII 解析，不用 `AppDomain`、`File.ReadAllLines`、
  带 `Encoding`/`NumberStyles` 的重载 —— 任一 API 缺失都只记录原因并返回默认值，不会崩游戏。
