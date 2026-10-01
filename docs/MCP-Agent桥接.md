# MCP-Agent 桥接：让 AI agent 接管《吉星派对》对局

> 面向维护者。结论按 `AGENTS.md` §13 分「已确认 / 稳定推断 / 未决」。
> 使用者向的快速上手在 `mcp\README.md`；本文写**怎么做的、为什么这么做、哪里还没验证**。

---

## 1. 目标与边界

**目标**：外部 AI agent 能（1）读到足够做出决策的对局状态，（2）把自己的决策落成游戏**合法的
客户端上传（C2S）动作**，从而在**派对模式**（真实服务器、真人/机器人同局）里接管操作。

**边界（必须写清楚）**：

- 服务器权威。客户端不做任何规则运算，agent 能做的只有"点按钮"，不能改数值、不能伪造结果。
  反编译已确认客户端**没有任何本地服务器实现**（无 LocalServer/Offline 战斗逻辑），
  所以 PVE 打怪房同样是远程服务器权威 —— **不存在"本地随便作弊"的安全区**。
- 因此接管**只应发生在自己有权操作的对局**（自己的账号、自己的房间、练习/单人回放）。
  用第三方工具在服务器权威的**在线对局**里代替人操作，是否违反用户协议由使用者自负，
  本仓库不提供任何绕过服务器的能力。
- 桥接**只读游戏内存/对象 + 发合法 RPC**，不注入、不改游戏文件（除了加载器本身的部署）。

---

## 2. 为什么是文件通道（硬约束推导）

| 约束 | 来源 | 后果 |
|---|---|---|
| 热更程序集不能 P/Invoke | HybridCLR 只为构建期已知程序集生成 managed→native thunk；调 `[DllImport]` 抛 `NotSupportManaged2NativeFunctionMethod` | mod 不能调 kernel32/开管道/共享内存 |
| 热更侧 BCL 被裁剪 | IL2CPP BCL 不全 | 避开 `AppDomain.BaseDirectory`、`Assembly.Location`、部分 `Encoding/CultureInfo` 重载 |
| 加载器↔mod 已有先例 | 变速的 `speed\request.txt` / `state.txt` | 文件通道是这套加载器里**已被验证过**的通信方式 |

→ 桥接 = **环境变量定目录 + 原子写的 JSON 文件 + 追加型 jsonl**。

**目录解析必须两侧一致**：`AgentBridgeLayout.ResolveRoot()` 是唯一出处，
`mcp\Shared\AgentBridgeLayout.cs` 被编进 mod 与 MCP server **两份**（mod 用 `CesiumJson`，
server 用 `System.Text.Json`），所以字段名/文件名/工具名**没有第二处副本**。
（两边 JSON 读取都做大小写不敏感，避免手改文件时炸掉。）

**为什么默认落在 `%LocalAppData%` 而不是游戏目录**：游戏装在 `Program Files (x86)` 下，
不保证普通权限可写；LocalAppData 一定可写，而且外部进程能算出同一个路径。

---

## 3. 进程与线程模型

```
游戏进程                                          外部进程
 AgentBridgeModule(ModBase)                        AstralParty.Mcp.exe (stdio MCP server)
   OnUpdate() 每帧(节流)                             initialize / tools/list / tools/call
     ├ controlPollMs  → 读 control.json                │
     ├ pollIntervalMs → 扫 commands\*.json  ──文件──▶ AgentBridgeClient.SendCommand
     │                   ↓ 主线程执行 CommandRunner        (写 commands\{seq}-{id}.json)
     │                   └ 写 results\{id}.json  ──文件──▶ 轮询 results\ 取回执
     ├ stateIntervalMs → StateProbe.Capture(主线程) → state.json
     └ heartbeatMs    → bridge.json + 清理垃圾
```

- **一切读游戏对象的动作都在 `OnUpdate`（Unity 主线程）里做**：`StateProbe` 会碰
  `GameLogicManager` 的单例与玩家数据，从别的线程读会炸。
- 命令轮询也在 `OnUpdate`，所以 agent 的命令天然在主线程落地。
- 时间统一用 **`DateTime.UtcNow`**（`AgentBridgeLayout.NowMs()`）：变速 hook 会改
  `GetTickCount/GetTickCount64/timeGetTime/QueryPerformanceCounter`，`Stopwatch` 与
  `Environment.TickCount` 是虚拟时间，用它算 TTL/超时会随倍速漂移。

---

## 4. 状态快照（agent 的"眼睛"）

`state.json` 每 250ms 全量原子替换一次，字段（PascalCase，`CesiumJson.Serialize` 直出）：

