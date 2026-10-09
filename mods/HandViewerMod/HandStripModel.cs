using System.Collections.Generic;

namespace HandViewerMod
{
    public sealed class HandCardGroup
    {
        public HandCard Card;
        public int Count;
    }

    public sealed class HandStripModel
    {
        public static int CardsPerRow(int count, bool doubleRow)
            => count <= 0 ? 1 : (count - 1) / (doubleRow ? 2 : 1) + 1;
        public long PlayerId { get; private set; }
        public bool Open { get; private set; }
        public void Bind(long playerId)
        {
            if (PlayerId == playerId) return;
            PlayerId = playerId; Open = false;
        }
        public bool Toggle()
        {
            if (PlayerId == 0) return false;
            Open = !Open;
            return Open;
        }
        public static List<HandCardGroup> Group(PlayerHand hand)
        {
            var result = new List<HandCardGroup>();
            if (hand == null || !hand.handKnown || hand.cards == null) return result;
            foreach (var card in hand.cards)
            {
                HandCardGroup found = null;
                foreach (var group in result)
                    if (group.Card.cardId == card.cardId && group.Card.purifyNum == card.purifyNum
                        && group.Card.isTemp == card.isTemp && group.Card.battleCost == card.battleCost)
                    { found = group; break; }
                if (found == null) result.Add(new HandCardGroup { Card = card, Count = 1 });
                else found.Count++;
            }
            return result;
        }
        public static string Signature(PlayerHand hand)
        {
            if (hand == null) return "waiting";
            if (!hand.handKnown) return "unknown:" + hand.handReason;
            string value = "known";
            foreach (var group in Group(hand))
                value += "/" + group.Card.cardId + ":" + group.Card.purifyNum + ":" + group.Card.isTemp
                    + ":" + group.Card.battleCost + ":" + group.Count;
            return value;
        }
    }
}
