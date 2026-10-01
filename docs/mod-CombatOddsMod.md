# 战斗胜率助手（CombatOddsMod）

CesiumLoader 内置向 mod：在**打怪投牌（FightWindow）**界面实时计算并显示
防御/闪避的**期望受到伤害与被击倒率**、以及我方攻击的**期望伤害与击杀概率**，
每投一张牌（每次 `BattleUpdate`）刷新一次。

> 来源需求（c204）：打怪投牌界面实时显示"当前期望伤害和击杀概率，每投一张更新一次"，
> 以及"闪避/防御受到的期望伤害和被击倒率"，并给出"该防该闪"的建议。

---

## 1. 分阶段交付与状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| (1) | 显示探针 / FightWindow 覆盖层 | **代码就绪，待进游戏目视确认位置** |
| (2) | 基础概率计算模块（可离线单测） | ✅ 完成（`CombatMath`，单测通过） |
| (3) | 事件驱动的实时更新与显示 | ✅ 完成（`GameEvents.BattleUpdate` 驱动） |
| (4) | 实时 buff 伤害修正（受伤±/免疫） | ✅ 完成（`Players.BuffsOf` + buff 表，取代旧的"特殊机制脚注"） |
| (5) | 观战他人战斗（攻/守双视角） | ✅ 完成（`ShowForOthers` 默认开） |
| (6) | **实时 buff 伤害修正**（标记/狂暴/护盾…） | ✅ 完成（`Players.BuffsOf` + `CombatMath` buff 表） |

计算核心用 11 份真实回放对拍校准（见 `combatvalidate/`、`AGENTS.md` §17）。

---

## 2. 计算模型（已回放校准）

- **战斗骰是 d6**（`StaticGlobalData.GAME_JUDGE_DICE_LIMIT`=6）。攻击/防御/闪避的随机项 = 已知基数 + N×d6
  （N 默认 1，偶尔 2），颗数按上限 `ceil((max-base)/6)` 推断（`CombatMath.D6ModelFromRange`）。
- **伤害基线** = `max(1, 最终攻击 - 防御)`（最少 1 点筹码伤害）。干净子集精确率 ~68%。
- **帧序**：攻方投骰(5038) → 防守方选择(5040)。防守方做决策时**攻方最终攻击已锁定**：
  - 防御被击倒 = `P(max(1, finalAtk-(InitDef+d6)) >= HP)`，单 d6 精确；
  - 闪避成功 ⇔ `防点 > 攻点`，被击倒 = `P(d6 > atkPoint)`，精确。
- **建议**：比较防御与闪避的**被击倒率**，低者胜 → 输出「建议【防御】/【闪避】」。

### 残差、实时 buff 修正与脚注

回放实测在基线 `max(1,Atk-Def)` 之上有 +1~+3 的残差，**逐攻击者不同**，且**不是**
`AttackBonus`（加上反而更差）、**不是** `ChainAttackDamage`（样本 0）、**不是**追击。
结论：残差来自**"受到伤害±N"这类效果**（标记、卡牌狂暴、蓝海晴印记、护盾/免疫…）。

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

`CombatOddsMod` **尚未**加入 `tools/builtin-mods.json`（内置发布清单）——
待下面的游戏内验证通过后再加入。

---

## 6. 进游戏验证清单（完成阶段 1 的唯一剩余步骤）

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
4. 对照读数与实际战斗结果，抽查几次 `max(1,Atk-Def)` 是否吻合；有目标 buff 时确认易伤/减伤/免疫一行出现。
5. 位置/可读性满意后，把 `CombatOddsMod` 加入 `tools/builtin-mods.json`，随加载器发布。

> 该验证需要真机运行游戏，**无法在离线分析环境完成**。除此之外的所有部分
>（计算、事件驱动、buff 修正、覆盖层编排逻辑）均已离线单测/对拍通过。

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
