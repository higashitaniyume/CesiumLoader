using System.Runtime.CompilerServices;
using System.Text.Json;
namespace Cysharp.Threading.Tasks {
 [AsyncMethodBuilder(typeof(UniTaskVoidBuilder))] public struct UniTaskVoid { public Task Task; public void Forget() {} }
 public struct UniTaskVoidBuilder { private AsyncTaskMethodBuilder b; public static UniTaskVoidBuilder Create()=>new(){b=AsyncTaskMethodBuilder.Create()}; public UniTaskVoid Task=>new(){Task=b.Task}; public void SetResult()=>b.SetResult(); public void SetException(Exception e)=>b.SetException(e); public void SetStateMachine(IAsyncStateMachine s)=>b.SetStateMachine(s); public void Start<T>(ref T s) where T:IAsyncStateMachine=>b.Start(ref s); public void AwaitOnCompleted<T,U>(ref T a,ref U s) where T:INotifyCompletion where U:IAsyncStateMachine=>b.AwaitOnCompleted(ref a,ref s); public void AwaitUnsafeOnCompleted<T,U>(ref T a,ref U s) where T:ICriticalNotifyCompletion where U:IAsyncStateMachine=>b.AwaitUnsafeOnCompleted(ref a,ref s); }
}
namespace CesiumLoader.SDK.Manifests { [Flags] public enum ModPermission { ReadGameState=1,GameActions=2,FileSystem=4 } [AttributeUsage(AttributeTargets.Assembly)] public class ModManifestAttribute:Attribute { public ModManifestAttribute(string n,string v,string a){} public ModPermission Permissions{get;set;} public string SdkVersion{get;set;} } }
namespace CesiumLoader.SDK.Mods { public class Logger { public void Info(string s){} public void Warn(string s){} } public abstract class ModBase { public Logger Log=new(); public virtual string Version=>""; public virtual void OnInitialize(){} public virtual void OnUpdate(){} public virtual void OnUnload(){} public static void Run(ModBase m,int delay){} } }
namespace CesiumLoader.SDK.Configuration { public static class CesiumJson { public static string Serialize(object o)=>JsonSerializer.Serialize(o); public static object Deserialize(string s)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(s).ToDictionary(p=>p.Key,p=>(object)(p.Value.ValueKind==JsonValueKind.String?p.Value.GetString():p.Value.ToString())); } }
namespace CesiumLoader.SDK.Gameplay { public static class Names { public static string Card(int id)=>"card "+id; } }
namespace Tools { public static class SimpleSingletonProvider<T> { public static T inst; } public static class MonoSingletonProvider<T> { public static T inst; } public static class StaticGlobalData { public static int ROOM_AUDIENCE_NUMBLIMIT=10; } }
namespace Core.Net { public class Signal { readonly List<Action<RPCAsyncResult>> callbacks=new(); public void AddOnce(Action<RPCAsyncResult> c)=>callbacks.Add(c); public void Fire(RPCAsyncResult r){var c=callbacks.ToArray();callbacks.Clear();foreach(var a in c)a(r);} } public class RPCAsyncResult { public int errId; public Signal OnFinished=new(); public void Finish(int error=0){errId=error;OnFinished.Fire(this);} } public class RefreshRpc { public List<RPCAsyncResult> Calls=new(); public RPCAsyncResult WatchRefreshRoomStateC2SCall(party.protocol.WatchRefreshRoomStateC2S r){var c=new RPCAsyncResult();Calls.Add(c);return c;} } public class Rpc { public RefreshRpc WatchRefreshRoomStateC2S=new(); } public class NetManager { public Rpc RPC=new(); } }
namespace party.protocol { public class WatchRefreshRoomStateC2S { public long RoomId;public long RoomServerId; } }
namespace Core { public class InternalAssetManager { public TaskCompletionSource<bool> Gate; public int Calls; public Task PreLoadBattleAsset(){Calls++;return Gate?.Task??Task.CompletedTask;} } }
namespace UI { public enum UIPanelType { RoomHero } public class UIManager { public TaskCompletionSource<bool> Gate;public int Calls; public Task OpenPanel(UIPanelType t){Calls++;return Gate?.Task??Task.CompletedTask;} } }
namespace GameLogic {
 public enum RoomStateType { NONE,WAIT,CHOICE,READY,RUNNING,SETTLEMENT }
 public class Player { public long Id;public int Slot; }
 public class RoomInfo { public long Id=123;public int WatchCount;public bool Pve=true,Single;public RoomInfo info=>this;public long RoomServerId=456;public List<Player> Players=new(){new(){Id=1,Slot=0},new(){Id=2,Slot=1}};public bool IsSingleGameModel()=>Single;public bool IsPVE()=>Pve;public Player GetPlayerById(long id)=>Players.Find(p=>p.Id==id); }
 public class RoomController { public RoomStateType roomStateType=RoomStateType.NONE; }
 public class RoomLogic { public RoomController roomController=new();public RoomInfo curRoomInfo;public int Clears;public void ClearRoomInfo(){Clears++;roomController.roomStateType=RoomStateType.NONE;} }
 public class AccountLogic { public bool StartGameLicense()=>true; }
 public class MatchLogic { public bool Allowed=true;public bool CheckOperateForMatch()=>Allowed; }
 public class WatchLogic { public bool Watcher;public long Subscribe=1;public List<Core.Net.RPCAsyncResult> Joins=new(),Exits=new();public bool PlayerIsWatcher()=>Watcher;public Core.Net.RPCAsyncResult RequestWatchJoinRoomC2S(string c){var r=new Core.Net.RPCAsyncResult();Joins.Add(r);return r;}public Core.Net.RPCAsyncResult RequestWatchExitRoomC2S(){var r=new Core.Net.RPCAsyncResult();Exits.Add(r);return r;}public bool Follow=true;public void SwitchFollow(bool isFollow){Follow=isFollow;}public int FocusChanges;public void UpdateSubscribePlayer(long p){Subscribe=p;FocusChanges++;} }
 public class Card { public int CardId=20001;public int BattleCost=3; }
 public class Cards { public List<Card> _HandCards=new(){new()}; }
 public class BattlePlayerData { public Player player;public Cards cardContainer=new(); }
 public class BattleLogic { public bool Ready;public BattlePlayerData GetSelfPlayerData()=>Ready?GetPlayerDataById(Tools.SimpleSingletonProvider<GameLogicManager>.inst.watch.Subscribe):null;public BattlePlayerData GetPlayerDataById(long id)=>new(){player=new(){Id=id}}; }
 public class GameLogicManager { public WatchLogic watch=new();public RoomLogic room=new();public AccountLogic account=new();public MatchLogic match=new();public BattleLogic battle=new(); }
}
