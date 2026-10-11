# 战斗胜率助手（CombatOddsMod）

CesiumLoader 内置向 mod：在**打怪投牌（FightWindow）**界面实时计算并显示
防御/闪避的**期望受到伤害与被击倒率**、以及我方攻击的**期望伤害与击杀概率**，
每投一张牌（每次 `BattleUpdate`）刷新一次。

战斗角色头顶的「骰点 >= 多少」提示已移除。保留 FightWindow 概率面板，以及棋盘角色头顶的攻防/血量显示。

> 来源需求（c204）：打怪投牌界面实时显示"当前期望伤害和击杀概率，每投一张更新一次"，
> 以及"闪避/防御受到的期望伤害和被击倒率"，并给出"该防该闪"的建议。

---

## 1. 分阶段交付与状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| (1) | 显示探针 / FightWindow 覆盖层 | **代码就绪，待进游戏目视确认位置** |
| (2) | 基础概率计算模块（可离线单测） | 移除头顶骰点提示后 101 项离线测试通过，Release 构建通过；尚未真机验证 |
| (3) | 事件驱动的实时更新与显示 | ✅ 完成（`GameEvents.BattleUpdate` 驱动） |
| (4) | 实时 buff 伤害修正（受伤±/免疫） | ✅ 完成（`Players.BuffsOf` + buff 表，取代旧的"特殊机制脚注"） |
| (5) | 观战他人战斗（攻/守双视角） | ✅ 完成（`ShowForOthers` 默认开） |
| (6) | **实时 buff 伤害修正**（标记/狂暴/护盾…） | ✅ 完成（`Players.BuffsOf` + `CombatMath` buff 表） |

旧模型曾用 11 份真实回放对拍（见 `combatvalidate/`、`AGENTS.md` §17）；该历史结果不构成当前「范围整数 + 单颗 d6」公式已验证的证据。当前模型与验证边界见 §2、§6。

---

## 2. 计算模型与适用边界

- **攻击/防御各为范围整数 + 单颗 d6**：攻击未锁定时 `A = X + U`，`X ∈ [MinAtk, MaxAtk]`；防御 `D = Y + V`，`Y ∈ [MinDef, MaxDef]`；`U`、`V` 各为一颗判定骰，取值 1..6（`StaticGlobalData.GAME_JUDGE_DICE_LIMIT`=6）。范围端点均包含在内，不能从上限推断 N 颗 d6。
- **分布假设**：范围内整数暂按等概率、且与判定骰独立枚举。用户已明确「范围 + 单骰」，但范围整数的等概率分布尚未证实；因此计算结果是这一假设下的条件概率，不应称为已确证的真实概率。
- **帧序与锁定值**：攻方投骰(5038) → 防守方选择(5040)。攻击已锁定时直接使用 `BattleRole.Atk` 作为最终 `A`，不重复加骰；攻击未投时才枚举 `MinAtk..MaxAtk + 单颗 d6`。
- **基础伤害** = `max(0, A-D)`。普通 live 调用使用 `minChip=0`，不再猜测保底 1 点伤害；基础伤害之后应用已登记的受伤 buff 修正并将伤害下限限制为 0，免疫时伤害为 0。
- **防御存活边界**：正生命值且无伤害修正时，存活严格满足 `A < HP + D`；`A = HP + D` 已足以击倒。计入 buff 后以最终伤害 `>= HP` 判定击倒，`HP` 始终取防守者的当前生命值。
- **攻击显示的概率**：目标选择防御时，枚举尚未锁定的攻击与防御，计算期望伤害及 `P(最终伤害 >= 防守者 HP | 目标选择防御)`。不混合闪避策略，也不表示整场战斗胜率。
- **防守方的最坏估计**：攻击未投时使用 `MaxAtk + 6`；攻击已锁定时使用最终 `Atk`。未投攻击的最坏估计应与枚举得到的概率读数区分。
- **闪避**：按判定骰点数比较，通常成功条件为 `闪避骰 > 攻骰`；攻骰达到判定上限时，闪避骰必须 `= 上限` 才成功。闪避失败率为 `1-P(成功)`，不能把成功率当被击倒率；仅当失败后的最终伤害足以致死时，该失败结果才计入被击倒概率。失败伤害按 `DodgeKeepsBaseDefense` 设置及已登记 buff 修正/免疫计算。
- **建议**：比较防御与闪避的**被击倒率**，较低者优先，输出「建议【防御】/【闪避】」；读数受上述分布假设和已登记效果范围限制。

### 历史残差、实时 buff 修正与脚注

