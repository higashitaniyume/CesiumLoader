# 机制 · Buff 伤害修正（吉星派对）

> 面向 CombatOddsMod 的 buff 逆向记录：游戏的 buff 是怎么组织的、哪些 buff 会改"受到伤害"、
> 客户端能实时读到什么、以及 mod 如何把它换算成击倒/击杀率的修正。
> 全部结论来自**只读**反编译（`extracted_dlls/AstralParty.Runtime.dll`）与配置解包
> （`config_extract/Buff.bin`、`STRBuff.bin`、`STRCard.bin`），提取工具在 `buffcat/`。

---

## 1. Buff 的三层结构

派对模式服务器权威，buff 也是服务器算、下发给客户端展示：

1. **静态配置**（`Buff.bin` → `BuffConfigure.Infos : RepeatedField<BuffInfoConfigure>`，共 **465** 条）
   每条 `BuffInfoConfigure` 的关键字段：
   - `Id`(1)、`BuffType`(2, 枚举 `None/Normal/Mark/Relate/Battle`)、`BuffTagType`(3, `None/Poison/Curse`)、
     `BuffRoundCountType`(4)、`KeepRound`(6)、`IsShow`(11)、`Icon`(12)、
     `IsShowIconProgressZero`(13)、`NameId`(14)、`DescId`(15)…
   - **注意**：配置里**没有**"受到伤害+N"这种数值字段——具体效果是服务端代码按 `BuffId` 实现的，
     只能从**本地化说明文本**（`STRBuff`）里读出效果语义。
2. **文案表**（`STRBuff.bin` → `STRBuffConfigure.LocalDict : MapField<int, STRBuffLocalConfigure>`）
   `STRBuffLocalConfigure{Id, Simplified, English, Japanese, Traditional, Korean}`；
   `BuffInfoConfigure.NameId/DescId` 即指向这里的 `Id`。465 条 buff 里有 311 条带简体文案。
3. **运行时实例**（每个单位身上）：`party.model.Hero.Buffs : MapField<long, Buff>`。
   每条 `party.model.Buff` 关键字段：
   - `BuffId`(2)、`Progress`(11, **层数/进度**，即图标上显示的堆叠数)、`KeepRound`(5)、
     `UniqueId`(1)、`Params`(3)、`UseTime`(7)…
   - `Progress` 就是"层数"的判据：`BuffInfoConfigure.IsShowIconProgressZero` 字面意思是
     "进度为 0 时是否仍显示图标"，即 `Progress` = 图标上的那个堆叠数字。

> `Core.Unit.BattleProperty` 另有一批"关键词计数"buff（`propertyBuffDataList`），
> 用 `RegisterPropertyBuff(value, buffId)` 登记：治疗 10004 / 薪水 10005 / **标记 10006** /
> 反击 10008 / 改造 10009 / 唯一 10012 / 通缉 10013 / 能量 10014。它们的计数来自
> `Hero` 的对应字段（如 `Hero.MarkNum`=标记层数），也会同时体现在 `Hero.Buffs` 的 `Progress` 里。

---

## 2. `OnHitExtraDamage` 只是减伤，不是易伤

`BattleProperty` 有 `public int OnHitExtraDamage => _onHitExtraDamage;`，但其 setter 是
`_onHitExtraDamage = Mathf.Min(0, _onHitExtraDamage + changeDamage);`——**永远 ≤ 0**，
即它只表示"受到伤害减少 X"（护盾/减伤的聚合），**不含**易伤(+)。
所以"受到伤害+1"这类效果**不在**这个聚合里，只能逐 buff 从 `Hero.Buffs` 读。

---

## 3. 哪些 buff 改"受到伤害"（全量提取）

`buffcat/` 把 465 条 buff 的简体说明拉出来，正则匹配"受到(的)?…伤害 ±N"，命中 **18 条**
（外加"降为0"的免疫类 1 条不带符号，单独识别）。按语义分三类：

### 3.1 通用易伤（+，进入普通投牌结算）

| buffId | 名称 | 每单位 | 叠层 | 说明原文 |
|---|---|---:|:--:|---|
| 10006 | 标记 | +1 | 每层 | 受到伤害+1 |
| 2000801 | 狂暴 | +1 | 每层 | 攻击力+3，受到伤害+1（`BuffType=Battle`） |
| 1140101 | 宿命回响 | +1 | — | 受到的伤害+1（蓝海晴 PVP 技 11401，2 回合） |
| 1140102 | 虚弱印记 | +1 | — | 受到的伤害+1（蓝海晴 PVE 技 11402，2 回合） |
| 4000401 | 霉运 | +2 | — | 下次受到伤害+2 |
| 3200501 | （易伤） | +2 | — | 下次受到伤害+2 |
| 10671302 | 真凶 | +2 | — | 攻+2/移+2/出牌+1，受到伤害+2 |
| 10141101 | 脆弱中枢 | +1 | — | 每次受到伤害，本轮受到伤害+1（条件性，mod 近似取 +1） |

### 3.2 通用减伤（−）

