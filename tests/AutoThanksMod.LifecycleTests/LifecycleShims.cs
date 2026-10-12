using System.Collections.Generic;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CesiumLoader.SDK.Mods
{
    public abstract class ModBase
    {
        public TestConfig Config { get; set; } = new();
        public TestLog Log { get; } = new();
        public abstract string Name { get; }
        public abstract string Version { get; }
        public abstract string Author { get; }
        public abstract string Description { get; }
        public virtual void OnInitialize() { }
        public virtual void OnUpdate() { }
        public virtual void OnUnload() { }
        public static void Run(ModBase mod) => mod.OnInitialize();
    }
    public sealed class TestConfig
    {
        private readonly Dictionary<string, object> _values = new();
        public int SaveCount, LoadCount;
        public bool Has(string key) => _values.ContainsKey(key);
        public void Set(string key, object value) => _values[key] = value;
        public bool GetBool(string key, bool fallback) => _values.TryGetValue(key, out var value) ? (bool)value : fallback;
        public int GetInt(string key, int fallback) => _values.TryGetValue(key, out var value) ? (int)value : fallback;
        public void Save() => SaveCount++;
        public void Load() => LoadCount++;
    }
    public sealed class TestLog
    {
        public readonly List<string> InfoMessages = new();
        public readonly List<string> Warnings = new();
        public void Info(string message) => InfoMessages.Add(message);
        public void Warn(string message) => Warnings.Add(message);
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
            Assert.Equal("AutoThanksMod.SendShortChat", action);
            Checks++;
            return Allowed;
        }
    }
}
namespace Tools
{
    public static class SimpleSingletonProvider<T> { public static T inst; }
}
namespace Core
{
    public static class MonoSingletonProvider<T> { public static T inst; }
}
namespace Core.Scene
{
    public enum SceneType { Battle, Home }
    public sealed class SceneValue { public SceneType Value = SceneType.Battle; }
    public sealed class SceneManager { public SceneValue currentType = new(); }
}
namespace party.model
{
    public sealed class TriggerPhrase
    {
        public static class Types { public enum Type { None, TransferGold, GiveCard, CureFriend } }
        public Types.Type TriggerType;
        public long ActivePlayerId, PassivePlayerId;
    }
}
namespace party.protocol
{
    public sealed class SayPhraseNotifyS2C { public party.model.TriggerPhrase Phrase; }
}
namespace Core.Net
{
    public sealed class SayPhraseNotifyS2CRPC
    {
        public delegate object OnSayPhraseNotifyS2CServerDelegate(party.protocol.SayPhraseNotifyS2C model, int errId, bool isDispatch);
        public OnSayPhraseNotifyS2CServerDelegate OnSayPhraseNotifyS2CServerCallBackAsync;
    }
    public sealed class RPCManager { public SayPhraseNotifyS2CRPC SayPhraseNotifyS2C = new(); }
    public sealed class NetManager { public RPCManager RPC = new(); }
}
namespace GameLogic
{
    public enum RoomStateType { WAITING, RUNNING, SETTLEMENT }
    public sealed class RoomPlayer { public long Id, TeamId; }
    public sealed class BattlePlayerData { public RoomPlayer player; }
    public sealed class RoomInfo { }
    public sealed class RoomController { public RoomStateType roomStateType = RoomStateType.RUNNING; }
    public sealed class RoomLogic
    {
        public bool IsInRoom = true;
        public RoomInfo curRoomInfo = new();
        public RoomController roomController = new();
    }
    public sealed class AccountLogic
    {
        public long Id = 1;
        public long GetPlayerID() => Id;
    }
    public sealed class BattleLogic
    {
        public bool clientFinishReady = true;
        public readonly Dictionary<long, BattlePlayerData> Players = new()
        {
            [1] = new() { player = new RoomPlayer { Id = 1, TeamId = 10 } },
            [2] = new() { player = new RoomPlayer { Id = 2, TeamId = 10 } },
            [3] = new() { player = new RoomPlayer { Id = 3, TeamId = 10 } }
        };
        public BattlePlayerData GetPlayerDataById(long id) => Players.TryGetValue(id, out var player) ? player : null;
    }
    public enum MessageType { SHORTINFO }
    public sealed class BattleMessage
    {
        public readonly MessageType Type;
        public readonly BattlePlayerData Self;
        public readonly int ChatId;
        public BattleMessage(MessageType type, BattlePlayerData self, int chatId) { Type = type; Self = self; ChatId = chatId; }
    }
    public sealed class MessageSignal
    {
        public readonly List<BattleMessage> Messages = new();
        public void Dispatch(BattleMessage message) => Messages.Add(message);
    }
    public sealed class CommunicateSignal { public MessageSignal battleMessage = new(); }
    public sealed class CommunicateLogic
    {
        public readonly List<int> ChatRequests = new();
        public CommunicateSignal Signal = new();
        public object RequestRoomShortChatC2S(int chatId) { ChatRequests.Add(chatId); return new object(); }
    }
    public sealed class ReplaySession { public bool IsReplay; }
    public sealed class ReplayLogic { public ReplaySession Session = new(); }
    public sealed class WatchLogic
    {
        public bool Watching;
        public bool PlayerIsWatcher() => Watching;
    }
    public sealed class GameLogicManager
    {
        public RoomLogic room = new();
        public AccountLogic account = new();
        public BattleLogic battle = new();
        public CommunicateLogic communicate = new();
        public ReplayLogic replay = new();
        public WatchLogic watch = new();
        public readonly HashSet<long> lockExpressionPlayerID = new();
    }
}
namespace UI
{
    public sealed class BattleSettlementPanel { }
    public sealed class UIManager { public object currentPanel = new BattleSettlementPanel(); }
}
namespace AutoThanksMod
{
    // The UI/event implementation is tested by the separate IntegrationTests project.
    // This recorder isolates ModEntry's real lifecycle guards from its rendering dependency.
    internal sealed class SettlementLikes
    {
        internal int ResetCount;
        internal readonly List<(UI.BattleSettlementPanel Panel, GameLogic.BattleLogic Battle, long Account, long Ticks, int Interval)> Calls = new();
        internal bool Tick(UI.BattleSettlementPanel panel, GameLogic.BattleLogic battle, long accountId,
            long nowTicks, int intervalMilliseconds, out long target)
        {
            Calls.Add((panel, battle, accountId, nowTicks, intervalMilliseconds));
            target = 2;
            return true;
        }
        internal void Reset() => ResetCount++;
    }
}
