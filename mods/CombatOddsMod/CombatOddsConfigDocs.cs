using CesiumLoader.SDK.Configuration;

namespace CombatOddsMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class CombatOddsConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("Enabled", D(
                "总开关：关闭后不显示胜率读数与覆盖层。",
                "總開關：關閉後不顯示勝率讀數與覆蓋層。",
                "Master switch: hides all odds readouts and overlays when off.",
                "全体スイッチ：オフにすると勝率表示とオーバーレイを出しません。"));
            changed |= config.Describe("ShowForOthers", D(
                "也为队友或观战对象的战斗显示读数。",
                "也為隊友或觀戰對象的戰鬥顯示讀數。",
                "Also show odds for teammates' and spectated players' battles.",
                "味方や観戦対象の戦闘でも勝率を表示します。"));
            changed |= config.Describe("PopupNotification", D(
                "用游戏内通知显示战斗结论。",
                "用遊戲內通知顯示戰鬥結論。",
                "Show battle conclusions as in-game notifications.",
                "戦闘結果をゲーム内通知で表示します。"));
            changed |= config.Describe("NotificationTtl", D(
                "通知停留的秒数，默认 8。",
                "通知停留的秒數，預設 8。",
                "How long notifications stay visible, in seconds (default 8).",
                "通知を表示しておく秒数（既定 8）。"));
            changed |= config.Describe("DodgeKeepsBaseDefense", D(
                "闪避失败时仍保留基础防御减伤；默认关闭，按承受全额攻击计算。",
                "閃避失敗時仍保留基礎防禦減傷；預設關閉，按承受完整攻擊計算。",
                "Keep base defense reduction even when dodge fails; off by default for a conservative estimate.",
                "回避失敗時も基礎防御の軽減を残します（既定オフ、被弾全額で計算）。"));
            changed |= config.Describe("ColorHighlight", D(
                "用颜色高亮结论与关键数字。",
                "用顏色突顯結論與關鍵數字。",
                "Highlight conclusions and key numbers with color.",
                "結果と重要な数値を色で強調します。"));
            changed |= config.Describe("ShowBuffBreakdown", D(
                "显示目标身上的受伤/减伤 Buff 明细，并计入结果。",
                "顯示目標身上的受傷/減傷 Buff 明細，並計入結果。",
                "Show incoming damage/reduction buffs on the target and include them in the result.",
                "対象のダメージ増減バフを表示し、計算に含めます。"));
            changed |= config.Describe("ShowMosesDodge", D(
                "玩摩西时，把【精密技巧】层数对应的闪避最小骰点计入闪避概率，并在战斗面板显示闪避骰范围。",
                "玩摩西時，把【精密技巧】層數對應的閃避最小骰點計入閃避機率，並在戰鬥面板顯示閃避骰範圍。",
                "When playing Moses, include the dodge-die floor from Precision Technique in the dodge odds and show the dodge range on the battle panel.",
                "モーゼス使用時、【精密無比】のスタック数による回避ダイスの下限を回避確率に反映し、戦闘パネルに回避ダイスの範囲を表示します。"));
            changed |= config.Describe("InGameOverlay", D(
                "在战斗窗口显示 FairyGUI 覆盖层；失败时自动降级到通知或控制台。",
                "在戰鬥視窗顯示 FairyGUI 覆蓋層；失敗時自動降級到通知或主控台。",
                "Draw a FairyGUI overlay on the battle window; falls back to notifications or the console on failure.",
                "戦闘ウィンドウに FairyGUI オーバーレイを表示します（失敗時は通知やコンソールへ自動降格）。"));
            changed |= config.Describe("OverlayFontScale", D(
                "覆盖层字号缩放，1.0 为默认，建议 0.8–1.5。",
                "覆蓋層字級縮放，1.0 為預設，建議 0.8–1.5。",
                "Overlay font scale (1.0 = default; 0.8–1.5 recommended).",
                "オーバーレイの文字倍率（1.0 が既定、0.8～1.5 推奨）。"));
            changed |= config.Describe("OverlayAnchor", D(
                "覆盖层锚点：left-center / top-left / top-center / top-right / right-center。",
                "覆蓋層錨點：left-center / top-left / top-center / top-right / right-center。",
                "Overlay anchor: left-center, top-left, top-center, top-right, or right-center.",
                "オーバーレイの基準位置：left-center / top-left / top-center / top-right / right-center。"));
            changed |= config.Describe("OverlayOffsetX", D(
                "覆盖层水平微调（像素，正数右移）。",
                "覆蓋層水平微調（像素，正數右移）。",
                "Overlay horizontal offset in pixels (positive moves right).",
                "オーバーレイの水平方向の微調整（ピクセル、正で右へ）。"));
            changed |= config.Describe("OverlayOffsetY", D(
                "覆盖层垂直微调（像素，正数下移）。",
                "覆蓋層垂直微調（像素，正數下移）。",
                "Overlay vertical offset in pixels (positive moves down).",
                "オーバーレイの垂直方向の微調整（ピクセル、正で下へ）。"));
            changed |= config.Describe("ShowRelicAtkBonus", D(
                "在主 HUD 的攻击力后显示筹码提供的攻击加成。",
                "在主 HUD 的攻擊力後顯示籌碼提供的攻擊加成。",
                "Append the attack bonus from chips after the main HUD attack value.",
                "メイン HUD の攻撃力の後ろにチップによる攻撃ボーナスを表示します。"));
            changed |= config.Describe("ShowBoardPlayerAttrs", D(
                "在棋盘上每个玩家头顶显示实时攻击/防御。",
                "在棋盤上每個玩家頭頂顯示即時攻擊/防禦。",
                "Show each player's current attack/defense above their board avatar.",
                "盤面上の各プレイヤーの頭上に現在の攻撃力/防御力を表示します。"));
            changed |= config.Describe("LogAtkBonusDetail", D(
                "把攻击力加成明细写入加载器日志。",
                "把攻擊力加成明細寫入載入器日誌。",
                "Write detailed attack bonus calculations to the loader log.",
                "攻撃力ボーナスの内訳をローダーログへ出力します。"));
            changed |= config.Describe("LogBattleOdds", D(
                "把每次战斗读数写入加载器日志；默认关闭。",
                "把每次戰鬥讀數寫入載入器日誌；預設關閉。",
                "Write each battle odds readout to the loader log (off by default).",
                "各戦闘の勝率表示をローダーログへ出力します（既定オフ）。"));
            changed |= config.Describe("ShowSkillCooldown", D(
                "在玩家头像上方显示主动技能冷却。",
                "在玩家頭像上方顯示主動技能冷卻。",
                "Show active skill cooldowns above player avatars.",
                "プレイヤーのアイコンの上にアクティブスキルのクールダウンを表示します。"));
            changed |= config.Describe("LogCdDetail", D(
                "把技能冷却数据与最终显示值写入日志。",
                "把技能冷卻資料與最終顯示值寫入日誌。",
                "Write skill cooldown data and the final displayed values to the log.",
                "スキルクールダウンのデータと最終表示値をログへ出力します。"));
            changed |= config.Describe("ShowHandCount", D(
                "在技能冷却旁显示玩家剩余手牌数。",
                "在技能冷卻旁顯示玩家剩餘手牌數。",
                "Show each player's remaining hand count next to skill cooldowns.",
                "スキルクールダウンの横に残り手札数を表示します。"));
            changed |= config.Describe("LogHandDetail", D(
                "把手牌数变化写入日志。",
                "把手牌數變化寫入日誌。",
                "Write hand-count changes to the log.",
                "手札数の変化をログへ出力します。"));
            changed |= config.Describe("ShowMegasDamage", D(
                "玩梅加斯时显示轨道轰炸的理论伤害。",
                "玩梅加斯時顯示軌道轟炸的理論傷害。",
                "Show theoretical orbital bombardment damage while playing Megas.",
                "メガス使用時に軌道爆撃の理論ダメージを表示します。"));
            changed |= config.Describe("ShowMonsterSkillCooldown", D(
                "在怪物信息条旁显示主动技能冷却。",
                "在怪物資訊條旁顯示主動技能冷卻。",
                "Show active skill cooldowns next to monster info bars.",
                "モンスター情報バーの横にアクティブスキルのクールダウンを表示します。"));
            changed |= config.Describe("ShowMonsterCdForAllMonsters", D(
                "连普通/精英怪也显示技能冷却；默认只显示 BOSS 与带主动技能的怪。",
                "連普通/精英怪也顯示技能冷卻；預設只顯示 BOSS 與帶主動技能的怪。",
                "Also show cooldowns for normal/elite monsters; by default only bosses and monsters with active skills.",
                "通常/エリートモンスターにもクールダウンを表示します（既定はボスとアクティブスキル持ちのみ）。"));
            changed |= config.Describe("MonsterCdOffsetX", D(
                "怪物冷却文本的水平微调（像素，正数右移）。",
                "怪物冷卻文字的水平微調（像素，正數右移）。",
                "Horizontal offset of monster cooldown text in pixels (positive moves right).",
                "モンスターのクールダウン表示の水平微調整（ピクセル、正で右へ）。"));
            changed |= config.Describe("MonsterCdOffsetY", D(
                "怪物冷却文本的垂直微调（像素，正数下移）。",
                "怪物冷卻文字的垂直微調（像素，正數下移）。",
                "Vertical offset of monster cooldown text in pixels (positive moves down).",
                "モンスターのクールダウン表示の垂直微調整（ピクセル、正で下へ）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