旧回放对拍曾在旧基线 `max(1,Atk-Def)` 下观察到 +1~+3 的残差，且逐攻击者不同；旧模型干净子集约 68% 的匹配率仅是历史记录，不能验证当前公式。历史样本中，直接加 `AttackBonus` 反而降低匹配率，`ChainAttackDamage` 样本为 0；这些观察不能确证残差来源全是 buff，也不足以排除所有其他机制。当前应按修正后的范围、单骰与零保底模型重新对拍，再分别核查已登记 buff、条件性效果和未覆盖机制。

关键突破（阶段 6）：这些效果**其实是可以实时读到的**——服务器把每个单位的激活 buff 放在
`party.model.Hero.Buffs`（`MapField<long, Buff>`），每条含 `BuffId` 与 `Progress`（层数）。
mod 通过 `Players.BuffsOf(unitId)` 读到**被击中者**（防守方）身上的 buff，用
`CombatMath.DefaultBuffTable()` 把"受到伤害±N"换算成这一击的伤害修正，直接计入期望伤害与击倒/击杀率。

- 表由 `buffcat/` 工具从 `Buff.bin` + `STRBuff.bin` 反编译全部 465 条 buff 的说明逐条提取，
  只保留携带"受到伤害/受到的伤害 ±N"的 18 条（详见 §7 与 `docs/机制-Buff伤害修正.md`）。
- **只取受伤侧的 ±N**：狂暴的"攻击力+3"、逆鳞的"攻+2"这类攻击加成**一般已并入服务器下发的
  `BattleRole.Atk`**，若再加就会重复计数，故本表不碰攻击值。
- 免疫/护盾（护盾 `1071101` 下次-99、深度改造 `10331101` 受伤降为0）按"这一击伤害归零"处理。
- 语境限定（精准打击只吃【轨道轰炸】、渊蚀印记只吃魔渊触须/赛克斯）**不计入**普通投牌结算，
  只在读数里作提示。
- 表里没有的 buff（绝大多数不影响伤害）**一律忽略**。

因此旧的 `DefaultCorrectionTable()`（`Modeled=false` 名单 + 强制"仅供参考"脚注）已被**实时 buff 表整体取代并删除**，
不再有硬编码的"摩西/鲸鱼/水乡Boss"等未建模名单与脚注开关。
详见 `AGENTS.md` §17。

---

## 3. 显示路径（阶段 1）

- SDK **不自带 IMGUI 后端**，IL2CPP 也不能运行时挂 `MonoBehaviour` 拿 `OnGUI`
  → 唯一能画进战斗界面的路是**注入游戏自己的 FairyGUI**。
- 取窗口：`UI.UIManager.inst.Fight`（`FightWindow : BaseWindow : FairyGUI.Window`），
  根容器 `contentPane`（`GComponent`），往里 `AddChild` 一个 `GTextField`。
- 实现**不编译期引用 FairyGUI**：`UIManager/FightWindow` 强引用 `AstralParty.Runtime`，
  所有 FairyGUI 成员一律走 `RuntimeAssemblyService` 反射（`GTextField` 已确认是运行时真实类型，
  `.text`/`.visible`/`AddChild` 均为已确认成员）。任一步失败自动降级到控制台/通知。
- 编排逻辑抽到 `FightOverlayController`（纯状态机，单测覆盖：建/复用/换父/隐藏/建失败重试），
  真实反射在 `RuntimeFightOverlayReflector`。
- **兜底显示始终可用**：无论覆盖层是否成功，读数都会输出到加载器控制台（`SdkLog`）
  与 `UiService.Notify`。

---

## 4. 配置（`CombatOddsMod` sidecar / SdkConfig）

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `ShowForOthers` | true | 是否也为非自己的战斗显示（观战/队友打怪与防御，攻守双视角） |
| `PopupNotification` | true | 是否弹游戏内通知 |
| `NotificationTtl` | 8 | 通知存活秒数 |
| `DodgeKeepsBaseDefense` | false | 闪避失败时是否仍保留基础防御减伤（默认按承受全额攻击算，更保守） |
| `ColorHighlight` | true | 是否用颜色高亮结论与关键数字（覆盖层富文本；控制台自动降级为纯文本） |
| `ShowBuffBreakdown` | true | 是否显示目标身上"受到伤害±"类 buff 明细并计入伤害/击倒率 |
| `InGameOverlay` | true | 是否把读数以 FairyGUI 覆盖层显示在 FightWindow 上 |

---

## 5. 构建 / 测试 / 部署

```powershell
# 构建
dotnet build modding\msvc\mods\CombatOddsMod\CombatOddsMod.csproj -c Release --nologo
# 单测（计算核心 + 覆盖层编排状态机）
dotnet test  modding\msvc\tests\CombatOddsMod.Tests\CombatOddsMod.Tests.csproj -c Release --nologo
# 回放对拍
cd combatvalidate; dotnet run -c Release
# 部署到游戏（需先装 CesiumLoader 加载器；游戏须已退出）
pwsh -File modding\msvc\tools\deploy-combatodds.ps1
```

