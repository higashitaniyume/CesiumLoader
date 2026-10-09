using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StandaloneSpectator;

var directory=args.Length>0?args[0]:Path.GetFullPath("../../../../extracted_dlls",AppContext.BaseDirectory);
using var protocol=new ProtocolMessages(directory);
object Message(string name, params (string Key,object? Value)[] props)=>protocol.Create(name,props.ToDictionary(p=>p.Key,p=>p.Value));
object Card(int id,int uid)=>Message("party.model.CardInfo",("CardId",id),("UniqueId",uid),("BattleCost",2));
object Player(long id,int slot,params object[] cards)=>Message("party.model.Player",("Id",id),("Slot",slot),("Nick","fixture"),("Hero",Message("party.model.Hero",("PlayerId",id),("HeroId",101),("Cards",cards))));
int pass=0;
void Check(bool result,string title){if(!result)throw new Exception(title);Console.WriteLine("PASS "+title);pass++;}
using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
int port=((IPEndPoint)listener.LocalEndpoint).Port;
var login=Message("party.protocol.ConnectC2S",("Auth","China"),("China",Message("party.protocol.ChinaInfo",("Sid","fake-test-not-a-credential"))));
var auth=new AuthHandoff("stub",DateTime.UtcNow,"127.0.0.1",port,"fixture",Convert.ToBase64String(protocol.MessageBytes(login)),3,2,1);
using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
NetworkStream? stream=null;
var server=Task.Run(async()=>
{
    using var peer=await listener.AcceptTcpClientAsync(stop.Token);stream=peer.GetStream();
    while(!stop.IsCancellationRequested)
    {
        Frame request;try{request=await Frame.ReadAsync(stream,stop.Token);}catch(OperationCanceledException){return;}catch(EndOfStreamException){return;}
        object response;
        switch(request.Command)
        {
            case 5001: response=Message("party.protocol.ConnectS2C",("SessionId",555L),("Player",Player(99,0)));break;
            case 5191: response=Message("party.protocol.WatchJoinRoomS2C",("RoomId",123L),("RoomServerId",456L),("MapType",4),("Players",new[]{Player(1,0,Card(20010,11)),Player(2,1,Card(10003,22))}));break;
            case 5193:
                var room=Message("party.model.Room",("Id",123L),("State",25),("MapType",4),("WatchPlayers",new[]{Player(99,9)}),("Players",new[]{Player(1,0,Card(20010,11)),Player(2,1,Card(10003,22))}));
                response=Message("party.protocol.WatchRefreshRoomStateS2C",("RoomId",123L),("Room",room));break;
            case 5195: response=Message("party.protocol.WatchExitRoomS2C");break;
            case 5003: response=Message("party.protocol.HeartbeatS2C");break;
            default:throw new Exception("Unexpected outgoing command "+request.Command);
        }
        Check(request.Command==5001?request.SessionId==0:request.SessionId==555,"outgoing session "+request.Command);
        await stream.WriteAsync(new Frame(555,(short)(request.Command+1),request.Upsn,0,0,protocol.MessageBytes(response),3,2,1).Encode(),stop.Token);
    }
},stop.Token);
await using var client=new SpectatorClient(new ProtocolMessages(directory),auth);
await client.Login(stop.Token);Check(client.Snapshot()==null,"login has no spectator authority");
await client.Watch("fake-loopback-code",stop.Token);Check(client.Snapshot()!=null,"official watcher room enables snapshot");
await client.Focus(2,stop.Token);
var snapshot=JsonSerializer.SerializeToElement(client.Snapshot());
Check(snapshot.GetProperty("playerId").GetString()=="2","explicit spectator focus");
// Simulate a masked authoritative replacement, which must discard previous known hand.
var masked=Message("party.protocol.RoomHeroCardChangeS2C",("PlayerId",2L),("Cards",new[]{Card(-1,22)}));
await stream!.WriteAsync(new Frame(555,1109,0,1,0,protocol.MessageBytes(masked)).Encode(),stop.Token);
var deadline=DateTime.UtcNow.AddSeconds(2);
while(JsonSerializer.SerializeToElement(client.Snapshot()).GetProperty("cards").GetArrayLength()!=0&&DateTime.UtcNow<deadline)await Task.Delay(10,stop.Token);
var maskedSnapshot=JsonSerializer.SerializeToElement(client.Snapshot());
Check(maskedSnapshot.GetProperty("cards").GetArrayLength()==0,"masked hand suppresses old cards");
var roster=maskedSnapshot.GetProperty("players").EnumerateArray().ToArray();
Check(!roster.Single(p=>p.GetProperty("playerId").GetString()=="2").GetProperty("handKnown").GetBoolean()
 &&roster.Single(p=>p.GetProperty("playerId").GetString()=="1").GetProperty("handKnown").GetBoolean(),"masked player does not suppress other authorized hands");
await client.Leave(stop.Token);Check(client.Snapshot()==null,"leave clears spectator authority");
await client.DisposeAsync();stop.Cancel();try{await server;}catch(OperationCanceledException){}
using(var versionListener=new TcpListener(IPAddress.Loopback,0))
{
    versionListener.Start();int versionPort=((IPEndPoint)versionListener.LocalEndpoint).Port;
    var tooNewServer=Task.Run(async()=>{using var peer=await versionListener.AcceptTcpClientAsync();var s=peer.GetStream();var req=await Frame.ReadAsync(s);await s.WriteAsync(new Frame(555,5002,req.Upsn,0,0,Array.Empty<byte>(),4,2,1).Encode());});
    await using var restricted=new SpectatorClient(new ProtocolMessages(directory),auth with{Port=versionPort});
    bool rejected=false;try{await restricted.Login(CancellationToken.None);}catch(InvalidDataException){rejected=true;}
    Check(rejected,"newer server resource version rejected");
    var diagnostic=JsonSerializer.SerializeToElement(restricted.Status());
    Check(diagnostic.GetProperty("failureStage").GetString()=="server_resource_version" && diagnostic.GetProperty("lastFrame").GetProperty("version1").GetInt32()==4,"nonsecret protocol version diagnostics");
    await tooNewServer;
}
Console.WriteLine($"Integration smoke: {pass} checks passed; loopback only.");
