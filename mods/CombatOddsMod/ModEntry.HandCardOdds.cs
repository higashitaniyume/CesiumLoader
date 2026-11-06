using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
        private static HandCardOddsOverlay _handOdds;

        private sealed class HandCardOddsOverlay : IDisposable
        {
            private const string BadgeName = "combat_odds_hand_preview";
            private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            private static readonly FieldInfo WindowField = typeof(UIManager).GetField("_cachedFightWindow", PrivateInstance);
            private static readonly FieldInfo CardsField = typeof(FightWindow).GetField("cardItemList", PrivateInstance);
            private static readonly FieldInfo CardActionField = typeof(FightWindow).GetField("useBattleCardSn", PrivateInstance);
            private readonly List<GTextField> _badges = new List<GTextField>();
            private readonly HashSet<GTextField> _current = new HashSet<GTextField>();
            private readonly Dictionary<int, string> _textByCard = new Dictionary<int, string>();
            private string _lastError;

            public void Start() => Timers.inst.Add(0.15f, 0, Refresh);

            public void Dispose()
            {
                Timers.inst.Remove(Refresh);
                _current.Clear();
                RemoveUnused();
            }

            private void Refresh(object unused)
            {
                _current.Clear();
                try
                {
                    UpdateCards();
                    _lastError = null;
                }
                catch (Exception e)
                {
                    _current.Clear();
                    if (_lastError != e.Message)
                        SdkLog.Warn("CombatOdds", "手牌概率刷新失败: " + e.Message);
                    _lastError = e.Message;
                }
                finally { RemoveUnused(); }
            }

            private void UpdateCards()
            {
                if (_cfg == null || !_cfg.Enabled || !_cfg.InGameOverlay) return;
                var fight = SimpleSingletonProvider<GameLogicManager>.inst?.fight;
                var live = fight?.battleFightData;
                if (fight == null || !fight.fightStatus || live == null || live.isEnd) return;
                var atk = live.attackerInfo;
                var def = live.defenderInfo;
                if (atk == null || def == null) return;
                bool attacking = Players.IsSelf(atk.PlayerId);
                if (!attacking && !Players.IsSelf(def.PlayerId)) return;
                var self = attacking ? atk : def;
                if (live.isFinishCard != null && live.isFinishCard.TryGetValue(self.PlayerId, out bool ready) && ready) return;

                var ui = UIManager.inst;
                if (ui == null) return;
                var window = WindowField != null ? WindowField.GetValue(ui) as FightWindow : ui.Fight;
                if (window == null || !window.isShowing) return;
                if (CardActionField?.GetValue(window) is long actionSn && actionSn == 0) return;
                var cards = CardsField?.GetValue(window) as List<UIHandCard_Button_Card>;
                if (cards == null || cards.Count == 0) return;

                var atkRolled = _lastBattle?.BattleId == live.battleId && GameEvents.AttackerPoint >= 0;
                if (attacking && atkRolled) return;
                bool powerfulAttackActive = !atkRolled && CardPointHelper.HasPlayedCard(atk, CombatMath.PowerfulAttackCardId);
                int hp = HpOf(def.PlayerId);
                if (hp <= 0) return;

                CardPointHelper.TryNarrowPlayedRange(atk, true, out int minAtk, out int maxAtk);
                CardPointHelper.TryNarrowPlayedRange(def, false, out int minDef, out int maxDef);
                int bonnie = BonnieMarkedMonsterBonus(atk, def);
                bool handDefIsMonster = IsMonster(def.PlayerId);
                var adjust = BuffAdjOf(def.PlayerId, CombatMath.DamageContext.NormalBattle, 0, handDefIsMonster, HeroIdOf(atk));
                int handAdjustDelta = adjust.Delta + SeaxEntryMarkExtra(def.PlayerId, HeroIdOf(atk), handDefIsMonster, adjust.Immune, live.battleId, adjust);
                int faces = JudgeFaces();
                int aFlat = atk.Atk + bonnie;
                int[] aDice = Array.Empty<int>();
                if (!atkRolled)
                    CombatMath.RangeWithJudgeDice(minAtk + bonnie, maxAtk + bonnie, out aFlat, out aDice, faces);
                CombatMath.RangeWithJudgeDice(minDef, maxDef, out int dFlat, out int[] dDice, faces);

                int fontSize = _cfg.HandOddsFontSize;
                Color color = attacking ? new Color32(181, 26, 36, 255) : new Color32(16, 82, 159, 255);
                _textByCard.Clear();
                foreach (var item in cards)
                {
                    if (item == null || item.isDisposed || !item.visible || !item.touchable || !item.draggable) continue;
                    var card = item.com_Card;
                    var data = item.CardData;
                    var config = data?.Config;
                    if (card == null || card.isDisposed || !card.visible || config == null ||
                        self.UseCards.Contains(data.Guid) ||
                        config.EffectType != (attacking ? EffectType.Attack : EffectType.Defense)) continue;
                    var range = CardPointHelper.GetPointRange(config.Id);
                    if (!range.IsValid || range.IsAttack != attacking) continue;

                    if (!_textByCard.TryGetValue(config.Id, out var label))
                    {
                        var odds = CombatMath.PreviewCardOdds(attacking, range.Min, range.Max, aFlat, aDice, dFlat, dDice,
                            hp, handAdjustDelta, adjust.Immune, adjust.MaxDamagePerHit, config.Id, def.InitDef, powerfulAttackActive);
                        label = PctPlain(odds);
                        _textByCard.Add(config.Id, label);
                    }

                    var badge = card.GetChild(BadgeName) as GTextField;
                    if (badge == null)
                    {
                        badge = new GTextField
                        {
                            name = BadgeName, touchable = false, autoSize = AutoSizeType.Both,
                            stroke = 2f, strokeColor = UnityEngine.Color.white
                        };
                        badge.textFormat.bold = true;
                        card.AddChild(badge);
                        _badges.Add(badge);
                    }
                    var format = badge.textFormat;
                    if (format.size != fontSize || !format.color.Equals(color))
                    {
                        format.size = fontSize;
                        format.color = color;
                        badge.textFormat = format;
                    }
                    if (badge.text != label) badge.text = label;
                    badge.SetXY((card.width - badge.width) * 0.5f + _cfg.HandOddsOffsetX,
                        -badge.height - 4f + _cfg.HandOddsOffsetY);
                    _current.Add(badge);
                }
            }

            private void RemoveUnused()
            {
                for (int i = _badges.Count - 1; i >= 0; i--)
                {
                    var badge = _badges[i];
                    if (_current.Contains(badge)) continue;
                    if (!badge.isDisposed) badge.Dispose();
                    _badges.RemoveAt(i);
                }
            }
        }
    }
}
