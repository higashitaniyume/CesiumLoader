using System;
using System.Collections;
using System.Reflection;
using AutoThanksMod;
using CesiumLoader.SDK.Manifests;
using Core;
using Core.Net;
using Core.Scene;
using GameLogic;
using Tools;
using UI;
using party.model;
using party.protocol;
using Xunit;

namespace AutoThanksMod.LifecycleTests;

public sealed class AutoThanksLifecycleTests
{
    private sealed class Fixture
    {
        public readonly AutoThanks Mod = new();
        public readonly GameLogicManager GM = new();
        public readonly NetManager Net = new();
        public readonly Core.Scene.SceneManager Scene = new();
        public readonly UIManager UI = new();
        public readonly object OriginalResult = new();
        public readonly SayPhraseNotifyS2CRPC.OnSayPhraseNotifyS2CServerDelegate Original;
        public int OriginalCalls;
        public SayPhraseNotifyS2CRPC Rpc => Net.RPC.SayPhraseNotifyS2C;
        public SettlementLikes Likes => (SettlementLikes)Get("_likes");
        public int PendingCount => (int)Get("_pending").GetType().GetProperty("Count").GetValue(Get("_pending"));
        public Fixture(bool initialize = true)
        {
            Permissions.Allowed = true;
            Permissions.Checks = 0;
            SimpleSingletonProvider<GameLogicManager>.inst = GM;
            SimpleSingletonProvider<Core.Scene.SceneManager>.inst = Scene;
            SimpleSingletonProvider<UIManager>.inst = UI;
            MonoSingletonProvider<NetManager>.inst = Net;
            Original = (model, error, dispatch) => { OriginalCalls++; return OriginalResult; };
            Rpc.OnSayPhraseNotifyS2CServerCallBackAsync = Original;
            if (initialize) Initialize();
        }
        public void Initialize()
        {
            Mod.OnInitialize();
            Set("_nextConfigRead", long.MaxValue);
        }
        public object Get(string name) => typeof(AutoThanks).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Mod);
        public void Set(string name, object value) => typeof(AutoThanks).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Mod, value);
        public void Update()
        {
            Mod.OnUpdate();
            Assert.Empty(Mod.Log.Warnings);
        }
        public void Configure(string key, object value)
        {
            Mod.Config.Set(key, value);
            Set("_nextConfigRead", 0L);
        }
        public static SayPhraseNotifyS2C Notice(int type = 3) => new()
        {
            Phrase = new TriggerPhrase
            {
                ActivePlayerId = 2, PassivePlayerId = 1, TriggerType = (TriggerPhrase.Types.Type)type
            }
        };
        public void Notify(int type = 3, int error = 0)
        {
            Assert.Same(OriginalResult, Rpc.OnSayPhraseNotifyS2CServerCallBackAsync(Notice(type), error, true));
        }
        public void AgePending(int milliseconds)
        {
            // Keep the actual Notice produced by the real RPC wrapper; change only its timestamp.
            object queue = Get("_pending");
            Type queueType = queue.GetType();
            Array entries = (Array)queueType.GetMethod("ToArray").Invoke(queue, null);
            queueType.GetMethod("Clear").Invoke(queue, null);
            foreach (object entry in entries)
            {
                entry.GetType().GetField("ReceivedTicks", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(entry, DateTime.UtcNow.Ticks - milliseconds * 10000L);
                queueType.GetMethod("Enqueue").Invoke(queue, new[] { entry });
            }
        }
        public void AssertNoChats()
        {
            Assert.Empty(GM.communicate.ChatRequests);
            Assert.Empty(GM.communicate.Signal.battleMessage.Messages);
        }
    }

    [Fact]
    public void InitializePersistsDefaultConfigurationWithoutOverwritingExistingPreferences()
    {
        var f = new Fixture(false);
        f.Mod.Config.Set("cooldownSeconds", 7);
        f.Initialize();
        Assert.True(f.Mod.Config.GetBool("enabled", false));
        Assert.True(f.Mod.Config.GetBool("autoLikeOnGameEnd", false));
        Assert.True(f.Mod.Config.GetBool("thankForHealing", false));
        Assert.True(f.Mod.Config.GetBool("thankForCards", false));
        Assert.True(f.Mod.Config.GetBool("thankForTransfers", false));
        Assert.Equal(1000, f.Mod.Config.GetInt("thankDelayMilliseconds", -1));
        Assert.Equal(500, f.Mod.Config.GetInt("likeIntervalMilliseconds", -1));
        Assert.Equal(7, f.Mod.Config.GetInt("cooldownSeconds", -1));
        Assert.Equal(1, f.Mod.Config.SaveCount);
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
    }

    [Fact]
    public void RealRunningBattleArmsSettlementTickAndStopsNotificationHook()
    {
        var f = new Fixture();
        f.Update();
        Assert.NotSame(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        f.Notify();
        Assert.Equal(1, f.PendingCount);
        int resets = f.Likes.ResetCount;
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        var call = Assert.Single(f.Likes.Calls);
        Assert.Same(f.UI.currentPanel, call.Panel);
        Assert.Same(f.GM.battle, call.Battle);
        Assert.Equal(1, call.Account);
        Assert.Equal(500, call.Interval);
        Assert.True(call.Ticks > 0);
        Assert.Equal(resets, f.Likes.ResetCount);
        Assert.Equal(0, f.PendingCount);
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        f.AssertNoChats();
    }

    [Fact]
    public void EnteringSettlementDirectlyNeverArmsAutomaticLikes()
    {
        var f = new Fixture();
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        f.Update();
        Assert.Empty(f.Likes.Calls);
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        f.AssertNoChats();
    }

    [Fact]
    public void RunningBeforeClientFinishReadyCannotArmSettlement()
    {
        var f = new Fixture();
        f.GM.battle.clientFinishReady = false;
        f.Update();
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        Assert.Empty(f.Likes.Calls);
        f.AssertNoChats();
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("watch")]
    [InlineData("leave")]
    [InlineData("home")]
    [InlineData("account")]
    [InlineData("room")]
    public void ContextChangesDiscardPendingNoticesAndCannotLikePreviousBattle(string change)
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(1500);
        int resets = f.Likes.ResetCount;
        switch (change)
        {
            case "replay": f.GM.replay.Session.IsReplay = true; break;
            case "watch": f.GM.watch.Watching = true; break;
            case "leave": f.GM.room.IsInRoom = false; break;
            case "home": f.Scene.currentType.Value = SceneType.Home; break;
            case "account": f.GM.account.Id = 3; break;
            case "room": f.GM.room.curRoomInfo = new RoomInfo(); break;
        }
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        Assert.Empty(f.Likes.Calls);
        Assert.Equal(0, f.PendingCount);
        Assert.True(f.Likes.ResetCount > resets);
        Assert.Null(f.Get("_sessionRoom"));
        Assert.Equal(0L, f.Get("_sessionAccount"));
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        f.AssertNoChats();
        // Returning to a valid settlement still lacks a newly observed RUNNING battle.
        f.GM.replay.Session.IsReplay = false;
        f.GM.watch.Watching = false;
        f.GM.room.IsInRoom = true;
        f.Scene.currentType.Value = SceneType.Battle;
        f.GM.account.Id = 1;
        f.Update();
        Assert.Empty(f.Likes.Calls);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("room")]
    public void ContextChangeDuringRunningRearmsNewSessionButDropsOldPendingMessage(string change)
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(1500);
        if (change == "account") f.GM.account.Id = 3;
        else f.GM.room.curRoomInfo = new RoomInfo();
        f.Update();
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        var call = Assert.Single(f.Likes.Calls);
        Assert.Equal(f.GM.account.Id, call.Account);
        Assert.Same(f.GM.room.curRoomInfo, f.Get("_sessionRoom"));
    }

    [Fact]
    public void DisablingModStopsSendingAndPreservesThisBattlesLikeState()
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        int resets = f.Likes.ResetCount;
        f.Configure("enabled", false);
        f.Update();
        Assert.Equal(0, f.PendingCount);
        Assert.Equal(resets, f.Likes.ResetCount);
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Update();
        Assert.Empty(f.Likes.Calls);
        Assert.Equal(resets, f.Likes.ResetCount);
        f.Configure("enabled", true);
        f.Update();
        Assert.Single(f.Likes.Calls);
        Assert.Equal(resets, f.Likes.ResetCount);
        f.AssertNoChats();
    }

    [Fact]
    public void AutoLikeSwitchPreservesStateAndConfigIntervalIsClamped()
    {
        var f = new Fixture();
        f.Update();
        int resets = f.Likes.ResetCount;
        f.GM.room.roomController.roomStateType = RoomStateType.SETTLEMENT;
        f.Configure("autoLikeOnGameEnd", false);
        f.Update();
        Assert.Empty(f.Likes.Calls);
        Assert.Equal(resets, f.Likes.ResetCount);
        f.Configure("autoLikeOnGameEnd", true);
        f.Configure("likeIntervalMilliseconds", 0);
        f.Update();
        Assert.Equal(100, Assert.Single(f.Likes.Calls).Interval);
        f.Configure("likeIntervalMilliseconds", int.MaxValue);
        f.Update();
        Assert.Equal(30000, f.Likes.Calls[1].Interval);
        Assert.Equal(resets, f.Likes.ResetCount);
    }

    [Fact]
    public void DefaultOneSecondDelayWaitsThenSendsActualThankAndLocalMessage()
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        Assert.Equal(1, f.OriginalCalls);
        Assert.Equal(1, f.PendingCount);
        f.AgePending(100);
        f.Update();
        Assert.Equal(1, f.PendingCount);
        f.AssertNoChats();
        f.AgePending(1500);
        f.Update();
        Assert.Equal(0, f.PendingCount);
        Assert.Equal(new[] { 50001 }, f.GM.communicate.ChatRequests);
        var message = Assert.Single(f.GM.communicate.Signal.battleMessage.Messages);
        Assert.Equal(MessageType.SHORTINFO, message.Type);
        Assert.Same(f.GM.battle.Players[1], message.Self);
        Assert.Equal(50001, message.ChatId);
        Assert.Equal(1, Permissions.Checks);
    }

    [Fact]
    public void MaximumThankDelayIsCappedAtThreeSecondsAndStillWaits()
    {
        var f = new Fixture(false);
        f.Mod.Config.Set("thankDelayMilliseconds", int.MaxValue);
        f.Initialize();
        Assert.Equal(3000, f.Get("_thankDelay"));
        f.Update();
        f.Notify(1);
        f.AgePending(2000);
        f.Update();
        Assert.Equal(1, f.PendingCount);
        f.AssertNoChats();
        f.AgePending(3500);
        f.Update();
        Assert.Equal(new[] { 50000 }, f.GM.communicate.ChatRequests);
        Assert.Equal(0, f.PendingCount);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("settlement")]
    [InlineData("waiting")]
    public void DisablingOrLeavingRunningWhileDelayIsPendingDiscardsNotice(string transition)
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(100);
        f.Update();
        Assert.Equal(1, f.PendingCount);
        if (transition == "disabled") f.Configure("enabled", false);
        else f.GM.room.roomController.roomStateType = transition == "settlement" ? RoomStateType.SETTLEMENT : RoomStateType.WAITING;
        f.Update();
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
        f.Configure("enabled", true);
        f.GM.room.roomController.roomStateType = RoomStateType.RUNNING;
        f.Update();
        f.AssertNoChats();
        Assert.Equal(0, f.PendingCount);
    }

    [Fact]
    public void UnloadRestoresOriginalRpcAndOldCallbackGenerationStaysInvalidAfterRehook()
    {
        var f = new Fixture();
        f.Update();
        var oldWrapper = f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync;
        f.Notify();
        Assert.Equal(1, f.PendingCount);
        f.Mod.OnUnload();
        Assert.Same(f.Original, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        Assert.Equal(0, f.PendingCount);
        Assert.Null(f.Get("_sessionRoom"));
        Assert.Same(f.OriginalResult, oldWrapper(Fixture.Notice(), 0, true));
        Assert.Equal(0, f.PendingCount);
        f.Update();
        var newWrapper = f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync;
        Assert.NotSame(oldWrapper, newWrapper);
        Assert.Same(f.OriginalResult, oldWrapper(Fixture.Notice(), 0, true));
        Assert.Equal(0, f.PendingCount);
        f.Notify();
        Assert.Equal(1, f.PendingCount);
        Assert.Equal(4, f.OriginalCalls);
        f.AgePending(1500);
        f.Update();
        Assert.Equal(new[] { 50001 }, f.GM.communicate.ChatRequests);
    }

    [Fact]
    public void UnloadDoesNotOverwriteAnotherModsTopLevelRpcCallback()
    {
        var f = new Fixture();
        f.Update();
        var wrapped = f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync;
        SayPhraseNotifyS2CRPC.OnSayPhraseNotifyS2CServerDelegate other = (model, error, dispatch) => wrapped(model, error, dispatch);
        f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync = other;
        f.Mod.OnUnload();
        Assert.Same(other, f.Rpc.OnSayPhraseNotifyS2CServerCallBackAsync);
        Assert.Same(f.OriginalResult, other(Fixture.Notice(), 0, true));
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
    }

    [Fact]
    public void RpcErrorsAndUnrelatedNotificationsPreserveOriginalWithoutQueueing()
    {
        var f = new Fixture();
        f.Update();
        f.Notify(3, 1);
        f.Notify(0);
        f.Notify(99);
        Assert.Equal(3, f.OriginalCalls);
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
    }

    [Fact]
    public void StaleNotificationsAreDequeuedWithoutSending()
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(6000);
        f.Update();
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
    }

    [Fact]
    public void FutureDatedHeadDoesNotBlockLaterReadyNotice()
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(-10000);
        f.Notify(2);
        // Age only the second real Notice while preserving the future head.
        object queue = f.Get("_pending");
        Type queueType = queue.GetType();
        Array entries = (Array)queueType.GetMethod("ToArray").Invoke(queue, null);
        object later = entries.GetValue(1);
        later.GetType().GetField("ReceivedTicks", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(later, DateTime.UtcNow.Ticks - 1500 * 10000L);
        entries.SetValue(later, 1);
        queueType.GetMethod("Clear").Invoke(queue, null);
        foreach (object entry in entries) queueType.GetMethod("Enqueue").Invoke(queue, new[] { entry });
        f.Update();
        Assert.Equal(0, f.PendingCount);
        Assert.Equal(new[] { 50002 }, f.GM.communicate.ChatRequests);
    }

    [Fact]
    public void PermissionDenialNeverSendsOrRendersThank()
    {
        var f = new Fixture();
        f.Update();
        f.Notify();
        f.AgePending(1500);
        Permissions.Allowed = false;
        f.Update();
        Assert.Equal(1, Permissions.Checks);
        Assert.Equal(0, f.PendingCount);
        f.AssertNoChats();
    }
}