`CombatOddsMod` 已加入 `tools/builtin-mods.json`（内置发布清单），随加载器发布。

---

## 5.1 棋盘头顶攻防的刷新（2026-10-06 修复）

**症状**：棋盘上角色头顶的「攻 N 防 M」在快速滑动屏幕时跟不上角色，出现残影。

**根因**：位置更新挂在 FairyGUI 定时器上（`Timers.inst.Add(0.05f, 0, Poll)`）。
游戏自己的 `Timers.Update()` 是**每帧累加、攒够 interval 才回调**并丢弃余量
（`Timers.cs:177-186`），所以那是 **~20Hz 的离散阶跃**，相位还与相机不同步；
相机跟随是连续插值，两者相位差被放大就成了残影。

**修法**：改用 SDK 的每帧钩子 `UpdateService.SubscribeLateUpdate`（主线程 `PostLateUpdate`），
与游戏自己的头顶名牌 `UICom_PlayerAttrInfo` 同一个节奏 —— 后者正是在
`UIBattleInfoPanel.OnUpdate()` 里每帧 `WorldToScreenPoint` 摆位置的。
选 **LateUpdate**（而非 Update）是为了排在相机移动之后算坐标，同一帧内位置才自洽。

同时把每帧的重活挪出热路径：

- 文本/尺寸只在**数值真变**时重排（判定签名覆盖浮框会显示的全部字段：最终攻/基础攻/防/血）；
- `SetChildIndex`（子节点置顶）只在真的不在最上层时才做 —— `com_PlayerAttrInfos` 是与游戏
  自己的名牌**共用**的容器，游戏后加的名牌会盖住我们的字，所以不能完全不置顶。

**两个坑（改这块时务必注意）**：

1. **配置开关会被每帧刷新顶回来**。`Poll` 每帧调 `Update()`，而 `Update` 默认
   `enabled=true`；若不同步开关，`ShowBoardPlayerAttrs=false` 隐藏的标签会被每帧重新显示。
   现在开关存在 `_enabled`，由 `ModEntry.OnTick` 每秒调 `SetEnabled` 同步（每秒只同步开关，
   不承担跟随刷新）。
2. **不能给 `GTextField.SetSize` 设尺寸**。该字段 `autoSize=Both`，而
   `GTextField.HandleSizeChanged()` 在 Both 下**直接 return**，那个 `SetSize` 是空操作。
   尺寸必须给外层 `Box`（普通 `GComponent`，会老实采纳）。

**验证**：真机快速滑屏确认残影消失（2026-10-06）。

---

## 5.2 2.3.2 更新

- 头顶标签取 `SpriteRenderer.sprite.bounds` 的顶部点，经 `renderer.transform` 转为世界坐标，再用最终相机投影；`HeadGap = 8`，替代旧的角色根节点投影后固定上移 112（root + 112）。
- `UpdateEvents.LateUpdate` 在 `FreeCameraMod` 的订阅回调执行后触发，标签按该帧最终相机位置投影。
- SDK `Players.BuffsOf` 优先读取实时 `buffContainer._buffDict`，仅在该实时容器缺失时回退到快照；实时字典为空表示当前无 buff，不回填旧快照。
- 部署脚本同步 `sdkVersion`，保留玩家现有配置，不重置其他设置。

验证状态：此前构建与测试已通过，用户已确认护盾与加载修复；新版头顶锚点尚无明确真机确认，仍需进游戏目视验证。

---

## 5.3 2.3.3 更新

- 凤凰 Boss 的 `MapEvent / Skill#105311` 已从通用受伤 buff 修正中排除；服务器已结算的地图事件不再被错误显示为“灰烬xN 已匹配”。
- 普通战斗仍按实时灰烬层数计算减伤。
- `[凤凰探针]`、`[灰烬诊断]` 仅在 Debug 构建编译；Release DLL 不包含探针实现和调用。

---

## 6. 当前模型与进游戏验证清单

前提：游戏已装 CesiumLoader 加载器，`doorstop_config.json` 里 `console=true`（能看控制台）。

1. 跑 `deploy-combatodds.ps1` 部署，启动游戏，进入一局 **PVE 打怪**。
2. 进入打怪投牌（FightWindow）界面，观察：
   - **控制台**是否每投一张牌就打印一段「战斗胜率」读数（这一步不依赖覆盖层，必然可见）。
   - **FightWindow 上**是否出现覆盖层文本标签（`InGameOverlay=true` 时）。
