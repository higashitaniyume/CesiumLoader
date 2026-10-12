using System;
using System.Collections.Generic;
using System.Linq;
using AutoThanksMod;
using CesiumLoader.SDK.Manifests;
using FairyGUI;
using GameLogic;
using UI;
using Xunit;

namespace AutoThanksMod.IntegrationTests;

public sealed class SettlementLikesIntegrationTests
{
    private const long Start = 100 * ThanksPolicy.TicksPerSecond;
    private const long Interval = 500 * 10000L;

    private sealed class Fixture
    {
        public readonly SettlementLikes Likes = new();
        public readonly BattleSettlement Settlement = new();
        public readonly BattleLogic Battle = new();
        public readonly BattleSettlementPanel Panel;
        public readonly List<long> Requests = new();
        public readonly List<long> Chats = new();
        public bool Success = true;
        public bool Throw;
        public UIBattleSettlementPanel UI => Panel.UIObject;
        public Fixture(params long[] ids)
        {
            Permissions.Allowed = true;
            Permissions.Checks = 0;
            Panel = new BattleSettlementPanel(Settlement, new UIBattleSettlementPanel());
            SetPlayers(ids);
        }
        public void SetPlayers(params long[] ids)
        {
            Settlement.Players = ids.Select(id => new PlayerSettlement
            {
                PlayerData = new BattlePlayerData
                {
                    player = new RoomPlayer { Id = id, TeamId = id == 2 ? 10 : 20 }
                }
            }).ToList();
            Battle.Players = Settlement.Players.Select(x => x.PlayerData).ToList();
            BindRows();
        }
        public void BindRows()
        {
            var rows = UI.com_ShowTime.Rows;
            for (int i = 0; i < rows.Length; i++)
            {
                var player = Settlement.GetPlayerSettlement(i);
                var row = rows[i];
                row.visible = player != null;
                var button = row.btn_Praise;
                button.visible = true;
                button.isFinish.selectedIndex = 0;
                button.Cut_in.playing = false;
                // Equivalent to BattleSettlementPanel.RefreshPlayerItem's original click closure.
                button.onClick.Set((EventCallback0)(() =>
                {
                    if (button.isFinish.selectedIndex != 1)
                    {
                        button.onClick.Retain();
                        button.Cut_in.Play(() => button.isFinish.selectedIndex = 1);
                        Send(player.PlayerData.player.Id);
                    }
                }));
            }
        }
        public void BindWinner(long id)
        {
            Settlement.IsPVP = true;
            UI.step.selectedIndex = 1;
            var button = UI.com_Winner.btn_Praise;
            button.grayed = false;
            button.Cut_in.playing = false;
            // Equivalent to BattleSettlementPanel.ShowWinner's original click closure.
            button.onClick.Set((EventCallback0)(() =>
            {
                button.onClick.Retain();
                button.Cut_in.Play(() => button.grayed = true);
                Send(id);
            }));
        }
        private void Send(long id)
        {
            Requests.Add(id);
            if (Throw) throw new InvalidOperationException("Simulated RPC handler failure");
            if (Success) Chats.Add(id); // Original TryPraise calls ChatPraise only on RPC success.
        }
        public bool Tick(long ticks, out long target, int interval = 500) =>
            Likes.Tick(Panel, Battle, 1, ticks, interval, out target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WinnerPageManualLikeIsNeverRepeatedOnFourPlayerPage(bool animationCompleted)
    {
        var f = new Fixture(1, 2, 3, 4);
        f.BindWinner(2);
        f.UI.com_Winner.btn_Praise.onClick.Call();
        if (animationCompleted) f.UI.com_Winner.btn_Praise.Cut_in.Complete();
        Assert.Equal(animationCompleted, f.UI.com_Winner.btn_Praise.grayed);
        Assert.Equal(!animationCompleted, f.UI.com_Winner.btn_Praise.Cut_in.playing);
        Assert.False(f.Tick(Start, out long target));
        Assert.Equal(0, target);
        f.UI.step.selectedIndex = 2;
        Assert.False(f.Tick(Start + Interval - 1, out _));
        Assert.True(f.Tick(Start + Interval, out target));
        Assert.Equal(3, target);
        Assert.Equal(1, f.UI.com_ShowTime.com_Player2.btn_Praise.isFinish.selectedIndex);
        // Retain must suppress the original handler, even though it is still registered.
        f.UI.com_ShowTime.com_Player2.btn_Praise.onClick.Call();
        Assert.True(f.Tick(Start + 2 * Interval, out target));
        Assert.Equal(4, target);
        Assert.False(f.Tick(Start + 100 * Interval, out _));
        Assert.Equal(new long[] { 2, 3, 4 }, f.Requests);
        Assert.Equal(f.Requests, f.Chats);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleWinnersIncludingBotDoNotLetStaleHiddenWinnerButtonConsumeHuman(bool staleAnimation)
    {
        var f = new Fixture(1, 2, 3, 4);
        f.BindWinner(2);
        f.Battle.Players[2].player.IsBot = true;
        f.Battle.Players[2].player.TeamId = f.Settlement.WinTeamId;
        f.UI.com_Winner.visible = false;
        f.UI.com_Winner.btn_Praise.grayed = !staleAnimation;
        f.UI.com_Winner.btn_Praise.Cut_in.playing = staleAnimation;
        f.UI.step.selectedIndex = 2;
        Assert.True(f.Tick(Start, out long target));
        Assert.Equal(2, target);
        Assert.Equal(new long[] { 2 }, f.Requests);
        Assert.True(f.Tick(Start + Interval, out target));
        Assert.Equal(4, target);
        Assert.Equal(new long[] { 2, 4 }, f.Requests);
        Assert.Equal(f.Requests, f.Chats);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualRowAnimationOrFinishedControllerPreventsRepeatedRequests(bool animationCompleted)
    {
        var f = new Fixture(1, 2, 3, 4);
        var button = f.UI.com_ShowTime.com_Player2.btn_Praise;
        button.onClick.Call();
        if (animationCompleted) button.Cut_in.Complete();
        Assert.False(f.Tick(Start, out _));
        Assert.True(f.Tick(Start + Interval, out long target));
        Assert.Equal(3, target);
        button.Cut_in.Complete();
        Assert.True(f.Tick(Start + 2 * Interval, out target));
        Assert.Equal(4, target);
        button.onClick.Call();
        Assert.Equal(new long[] { 2, 3, 4 }, f.Requests);
        Assert.Equal(f.Requests, f.Chats);
    }

    [Fact]
    public void ManualLaterRowDelaysEarlierAutomaticRowWithoutRefreshingDeadlineEachTick()
    {
        var f = new Fixture(1, 2, 3, 4);
        f.UI.com_ShowTime.com_Player4.btn_Praise.onClick.Call();
        Assert.False(f.Tick(Start, out _));
        Assert.False(f.Tick(Start + Interval - 1, out _));
        Assert.True(f.Tick(Start + Interval, out long target));
        Assert.Equal(2, target);
        Assert.True(f.Tick(Start + 2 * Interval, out target));
        Assert.Equal(3, target);
        Assert.Equal(new long[] { 4, 2, 3 }, f.Requests);
    }

    [Fact]
    public void RealEventBridgeLocksDuringAnimationAndChatRequiresSuccess()
    {
        var f = new Fixture(1, 2, 3, 4) { Success = false };
        Assert.True(f.Tick(Start, out long target));
        Assert.Equal(2, target);
        var button = f.UI.com_ShowTime.com_Player2.btn_Praise;
        Assert.True(button.Cut_in.playing);
        Assert.Equal(0, button.isFinish.selectedIndex);
        Assert.False(button.onClick.isDispatching);
        button.onClick.Call();
        Assert.Single(f.Requests);
        Assert.Empty(f.Chats);
        button.Cut_in.Complete();
        Assert.Equal(1, button.isFinish.selectedIndex);
        Assert.False(f.Tick(Start + Interval - 1, out _));
        f.Success = true;
        Assert.True(f.Tick(Start + Interval, out target));
        Assert.Equal(3, target);
        Assert.Equal(new long[] { 2, 3 }, f.Requests);
        Assert.Equal(new long[] { 3 }, f.Chats);
    }

    [Fact]
    public void EachTickSendsOnceAtExact500msBoundaryAndNeverCatchesUpAfterStall()
    {
        var f = new Fixture(1, 2, 3, 4);
        Assert.True(f.Tick(Start, out long target));
        Assert.Equal(2, target);
        Assert.False(f.Tick(Start, out _));
        Assert.False(f.Tick(Start + Interval - 1, out _));
        long afterStall = Start + 60 * ThanksPolicy.TicksPerSecond;
        Assert.True(f.Tick(afterStall, out target));
        Assert.Equal(3, target);
        Assert.False(f.Tick(afterStall, out _));
        Assert.False(f.Tick(afterStall + Interval - 1, out _));
        Assert.True(f.Tick(afterStall + Interval, out target));
        Assert.Equal(4, target);
        Assert.Equal(new long[] { 2, 3, 4 }, f.Requests);
    }

    [Fact]
    public void SelfBotsMonstersAndZeroIdsAreNeverClicked()
    {
        var f = new Fixture(1, 2, 3, 0);
        f.Settlement.Players[1].PlayerData.player.IsBot = true;
        f.Settlement.Players[2].PlayerData.player.characterType = CharacterType.Monster;
        Assert.False(f.Tick(Start, out long target));
        Assert.Equal(0, target);
        Assert.Empty(f.Requests);
        Assert.Empty(f.Chats);
        Assert.Equal(0, Permissions.Checks);
    }

    [Fact]
    public void EmptyOrMissingRowsAndHandlersCannotConsumeAnAttempt()
    {
        var f = new Fixture(1, 2);
        f.UI.com_ShowTime.com_Player2.btn_Praise.onClick.Clear();
        Assert.False(f.Tick(Start, out _));
        Assert.Empty(f.Requests);
        f.BindRows();
        Assert.True(f.Tick(Start, out long target));
        Assert.Equal(2, target);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("offstage")]
    [InlineData("step0")]
    [InlineData("step1")]
    [InlineData("step3")]
    [InlineData("showtime")]
    [InlineData("row")]
    [InlineData("button")]
    public void HiddenOrUnavailablePagesAndRowsDoNotSend(string condition)
    {
        var f = new Fixture(1, 2);
        switch (condition)
        {
            case "offstage": f.UI.onStage = false; break;
            case "step0": f.UI.step.selectedIndex = 0; break;
            case "step1": f.UI.step.selectedIndex = 1; break;
            case "step3": f.UI.step.selectedIndex = 3; break;
            case "showtime": f.UI.com_ShowTime.visible = false; break;
            case "row": f.UI.com_ShowTime.com_Player2.visible = false; break;
            case "button": f.UI.com_ShowTime.com_Player2.btn_Praise.visible = false; break;
        }
        Assert.False(f.Tick(Start, out long target));
        Assert.Equal(0, target);
        Assert.Empty(f.Requests);
        f.UI.onStage = true;
        f.UI.step.selectedIndex = 2;
        f.UI.com_ShowTime.visible = true;
        f.UI.com_ShowTime.com_Player2.visible = true;
        f.UI.com_ShowTime.com_Player2.btn_Praise.visible = true;
        Assert.True(f.Tick(Start, out target));
        Assert.Equal(2, target);
        Assert.Single(f.Requests);
    }

    [Fact]
    public void ResetAllowsSamePlayersInNewBattleWithReusedUI()
    {
        var f = new Fixture(1, 2);
        Assert.True(f.Tick(Start, out _));
        f.UI.com_ShowTime.com_Player2.btn_Praise.Cut_in.Complete();
        f.Likes.Reset();
        f.SetPlayers(1, 2);
        Assert.True(f.Tick(Start, out long target));
        Assert.Equal(2, target);
        Assert.Equal(new long[] { 2, 2 }, f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UIReplacementOrRefreshCannotRepeatEarlierAttempt(bool replacement)
    {
        var f = new Fixture(1, 2, 3, 4);
        Assert.True(f.Tick(Start, out _));
        if (replacement) f.Panel.UIObject = new UIBattleSettlementPanel();
        // Original refresh resets controller and releases Retain via EventListener.Set.
        f.BindRows();
        Assert.True(f.Tick(Start + Interval, out long target));
        Assert.Equal(3, target);
        var attempted = f.UI.com_ShowTime.com_Player2.btn_Praise;
        Assert.Equal(1, attempted.isFinish.selectedIndex);
        attempted.onClick.Call();
        Assert.Equal(new long[] { 2, 3 }, f.Requests);
    }

    [Fact]
    public void RebindingSameUIToDifferentRowOrderTracksPlayerIdsRatherThanSlots()
    {
        var f = new Fixture(1, 2, 3, 4);
        Assert.True(f.Tick(Start, out _));
        f.SetPlayers(4, 3, 2, 1);
        Assert.True(f.Tick(Start + Interval, out long target));
        Assert.Equal(4, target);
        Assert.True(f.Tick(Start + 2 * Interval, out target));
        Assert.Equal(3, target);
        Assert.False(f.Tick(Start + 3 * Interval, out _));
        Assert.Equal(new long[] { 2, 4, 3 }, f.Requests);
    }

    [Fact]
    public void PermissionDenialDoesNotClickOrConsumeEligibility()
    {
        var f = new Fixture(1, 2, 3, 4);
        Permissions.Allowed = false;
        Assert.False(f.Tick(Start, out long target));
        Assert.Equal(0, target);
        Assert.Empty(f.Requests);
        Assert.Equal(1, Permissions.Checks);
        Permissions.Allowed = true;
        Assert.True(f.Tick(Start, out target));
        Assert.Equal(2, target);
        Assert.Equal(2, Permissions.Checks);
        Assert.Equal(new long[] { 2 }, f.Requests);
    }

    [Fact]
    public void ThrowingOriginalHandlerIsReservedAndNeverAutomaticallyRetried()
    {
        var f = new Fixture(1, 2, 3, 4) { Throw = true };
        Assert.Throws<InvalidOperationException>(() => f.Tick(Start, out _));
        var attempted = f.UI.com_ShowTime.com_Player2.btn_Praise;
        Assert.False(attempted.onClick.isDispatching); // Actual EventBridge's finally executes.
        Assert.Single(f.Requests);
        Assert.Empty(f.Chats);
        f.Throw = false;
        // Simulate a refresh after the exception, including releasing the original listener lock.
        f.BindRows();
        Assert.False(f.Tick(Start + Interval - 1, out _));
        Assert.True(f.Tick(Start + Interval, out long target));
        Assert.Equal(3, target);
        Assert.True(f.Tick(Start + 2 * Interval, out target));
        Assert.Equal(4, target);
        Assert.False(f.Tick(Start + 60 * ThanksPolicy.TicksPerSecond, out _));
        Assert.Equal(new long[] { 2, 3, 4 }, f.Requests);
        Assert.Equal(new long[] { 3, 4 }, f.Chats);
    }
}
