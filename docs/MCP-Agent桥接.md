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
  ⚠ `NowMs()` = `DateTime.UtcNow.Ticks / 10000`，纪元是 **0001-01-01**，**不是 Unix 毫秒**
  （今天前者约 `6.39e13`、后者 `1.79e12`）。所有绝对时间戳字段都用这个纪元；混用会算出天文数字的
  年龄，把活着的桥接判成"没有心跳"。相对量（`RemainingMs` / `LatencyMs`）不受影响。

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
Pending{Kind,Actionable,Sn,Source,SinceMs,DeadlineMs,RemainingMs,
        AskPlayerId,NoDodge,ResidueCost,IsAttacker,Candidates[],Options[],Notes[]}
Counters{CommandsExecuted,CommandsRejected,…}
Control{EnableActions,PauseActions,DryRun,…}
RecentActions[]{AtMs,Tool,Ok,Code,Detail}
```

`Pending` 是核心 —— 见下节。

> **字段名 PascalCase，但 `Kind` 的取值是小写 camelCase 字符串常量**（`AgentPendingKind`:
> `none` / `throwDice` / `battleDice` / `selectRelic` / `rewardCard` / `shop` / `move` /
> `cardChoice` / `buyRelic` / `selectEvent` / `askFight` / `fightCard` / `fightChoice`；
> 候选的 `Kind` 是 `card` / `land` / `event`）。桥接状态模型里**没有枚举**，
> 这些就是 2026-10-01 真机 `state.json` 的实测值（无窗口时是 `"Kind": "none"`）。
> MCP server 侧读 `Kind` 一律**忽略大小写**（`IsNoPending`）——那只是防御，
> 不代表契约允许两种写法，写文档/夹具请用小写那一套。

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
| **5317** | `SelectEventC2S{Events,Idx}`（`Events` 非空才算窗口） | SelectEvent |
| **5037** | 不解负载（客户端也不解）：窗口归属 `PlayerId`、`sn`=自带 `Sn` | BattleDice |
| **5047** | `AskBattleC2S{AskPlayerId,FightBack,…}`；**`FightBack=true` 不开窗**（客户端自己立刻接受） | AskFight |
| **5035** | 不解负载（客户端也不解，实测 537/537 条 `Data` 长度为 0）：`sn`=自带 `Sn` | FightCard |
| **5039** | `BattleChoiceC2S{NoDodge,…}`（只用得上 `NoDodge`，其余字段是服务器填的） | FightChoice |
| **5077** | **零负载**（实测 63/63 条 `DataLen=0`）：`sn`=自带 `Sn` | StopOrContinue |
| **5213** | **零负载**（实测 10/10 条长度为 0）：`sn`=自带 `Sn`；候选是客户端本地算的 | PursueMonster |
| **5323** | `VendorBuyCardC2S`：`Info.Sn==0` → offer（读 `CardId`/`Gold`）；`Info.Sn!=0` → 某人的答案 | VendorCard |
| **5067** | `ThrowDiceResultC2S`：`Info.Sn==0` → offer（读 `MaxPoint`）；`Info.Sn!=0` → 某人的答案 | SelectPoint |
| 5030 / 5216 / 5250 | 各回执 → 关窗口 | — |
| 5212 | `SelectRelicS2C`（`IsReroll` 时 SDK 事件被过滤） | — |
| **5318** | `SelectEventS2C{PlayerId,EventId}` → 关窗口 | — |
| **5038** | `BattleThrowDiceS2C{PlayerId,Val}` → 关窗口 | — |
| **5048** | `AskBattleS2C{PlayerId,IsBattle}` → 关窗口 | — |
| **5036** | `BattleUseCardS2C{PlayerId,CardId}`（`CardId==0` = 这次没出牌）→ 关窗口 | — |
| **5040** | `BattleChoiceS2C{PlayerId,Val,Dodge}` → 关窗口 | — |
| **5078** | `StopOrContinueS2C{PlayerId,Stop}` → 关窗口 | — |
| **5214** | `MonsterPursuitS2C{PlayerId,NodeId,FrontIds,BackId,Exit}` → 关窗口 | — |
| **5324** | `VendorBuyCardS2C{PlayerId,IsBuy}` → 关窗口 | — |
| **5068** | `ThrowDiceResultS2C{PlayerId,MaxPoint,Point}` → 关窗口 | — |

**战斗三件套（5047 / 5035 / 5039，PVE 里出现频率最高的窗口）**的依据（反编译 + 3 局真实回放实测）：

- 三者**都只在 `IsSelf(action.PlayerId)` 时才注册操作倒计时**；别人的窗口照常广播，桥接必须报 `none`
  （4 人局会有多人同时各自持窗）。`PendingTracker.SetSelf(playerId)` + `SetWindow` 里的
  "我的窗口不许被别人的顶掉"就是为此。
- **超时代答方向**（决定 agent 能不能拖）：5047 → `IsBattle=false`（不打）、5035 → `CardUid=0`（不出牌）、
  5039 → `Dodge=false`（不闪避）。倒计时 = `ChoosingTimeLimit[TimePlan].OtherTimeLimit`，**10 / 20 / 40 秒**
  （`Info.UseTime = GetExtraTime()` 恒 0）；超时后会累计 **AFK 惩罚**，别拖。
- **5047 `FightBack=true` 必须不发也不开窗**：那是"反击"的第二次询问，客户端对**自己**这条动作
  立刻自动回 `IsBattle=true`（`FightLogic.AskFight` 里 `if (FightBack) { RequestAskBattleC2S(sn,true); return; }`），
  真人没有操作机会。桥接抢答会和客户端撞同一条 sn。
- **5035 的候选是客户端本地算的**（服务器不发候选列表）：取我方手牌里 `Config.EffectType` 与
  "我方角色"匹配的牌 —— 我是攻击方 → `Attack`，防守方 → `Defense`；每张牌的消耗走
  `cardActions[CardId].GetCostValue(self, handCard)`（默认 `BattleCost ?? Config.Cost`），
  超过 `fightData.attackerInfo.Cost` / `defenderInfo.Cost` 的牌客户端会置灰。
  桥接复刻了这一口径（`GameProbe.TrySelfFightCards`），把 `ResidueCost` + 每张牌的 `Cost` 一并报给 agent。
- **5035 出牌发的 `CardUid` 是手牌 Guid，不是卡牌配置 CardId**（实测：1 局样本里 57 条非零 5036 的
  `CardId` 全部命中某玩家手牌的 `UniqueId`，0 次等于配置 id；配置 id 是 5 位数，线上值域 1..42）。
  这是**早期实现的一个真 bug**（`astral_use_card` 曾把 `CardId` 填进 `CardUid`），现已修正；
  `astral_use_card` 的 `cardId` 参数就是"候选里的 id = 手牌 Guid"，另支持 `{"pass":true}` 表示不出牌。
- **5039 `NoDodge=true` 时客户端直接拒绝闪避**（只弹提示 10003、不发包），所以 `CommandRunner`
  也挡下 `dodge=true`，免得 agent 以为闪了其实什么都没发生。
- `GMConfig.dev_AttackerPoint` / `dev_DefenderPoint` 实测**都是 0**（没有任何配置来源，只有 GM 调试窗口写它），
  所以 `BattleThrowDice`/`BattleChoice` 按客户端原样带 0 即可。
- 教学/战役图（`roomInfo.IsCampaign() || MapType == 10`）**不注册倒计时**，且走
  `GameLogicManager.tutorial.Request*` 的本地假回包路径；在这种图上用 `fight.Request*` 无意义。

**地块四件套（5077 加油站/出生点 / 5213 怪物追击 / 5323 商人买卡 / 5067 控移选点）**的依据
（反编译 `AstralParty.Runtime.dll` + 3 局真实回放实测，逐条证据见 `decomp\四窗口契约.md`）：

- **5077 加油站/出生点**：`StopOrContinueC2S{Info,Stop}`（field2 `Stop`，`true`=停留、`false`=继续走）。
  offer **零负载**（63 条实测全为 0），客户端从不读 `Data`；超时回调点的是 `btn_Continue` →
  **不答 = 继续走**。窗口只在我身上（`IsSelf(action.PlayerId)`），信息全在本地（站在哪种地块、
  星币/等级），所以桥接把 `GameProbe.SelfStandLand()` 读出来的地块名（`born`/`fillingStation`/`other`）
  一并报给 agent。
- **5213 怪物追击**：`MonsterPursuitC2S{Info,SelectId}`（`SelectId` = 候选怪物自己的 `playerId`，
  `0` = 不追）；offer **零负载**（10 条实测全为 0）。**候选不在协议里** —— 客户端走
  `LandLogic.GetVailPursuitMonster()` 本地过滤：`characterType==Monster && !Property.NotSelect && HP>0 &&
  CharacterInst!=null && standLand.LandType!=Hospital && TeamId != 自己`。桥接复刻了同一口径
  （`GameProbe.TrySelfPursuitMonsters`），并且**只接受候选里的 id**（不在候选里的请求服务器不会受理，
  agent 却会以为追成了）。超时 → `SelectId=0`（不追）。候选 id 是 64 位 `playerId`，
  走 `AgentCandidate.LongId` 传给 agent（`Id` 仍是 `int`，只作展示）。
- **5323 商人买卡**：offer 与答案**是同一个消息类** `VendorBuyCardC2S` —— offer 只有 `CardId`（sfixed32）
  与 `Gold`（价格），**没有 `Info`**；答案是 `{Info, IsBuy}`，**不回填 `CardId`/`Gold`**。
  所以判别只能靠 `Info.Sn`：`0` → offer，非 0 → 某人的答案。超时 → `IsBuy=false`（不买）；
  星币不足时客户端**只弹提示、不发包**，所以 `CommandRunner` 也挡下 `buy=true`。
- **5067 控移卡选点**：offer = `ThrowDiceResultC2S{MaxPoint}`（没有 `Info`、没有 `Point`），
  答案 = `{Info, Point}`，`Point ∈ 1..MaxPoint`；同一条消息，同样用 `Info.Sn==0` 判别。
  `ActionListener` 只对 `IsSelf` 开窗口；超时 → `Point=1`。
- 四者**都用 `Info.Sn` 回传窗口的 sn**、回执一律 `id+1`；教学/战役图上同样
  `ActionDownTime` 返回 null（没有倒计时、没有自动代答）。

**地块应答三件套（5233 复活队友 / 5259 机制选择 / 5093 医院）**的依据（`decomp\_full` 全量反编译
`AstralParty.Runtime.dll` + `decomp\LandLogic.cs` / `LandFillingStationWindow.cs` / `LandHospitalWindow.cs`）：

判"这个窗口要不要玩家点"的**唯一可靠判据是 `OperationTimer.ActionDownTime(sn, <id>, onComplete)` 的调用点**：
有它 = 客户端弹窗等玩家操作，而 `onComplete` 就是"服务器倒计时结束时替你按的那个按钮"。
全量反编译里共 **28 处**调用点（另有 1 处是 `OperationTimer.ActionDownTime` 自身的定义），
覆盖了全部真人输入窗口；这三条都在其中 —— 5093 在 `LandHospitalWindow.InitHospital`，
5233/5259 分别在 `LandFillingStationWindow.ShowAskReviveTeammate` / `ShowSelectMechanism`。

- **5233 复活队友**：`LandLogic.DealAskReviveTeammate` → 只有 `IsSelf` 才弹
  `landFillingStation.ShowAskReviveTeammate(action)`，窗口**只读 `action.Sn`**（offer 零负载）。
  应答 `AskReviveTeammateC2S{Info, IsRevive}`：客户端只填 `Info` 与 `IsRevive`，
  `AskPlayerId`/`Gold` 一律留默认 `0`（救谁由服务器决定）。按钮映射是 `btn_Stop` → `IsRevive=true`、
  `btn_Continue` → `IsRevive=false`；倒计时 `onComplete` 点 `btn_Continue` → **不答 = 不复活**。
  回执 5234 `AskReviveTeammateS2C{PlayerId, AskPlayerId, IsRevive}`。
- **5259 机制选择**：`LandLogic.DealAskSelectMechanism` → `landFillingStation.ShowSelectMechanism(action)`，
  同样只读 `action.Sn`。应答 `SelectMechanismC2S{Info, Select}`；`btn_Stop`("启动") → `Select=true`、
  `btn_Continue` → `Select=false`；`onComplete` 点 `btn_Continue` → **不答 = 不启动**。
  回执 5260 `SelectMechanismS2C{PlayerId, Select}`。
- **5093 医院**：`UI.LandHospitalWindow.DealLand_TriggerHospital` → 只有 `IsSelf` 才弹窗，只读 `_action.Sn`。
  这条窗口**没有"拒绝"这个语义**：`btn_check` → `RequestTriggerHospitalC2S(sn)` →
  `TriggerHospitalC2S`（该消息**只有 `Info` 一个字段**）；`btn_noSick` 只切本地视图、**不发包**，
  而倒计时 `onComplete` 点的也是 `btn_check`。所以工具 `astral_hospital_check` **不带选项**，
  而且"不答"与"答"在服务器看来是同一件事（是否住院由服务器在回执里决定）。
  回执 5094 `TriggerHospitalS2C{PlayerId, InHospital}`。
- 三个回执号都已核实：`RPCMsgManager.DealServerCallback` 里分别是 `cmdID == 5234` / `5260` / `5094`
  （即 `Action.Id + 1`，与前述四件套同一规律）。

> 另外三条动作**没有任何上行**，所以**不需要工具**：**5043 再走一次**
> （`DealMoveAgain` 在 `IsSelf` 时直接发 `MoveAgainC2S`）、**5059 炸弹骰**
> （`DealBombThrowDice` 在 `IsSelf` 时直接发 `BombThrowDiceC2S`）、**5313 剧情**
> （`StoryLogic.OpenStoryByServer` 在剧情播放完时自动回 `NotifyStoryC2S`）。
> 这三条都**不在** `ActionDownTime` 调用点里 —— 依据是判据，不是"看着像不用点"。

**5063 炮台选目标**（`decomp\UI\LandBatteryWindow.cs` / `LandLogic.cs`；`ActionListener` case 5063 只走
`landBattery.DealLand_LandChoiceTarget`，且**只在 `LandType==11`** 时刷新数据）：

- 这条和 5323/5067 一样，**offer 与答案是同一个消息类** `LandChoiceTargetC2S` —— offer 带
  `LandType(11)`/`TargetNum`/`CanTargetIds`，答案是 `Info` + `TargetIds`（或 `Exit=true`），
  所以判别同样靠 `Info.Sn==0`。桥接在 `Tool.BatteryPick`（`astral_battery_pick`）里接它。
- 候选英雄由客户端本地过滤：遍历 `battle.PlayerDatas`，只收 `characterType==Hero && CanTargetIds[id]==true`
  （**不是**直接拿 `CanTargetIds` 的键），所以桥接复刻同一口径（`GameProbe.TryBatteryTargets`），
  且**只接受候选里的 playerId** —— 否则 agent 会以为选上了服务器不认的目标。
- 选择数量是 **1..TargetNum**：客户端只在 `targetPlayerBtns.Count > 0` 时点亮"确定"键，**不要求选满**，
  超过上限时它会把最早选的换掉；桥接因此允许 1..TargetNum 并拒绝超选。
- 上行：`btn_SureTarget` → `RequestLandChoiceTargetC2S(sn, targetIds)`（`{Info, TargetIds}`）、
  `btn_Leave` → `RequestBatteryLeave(sn, exit:true)`（`{Info, Exit=true}`）；倒计时 `onComplete`
  点的是 `btn_Leave` → **不答 = 离开**。回执 5064 `LandChoiceTargetS2C{PlayerId, TargetIds, Exit}`。

**5069 占卜**（`decomp\UI\LandDivinationWindow.cs` / `LandLogic.RequestTriggerDivinationC2S`；
`ActionListener` case 5069 → `landDivination.DealLand_Divination`，回执 cmdID 5070）：

- offer 与答案**又是同一个消息类** `TriggerDivinationC2S`：offer 带 `CanChoiceIds`（**恰好两张**，
  客户端直接取 `_divinationIds[0]`/`[1]`），答案是 `Info` + `Id` —— 同样靠 `Info.Sn==0` 判别。
- 只有本人能点（`IsSelf` 才 `HideUI.selectedIndex=1` 并注册倒计时；其他人只看到"思考中"）。
- 上行 `RequestTriggerDivinationC2S(sn, divinationId)`（`Id` = 选中的占卜卡 id = `btn.data`）；
  倒计时 `onComplete` 点的是 `btn_Divination_1` → **不答 = 选第 1 张**（`CanChoiceIds[0]`）。
- 回执 5070 `TriggerDivinationS2C{PlayerId, Id, TargetType, TargetIds}` → 翻牌并关窗。
  桥接工具 `astral_divination_pick`（`index` 0/1 或 `divinationId`），不传参数按 `index=0`（与超时一致）。
  候选显示名走 SDK 新增的 `Names.Divination(id)`（读 `StaticConfigure.Divination.InfoDict`）。

**5081 赌场押注 / 5083 赌场掷骰**（`decomp\UI\LandGambleWindow.cs` / `LandLogic.RequestStartGambleC2S` /
`RequestGambleThrowDicC2S`；回执 5082 `StartGambleS2C`（**没有任何字段**）/ 5084 `GambleThrowDicS2C{PlayerId,Point}`；
状态 **1022 `GambleChangeS2C{Hall}`**）：

- 这两个窗口**没有"id+1 回执即关窗"那种干净信号** —— 真正决定"按钮还在不在"的是 `Hall.S` 与我的
  `GambleRole.GuessCode`/`Point`，而 `Hall` 是通过 **1022 `GambleChangeS2C`** 推来的
  （`LandLogic.OnGambleChangeS2CServerCallBack` → `RefreshGambleData(model.Hall)`）。
  所以桥接除了 5082/5084 之外**必须处理 1022**：由观察者按 `Hall` 算出"押注窗口还开不开、掷骰窗口还开不开"
  传给状态机，**只用来关窗**（窗口的"开"只能由 5081/5083 的 offer 触发，否则会给 agent 一个没有 sn 的窗口）。
- **5081 押注**：`DealLand_Gamble` 第一件事就是 `GetSelfPlayerData().player.Id != action.PlayerId → return`
  （**只有本人**），offer = `StartGambleC2S{Hall, IsExec}`（`IsExec` = 我能不能参与 = 客户端的 `_enableJoinGamble`），
  答案 = `{Info, IsExec, GuessCode}` —— 又是"offer 与答案同一个消息类"，靠 `Info.Sn` 区分。
  按钮：`btn_odd` → `GuessCode=1`、`btn_even` → `GuessCode=2`（两者都把 `_enableJoinGamble` 原样回传）；
  倒计时点的是 `btn_odd` → **不答 = 押奇数**。
  `IsDie || GoldLack` 时客户端把两个按钮 `grayed/touchable=false`（真人点不动，但倒计时 `.Call()` 绕过它）——
  桥接按"真人能不能点"设闸：记 `canAct=false` 并**拒答**（不答时客户端自己的超时仍会押奇数，对局不会卡住）。
  另外 `!_enableJoinGamble` 时 `OnClick*` 直接 return，所以 offer 的 `IsExec=false` 时桥接**根本不开窗**。
- **5083 掷骰**：`DealLand_GambleDice` **完全不读 `action.Data`**，状态一律来自 `room.curRoomInfo.Hall`：
  "我能不能参与"= 我的 playerId 在 `Hall.Roles` 里，"按钮能不能点"= 我的 role 的 `IsDie`/`GoldLack`。
  所以桥接用 `GameProbe.TrySelfGambleHall` 复刻这段（读不到 `Hall` 就不开窗 —— 此时客户端也不会发上行）。
  上行只有 `RequestGambleThrowDicC2S(sn)` → `GambleThrowDicC2S{Info, DevPoint = GMConfig.dev_GamblePoint}`，
  **没有任何可选参数**；倒计时点的也是 `btn_Dice` → "不答"与"答"在服务器看来一样（同 5093 医院）。
- 工具：`astral_gamble_guess`（`guessCode` 1/2 或 `guess` `"odd"`/`"even"`，**必须明确选** —— 要花星币，
  所以不像选点那样给默认值）与 `astral_gamble_dice`（无参数）。两者在 `canAct=false` 时都拒答。

**5041 抽奖选号**（`decomp\UI\LandLotteryWindow.cs` / `LandLogic.RequsetLotteryChoiceC2S`（原方法名就是这个拼写）；
回执 5042 `LotteryChoiceS2C`）：

- `DealLand_Lottery` **只给本人**（其他人只弹"思考中"并 return）。offer = `LotteryChoiceC2S{Num}`（`Num` = 这次能选几个），
  答案是同一个消息类的 `{Info, Vals}`（`Vals` = 选中的号码）—— 同样靠 `Info.Sn` 区分。
- 候选**不在协议里**：号码范围是 `StaticGlobalData.GAME_LAND_LOTTERY_NUMB_LIMIT`（该类型在**全局命名空间**），
  还要刨掉自己已经占了的 `player.Hero.Lotterys`（值为 true 的号码，客户端会把它们置为不可选）。
  所以桥接用 `GameProbe.TrySelfLottery` 复刻这段，只把**剩下的**号码作为候选给 agent。
- 两条路径的语义不同，都要复刻：**手点确定**时客户端要求选中个数**正好等于** `Num`（否则只弹提示、不发包）；
  **超时**回调 `OnCompleteSelectLottery` 则从**最小的可用号码**开始补满 `Num` 个 → **不答 = 最小的那几个**。
  桥接因此：给了 `numbers` 就校验"都是候选 + 不重复 + 个数 == min(Num, 候选数)"；不给就按超时口径补。
- 工具 `astral_lottery_pick {"numbers":[n1,n2,...]}`（可省）；回执 5042 关窗。

**5033 追击地块（追敌方英雄）** —— 与 **5213 怪物追击是两个不同的窗口/消息类**（5213 = `MonsterPursuitC2S` 追怪，
5033 = `PursuitC2S` 追人），依据是 `decomp\UI\LandPursuitWindow.cs` / `LandLogic.RequsetPursuitC2S`（原拼写如此）；
回执 5034 `PursuitS2C{PlayerId, NodeId, FrontIds, BackId, Exit}`：

- `DealLand_Pursuit` **只给本人开窗**（其他人只弹"思考中"），而且**从不解码 `action.Data`** ——
  窗口只用 `action.Sn`，所以 offer 基本是**零负载**（走观察者的空负载分支）；带负载的 5033 用 `Info.Sn` 区分
  "某人的答案被回播"还是"服务器这次真带了 Data"。
- 候选**不在协议里**：`LandPursuitWindow.InitAvailablePlayer` 本地过滤
  `characterType==Hero && 不是我 && 不同队 && !Property.NotSelect`（客户端再把列表补齐到 3 行，空位填 `-1`），
  并且把"血量=0 或在医院地块"的行**置灰**（确定键点不亮）。桥接按**能追的**那一档给候选
  （`GameProbe.TrySelfPursuitPlayers`，与 5213 的候选人过滤同一口径），客户端补的 `-1` 占位不进候选。
- 上行 `RequsetPursuitC2S(sn, playerId)` → `PursuitC2S{Info, SelectPlayerId}`，`SelectPlayerId=0` = 不追；
  `btn_Stay` 发的就是 `0`。倒计时回调点的是 `ClosePursuit`（= 发 `0`）→ **不答 = 不追/停留**。
- 工具 `astral_pursue_player {"playerId":N}` / `{"stay":true}`；桥接只接受候选里的 playerId。

**5309 助力投票**（`decomp\GameLogic\AssistVoteLogic.cs` + `UI\AssistVoteWindow.cs`（地图 82013）/
`UI\AssistVoteS7Window.cs`（地图 82015，**三路**）；回执 5310 `VoteS2C`、选路广播 5312 `VoteSelectS2C`、
结束 **1093 `PkAfterVoteS2C`**）：

- 这是**两步**窗口，两步用的消息类不同：
  ① 选路 `RequestVoteSelectC2S(monsterId)` → `VoteSelectC2S{SelectId}` —— **不带 `Info`/`Sn`**，
     所以它不属于某一次投票窗口，只是一次"我当前选谁"的广播，**可以反复改**；
  ② 确认 `RequestVoteC2S(sn)` → `VoteC2S{Info}`（**只有 sn、不带选择**）。
  倒计时 `ActionDownTime(action.Sn, 5309, SureVote, ...)` 点的是 **SureVote(第二步)** → **不答 = 直接确认**
  （没先选过就等于弃票）。
- offer 又是"与答案同一个消息类" `VoteC2S`：offer 带 `VoteIds`（客户端只在 `Count > 0` 时开窗），
  答案是 `Info` —— 靠 `Info.Sn` 区分；并且**只给本人**（`GetSelfPlayerData().player.Id != action.PlayerId` 直接 return）。
- 候选**不在协议里**：客户端从**本地配置**取 `StaticConfigure.PVEMission.Votes` 里 `MapId` 命中的那一项，
  再取下标 **+0=右 / +1=左 / +2=中**（`GetSafeByIndex`，越界给 null；所以 82013 图只有右/左，82015 才有中）。
  桥接复刻这段（`GameProbe.TrySelfAssistVote`），pending 里直接写着"左=… 右=… 中=…（0 = 本图没有这一路）"。
- **关窗信号是 1093**（PK 结束 → `VoteOver()`），不是 5310/5312 —— 那两条只是"某人确认/某人选路"的广播。
  另外确认之后客户端会把三个按钮都收起来，所以桥接在 `assist_vote_sure` 里就把窗口清掉（不再给 agent"还能改"的错觉）。
- 工具：`astral_assist_vote_select {"side":"left"|"right"|"center"}`（或 `monsterId`）与
  `astral_assist_vote_sure {}`。**选路那条不记 `OnSent`**（记了会把窗口标记成已应答，之后就改不了了）。

**不需要工具的四条**（客户端自己会立即上行、玩家没有任何点击机会）—— 逐条读过代码确认：
**5043 再走一次**（`DealMoveAgain` → `MoveAgainC2S`）、**5049 掷骰得星币**（`DealLand_RollGold` → 立即 `RequsetRollGoldC2S`）、
**5053 事件触发**（`DealLand_EventTigger` → 立即 `RequestTriggerEvent`）、**5059 炸弹骰**（→ `BombThrowDiceC2S`）、
**5071 命运**（`DealLand_Destiny` 在 `IsSelf` 时**立即** `RequestTriggerDestinyC2S`，没有按钮/倒计时）、
**5313 剧情**（剧情播完自动回 `NotifyStoryC2S`）。

`5037`/`5038`/`5317`/`5318` 的依据（反编译）：

- `FightLogic.ReadyFightThrowDice(action)` → `ShowWin().RefreshThrowDice(action.PlayerId, action.Sn)`：
  窗口归属 `action.PlayerId`，要回传的 `sn` 就是 `action.Sn`，**不读 `action.Data`**。
  应答是 `RequestBattleThrowDiceC2S(sn)` → `BattleThrowDiceC2S{Info={Sn,UseTime}, DevPoint=GMConfig.dev_AttackerPoint}`。
  **注意**：战斗攻击骰的 `sn` 与普通回合投骰（`ActionLogic.throwDiceSn`）**不是同一个来源**，
  拿错会被服务器当非法 sn 拒掉 —— 所以 `astral_throw_dice{"battle":true}` 只认 BattleDice 窗口的 sn，
  没有窗口就直接报错（不去猜 SDK 默认 sn）。
- `UI.LandEventWindow.ShowSkill10202(action)`：候选 = `SelectEventC2S.Events`，应答时把**服务器那条消息原样**
  改 `Idx`（选中下标）与 `Info.Sn`（`=action.Sn`）后发回 —— 所以 `Events` 列表必须回传，不能只发下标。
  超时回调把 `Idx` 兜成 `0`（界面上 `selectedIndex` 是 `-1`），即**不选就默认第一个**。

**两条关键设计决定（都是踩过的坑）**：

1. **窗口只能来自原始动作流，不能来自 SDK 事件**。SDK 事件（`OnRelicCandidates` / `OnRewardCardSelected`…）
   **不带 `sn`**。如果拿它们建窗口，`Sn` 会是 0 → agent 发出的请求缺 `sn` → 服务器不认，
   表现为"点了没反应"。所以：`SetWindow` 直接**拒绝 `Sn==0`**，SDK 事件只写流水、不建窗口。
2. **一次只报一个窗口**（`Kind` 是单值）。真人 UI 也不会同时弹两个；多报会让 agent 发错招。
   优先级：我的窗口 → 掷骰 → 卡牌选择（`cardSn>0 && usable.Length>0`）；别人的窗口报 `none` 并在
   `Notes` 里说明"在等其他玩家"。
3. **已应答的 sn 不会再开窗**（`_answeredSn`，有界 128）。服务器会把**我自己的决定**当成一条**同 sn** 的
   动作广播回来（5029 的购买、5037 的战斗投骰都是这样）。`CommandRunner.OnSent()` 在真正发出请求后
   调 `NoteAnswered(sn)`，`SetWindow` 见到已应答的 sn 直接忽略 —— 否则刚回完的窗口会被自己的回声重新打开，
   agent 就会对着同一个窗口反复出招。sn 在一次对局里单调唯一，所以这条不会误伤新窗口。

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
  `buy_relic`→BuyRelic、`move`→Move、`select_event`→SelectEvent、`throw_dice{"battle":true}`→BattleDice、
  `use_card`→FightCard、`ask_battle`→AskFight、`battle_choice`→FightChoice、
  `use_effect_card`/`use_quick_card`/`abandon_card`→CardChoice…），
  类型不匹配直接拒绝。拿不到窗口信息时宁可不发。
- **战斗骰的 sn 单独解析**：`astral_throw_dice{"battle":true}` 只认 BattleDice 窗口的 sn
  （普通回合投骰用的是 `ActionLogic.throwDiceSn`，两者不同源）；没有窗口就 `bad_args`，不去猜默认 sn。
- **reroll**：`astral_select_relic{"reroll":true}` → `SelectRelicC2S{Info, IsReroll=true}`；
  窗口**不关闭**（服务器随后推新的一组候选），所以这一条不调 `OnRelicSelected`，只记已应答 sn。
- **战斗出牌的 `CardUid` 是手牌 Guid**：`astral_use_card{"cardId":<候选 id>}` 里的 id **就是 Guid**；
  也接受显式 `cardUid`/`cardGuid`，或给配置 `CardId` 让桥接反查第一张同配置手牌
  （`Players.HandHasGuid` 先用"这张牌确实在我手上"认一次，认不出才当配置 id 反查）。
  `{"pass":true}` → `CardUid=0`（与客户端点"结束出牌"/超时同一条路径）。
  早期版本把配置 `CardId` 直接填进 `CardUid`，**那是错的**（见 §5 的实测证据）。
- **`sn` 主动清理**：发出后 `CancelOperationTimer(sn)`，让客户端自己的超时逻辑别再触发一次
  （否则可能出现"agent 已经点了、客户端又替我点一次"）；同时 `NoteAnswered(sn)` 挡住回声重开窗口。
  这条对战斗三件套**尤其致命**：5047/5035/5039 的超时代答方向与"接受"相反
  （不打 / 不出牌 / 不闪避），不取消计时器就会出现"agent 说打、20 秒后客户端自己补一条不打"。
  SDK 的 `GameActions.Ready()` 里也补上了同样的 `OperationTimer.CancelOperatTimer(sn)`
  （与游戏自己的 `Request*C2S` 一致），双保险。
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
| 纯逻辑 | `tests\AstralParty.AgentMod.Tests`（143 个） | 目录/文件协议（原子写、日志尾部按行截断、轮转、`Sanitize` 防穿越、`{seq:D8}` 排序）、`PendingTracker` 逐窗口（含 `Sn==0` 拒绝、别人的窗口、优先级、倒计时、副本语义、**已应答 sn 的回声防护**、事件选择与战斗骰窗口、**战斗三件套 5047/5035/5039（含"别人的窗口不许顶掉我的"与 `NoDodge` 查询）**、**地块四件套 5077/5213/5323/5067（含 64 位 `LongId`、价格/点数上限查询、回声防护）**、**地块应答三件套 5233/5259/5093（含"别人的窗口不当作我的"、"医院窗口只有一个选项"）**、**炮台选目标 5063（含候选数量上限、"候选未知 ≠ 没有候选"）**、**占卜 5069（含两张候选与"超时 = 第 1 张"）**、**赌场 5081/5083（含"按钮置灰不可操作"、"不能参与就不开窗"、"状态变化只关窗不开窗"）**、**抽奖 5041（含"号码全占满就不可操作"与"超时 = 最小的可用号码"）**、**追击地块 5033（含"候选为空是合法结果"、"候选未知 ≠ 没有候选"）**、**助力投票 5309（含三路槽位、"两路图中间那路是 0"、"超时 = 直接确认"）**）、命令解析与回执序列化、`control.json` 读取（含大小写容错与急停/恢复往返）、**热更 BCL 禁用模式 lint** |
| 协议 + 集成 | `tests\AstralParty.Mcp.Tests`（67 个） | JSON-RPC 全路径、工具清单与注解、参数校验、开关合并、**真文件往返**（假游戏线程消费 `commands` 写 `results`）、**新工具的参数确实落进命令文件**（含 `ask_battle`/`battle_choice`/`use_card` 的 `pass`、以及地块四件套的 `stop`/`monsterId`/`buy`/`point`）、**工具清单与 `AgentBridgeLayout.Tool` 的双向一致性守卫**（加了契约常量却忘了暴露 MCP 工具、或名字拼错都会挂）、超时清理、事件尾部截取、`Pending.Kind=None` 的大小写判定 |
| 端到端冒烟 | `tools\smoke-agent-bridge.ps1`（88 项断言） | **真 server exe** + 临时桥接目录扮演游戏：握手/工具清单（38 个）、state/bridge/control 字段与大小写、命令文件往返与两侧清理（含战斗三件套、地块四件套、地块应答三件套、炮台选目标、占卜、赌场、抽奖、追击地块与助力投票两步）、心跳过期拒绝下发 |
| 真机 | 需要用户配合 | 见下 |

**为什么要"假桥接目录"这种测法**：整条链路的契约就是目录里的文件。测试里真写 `state.json`、
真起一个线程扮演游戏侧消费 `commands\` 并回 `results\`，就能在不装游戏的情况下验证
命名规则、TTL、原子写、回执解析、超时清理是否互相吻合 —— 这是最容易悄悄坏掉的一层。

冒烟脚本（`-Keep` 保留现场）：

```powershell
pwsh -NoProfile -File tools\smoke-agent-bridge.ps1
```

它跑的是**真正发布出来的 exe**（缺了就现场 `dotnet publish`），所以能抓到"单元测试绿、exe 是旧的"
这类问题 —— 本轮 `Kind=None` 的判定就是这么发现旧 exe 还没重发布的。

---

## 10. 真机验证清单（需要游戏在跑）

### 10.1 已完成：只读连通性（2026-10-01，游戏 PID 27504）

结论：**桥接链路已打通**（mod → 桥接文件 → MCP server → MCP client），全程**未下发任何操作**
（`control.json` 里 `EnableActions=false`，`state.json` 的 `Control.ControlSource` 实测为 `control.json`）。

| 验证项 | 实测结果 |
|---|---|
| mod 加载 | 加载器日志 `✔ [2/5] AstralParty.AgentMod 加载成功`（22:02:58）；此后**没有** `[ERR]`/`MethodNotFind` |
| `bridge.json` | `ProcessId=27504` 与游戏进程一致；`TickCount` 持续推进（3445 → 35453）、`StateSeq` 同步增长 |
| `state.json` | 959 B，`StateWrites` 稳定增长（107 → 1064+），`UpdatedAtUtc` 是 ISO `"o"` 格式 |
| MCP 握手 | `initialize` → `astral-party-mcp 1.0.0` / protocol `2025-06-18`；`tools/list` → **21** 个工具（当时） |
| `astral_status` | `✅ 桥接活着 (心跳 116ms 前, 进程 27504)`；场景/房间/战斗/待响应窗口全部正确（主界面：无房间、无窗口） |
| `astral_state` | 原样回状态 JSON（见 §4 字段表；`"Kind": "none"` 是实测值） |
| `astral_pending` | `当前没有需要你响应的窗口(可能在等别的玩家, 或不在对局里)` —— 主界面下的正确结论 |
| `astral_events` | `events.jsonl 还没有内容` —— 不在对局，本来就没有事件 |
| 命令计数 | `CommandsExecuted=0 / CommandsRejected=0` —— 只读阶段一单未发 |

> 这轮抓到并修掉了一个**只有真机才会暴露**的 bug：第一版 `WriteAtomic` 用了 `fs.Flush(true)`，
> 而热更侧 BCL 缺 `FileStream.Flush(bool)` → `state.json` 一条都写不出来（当时桥接目录里只有
> `control.json`）。修法与预防见 §12 与 `agents.md §12.1`。

### 10.2 待做：从"只读"到"能动手"

按顺序做，任何一步不对就停在那一步排查：

1. **加载器在位**：游戏 exe 同目录有 `version.dll` + `AstralParty_ModLoader\`；
   `logs\` 里有 mod 加载记录。（✅ 本轮已完成）
2. **mod 起来了**：`%LocalAppData%\AstralParty_ModLoader\agent\bridge.json` 存在且
   `LastTickMs` 每秒在动；`AstralParty.Mcp.exe --print-config` 显示"心跳: 活着"。（✅ 本轮已完成，见 §10.1）
3. **只读状态**：进房间后 `astral_state` 能看到自己、队友、手牌；`astral_pending` 在轮到人操作时
   报出窗口与候选。（主界面部分 ✅；**进对局那部分还没做**）
4. **演练模式**：`astral_control {"dryRun":true}` → 发一次 `astral_throw_dice`，应回 `dry_run`
   且游戏无反应（验证闸门与流水）。
5. **最小真实操作**：关掉演练，在**自己房间/练习或单人对局**里先做最无害的一步
   （掷骰 → 移动），确认画面真的动了、`astral_actions` 有回执。
6. **窗口类操作逐个验**：筹码三选一（含 reroll）→ 奖励卡 → 商店（买/离店）→ 筹码地块买/不买 →
   事件选择（5317）→ 战斗攻击骰（5037）→ **战斗三件套**（5047 打不打 / 5035 出牌 / 5039 闪避）→
   **地块四件套**（5077 停留/继续走 / 5213 追不追怪 / 5323 商人买不买 / 5067 选几点移动力）→
   **地块应答三件套**（5233 复活队友 / 5259 机制选择 / 5093 医院）→ **炮台选目标**（5063）→
   **占卜**（5069 两张牌选一张）→ **赌场**（5081 押奇偶 / 5083 掷骰）→ **抽奖**（5041 选号）→
   **追击地块**（5033 追敌方英雄；与 5213 追怪是两个窗口）→ **助力投票**（5309 两步: 选路 + 确认）。
   每验一个都去 `docs` 或本文把"未验证"标注改成"已确认"（含日期）。
   > 事件选择、战斗骰、战斗三件套、地块四件套、地块应答三件套、炮台选目标、占卜、赌场、抽奖、追击地块都是 2026-10-01 按反编译（+ 前两批有 3 局真实回放）
   > **协议实测**补进工具的，**契约与状态机有离线测试，但真机上一个都没验过** —— 真机第一件事是看
   > `astral_actions` 里出现 5317/5037/5047/5035/5039/5077/5213/5323/5067/5233/5259/5093/5063/5069/5081/5083/5041/5033 时
   > `astral_pending` 是否报出对应 kind（`selectEvent`/`battleDice`/`askFight`/`fightCard`/`fightChoice`/
   > `stopOrContinue`/`pursueMonster`/`vendorCard`/`selectPoint`/`reviveTeammate`/`selectMechanism`/`hospitalCheck`/`batteryTarget`/`divination`/`gambleGuess`/`gambleDice`/`lotteryPick`/`pursuePlayer`），
   > 以及 `astral_use_card` 报的候选是不是我手上真有的牌、`astral_pursue_monster`/`astral_battery_pick`/`astral_pursue_player` 的候选是不是服务器也认。
   > 战斗三件套在 PVE 里出现频率极高（3 局样本里 5035 出现 376 次、5047 205 次、5039 159 次），
   > 四件套里 5213/5323 稀少（3 局样本里 5323 只在 `1790736781145194` 那局出现 10 次），所以这一条是最值得优先验的。
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
- 回执命令：5030 / 5216 / 5250 / 5212 / 5378 / 5318 / 5038。
- **5037 战斗攻击骰窗口**：`FightLogic.ReadyFightThrowDice` 只读 `action.PlayerId`/`action.Sn`（不解 `Data`），
  应答 `BattleThrowDiceC2S{Info, DevPoint=GMConfig.dev_AttackerPoint}`（SDK 的 `GameActions.BattleThrowDice` 已经一致）。
- **5317 事件选择**：候选 = `SelectEventC2S.Events`，应答要把候选列表原样回传（只改 `Idx` 与 `Info.Sn`）。
- **reroll**：`RelicLogic.RequestResetRelic` = `SelectRelicC2S{Info, IsReroll=true}`（`Relics`/`Idx` 保持默认），
  选与重摇是同一条消息。
- **5047 战斗询问**：`AskBattleC2S` 有 6 个字段（`Info`/`AskPlayerId`/`IsBattle`/`IsPursuit`/`FightBack`/`SkillPlayerId`），
  **客户端只填 `Info` + `IsBattle`**；`FightBack=true` 时客户端对**自己**这条动作立刻自动回 `IsBattle=true`
  且不弹窗（`FightLogic.AskFight`）；超时（cmd 5047）点的是 `btn_Leave` → `IsBattle=false`；
  只有 `IsSelf(action.PlayerId)` 才注册倒计时。
- **5035 战斗出牌**：`Data` 恒空（实测 537/537 长度为 0）；候选是客户端本地按 `EffectType` 过滤手牌算的；
  **`BattleUseCardC2S.CardUid` 装的是手牌 Guid**（实测 57 条非零 5036 的 `CardId` 全部命中手牌 `UniqueId`，
  0 次等于配置 id）；`CardUid=0` 即不出牌，超时/点 `btn_FinishPkCard` 都走这一条。
- **5039 闪避选择**：应答 `BattleChoiceC2S{Info, DevPoint=GMConfig.dev_DefenderPoint(=0), Dodge}`；
  `NoDodge=true` 时客户端直接拒绝 `Dodge=true`（只弹提示 10003）；超时点 `btn_Defend` → `Dodge=false`。
- **战斗超时时长**：`ChoosingTimeLimit[TimePlan].OtherTimeLimit` = 10/20/40 秒，`ExtraTime` 恒 0；
  超时累计会进 AFK 惩罚。
- **5077 加油站/出生点**：`StopOrContinueC2S{Info,Stop}`（`true`=停留、`false`=继续走）；Action `Data` 恒空
  （63/63），窗口只属 `IsSelf`；超时点 `btn_Continue` → `Stop=false`。
- **5213 怪物追击**：`MonsterPursuitC2S{Info,SelectId}`（`SelectId`=怪物 `playerId`，0=不追）；`Data` 恒空（10/10）；
  候选 = 客户端 `LandLogic.GetVailPursuitMonster()` 本地过滤（`Monster && !NotSelect && HP>0 && CharacterInst!=null
  && standLand.LandType!=Hospital && TeamId!=自己`）；超时 `SelectId=0`。
- **5323 商人买卡**：offer `{CardId,Gold}`（无 `Info`，实测 `CardId` 21014/21015/21016、`Gold`=5）、
  答案 `{Info,IsBuy}`（不回填 `CardId`/`Gold`），靠 `Info.Sn` 判别；超时 `IsBuy=false`；星币不足客户端不发包。
- **5067 控移卡选点**：offer `{MaxPoint}`（无 `Info`）、答案 `{Info,Point∈1..MaxPoint}`，靠 `Info.Sn` 判别；
  只对 `IsSelf` 开窗；超时 `Point=1`。
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
- **PVE 里"还需要玩家点击"的窗口已经全部接管完了**：战斗三件套 5047/5035/5039、筹码三选一/奖励卡/商店/ATM/
  筹码地块购买、事件选择 5317、地块四件套 5077/5213/5323/5067、地块应答三件套 5233/5259/5093、
  炮台选目标 5063、占卜 5069、赌场 5081+5083、抽奖 5041、追击地块 5033、助力投票 5309（都用离线测试锁住了契约）。
  判据始终是同一条：**`OperationTimer.ActionDownTime(sn, <id>, onComplete)` 的调用点**——
  有它才有"等玩家点"的窗口；`onComplete` 就是"不答时服务器替你按的按钮"。
  **5029 PVP 商店**按"只做 PVE"的范围决定**不做**。
- **已确认不需要工具的动作**（客户端自己会立即上行、玩家没有任何点击机会）：
  再走一次 5043（`DealMoveAgain` 在 `IsSelf` 时直接发 `MoveAgainC2S`）、
  掷骰得星币 5049（`DealLand_RollGold` → 立即 `RequsetRollGoldC2S`）、
  事件触发 5053（`DealLand_EventTigger` → 立即 `RequestTriggerEvent`）、
  炸弹骰 5059（`DealBombThrowDice` 在 `IsSelf` 时直接发 `BombThrowDiceC2S`）、
  命运 5071（`DealLand_Destiny` 在 `IsSelf` 时**立即** `RequestTriggerDestinyC2S`，无按钮无倒计时）、
  剧情 5313（`StoryLogic.OpenStoryByServer` 在剧情播完时自动回 `NotifyStoryC2S`）。
- **真机验证：全部为"未验证"** —— 上面这些窗口的契约与状态机都只有离线测试，真机上一条都没验过，
  验收步骤见 §11（`astral_pending` 报不报出对应 kind、动作能不能真的落到游戏里）。
- `AskBattleC2S.IsPursuit` / `SkillPlayerId` 的游戏语义（全量反编译里既不读也不写，只有服务器填）。
- `BattleUseCardS2C.NoCard` 字段（客户端从不读，实测 3 局 376 条全为 `false`；跳过语义是 `CardId==0`）。
- `ShopBuyS2C` / `PVEShopBuyS2C.AssistPlayer` 的服务端语义。
- 服务器 1097 超时踢人机制。
- 战役图（`MapType==10`）无倒计时（`RemainingMs=-1`）。
- 5037 的 `Data` 里到底有没有可区分的业务负载（当前靠"已应答 sn"挡回声，不解析负载）。

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