3. 若覆盖层：
   - **没出现** → 看控制台是否有「创建覆盖层标签失败」或反射告警；据此调整
     `RuntimeFightOverlayReflector`（可能需要换 `GRichTextField`、或改用 FairyGUI 版本对应的定位方法）。
   - **出现但位置/大小不对** → 调 `RuntimeFightOverlayReflector.CreateLabel` 里的 `SetSize/SetXY`
     与 `textFormat`（这就是企鹅鹅点名的"显示在哪里"，需真机迭代）。
4. 对照读数与实际战斗结果，记录 `MinAtk/MaxAtk`、`MinDef/MaxDef`、判定骰、锁定 `Atk`、防守者 HP 和目标实时 buff；先核对 `max(0,A-D)`，再核对已登记易伤/减伤/免疫后的伤害。不能仅因某次结果吻合就认定范围整数等概率。
5. 核查边界：锁定 `Atk` 不重复加骰；`A=HP+D` 时击倒；无保底时基础伤害可为 0；未投攻击的最坏估计为 `MaxAtk+6`。
6. 核查闪避：普通点数必须大于攻骰，上限点数须等于上限；仅把足以致死的失败结果计入击倒率。攻击侧读数应明确限定目标选择防御，不混入闪避概率。
7. 确认覆盖层位置与可读性，并记录此前头顶锚点改动的目视结果；`CombatOddsMod` 已在内置发布清单中。

离线验证应针对当前模型独立枚举范围整数与单颗 d6，覆盖固定范围、非固定范围、锁定攻击、零伤害、严格存活边界、闪避上限特例及 buff 修正/免疫；回放对拍也应切换到当前公式后重新统计匹配率与残差。旧模型 11 份回放和约 68% 的历史匹配率不能替代这一步。

> 移除头顶骰点提示及其专用测试后，剩余 101 项离线测试全部通过（含独立枚举和严格边界），Release 构建成功、零警告零错误。尚未完成当前公式的回放对拍或游戏验证，范围整数等概率假设仍待证实。

---

## 7. 实时 buff 伤害修正表（阶段 6）

完整机制与逆向过程见 **`docs/机制-Buff伤害修正.md`**。这里只列 mod 实际计入的表
（`CombatMath.DefaultBuffTable()`，数据来自 `buffcat/` 对 `Buff.bin`+`STRBuff.bin` 的全量提取）：

| buffId | 名称 | 受伤修正 | 叠层 | 类别 | 说明 |
|---|---|---:|:--:|---|---|
| 10006 | 标记 | +1 | 每层 | 通用 | 用户点名的"标记—受到伤害+1"，玩家/怪物通用 |
| 2000801 | 狂暴 | +1 | 每层 | 通用 | **卡牌【狂暴】**(卡 20008)；每层攻+3、受伤+1 |
| 1140101 | 宿命回响 | +1 | — | 通用 | 蓝海晴 PVP，2 回合 |
| 1140102 | 虚弱印记 | +1 | — | 通用 | 蓝海晴 PVE，2 回合 |
| 4000401 | 霉运 | +2 | — | 通用 | 下次受到伤害+2 |
| 3200501 | 易伤 | +2 | — | 通用 | 下次受到伤害+2 |
| 10671302 | 真凶 | +2 | — | 通用 | 攻+2/移+2/出牌+1，受伤+2 |
| 10141101 | 脆弱中枢 | +1 | — | 通用 | 每次受伤本轮再+1（条件性，近似取 +1） |
| 4000301 | 好运 | −2 | — | 通用 | 下次受到伤害−2 |
| 1261101 | 湖沼之王 | −1 | 每层 | 通用 | 每层攻+1、受伤−1 |
| 5006001 / 10261101 | 逆鳞 | −2 | 每层 | 通用 | 每层攻+2、受伤−2 |
| 5007002 | 大铜锣 | −1 | — | 通用 | 攻+1、受伤−1 |
| 10531201 | 灰烬 | −1 | 每层 | 通用 | 每层受伤−1 |
| 10551101 | 稳定中枢 | −1 | 每层 | 通用 | 每层受伤−1 |
| 1071101 | 护盾 | 免疫 | — | 通用 | 下次受到伤害−99，视为挡下这一击 |
| 10331101 | 深度改造 | 免疫 | — | 通用 | 受到伤害降为 0 |
| 1221202 | 精准打击 | (+1) | — | 语境 | 只对【轨道轰炸】伤害，**不计入**投牌结算 |
| 1291202 | 渊蚀印记 | (+1) | — | 语境 | 只对魔渊触须/赛克斯伤害，**不计入** |

数据链：`Players.BuffsOf(unitId)` 读被击中者（防守方）身上的 buff → `CombatMath.EvaluateTargetBuffs`
按上表求 `Delta`/`Immune` → 传进 `CombatMath.Defend/Dodge/Attack` 的 `damageAdjust`/`immune` 参数，
直接改期望伤害与击倒/击杀率；读数里 `🧬` 行展示计入了哪些 buff。