```
Schema/ModVersion/SdkVersion/UpdatedAtMs/UpdatedAtUtc/StateSeq
Scene/InRoom/InBattle/IsMyTurn/CurrentSn/NotMove
Self{PlayerId,Nick,Gold,HandCount}
Units[]{PlayerId,Nick,Gold,Hp,MaxHp,LandId,IsSelf,IsBot,Alive…}
Hand[]{Id,Guid,Name,Kind,Usable,Price…}
SelfBuffs[]{Id,Name,Stacks,…}
Pending{Kind,Actionable,Sn,Source,SinceMs,DeadlineMs,RemainingMs,Candidates[],Options[],Notes[]}
Counters{CommandsExecuted,CommandsRejected,…}
Control{EnableActions,PauseActions,DryRun,…}
RecentActions[]{AtMs,Tool,Ok,Code,Detail}
```

`Pending` 是核心 —— 见下节。

---

## 5. 待响应窗口（PendingTracker）：整件事最容易错的地方

**问题**：agent 必须知道"服务器现在在等我做什么、sn 是多少、有哪些候选、还有多久超时"。
但游戏客户端**没有**统一的"待响应"字段。

**反编译事实（已确认）**：
- `GameLogicManager.action`（`ActionLogic`）只有 `throwDiceSn`、`CardSN`（+ `UsableCards`、`NotMove`），
  没有覆盖筹码/商店/奖励卡等窗口。
- 各个窗口分别散落在 UI 窗口类里，且**候选内容只在服务器下发的动作里**（客户端零本地生成，
  连"移动目标地块"都是客户端按棋盘拓扑自己算的）。

**做法**：桥接自己维护一个**单窗口状态机**，数据只来自一条信道 ——
`GameEvents.RawAction`（SDK 新增的第 16 个事件），它把 1002 `PredictActionS2C` 里的
**原始动作字节**交给桥接，由 `RawActionObserver` 按 `Action.Id` 解码：

| Action.Id | 解码为 | 填哪个窗口 |
|---|---|---|
| 5021 | `ThrowDiceC2S` | 掷骰（配合 SDK 事件） |
| **5027** | `MoveC2S`：`Direction!=0` → 已移动；`==0` → **正在等我选目标** | Move |
| **5029** | `ShopBuyC2S`（`Cards`=在售, `Gold`=单价, `Alreadys`=售罄, `FreeCardNum`） | Shop(PVP) |
| **5215** | `PVEShopBuyC2S`（+`DisCountGold`/`TalentSkillFreeCard`/`AssistPlayer`/`IsClose`） | Shop(PVE) |
| 5211 | `SelectRelicC2S`（`Relics` 非空才算窗口） | SelectRelic |
| **5249** | `BuyRelicC2S{RelicGold,DivinationGold}` | BuyRelic |
| **5377** | `SelectRewardCardC2S{CardIds,Idx}` | RewardCard |
| 5030 / 5216 / 5250 | 各回执 → 关窗口 | — |

**两条关键设计决定（都是踩过的坑）**：

1. **窗口只能来自原始动作流，不能来自 SDK 事件**。SDK 事件（`OnRelicCandidates` / `OnRewardCardSelected`…）
   **不带 `sn`**。如果拿它们建窗口，`Sn` 会是 0 → agent 发出的请求缺 `sn` → 服务器不认，
   表现为"点了没反应"。所以：`SetWindow` 直接**拒绝 `Sn==0`**，SDK 事件只写流水、不建窗口。
2. **一次只报一个窗口**（`Kind` 是单值）。真人 UI 也不会同时弹两个；多报会让 agent 发错招。
   优先级：我的窗口 → 掷骰 → 卡牌选择（`cardSn>0 && usable.Length>0`）；别人的窗口报 `none` 并在
   `Notes` 里说明"在等其他玩家"。

`RemainingMs` 来自 `GameProbe.RemainingMs(sn)`：反射
`GameLogic.OperationTimer`（在 `MoveC2S` 所在程序集里找类型）拿 `GetOperateTimer(sn).GetTimeRemaining()`，
退路是 `operationTime - downtime`；拿不到就是 `-1`（**不编造 deadline**）。
`Sn` 从 `OperationTimer.timerDict` 的键拿（键就是活跃操作的 sn 集合）。

**这个倒计时为什么重要（已确认）**：客户端超时后会**自动替玩家选**
（第一个候选 / 空手离店 / `Select=0`）。所以 agent 慢一步就等于放弃这一手。

---

## 6. 动作下发（CommandRunner）

只有这一个类会真正改游戏状态。统一流程：

