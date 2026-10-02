# AstralParty MCP —— 让 AI agent 接管《吉星派对》对局

这个目录是 **CesiumLoader 的第三个模块**（另外两个是 `loader\` 与 `mods\`）：把游戏状态暴露成
MCP 工具，并让 agent 的操作合法地落到游戏里。

```
mcp\
├── Shared\AgentBridgeLayout.cs          ← mod 与 server 共用的唯一契约(文件通道布局/字段名/工具名)
├── AstralParty.AgentMod\                ← 游戏内 mod(netstandard2.0, 走 CesiumLoader SDK)
│   ├── AgentBridgeModule.cs             ← ModBase 模块: 节流轮询/写状态/写心跳/清垃圾
│   ├── Bridge\AgentStateModels.cs        ← 状态 DTO(纯字段, CesiumJson 直接序列化)
│   ├── Bridge\StateProbe.cs              ← 主线程读场景/单位/自己/手牌/buff/回合
│   ├── Bridge\GameProbe.cs               ← 反射 OperationTimer / 卡牌窗口 / 移动候选地块
│   ├── Bridge\PendingTracker.cs          ← "现在服务器在等我做什么" 的单窗口状态机
│   ├── Bridge\RawActionObserver.cs       ← 从 1002 的原始动作流解出各种窗口(cmd 5027/5029/5215/5249/5377…)
│   ├── Bridge\CommandRunner.cs           ← 工具 → 游戏 RPC(唯一真正动游戏的地方)
│   ├── Bridge\BridgeSettings.cs          ← mods\AstralParty.AgentMod\config.json
│   ├── Bridge\BridgeJournal.cs           ← events.jsonl / actions.jsonl
│   └── AstralParty.AgentMod.json         ← sidecar(id/版本/权限)
├── AstralParty.Mcp.Core\                ← MCP 协议 + 工具面(net8.0 类库, 零 NuGet 依赖)
│   ├── McpServer.cs                      ← 手写 JSON-RPC 2.0 / stdio(initialize/tools/list/tools/call…)
│   ├── AstralToolHost.cs                 ← 32 个 astral_* 工具(name/title/schema/annotations/dispatch)
│   └── AgentBridgeClient.cs              ← 读写桥接目录 + 命令往返 + 开关控制
└── AstralParty.Mcp\                     ← stdio 可执行入口(AstralParty.Mcp.exe)
```

## 为什么要"文件通道"而不是命名管道

游戏是 **IL2CPP + HybridCLR 热更**：热更程序集里 `[DllImport]` 直接抛
`NotSupportManaged2NativeFunctionMethod`（HybridCLR 只为构建期已知程序集生成 thunk），
所以游戏内 mod **不能** P/Invoke、不能开命名管道、不能共享内存。
另外热更侧 BCL 被裁剪，`AppDomain.CurrentDomain.BaseDirectory` / `Assembly.Location` 也不可靠。

→ 结论：**游戏内 mod ↔ 外部进程只能靠环境变量 + 文件**。这也正好让"接管方"可以是任何语言写的
MCP host（Claude Desktop、自研 agent、脚本），而不用把 agent 塞进游戏进程。

桥接目录（两侧算法必须一致，见 `AgentBridgeLayout.ResolveRoot()`）：

1. 环境变量 `CESIUM_AGENT_DIR`（推荐：MCP server 与游戏都用同一个值，最不容易连不上）
2. `%LocalAppData%\AstralParty_ModLoader\agent`（默认；游戏装在 Program Files 下不可写，LocalAppData 一定可写）

## 文件通道布局

| 文件 | 方向 | 说明 |
|---|---|---|
| `state.json` | mod → agent | 全量状态快照(原子替换)。每 `stateIntervalMs`(默认 250ms) 写一次 |
| `bridge.json` | mod → agent | 心跳: `LastTickMs` / `TickCount` / `StateSeq` / 场景 / 开关计数。每 `heartbeatMs`(默认 1s) |
| `events.jsonl` | mod → agent | 追加型事件流(动作/结算/退房…), 超过 2MB 轮转成 `.1` |
| `actions.jsonl` | mod → agent | 追加型"我发了什么动作 + 服务器回执"流水 |
| `control.json` | agent → mod | 唯一开关来源: `EnableActions` / `PauseActions` / `DryRun` / `Note` / `UpdatedBy` |
| `commands\{seq:D8}-{id}.json` | agent → mod | 命令。零填充 seq ⇒ 按文件名排序 = 按下发顺序 |
| `results\{id}.json` | mod → agent | 回执(成功/失败/拒绝/过期)。读完两侧都删 |
| `commands\*.tmp` | mod 清理 | 写了一半的临时文件, 超过 60s 视为垃圾删掉 |

命令/回执格式：

```jsonc
// commands\00000007-c1727000000000-ab12cd34.json   ← 键名 = AgentBridgeLayout.Field.*(小写)
{ "schema":1, "id":"c1727000000000-ab12cd34", "seq":7, "tool":"throw_dice",
  "issuedAtMs":63900000000000, "args":{"noOper":true} }

