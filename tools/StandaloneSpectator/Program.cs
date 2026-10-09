using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StandaloneSpectator;

string? protocolDirectory=null, handoffDirectory=null, cachePath=null, webPath=null, dataPath=null;
int port=18743;
for(int i=0;i<args.Length;i++)
{
    if(args[i]=="--protocol"&&i+1<args.Length)protocolDirectory=Path.GetFullPath(args[++i]);
    else if(args[i]=="--handoff"&&i+1<args.Length)handoffDirectory=Path.GetFullPath(args[++i]);
    else if(args[i]=="--credential-cache"&&i+1<args.Length)cachePath=Path.GetFullPath(args[++i]);
    else if(args[i]=="--web-root"&&i+1<args.Length)webPath=Path.GetFullPath(args[++i]);
    else if(args[i]=="--data"&&i+1<args.Length)dataPath=Path.GetFullPath(args[++i]);
    else if(args[i]=="--port"&&i+1<args.Length&&int.TryParse(args[++i],out var p)&&p>0&&p<=65535)port=p;
    else throw new ArgumentException("Usage: --protocol <extracted_dlls> --handoff <auth-handoff-directory> [--port 18743]");
}
if(protocolDirectory==null||handoffDirectory==null)throw new ArgumentException("--protocol and --handoff are required.");
var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
CredentialStore? credentialStore=cachePath==null?null:new CredentialStore(cachePath);
SpectatorClient? client=credentialStore?.Exists==true?new SpectatorClient(new ProtocolMessages(protocolDirectory),credentialStore.Load()):null;
using var authGate=new SemaphoreSlim(1,1);
SpectatorCatalog? catalog=dataPath==null?null:new SpectatorCatalog(protocolDirectory,dataPath);
var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=Array.Empty<string>(),WebRootPath=webPath});
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k=>{k.Listen(IPAddress.Loopback,port);k.Limits.MaxRequestBodySize=4096;});
var app=builder.Build();
app.Use(async(ctx,next)=>
{
    ctx.Response.Headers.CacheControl="no-store";
    ctx.Response.Headers["X-Frame-Options"]="DENY";
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    if(ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")){ctx.Response.StatusCode=403;return;}
    bool uiFile=ctx.Request.Path=="/"||ctx.Request.Path.StartsWithSegments("/assets")||ctx.Request.Path=="/favicon.svg";
    bool uiConfig=ctx.Request.Path=="/ui/config";
    if(uiConfig&&(ctx.Request.Headers["X-Spectator-UI"]!="1"||ctx.Request.Headers["Sec-Fetch-Site"]=="cross-site")){ctx.Response.StatusCode=403;return;}
    if(!uiFile&&!uiConfig&&ctx.Request.Path!="/health"&&!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()),Encoding.UTF8.GetBytes("Bearer "+token)))
    {ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="unauthorized"});return;}
    try{await next(ctx);}
    catch(OperationCanceledException){if(!ctx.Response.HasStarted)ctx.Response.StatusCode=408;}
    catch(Exception e)
    {
        // Never serialize exceptions or raw protocol payload: authentication may contain secrets.
        var code=e is ArgumentException or JsonException or FormatException ? 400 : e is TimeoutException ? 504 : e is InvalidOperationException ? 409 : 503;
        if(!ctx.Response.HasStarted){ctx.Response.StatusCode=code;await ctx.Response.WriteAsJsonAsync(new{error=e is TimeoutException?"operation_timeout":"operation_failed",category=e.GetType().Name,serverError=e is OfficialRpcException rpc?(short?)rpc.Code:null});}
    }
});
if(webPath!=null){app.UseDefaultFiles();app.UseStaticFiles();}
app.MapGet("/ui/config",()=>Results.Json(new{token}));
app.MapGet("/health",()=>Results.Json(new{status="ok",implementation="standalone_tcp",unityRuntimeLoaded=false}));
app.MapGet("/status",()=>Results.Json(client?.Status()??new{status="authentication_required"}));
app.MapPost("/auth/import",async(HttpContext ctx)=>
{
    await authGate.WaitAsync(ctx.RequestAborted);
    try
    {
        if(client!=null)throw new InvalidOperationException("Authentication already loaded; restart process to change account.");
        Directory.CreateDirectory(handoffDirectory);
        string requestPath=Path.Combine(handoffDirectory,"request.json"),responsePath=Path.Combine(handoffDirectory,"response.json");
        if(File.Exists(requestPath)||File.Exists(responsePath))throw new InvalidOperationException("Existing handoff must be inspected before use.");
        string nonce=Guid.NewGuid().ToString("N"),temp=requestPath+".tmp";
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(new{nonce}),ctx.RequestAborted);
        File.Move(temp,requestPath);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while(!deadline.IsCancellationRequested)
            {
                if(File.Exists(responsePath))
                {
                    var bytes=await File.ReadAllBytesAsync(responsePath,deadline.Token);
                    if(bytes.Length>65536)throw new InvalidDataException("Handoff too large");
                    var handoff=JsonSerializer.Deserialize<AuthHandoff>(bytes,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Handoff missing");
                    if(handoff.Nonce!=nonce)throw new InvalidDataException("Nonce mismatch");
                    var age=DateTime.UtcNow-handoff.CapturedUtc.ToUniversalTime();
                    if(age<TimeSpan.FromSeconds(-2)||age>TimeSpan.FromSeconds(30)||handoff.Port<1||handoff.Port>65535||string.IsNullOrWhiteSpace(handoff.Host)||string.IsNullOrEmpty(handoff.ConnectRequestBase64))throw new InvalidDataException("Handoff unavailable");
                    if(handoff.ProtocolVersion1 is <0 or >255 || handoff.ProtocolVersion2 is <0 or >255 || handoff.ProtocolVersion3 is <0 or >255)
                        throw new InvalidDataException("Invalid resource version");
                    credentialStore?.Save(handoff);
                    client=new SpectatorClient(new ProtocolMessages(protocolDirectory),handoff);
                    File.Delete(responsePath);
                    Array.Clear(bytes);
                    return Results.Json(new{status="authentication_loaded",next="Close the game before POST /login to avoid concurrent account sessions."});
                }
                await Task.Delay(200,deadline.Token);
            }
            throw new TimeoutException("Handoff not ready");
        }
        finally{if(File.Exists(requestPath))File.Delete(requestPath);}
    }
    finally{authGate.Release();}
});
SpectatorClient Current()=>client??throw new InvalidOperationException("Import authentication first.");
app.MapPost("/login",async(HttpContext ctx)=>{await Current().Login(ctx.RequestAborted);return Results.Json(new{status="logged_in"});});
app.MapPost("/watch",async(HttpContext ctx)=>
{
    var body=await ReadString(ctx,"watchCode");await Current().Watch(body,ctx.RequestAborted);return Results.Json(Current().Status());
});
app.MapPost("/focus",async(HttpContext ctx)=>
{
    var text=await ReadString(ctx,"playerId");if(!long.TryParse(text,out var id)||id<=0)throw new ArgumentException("Invalid playerId");await Current().Focus(id,ctx.RequestAborted);return Results.Json(new{status="done"});
});
app.MapPost("/leave",async(HttpContext ctx)=>{await Current().Leave(ctx.RequestAborted);return Results.Json(new{status="done"});});
app.MapGet("/state",()=>{var snapshot=Current().Snapshot();return snapshot==null?Results.Json(new{error="state_unavailable"},statusCode:503):Results.Json(catalog==null?snapshot:catalog.Enrich(snapshot));});
Console.WriteLine($"StandaloneSpectator listening on http://127.0.0.1:{port}");
Console.WriteLine("Bearer token: "+token);
try{await app.RunAsync();}finally{if(client!=null)await client.DisposeAsync();}

static async Task<string> ReadString(HttpContext ctx,string key)
{
    using var document=await JsonDocument.ParseAsync(ctx.Request.Body,new JsonDocumentOptions{MaxDepth=4},ctx.RequestAborted);
    if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Object required");
    var props=document.RootElement.EnumerateObject().ToArray();
    if(props.Length!=1||props[0].Name!=key||props[0].Value.ValueKind!=JsonValueKind.String)throw new ArgumentException("Invalid request");
    var value=props[0].Value.GetString();if(string.IsNullOrWhiteSpace(value)||value.Length>128||value.Any(char.IsControl)||value!=value.Trim())throw new ArgumentException("Invalid value");return value;
}