```
命令 → 解析 → 开关闸门(enableActions/pause/dryRun) → 工具分派
     → 窗口类型校验(该工具此刻该不该发) → ResolveSn → 构造 protobuf → net.RPC.<X>.<XCall>(req)
     → GameProbe.CancelOperationTimer(sn) → 回执
```

- **`sn` 解析**：显式 `sn` → 当前窗口的 `sn` → 都没有就 `bad_args`。
  `ActionInfo{Sn, UseTime = OperationTimer.GetExtraTime()}`（与 SDK 内部一致的构造）。
- **窗口校验**：每个工具声明它属于哪种窗口（`shop_buy`→Shop、`select_relic`→SelectRelic、
  `buy_relic`→BuyRelic、`move`→Move、`use_effect_card`/`use_quick_card`/`abandon_card`→CardChoice…），
  类型不匹配直接拒绝。拿不到窗口信息时宁可不发。
- **`sn` 主动清理**：发出后 `CancelOperationTimer(sn)`，让客户端自己的超时逻辑别再触发一次
  （否则可能出现"agent 已经点了、客户端又替我点一次"）。
- 商店分 PVE/PVP 两条消息类；ATM 复用 PVE 售卖消息且 `BuyCards` 为空、`AssistPlayer=目标玩家 id`；
  筹码地块购买用 `Select=2` 表示买、`0` 表示离开（**判别依据是 `Select`，不是 `Exit`**，
  旧文档把这两个弄反过）。

---

## 7. MCP 协议层（手写，零 NuGet）

仓库的零依赖原则：MCP 协议层手写在 `McpServer.cs`（`System.Text.Json` only），
覆盖 `initialize`（协议版本协商 `2025-06-18` / `2025-03-26` / `2024-11-05`）、
`notifications/*`（**一律不回复**）、`tools/list`（含 `annotations{readOnlyHint,destructiveHint,…}`）、
`tools/call`、`resources|prompts` 空列表、`ping`、`logging/setLevel`、`completion/complete`；
未知方法 `-32601`、解析失败 `-32700`、非法请求 `-32600`。
宿主抛异常也回一个 `isError` 的 tool result，不让协议循环崩掉。

`McpServer.HandleLine(line)` 是纯函数式的可测入口（`Run()` 只是读一行写一行），
所以协议行为全部有单测覆盖，不用起真客户端。

---

## 8. 安全设计