// results\c1727000000000-ab12cd34.json           ← 键名 = CesiumJson 的公共字段(PascalCase)
{ "Schema":1, "Id":"c1727000000000-ab12cd34", "Tool":"throw_dice", "Ok":true,
  "Code":"ok", "Detail":"已发送 掷骰", "ExecutedAtMs":63900000000104, "LatencyMs":104 }
```

> 两侧读对方写的文件都**忽略大小写**（`AgentBridgeClient` 与 `CesiumJson` 的字典查找都是
> `OrdinalIgnoreCase`），所以命令用小写、回执用 PascalCase 是历史选择而非契约要求；
> 时间戳纪元见文末「时间戳纪元」。

错误码 `Code`：`ok` / `bad_args` / `unknown_tool` / `rejected`(动作总开关关) / `paused`(急停) /
`dry_run`(演练) / `expired`(超过 `commandTtlMs`) / `exception`。

**状态与操作的分工**：所有读游戏对象的操作都必须在主线程（Unity 主线程）跑，`OnUpdate` 里做；
命令轮询也在 `OnUpdate` 里，所以 agent 的命令天然在主线程执行，不会踩 Unity 的线程检查。

## 构建

```powershell
# 游戏内 mod(需要仓库同级的 extracted_dlls\ 里有游戏 DLL)
dotnet build mcp\AstralParty.AgentMod\AstralParty.AgentMod.csproj -c Release

