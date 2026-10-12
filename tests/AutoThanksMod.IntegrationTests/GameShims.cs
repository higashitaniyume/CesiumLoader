using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FairyGUI
{
    public delegate void EventCallback0();
    public delegate void EventCallback1(EventContext context);

    public sealed class EventContext
    {
        public object sender;
        public object data;
    }

    // Only dispatch plumbing is shimmed. Listener registration, Retain/Release and invocation
    // run through the game's linked EventListener and EventBridge implementations.
    public class EventDispatcher
    {
        private readonly Dictionary<string, EventBridge> _bridges = new();
        public EventBridge GetEventBridge(string type)
        {
            if (!_bridges.TryGetValue(type, out var bridge))
                _bridges.Add(type, bridge = new EventBridge(this));
            return bridge;
        }
        public bool hasEventListeners(string type) => !GetEventBridge(type).isEmpty;
        public bool isDispatching(string type) => GetEventBridge(type)._dispatching;
        public bool InternalDispatchEvent(string type, EventBridge bridge, object data, object initiator)
        {
            bridge.CallInternal(new EventContext { data = data });
            return false;
        }
        public bool BubbleEvent(string type, object data) => InternalDispatchEvent(type, GetEventBridge(type), data, null);
        public bool BroadcastEvent(string type, object data) => InternalDispatchEvent(type, GetEventBridge(type), data, null);
    }

    public class GObject : EventDispatcher
    {
        public bool visible = true;
        public bool grayed;
        public bool onStage = true;
    }
    public class GButton : GObject
    {
        public EventListener onClick { get; }
        public GButton() { onClick = new EventListener(this, "onClick"); }
    }
    public sealed class Controller { public int selectedIndex; }
    public sealed class Transition
    {
        public bool playing;
        private EventCallback0 _onComplete;
        public void Play(EventCallback0 onComplete)
        {
            playing = true;
            _onComplete = onComplete;
        }
        public void Complete()
        {
            playing = false;
            var callback = _onComplete;
            _onComplete = null;
            callback?.Invoke();
        }
    }
}

public enum CharacterType { None, Hero, Monster }

namespace GameLogic
{
    public sealed class RoomPlayer
    {
        public long Id;
        public bool IsBot;
        public long TeamId;
        public CharacterType characterType = CharacterType.Hero;
    }
    public sealed class BattlePlayerData
    {
        public RoomPlayer player;
        public CharacterType characterType => player.characterType;
    }
    public sealed class PlayerSettlement { public BattlePlayerData PlayerData; }
    public sealed class BattleSettlement
    {
        public bool IsPVP;
        public long WinTeamId = 10;
        public List<PlayerSettlement> Players = new();
        public PlayerSettlement GetPlayerSettlement(int index) => index < Players.Count ? Players[index] : null;
        public PlayerSettlement GetPlayerSettlementById(long id) => Players.Find(x => x.PlayerData.player.Id == id);
    }
    public sealed class BattleLogic
    {
        public List<BattlePlayerData> Players = new();
        public List<BattlePlayerData> GetPlayerDataByTeamId(long teamId) => Players.Where(x => x.player.TeamId == teamId).ToList();
    }
}

namespace UI
{
    using FairyGUI;
    using GameLogic;
    public sealed class BattleSettlementPanel
    {
        // Matches the real private field name and reflection visibility.
        private readonly BattleSettlement _BattleSettlement;
        public UIBattleSettlementPanel UIObject { get; set; }
        public BattleSettlementPanel(BattleSettlement settlement, UIBattleSettlementPanel ui)
        {
            _BattleSettlement = settlement;
            UIObject = ui;
        }
    }
    public sealed class UIBattleSettlementPanel : GObject
    {
        public Controller step = new() { selectedIndex = 2 };
        public UIBattleSettlement_Com_ShowWiner com_Winner = new();
        public UIBattleSettlement_Com_ShowTime com_ShowTime = new();
    }
    public sealed class UIBattleSettlement_Com_ShowWiner : GObject
    {
        public UIBattleSettlement_Button_Praise btn_Praise = new();
    }
    public sealed class UIBattleSettlement_Com_ShowTime : GObject
    {
        public UIBattleSettlement_Com_Item com_Player1 = new();
        public UIBattleSettlement_Com_Item com_Player2 = new();
        public UIBattleSettlement_Com_Item com_Player3 = new();
        public UIBattleSettlement_Com_Item com_Player4 = new();
        public UIBattleSettlement_Com_Item[] Rows => new[] { com_Player1, com_Player2, com_Player3, com_Player4 };
    }
    public sealed class UIBattleSettlement_Com_Item : GObject
    {
        public UIBattleSettlement_Button_OperatePlayer btn_Praise = new();
    }
    public sealed class UIBattleSettlement_Button_OperatePlayer : GButton
    {
        public Controller isFinish = new();
        public Transition Cut_in = new();
    }
    public sealed class UIBattleSettlement_Button_Praise : GButton
    {
        public Transition Cut_in = new();
    }
}

namespace CesiumLoader.SDK.Manifests
{
    public enum ModPermission { GameActions }
    public static class Permissions
    {
        public static bool Allowed = true;
        public static int Checks;
        public static bool Require(ModPermission permission, string action)
        {
            Assert.Equal(ModPermission.GameActions, permission);
            Assert.Equal("AutoThanksMod.LikePlayer", action);
            Checks++;
            return Allowed;
        }
    }
}