| buffId | 名称 | 每单位 | 叠层 | 说明原文 |
|---|---|---:|:--:|---|
| 4000301 | 好运 | −2 | — | 下次受到伤害−2 |
| 1261101 | 湖沼之王 | −1 | 每层 | 每层攻+1、受到伤害−1 |
| 5006001 | 逆鳞 | −2 | 每层 | 每层攻+2、受到伤害−2 |
| 10261101 | 逆鳞 | −2 | 每层 | 同上（另一 id） |
| 5007002 | 大铜锣 | −1 | — | 攻+1、受到伤害−1 |
| 10531201 | 灰烬 | −1 | 每层 | 每层受到伤害−1 |
| 10551101 | 稳定中枢 | −1 | 每层 | 每层受到伤害−1 |

### 3.3 免疫/护盾（这一击伤害归零）

| buffId | 名称 | 说明原文 |
|---|---|---|
| 1071101 | 护盾 | 下次受到的伤害−99（鼠鼠护盾，实际=挡下一击） |
| 10331101 | 深度改造 | 无法被攻击或选中，受到伤害降低为 0 |

### 3.4 语境限定（**不进**投牌结算，只提示）

| buffId | 名称 | 说明原文 |
|---|---|---|
| 1221202 | 精准打击 | 受到【轨道轰炸】伤害+1（只对该技能） |
| 1291202 | 渊蚀印记 | 每层使自身受到**来自魔渊触须和赛克斯**的伤害+1（只对该来源） |

---

## 4. 只取"受伤侧"，不重复加攻击

多数易伤 buff 同时给攻击加成（狂暴 攻+3、逆鳞 攻+2、湖沼 攻+1、真凶 攻+2…）。
这些**攻击力加成一般已并入服务器下发的 `BattleRole.Atk`**（客户端拿到的就是最终攻击值）。
若 mod 再把攻击加成加一遍就会**重复计数**。所以修正表**只取"受到伤害 ±N"那一项**，攻击项一律不碰。

而"受到伤害 ±N"在回放对拍里正是那 +1~+3 的**残差**（`max(1,Atk-Def)` 之外的部分），
说明它是**结算时另加**、没并进 `Atk`——所以必须、也只需要，由本表补上。

> 用户点名的两个例子都在表里：**标记**（`10006`，"标记—受到伤害+1"，怪物身上也读得到）；
> **卡牌狂暴**（卡牌 `20008` → buff `2000801`，"让自己受到伤害+1、能叠很多层"）。
> "怪物给的 buff / 玩家自身 buff"统一走同一条路——都是被击中者 `Hero.Buffs` 里的一条。

---

## 5. mod 的数据链

```
BattleUpdate(Battle b)                        // 攻=b.Attacker, 守=b.Defender（被击中者）
  → Players.BuffsOf(b.Defender.PlayerId)      // 读 Hero.Buffs：List<BuffOnUnit{BuffId,Layers,KeepRound}>
  → CombatMath.EvaluateTargetBuffs(buffs, CombatMath.DefaultBuffTable())
        → BuffAdjustment{ Delta, Immune, Applied[], ContextOnly[] }
  → CombatMath.Defend/Dodge/Attack(..., damageAdjust: Delta, immune: Immune)
        // 逐样本：dmg = max(1,Atk-Def)；dmg += Delta；if(immune) dmg = 0；再比 HP 求击倒/击杀
  → 读数里追加 🧬 行：显示计入了哪些 buff、净受伤 ±N
```

- `Layers` 取 `Buff.Progress`；无进度字段的 buff 兜底当 1 层。标记也可用
  `BattleProperty.MarkCount.Value`（=`Hero.MarkNum`）交叉校验。
- 攻、守两个视角命中的都是**同一个被击中者=防守方**，故一次 `BuffAdjOf(def)` 供两块复用。
- 表里没有的 buff 一律忽略；语境限定 buff 只进 `ContextOnly`（展示），不改数值。

---

## 6. 复现 / 扩充

```powershell
# 重新提取 buff 目录与"受到伤害±N"清单（输出 UTF-8 到 buffcat/out/）
cd C:\src\Study\astralparty\buffcat ; dotnet run -c Release
#   out\buff-catalog.tsv          全 465 条 id/类型/名/说明
#   out\buff-hurt-modifiers.tsv   命中"受到伤害±N"的条目
#   out\card-kuangbao.tsv         名字含"狂暴"的卡牌（20008）
```

要新增/修正一条 buff 规则，改 `CombatMath.DefaultBuffTable()`（每条都能对到一句明确的游戏说明），
并在 `CombatMathTests` 加一条断言。**不硬编码没有说明依据的数值。**

---

## 7. 局限（诚实标注）

- 服务器权威：客户端**没有**伤害结算公式，本表是"按说明文本建模 + 回放残差佐证"，非逐帧证明。
- `Progress`=层数是**强推断**（`IsShowIconProgressZero` 语义 + SDK 编译期强类型绑定通过），
  真机可用 `🧬` 行与实际掉血交叉验证。
- 条件性 buff（脆弱中枢"每次受伤再+1"、只在特定回合/来源生效者）只做**近似**或**排除**，已在表里标注。
- 攻方的条件性攻击加成若**未**并入 `BattleRole.Atk`，仍可能有 ±1 偏差——`ShowSpecialMechanicNote`
  的轻量脚注即为此保留。