# MCP server
dotnet build mcp\AstralParty.Mcp\AstralParty.Mcp.csproj -c Release
```

## 部署

1. 先装好 CesiumLoader 本体的加载器（`version.dll` + `AstralParty_ModLoader\`）。
2. 把 mod 放进 `AstralParty_ModLoader\mods\AstralParty.AgentMod\`：
   `AstralParty.AgentMod.dll` + `AstralParty.AgentMod.json`。
3. 起游戏（mod 会自己建桥接目录并开始写 `state.json` / `bridge.json`）。
4. 起 MCP server，或用一条命令直接排障：

```powershell
# 看清楚它连的是哪个目录、桥接是否活着、有哪些工具
.\mcp\AstralParty.Mcp\bin\Release\net8.0\AstralParty.Mcp.exe --print-config
```

5. 在 MCP 客户端里配置（Claude Desktop / 任意 MCP host）：

```json
{ "mcpServers": { "astral-party": { "command": "C:\\...\\AstralParty.Mcp.exe" } } }
```

> `AstralParty.Mcp.exe` 是**标准输入输出协议进程**：stdout 只有 JSON-RPC，日志一律走 stderr。

一键部署脚本：`tools\deploy-agentmod.ps1`（构建 → 拷贝到游戏目录 → 打印 MCP 客户端配置片段）。

## 工具一览（32 个）

读（只读，不改游戏）：

| 工具 | 说明 |
|---|---|
| `astral_status` | 桥接是否活着 + 场景/房间/开关 + 此刻待响应的窗口(先看这个) |
| `astral_state` | 完整状态快照 JSON |
| `astral_pending` | 只取"待响应窗口"，含候选(带价格/售罄/免费)、剩余时间、可用的操作列表 |
| `astral_events` | 最近 N 条事件 |
| `astral_actions` | 最近 N 条自己发过的动作 + 回执 |

写（会动游戏，全部走服务器合法 C2S）：

| 工具 | 关键参数 | 对应协议 |
|---|---|---|
| `astral_throw_dice` | `battle` / `noOper` / `moveNow` / `sn` | 掷骰动作 |
| `astral_move` | `landId`(取 `pending` 候选) | `MoveC2S`(5027) |
| `astral_use_card` | `cardId` 或 `cardGuid`(手牌 Guid!)/`pass` | `BattleUseCardC2S`(5035) |
| `astral_ask_battle` | `accept` | `AskBattleC2S`(5047) |
| `astral_battle_choice` | `dodge` | `BattleChoiceC2S`(5039) |
| `astral_use_effect_card` | `cardId`,`targetIds`,`landIds`,`effectIndex` | 效果牌 |
| `astral_use_quick_card` | `cardId`,`targetId` | 快速牌 |
| `astral_abandon_card` | `cardIds` | 弃牌 |
| `astral_select_relic` | `index` 或 `relicId` | `SelectRelicC2S`(5211) |
| `astral_select_reward_card` | `index` | `SelectRewardCardC2S`(5377) |
| `astral_select_event` | `index` | `SelectEventC2S`(5317) |
| `astral_shop_buy` | `indexes`(槽位下标; 不传=离店) | `ShopBuyC2S`(5029, PVP) / `PVEShopBuyC2S`(5215, PVE) |
| `astral_atm_transfer` | `targetId` | `PVEShopBuyC2S{AssistPlayer=targetId}` |
| `astral_buy_relic` | `confirm` | `BuyRelicC2S`(5249, `Select=2` 买 / `0` 离开) |
| `astral_stop_or_continue` | `stop` | `StopOrContinueC2S`(5077) |
| `astral_pursue_monster` | `monsterId`(取候选 `LongId`) / `pass` | `MonsterPursuitC2S`(5213) |
| `astral_vendor_buy_card` | `buy` | `VendorBuyCardC2S`(5323) |
| `astral_select_point` | `point`(1..`pending.MaxPoint`) | `ThrowDiceResultC2S`(5067) |
| `astral_revive_teammate` | `revive` | `AskReviveTeammateC2S`(5233) |
| `astral_select_mechanism` | `select` | `SelectMechanismC2S`(5259) |
| `astral_hospital_check` | 无(该窗口只有一个合法上行) | `TriggerHospitalC2S`(5093) |
| `astral_battery_pick` | `targetIds`(候选 `LongId`, 1..`pending.TargetNum` 个) / `leave` | `LandChoiceTargetC2S`(5063) |
| `astral_divination_pick` | `index`(0/1) 或 `divinationId` | `TriggerDivinationC2S`(5069) |
| `astral_speed` | `speed` | 变速(1.0–100.0) |
| `astral_control` | `enableActions`/`dryRun`/`pause`/`reason` | 写 `control.json` |
| `astral_emergency_stop` | `reason` | 急停 |
| `astral_resume` | — | 解除急停 |

## 安全开关

| 开关 | 默认 | 作用 |
|---|---|---|
| `enableActions` | `true` | `false` = 只读模式，任何写操作都被 `rejected` |
| `pauseActions` | `false` | 急停；`astral_emergency_stop` 置位，`astral_resume` 清除 |
| `dryRun` | `false` | 只记流水、不真发 RPC |
| `commandTtlMs` | `10000` | 超过 TTL 的命令回 `expired` 并丢弃（避免过期操作打乱当前回合） |

再加两条硬保护：

- **窗口校验**：写操作只有在"当前确实在等这个操作"时才发（如 `shop_buy` 需要商店窗口）。
  拿不准就回 `bad_args` + 说明，不会瞎点。
- **倒计时意识**：`pending.RemainingMs` 是服务器超时倒计时；客户端在超时后会**自动替你选**
  （第一个候选 / 空手离店 / `Select=0`），所以 agent 必须及时出手。

## 已知缺口（诚实清单）

- 抽奖/占卜/医院/赌场/命运/电池/再走一次等地块窗口尚未接管（PVP 商店 5029 按"只做 PVE"的范围**不做**）。
  已接管的地块窗口：加油站/出生点 `5077`、怪物追击 `5213`、商人买卡 `5323`、控移卡选点 `5067`。
- 战斗内掷骰靠 SDK 事件 + 倒计时推断，没有独立窗口类型。
- `ShopBuyS2C` / `PVEShopBuyS2C.AssistPlayer` 的服务端语义未验证（只按客户端反编译结论用）。
- 服务器 1097 超时踢人未验证。
- 战役图（`MapType==10`）没有倒计时，`RemainingMs` 会是 `-1`。

详见 `docs\MCP-Agent桥接.md`。

## 测试

```powershell
dotnet test tests\AstralParty.AgentMod.Tests\AstralParty.AgentMod.Tests.csproj -c Release   # 121 个
dotnet test tests\AstralParty.Mcp.Tests\AstralParty.Mcp.Tests.csproj -c Release             # 67 个
```

不需要开游戏：`AstralParty.Mcp.Tests` 用假桥接目录（真写 `state.json`、扮演游戏侧消费 `commands` 并回
`results`）覆盖了整条命令往返链路；`AstralParty.AgentMod.Tests` 直接编译桥接里的纯逻辑
（布局/状态机/命令解析/control.json 读取）离线跑。

端到端冒烟（拿**真正发布出来的 server exe** + 一个临时桥接目录扮演游戏，不需要游戏/加载器）：

```powershell
pwsh -NoProfile -File tools\smoke-agent-bridge.ps1        # 成功时最后一行是 [smoke] === 全部通过 ===
pwsh -NoProfile -File tools\smoke-agent-bridge.ps1 -Keep  # 失败时保留现场目录排查
```

它覆盖 65 项断言：MCP 握手与工具清单、state/bridge/control 的字段名与大小写、`commands\*.json` 的
往返与两侧清理、`Kind=None` 的判定、以及**心跳过期时动作工具必须拒绝下发**（且不留垃圾命令文件）。

### 时间戳纪元（容易踩）

`state.json` / `bridge.json` / `commands\*.json` 里的所有绝对时间戳
（`LastTickMs` / `SinceMs` / `issuedAtMs` / `ExecutedAtMs` / `UpdatedAtMs` …）都来自
`AgentBridgeLayout.NowMs()`，它等于 **`DateTime.UtcNow.Ticks / 10000`（自 0001-01-01 起的毫秒）**，
**不是 Unix 毫秒**（今天前者约 `6.39e13`，后者约 `1.79e12`，差 3 万多倍）。
两者混用会让心跳年龄算出天文数字、被判成"没有心跳"（离线冒烟脚本第一版就这么翻过车）。
自己写夹具或外部解析时请照抄 `NowMs()` 的算法；纯相对量（`RemainingMs` / `LatencyMs`）不受影响。