| 机制 | 位置 | 作用 |
|---|---|---|
| `EnableActions` | `control.json` → `CommandRunner` 闸门 | 只读模式 |
| `PauseActions` | 同上 | 急停（`astral_emergency_stop`） |
| `DryRun` | 同上 | 只记流水不发 RPC |
| `CommandTtlMs`(10s) | `PollCommands` | 过期命令回 `expired` 并删，避免上一回合的操作污染当前回合 |
| 心跳 | `bridge.json` | agent 一眼看出"游戏没开 / mod 没加载 / mod 卡死" |
| 命令/结果双向删除 | `PollCommands` / `SendCommand` | 目录不会无限长大 |
| 结果保留期 | `Maintain` | `results\` 里没人取的结果会被清理 |
| 原子写 | `AgentBridgeLayout.WriteAtomic` | 读方不会看到写了一半的 JSON |

`maxCommandsPerTick`(默认 8) 限制单帧消费命令数，避免一次灌一堆命令把主线程卡住。

---

## 9. 测试策略（不开游戏就能验绝大部分）

| 层 | 项目 | 覆盖 |
|---|---|---|
| 纯逻辑 | `tests\AstralParty.AgentMod.Tests`（59 个） | 目录/文件协议（原子写、日志尾部按行截断、轮转、`Sanitize` 防穿越、`{seq:D8}` 排序）、`PendingTracker` 逐窗口（含 `Sn==0` 拒绝、别人的窗口、优先级、倒计时、副本语义）、命令解析与回执序列化 |
| 协议 + 集成 | `tests\AstralParty.Mcp.Tests`（46 个） | JSON-RPC 全路径、工具清单与注解、参数校验、开关合并、**真文件往返**（假游戏线程消费 `commands` 写 `results`）、超时清理、事件尾部截取 |
| 真机 | 需要用户配合 | 见下 |

**为什么要"假桥接目录"这种测法**：整条链路的契约就是目录里的文件。测试里真写 `state.json`、
真起一个线程扮演游戏侧消费 `commands\` 并回 `results\`，就能在不装游戏的情况下验证
命名规则、TTL、原子写、回执解析、超时清理是否互相吻合 —— 这是最容易悄悄坏掉的一层。

---

## 10. 真机验证清单（需要游戏在跑）

按顺序做，任何一步不对就停在那一步排查：

1. **加载器在位**：游戏 exe 同目录有 `version.dll` + `AstralParty_ModLoader\`；
   `logs\` 里有 mod 加载记录。
2. **mod 起来了**：`%LocalAppData%\AstralParty_ModLoader\agent\bridge.json` 存在且
   `LastTickMs` 每秒在动；`AstralParty.Mcp.exe --print-config` 显示"心跳: 活着"。
3. **只读状态**：进房间后 `astral_state` 能看到自己、队友、手牌；`astral_pending` 在轮到人操作时
   报出窗口与候选。
4. **演练模式**：`astral_control {"dryRun":true}` → 发一次 `astral_throw_dice`，应回 `dry_run`
   且游戏无反应（验证闸门与流水）。
5. **最小真实操作**：关掉演练，在**自己房间/练习或单人对局**里先做最无害的一步
   （掷骰 → 移动），确认画面真的动了、`astral_actions` 有回执。
6. **窗口类操作逐个验**：筹码三选一 → 奖励卡 → 商店（买/离店）→ 筹码地块买/不买。
   每验一个都去 `docs` 或本文把"未验证"标注改成"已确认"（含日期）。
7. **急停**：`astral_emergency_stop` → 再发动作应回 `paused` 且游戏不动；
   `astral_resume` 恢复。
8. **超时行为**：故意在窗口里不作为，观察客户端是否真的自动选择（这会确认 §5 的"客户端自动替你选"
   这一条与 `RemainingMs` 的量纲）。

---

## 11. 已确认 / 稳定推断 / 未决

**已确认（反编译或实测）**
- 热更程序集不能 P/Invoke；文件+环境变量是唯一可行通道。
- 1002 `PredictActionS2C` 是动作广播信道，`Action.Id` 决定语义；`sn` 来自动作本身。
- 5027 移动、5029/5215 商店、5211 选筹码、5249 买筹码、5377 奖励卡的消息类与字段。
- 回执命令：5030 / 5216 / 5250 / 5212 / 5378。
- `Select=2` 买、`0` 离开（不是 `Exit`）。
- `OperationTimer` 有 `operationTime`/`downtime`/`timerDict`，`GetOperateTimer(sn).GetTimeRemaining()`。
- 客户端在超时后会替玩家自动选择。
- 客户端**没有**本地服务器实现（PVE 也是服务器权威）。

**稳定推断（多份反编译交叉一致，但没实机确认）**
- 移动候选 = `standLand.CanSelectedLandId(fromLandId)`，只有一个候选时客户端自动发送、不显示箭头。
- `RemainingMs` 的量纲是毫秒（`GetTimeRemaining()` 返回秒 ×1000）。
- PVE 商店 ATM 的 `AssistPlayer` = 收款队友玩家 id。
- `ActionLogic.throwDiceSn` / `CardSN` 足以推断掷骰与卡牌窗口。

**未决（不要当成已确认）**
- 事件三选一 `5317 SelectEventC2S`、抽奖/追击/占卜/医院/赌场地块等窗口。
- 战斗内掷骰是否有独立窗口（目前靠 SDK 事件+倒计时推断）。
- `ShopBuyS2C` / `PVEShopBuyS2C.AssistPlayer` 的服务端语义。
- 服务器 1097 超时踢人机制。
- 战役图（`MapType==10`）无倒计时（`RemainingMs=-1`）。
- 刷新（reroll）请求未接管（`SelectRelicC2S.IsReroll=true` 这条路还没做成工具）。

---

## 12. 排障

| 症状 | 先看 |
|---|---|
| `astral_status` 报"桥接未连接" | 游戏没开 / mod 没加载 / 两侧目录不一致（用 `--print-config` 看 server 侧算出的目录，与 `CESIUM_AGENT_DIR` 对比） |
| 心跳"已停止" | mod 崩了或游戏卡死；看 `logs\` 与 `events.jsonl` 末尾 |
| `pending` 一直 `none` 但游戏在等我 | 该窗口类型还没接管（见 §11 未决）；或该动作的 `sn==0` 被主动丢弃（看 `events.jsonl`） |
| 动作回 `rejected`/`paused`/`dry_run` | `control.json` 的开关（`astral_status` 会显示） |
| 动作回 `bad_args` | 参数或窗口不匹配（回执 `Error` 里有具体原因） |
| 动作回 `expired` 或没有回执 | TTL 太短 / mod 没在轮询；命令文件还在 `commands\` 说明 mod 侧没消费 |
| 发了动作游戏没反应 | 看 `actions.jsonl` 是否 `Ok`；`Ok` 但没反应通常是 `sn` 不对（窗口已被别人推进） |
