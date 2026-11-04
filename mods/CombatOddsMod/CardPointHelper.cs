using System;

using CesiumLoader.SDK.Gameplay;


namespace CombatOddsMod
{
    internal static class CardPointHelper
    {
        public struct PointRange
        {
            public int Min;
            public int Max;
            public bool IsAttack;
            public bool IsValid;

        }

        public static PointRange GetPointRange(int cardId)
        {
            if (cardId < 10001) return default;

            // 仅处理范围已知的普通攻防牌，其他牌继续使用服务器范围。
            switch (cardId)
            {
                case 10001: return new PointRange { Min = 1, Max = 3, IsAttack = true, IsValid = true };
                case 10002: return new PointRange { Min = 1, Max = 3, IsAttack = false, IsValid = true };
                case 10003: return new PointRange { Min = 1, Max = 6, IsAttack = true, IsValid = true };
                case 10004: return new PointRange { Min = 1, Max = 6, IsAttack = false, IsValid = true };
                case 10005: return new PointRange { Min = 1, Max = 10, IsAttack = true, IsValid = true };
                case 10006: return new PointRange { Min = 1, Max = 10, IsAttack = false, IsValid = true };
                case 10007: return new PointRange { Min = 1, Max = 20, IsAttack = true, IsValid = true };
                default: return default;
            }
        }

        internal static int ResolvePlayedCardId(long playerId, int cardGuid)
        {
            // Use the same retained hand-card table as the game's GetCardCost.
            var cards = Players.Get(playerId)?.cardContainer?._HandCardData;
            return cards != null && cards.TryGetValue(cardGuid, out var card) && card != null
                ? card.CardId
                : 0;
        }

        // Match by ID and occurrence, never by the position of the server's bonus list.
        public static int TakeBonus(party.model.BattleRole role, int cardId, bool[] used)
        {
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i] && role.CardCombatBonus[i].CardId == cardId)
                {
                    used[i] = true;
                    return i;
                }
            }
            return -1;
        }

        // Replace only the randomness that can be attributed to recognised cards.
        // An unexpected range or unmatched bonus leaves the server bounds intact.
        public static bool TryNarrowPlayedRange(party.model.BattleRole role, bool attack, out int min, out int max)
        {
            min = attack ? role.MinAtk : role.MinDef;
            max = attack ? role.MaxAtk : role.MaxDef;
            int count = role.CardCombatBonus.Count;
            if (count == 0 || count > role.UseCards.Count) return false;
            var used = new bool[count];
            long width = 0, lowDelta = 0, highDelta = 0;
            int matched = 0;
            for (int i = 0; i < role.UseCards.Count; i++)
            {
                // Once all bonuses arrive, the result list supplies the real card IDs.
                int id = count == role.UseCards.Count
                    ? role.CardCombatBonus[i].CardId
                    : ResolvePlayedCardId(role.PlayerId, role.UseCards[i]);
                var range = GetPointRange(id);
                if (!range.IsValid || range.IsAttack != attack) return false;
                width += range.Max - range.Min;
                int index = TakeBonus(role, id, used);
                if (index < 0) continue;
                int bonus = role.CardCombatBonus[index].CardBonus;
                if (bonus < range.Min || bonus > range.Max) return false;
                matched++;
                lowDelta += bonus - range.Min;
                highDelta += range.Max - bonus;
            }
            if (matched != count || (long)max - min != width) return false;
            min = (int)(min + lowDelta);
            max = (int)(max - highDelta);
            return true;
        }
    }
}
