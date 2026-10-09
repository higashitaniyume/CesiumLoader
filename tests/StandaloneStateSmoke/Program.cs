using StandaloneSpectator;
using System.Text.Json;
int count=0;
void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);count++;}
object Card(int uid,int id)=>new{UniqueId=uid,CardId=id,PurifyNum=0,IsTemp=false,BattleCost=2};
object Player(long id,int slot,params object[] cards)=>new{Id=id,Slot=slot,Nick="fixture",Hero=new{Cards=cards}};
object Room(int map=4,long watcher=99,int state=25,long id=123)=>new{Id=id,MapType=map,State=state,WatchPlayers=new[]{new{Id=watcher}},Players=new[]{Player(1,0,Card(10,20001)),Player(2,1,Card(20,10001))}};
SpectatorState Make(){var s=new SpectatorState(99);s.Apply(5192,new{RoomId=123L,MapType=4});s.Apply(5194,new{RoomId=123L,Room=Room()});return s;}
var state=new SpectatorState(99);state.Apply(5192,new{RoomId=123L,MapType=4});Check(!state.IsReady,"query alone has no authority");
state.Apply(5194,new{RoomId=123L,Room=Room()});Check(state.IsReady,"official watcher snapshot accepted");
state.Focus(2);Check(JsonSerializer.SerializeToElement(state.Snapshot()).GetProperty("playerId").GetString()=="2","focused player selected");
state.Apply(1109,new{PlayerId=2L,Cards=new[]{Card(30,20010),Card(31,10003)}});Check(JsonSerializer.SerializeToElement(state.Snapshot()).GetProperty("cards").GetArrayLength()==2,"full replacement not append");
state.Apply(1040,new{EffectDatas=new[]{new{PlayerId=2L,DataCase="ConvertCard",ConvertCard=new{ConvertCards=new[]{Card(30,20011)}}}}});Check(state.IsReady,"known UID conversion accepted");
state.Apply(1040,new{EffectDatas=new[]{new{PlayerId=2L,DataCase="ConvertCard",ConvertCard=new{ConvertCards=new[]{Card(999,20011)}}}}});Check(!state.IsReady,"unknown UID conversion invalidates");
state.Apply(1109,new{PlayerId=2L,Cards=Array.Empty<object>()});Check(state.IsReady&&JsonSerializer.SerializeToElement(state.Snapshot()).GetProperty("cards").GetArrayLength()==0,"empty authoritative hand");
state.Apply(1109,new{PlayerId=2L,Cards=new[]{Card(30,20010),Card(31,-1)}});Check(!state.IsReady,"masked second card suppresses identities");
state.Apply(1109,new{PlayerId=2L,Cards=new[]{Card(30,20010),Card(30,10003)}});Check(!state.IsReady,"duplicate UID invalidates");
foreach(int map in new[]{2,6,9,10}){state=Make();state.Apply(5008,new{Room=Room(map)});Check(!state.IsReady,"reject non-multiplayer-PVE map "+map);}
state=Make();state.Apply(5008,new{Room=Room(watcher:888)});Check(!state.IsReady,"lost watcher membership invalidates");
state=Make();state.Apply(5008,new{Room=Room(state:1)});Check(!state.IsReady,"non-running room invalidates");
state=Make();state.Apply(5008,new{Room=Room(id:456)});Check(!state.IsReady,"room identity change invalidates");
state=Make();state.Apply(1016,new{});Check(!state.IsReady,"settlement clears state");
state=Make();state.Reset("disconnect");Check(!state.IsReady,"disconnect clears state");
state=Make();state.Apply(1002,new{});Check(state.IsReady,"action prompts cannot mutate hand");
state=new SpectatorState(99);state.Apply(5192,new{RoomId=123L,MapType=4});state.Apply(5194,new{RoomId=0L,Room=Room()});Check(state.IsReady,"omitted outer room ID accepts matching authoritative inner room");
state=new SpectatorState(99);state.Apply(5192,new{RoomId=123L,MapType=4});state.Apply(5194,new{RoomId=456L,Room=Room()});Check(!state.IsReady,"explicit conflicting outer room ID rejected");
Console.WriteLine($"State smoke: {count} checks passed.");
