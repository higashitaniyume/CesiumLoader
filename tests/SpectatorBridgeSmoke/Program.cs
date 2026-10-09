using System.Reflection;
using System.Text.Json;
using Core;
using Core.Net;
using GameLogic;
using SpectatorBridgeMod;
using Tools;
using UI;
namespace Smoke;
public static class Program {
 static int passed,failed;
 public static int Main(){
  Run("non-PVE rejection cleans search then retry",()=>Reject(r=>r.Pve=false,null));
  Run("full room rejection cleans search then retry",()=>Reject(r=>r.WatchCount=10,null));
  Run("match rejection cleans search then retry",()=>Reject(null,g=>g.match.Allowed=false));
  Run("single-game rejection cleans search then retry",()=>Reject(r=>r.Single=true,null));
  foreach(var state in Enum.GetValues<RoomStateType>()) Run("join admission "+state,()=>{var f=new Fixture();f.Gm.room.roomController.roomStateType=state;var id=f.Command("join");Check(f.Gm.watch.Joins.Count==(state==RoomStateType.NONE?1:0),"join count");Check(f.Status(id)==(state==RoomStateType.NONE?"pending":"failed"),"result");});
  Run("late query success after timeout cleans search",()=>{var f=new Fixture();var id=f.Command("join");f.Timeout();Check(f.Status(id)=="timeout","timeout");f.QueryResponse();Check(f.Gm.room.roomController.roomStateType==RoomStateType.NONE,"late search cleanup");Check(f.Ui.Calls==0,"no panel open");f.Command("join");Check(f.Gm.watch.Joins.Count==2,"retry admitted");Check(f.Status(id)=="timeout","terminal timeout preserved");});
  Run("late query error unlocks retry",()=>{var f=new Fixture();f.Command("join");f.Timeout();f.Gm.watch.Joins[0].Finish(7);f.Command("join");Check(f.Gm.watch.Joins.Count==2,"retry");});
  Run("refresh timeout followed by readiness and real leave result",()=>{var f=new Fixture();var join=f.Command("join");f.QueryResponse();Check(f.Refresh.Calls.Count==1,"refresh submitted");f.Timeout();Check(f.Status(join)=="timeout","join timeout");f.BecomeWatcher();f.Refresh.Calls[0].Finish();f.Tick();var leave=f.Command("leave");Check(f.Status(leave)=="pending","leave not falsely completed");Check(f.Gm.watch.Exits.Count==1,"exit submitted");f.Gm.watch.Exits[0].Finish(9);Check(f.Status(leave)=="failed","actual error respected");});
  Run("exit timeout blocks focus and duplicate exit",()=>{var f=new Fixture();f.BecomeWatcher();var exit=f.Command("leave");f.Timeout();Check(f.Status(exit)=="timeout","exit timeout");var focus=f.Command("focus");var second=f.Command("leave");Check(f.Status(focus)=="failed"&&f.Status(second)=="failed","incompatible commands denied");Check(f.Gm.watch.FocusChanges==0&&f.Gm.watch.Exits.Count==1,"no mutation");f.Gm.watch.Exits[0].Finish(9);f.Command("focus");Check(f.Gm.watch.FocusChanges==1,"error response releases exit lock");Check(f.Status(exit)=="timeout","timeout preserved");});
  Run("unload before query callback prevents bridge mutations",()=>{var f=new Fixture();var id=f.Command("join");f.Mod.OnUnload();var before=f.State();f.QueryResponse();f.Tick();Check(f.Ui.Calls==0&&f.Refresh.Calls.Count==0,"no panel or refresh");Check(f.Gm.room.Clears==0,"no callback cleanup mutation after unload");Check(f.State()==before,"no state write after unload");Check(f.Status(id)=="pending","no callback result write");});
  Run("unload during OpenPanel await prevents refresh",()=>{var f=new Fixture();f.Ui.Gate=new();f.Command("join");f.QueryResponse();Check(f.Ui.Calls==1,"panel pending");f.Mod.OnUnload();var before=f.State();f.Ui.Gate.SetResult(true);Check(f.Assets.Calls==0&&f.Refresh.Calls.Count==0,"no asset or RPC work after unload");Check(f.State()==before,"no state changes");});
  Run("unload during asset await prevents refresh",()=>{var f=new Fixture();f.Assets.Gate=new();f.Command("join");f.QueryResponse();Check(f.Assets.Calls==1,"assets pending");f.Mod.OnUnload();f.Assets.Gate.SetResult(true);Check(f.Refresh.Calls.Count==0,"no refresh");});
  Run("unload before refresh callback prevents result writes",()=>{var f=new Fixture();var id=f.Command("join");f.QueryResponse();f.Mod.OnUnload();var before=f.State();f.BecomeWatcher();f.Refresh.Calls[0].Finish();f.Tick();Check(f.Status(id)=="pending"&&f.State()==before,"no completion writes");});
  Run("unload before exit callback prevents result writes",()=>{var f=new Fixture();f.BecomeWatcher();var id=f.Command("leave");f.Mod.OnUnload();var before=f.State();f.Gm.watch.Exits[0].Finish();Check(f.Status(id)=="pending"&&f.State()==before,"no completion writes");});
  Run("OpenPanel wait timeout does not refresh after late resume",()=>{var f=new Fixture();f.Ui.Gate=new();var id=f.Command("join");f.QueryResponse();f.Timeout();Check(f.Gm.room.roomController.roomStateType==RoomStateType.NONE,"timeout cleanup");f.Ui.Gate.SetResult(true);Check(f.Refresh.Calls.Count==0,"no stale refresh");Check(f.Status(id)=="timeout","terminal timeout");f.Command("join");Check(f.Gm.watch.Joins.Count==2,"retry");});
  Run("unrelated room protected from timed-out query cleanup",()=>{var f=new Fixture();f.Ui.Gate=new();f.Command("join");f.QueryResponse();f.Gm.room.curRoomInfo=new(){Id=999};f.Timeout();Check(f.Gm.room.Clears==0,"room ownership check");});
  Run("old panel continuation cannot clear a newer search room",()=>{var f=new Fixture();var oldGate=new TaskCompletionSource<bool>();f.Ui.Gate=oldGate;f.Command("join");f.QueryResponse();f.Timeout();var newGate=new TaskCompletionSource<bool>();f.Ui.Gate=newGate;var second=f.Command("join");f.QueryResponse(r=>r.Id=987);Check(f.Gm.room.roomController.roomStateType==RoomStateType.RUNNING,"new search exists");oldGate.SetResult(true);Check(f.Gm.room.roomController.roomStateType==RoomStateType.RUNNING,"old continuation cleared new room");newGate.SetResult(true);Check(f.Refresh.Calls.Count==1,"new operation can refresh");});
  Run("explicit focus disables automatic turn follow",()=>{var f=new Fixture();f.BecomeWatcher();Check(f.Gm.watch.Follow,"default follow");var id=f.Command("focus");Check(f.Status(id)=="done","focus completion");Check(f.Gm.watch.Subscribe==2 && !f.Gm.watch.Follow,"focus stays on requested player");});
  Console.WriteLine($"Smoke: {passed} passed, {failed} failed. Temporary fixture roots retained under {Path.Combine(Path.GetTempPath(),"SpectatorBridgeSmoke")}.");return failed==0?0:1;
 }
 static void Reject(Action<RoomInfo> change,Action<GameLogicManager> setup){var f=new Fixture();setup?.Invoke(f.Gm);var id=f.Command("join");f.QueryResponse(change);Check(f.Status(id)=="failed","rejection");Check(f.Gm.room.roomController.roomStateType==RoomStateType.NONE,"search cleanup");f.Gm.match.Allowed=true;f.Command("join");Check(f.Gm.watch.Joins.Count==2,"retry");}
 static void Run(string name,Action test){try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 sealed class Fixture {
  public SpectatorBridge Mod=new();public GameLogicManager Gm=new();public UIManager Ui=new();public InternalAssetManager Assets=new();public NetManager Net=new();public RefreshRpc Refresh=>Net.RPC.WatchRefreshRoomStateC2S;
  readonly string root;
  public Fixture(){var dir=Path.Combine(Path.GetTempPath(),"SpectatorBridgeSmoke",Guid.NewGuid().ToString("N"));root=Path.Combine(dir,"spectator");Environment.SetEnvironmentVariable("CESIUM_MODS_DIR",Path.Combine(dir,"mods"));SimpleSingletonProvider<GameLogicManager>.inst=Gm;SimpleSingletonProvider<UIManager>.inst=Ui;SimpleSingletonProvider<InternalAssetManager>.inst=Assets;MonoSingletonProvider<NetManager>.inst=Net;Mod.OnInitialize();}
  public string Command(string action){var id=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(root,"commands",id+".json"),JsonSerializer.Serialize(new{id,action,watchCode="stub",playerId="2"}));Tick();return id;}
  public void Tick(){Set("_next",DateTime.MinValue);Mod.OnUpdate();}
  public void Timeout(){Set("_deadline",DateTime.UtcNow.AddSeconds(-1));Tick();}
  void Set(string name,object value)=>typeof(SpectatorBridge).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Mod,value);
  public string Status(string id){using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"results",id+".json")));return json.RootElement.GetProperty("status").GetString();}
  public string State()=>File.ReadAllText(Path.Combine(root,"state.json"));
  public void QueryResponse(Action<RoomInfo> change=null){Gm.room.curRoomInfo=new();change?.Invoke(Gm.room.curRoomInfo);Gm.room.roomController.roomStateType=RoomStateType.RUNNING;Gm.watch.Joins[^1].Finish();}
  public void BecomeWatcher(){Gm.room.curRoomInfo??=new();Gm.room.roomController.roomStateType=RoomStateType.RUNNING;Gm.watch.Watcher=true;Gm.battle.Ready=true;}
 }
}
