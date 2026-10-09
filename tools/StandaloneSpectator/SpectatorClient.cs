using System.Globalization;
using System.Text.Json;

namespace StandaloneSpectator;

public sealed class OfficialRpcException : InvalidOperationException
{
    public short Code { get; }
    public OfficialRpcException(short code) : base("Official server rejected the request") { Code=code; }
}

public sealed record AuthHandoff(string Nonce, DateTime CapturedUtc, string Host, int Port, string ClientVersion, string ConnectRequestBase64,
    int ProtocolVersion1=1, int ProtocolVersion2=0, int ProtocolVersion3=0);

public sealed record ProtocolFrameDiagnostic(short command, byte version1, byte version2, byte version3, short error, int payloadBytes);

public sealed class SpectatorClient : IAsyncDisposable
{
    readonly ProtocolMessages protocol;
    readonly AuthHandoff auth;
    readonly SemaphoreSlim operations = new(1, 1);
    readonly object stateGate = new();
    readonly CancellationTokenSource lifetime = new();
    NativeTcpClient? tcp;
    SpectatorState? state;
    Task heartbeat = Task.CompletedTask;
    DateTime lastHeartbeat;
    string status = "authentication_loaded";
    bool authenticated;
    bool uncertain;
    int disposed;
    ProtocolFrameDiagnostic? lastFrame;
    string? failureStage;
    readonly Dictionary<short, string> incoming = new()
    {
        [1001]="KickS2C", [1016]="GameFinishS2C", [1040]="UpdateHeroAttrS2C",
        [1109]="RoomHeroCardChangeS2C", [5008]="SyncRoomS2C",
        [5192]="WatchJoinRoomS2C", [5194]="WatchRefreshRoomStateS2C", [5196]="WatchExitRoomS2C"
    };
    public SpectatorClient(ProtocolMessages protocol, AuthHandoff auth) { this.protocol=protocol; this.auth=auth; }
    public object Status() { lock(stateGate) return new { status, authenticated, uncertain, connected=tcp?.TerminalError==null && tcp!=null, failureStage, lastFrame,
        acceptedVersion=new[]{auth.ProtocolVersion1,auth.ProtocolVersion2,auth.ProtocolVersion3}, spectator=state?.Diagnostic() }; }
    public object? Snapshot()
    {
        lock(stateGate)
        {
            if (!authenticated || uncertain || tcp?.TerminalError!=null || DateTime.UtcNow-lastHeartbeat>TimeSpan.FromSeconds(12) || state?.HasAuthority!=true) return null;
            return state.Snapshot();
        }
    }
    public async Task Login(CancellationToken ct)
    {
        await operations.WaitAsync(ct);
        try
        {
            if (tcp!=null) throw new InvalidOperationException("One login attempt per process; restart after failure.");
            var request=Convert.FromBase64String(auth.ConnectRequestBase64);
            var login=protocol.Parse("party.protocol.ConnectC2S",request);
            if (ProtocolMessages.Get(login,"Auth")?.ToString()!="China" || ProtocolMessages.Get(login,"China")==null)
                throw new InvalidOperationException("Only normal China channel authentication is accepted.");
            failureStage="tcp_connect";
            tcp=new NativeTcpClient();
            tcp.FrameReceived+=frame => ApplyPush(frame).GetAwaiter().GetResult();
            await tcp.ConnectAsync(auth.Host,auth.Port,ct);
            failureStage="login_rpc";
            var response=await tcp.CallAsync(5001,request,ct);
            Check(response,5002);
            failureStage="login_protobuf";
            var data=protocol.Parse("party.protocol.ConnectS2C",response.Payload.ToArray());
            long session=Long(ProtocolMessages.Get(data,"SessionId"));
            var player=ProtocolMessages.Get(data,"Player");
            long account=Long(player==null?null:ProtocolMessages.Get(player,"Id"));
            if(session==0 || account==0) throw new InvalidDataException("Login response lacks an authenticated session/player.");
            lock(stateGate) { tcp.SessionId=session; state=new SpectatorState(account); authenticated=true; status="logged_in"; failureStage=null; lastHeartbeat=DateTime.UtcNow; }
            heartbeat=HeartbeatLoop();
        }
        catch { lock(stateGate) { status="login_failed"; authenticated=false; } throw; }
        finally { operations.Release(); }
    }
    public async Task Watch(string code,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(code)||code.Length>128||code.Any(char.IsControl))throw new ArgumentException("Invalid watch code.");
        await operations.WaitAsync(ct);
        try
        {
            EnsureLoggedIn();
            lock(stateGate) { if(state!.RoomId!=0)throw new InvalidOperationException("Leave the current room first."); status="joining"; }
            var query=await Request(5191,"WatchJoinRoomC2S",new(){{"WatchCode",code}},ct);
            var joined=protocol.Parse("party.protocol.WatchJoinRoomS2C",query.Payload.ToArray());
            int map=Convert.ToInt32(ProtocolMessages.Get(joined,"MapType"),CultureInfo.InvariantCulture);
            // Supported official multiplayer PVE modes only, never PVP/single-player.
            if(map!=4&&map!=12) { lock(stateGate){state!.Reset("Unsupported spectator mode");status="logged_in";} throw new InvalidOperationException("Only multiplayer PVE spectator rooms are supported."); }
            var refresh=await Request(5193,"WatchRefreshRoomStateC2S",new()
            { {"RoomId",ProtocolMessages.Get(joined,"RoomId")!},{"RoomServerId",ProtocolMessages.Get(joined,"RoomServerId")!} },ct);
            lock(stateGate){status=state!.IsReady?"spectating":"spectator_data_unavailable";}
        }
        catch(TimeoutException){lock(stateGate){uncertain=true;state?.Reset("RPC timeout; restart required");status="uncertain";}throw;}
        finally {operations.Release();}
    }
    public async Task Focus(long player,CancellationToken ct)
    {
        await operations.WaitAsync(ct);
        try {EnsureLoggedIn();lock(stateGate){state!.Focus(player);}}
        finally {operations.Release();}
    }
    public async Task Leave(CancellationToken ct)
    {
        await operations.WaitAsync(ct);
        try {EnsureLoggedIn();var result=await Request(5195,"WatchExitRoomC2S",new(),ct);lock(stateGate){state!.Reset("Left official spectator room");status="logged_in";}}
        catch(TimeoutException){lock(stateGate){uncertain=true;state?.Reset("Exit timeout; restart required");status="uncertain";}throw;}
        finally {operations.Release();}
    }
    async Task<Frame> Request(short command,string type,Dictionary<string,object?> values,CancellationToken ct)
    {
        var message=protocol.Create("party.protocol."+type,values);
        var response=await tcp!.CallAsync(command,protocol.MessageBytes(message),ct);
        Check(response,(short)(command+1));return response;
    }
    void ValidateVersion(Frame frame)
    {
        // Official GameSettings.VER1/2 come from RES_VERSION, not outgoing header 1/0/0.
        if(frame.Version1>auth.ProtocolVersion1 || frame.Version2>auth.ProtocolVersion2)
        { failureStage="server_resource_version"; throw new InvalidDataException("Unsupported server resource version."); }
    }
    void Check(Frame frame,short expected)
    {if(frame.Command!=expected)throw new InvalidDataException("Unexpected response command.");if(frame.Error!=0)throw new OfficialRpcException(frame.Error);ValidateVersion(frame);}
    void EnsureLoggedIn()
    {lock(stateGate)if(!authenticated||uncertain||tcp==null||tcp.TerminalError!=null)throw new InvalidOperationException("Login required or session unavailable.");}
    Task ApplyPush(Frame frame)
    {
        lock(stateGate)lastFrame=new(frame.Command,frame.Version1,frame.Version2,frame.Version3,frame.Error,frame.Payload.Length);
        ValidateVersion(frame);
        if(frame.Command==1001) {lock(stateGate){authenticated=false;status="server_kick";state?.Reset("Server disconnected account");}return Task.CompletedTask;}
        if(frame.Error!=0) {lock(stateGate){state?.Reset("Server event error");status="spectator_data_unavailable";}return Task.CompletedTask;}
        ApplyKnown(frame);return Task.CompletedTask;
    }
    void ApplyKnown(Frame frame)
    {
        if(!incoming.TryGetValue(frame.Command,out var type))return;
        var parsed=protocol.Parse("party.protocol."+type,frame.Payload.ToArray());
        lock(stateGate){state?.Apply(frame.Command,parsed);if(frame.Command==1016||frame.Command==5196)status="logged_in";}
    }
    async Task HeartbeatLoop()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                await Task.Delay(5000,lifetime.Token);
                lock(stateGate)if(!authenticated)return;
                var response=await Request(5003,"HeartbeatC2S",new(){{"Client",Environment.TickCount64}},lifetime.Token);
                lock(stateGate)lastHeartbeat=DateTime.UtcNow;
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch{lock(stateGate){authenticated=false;state?.Reset("Connection heartbeat failed");status="disconnected";}}
    }
    static long Long(object? value)=>value==null?0:Convert.ToInt64(value,CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        lifetime.Cancel();if(tcp!=null)await tcp.DisposeAsync();await heartbeat;protocol.Dispose();lifetime.Dispose();operations.Dispose();
    }
}
