using Xunit;
using HandViewerMod;

public class HandStripTests
{
    [Fact]
    public void DifferentUidsOfSameCardAreGroupedInFirstAppearanceOrder()
    {
        var hand = new PlayerHand { handKnown = true };
        hand.cards.Add(new HandCard { cardId = 2, cardUid = 1 });
        hand.cards.Add(new HandCard { cardId = 3, cardUid = 2 });
        hand.cards.Add(new HandCard { cardId = 2, cardUid = 3 });
        var groups = HandStripModel.Group(hand);
        Assert.Equal(2, groups.Count); Assert.Equal(2, groups[0].Card.cardId);
        Assert.Equal(2, groups[0].Count); Assert.Equal(3, groups[1].Card.cardId);
    }
    [Fact]
    public void MeaningfulCardStatesAreNotMerged()
    {
        var hand = new PlayerHand { handKnown = true };
        hand.cards.Add(new HandCard { cardId = 2 });
        hand.cards.Add(new HandCard { cardId = 2, purifyNum = 1 });
        hand.cards.Add(new HandCard { cardId = 2, isTemp = true });
        hand.cards.Add(new HandCard { cardId = 2, battleCost = 1 });
        Assert.Equal(4, HandStripModel.Group(hand).Count);
    }
    [Fact]
    public void UnknownAndEmptyHaveDifferentSignatures()
    {
        Assert.NotEqual(HandStripModel.Signature(new PlayerHand()),
            HandStripModel.Signature(new PlayerHand { handKnown = true }));
        Assert.Empty(HandStripModel.Group(new PlayerHand { cards = { new HandCard() } }));
    }
    [Fact]
    public void OpeningRefreshesClosingDoesNotAndIdentityResets()
    {
        var model = new HandStripModel(); model.Bind(12);
        Assert.True(model.Toggle()); Assert.False(model.Toggle()); Assert.True(model.Toggle());
        model.Bind(12); Assert.True(model.Open);
        model.Bind(13); Assert.False(model.Open);
        model.Bind(0); Assert.False(model.Toggle());
    }
    [Theory]
    [InlineData(7, false, 7)]
    [InlineData(7, true, 4)]
    [InlineData(6, true, 3)]
    [InlineData(1, true, 1)]
    [InlineData(0, true, 1)]
    public void RowLayoutSupportsSingleAndDoubleRows(int count, bool doubleRow, int expected)
    {
        int columns = HandStripModel.CardsPerRow(count, doubleRow);
        Assert.Equal(expected, columns);
        if (count > 0) Assert.True((count - 1) / columns < (doubleRow ? 2 : 1));
    }
    [Fact]
    public void AutomaticallyCollapsesAtConfiguredDeadlineAndReopenRestartsTimer()
    {
        var model = new HandStripModel(); model.Bind(1);
        model.Toggle(100, 5); model.Tick(5099); Assert.True(model.Open);
        model.Tick(5100); Assert.False(model.Open);
        model.Toggle(6000, 5); model.Tick(10999); Assert.True(model.Open);
        model.Tick(11000); Assert.False(model.Open);
    }
    [Fact]
    public void ZeroSecondsKeepsHandOpenAndManualCloseClearsPreviousTimer()
    {
        var model = new HandStripModel(); model.Bind(1);
        model.Toggle(0, 5); model.Toggle(1000, 5); model.Toggle(2000, 0);
        model.Tick(long.MaxValue); Assert.True(model.Open);
        model.Bind(2); model.Toggle(0, 0); model.Tick(long.MaxValue); Assert.True(model.Open);
    }
    [Fact]
    public void CollapseDeadlinesAreIndependentForEachPlayer()
    {
        var first = new HandStripModel(); first.Bind(1); first.Toggle(0, 5);
        var second = new HandStripModel(); second.Bind(2); second.Toggle(2000, 5);
        first.Tick(5000); second.Tick(5000);
        Assert.False(first.Open); Assert.True(second.Open);
        second.Tick(7000); Assert.False(second.Open);
    }
    [Fact]
    public void FourPlayersStayIndependent()
    {
        var models = new HandStripModel[4];
        for (int i = 0; i < 4; i++) { models[i] = new HandStripModel(); models[i].Bind(i + 1); Assert.True(models[i].Toggle()); }
        models[1].Toggle();
        Assert.True(models[0].Open); Assert.False(models[1].Open); Assert.True(models[2].Open); Assert.True(models[3].Open);
    }
}
