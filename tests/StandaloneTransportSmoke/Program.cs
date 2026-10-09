using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using StandaloneSpectator;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Header offsets, signed values, defensive payload copy", Header),
    ("Split header/payload, zero payload and unsolicited delivery", SplitReads),
    ("Negative payload length rejects and terminates pending", () => Malformed(-1)),
    ("Oversized payload length rejects and terminates pending", () => Malformed(Frame.MaxPayloadLength + 1)),
    ("Truncated header disconnect terminates pending", () => Truncated(12)),
    ("Truncated payload disconnect terminates pending", () => Truncated(37)),
    ("Concurrent writes and out-of-order UPSN correlation", Concurrent),
    ("Wrong correlated response command fails all pending", WrongCommand),
    ("Disconnect fails all pending and prevents reuse", Disconnect),
    ("Configurable timeout, cancellation, late response", TimeoutAndCancellation),
    ("Callback can await RPC and dispose safely", CallbackRpc),
    ("Callback exception terminates pending", CallbackError),
    ("Repeated concurrent disposal terminates pending", Disposal),
    ("Outgoing length bound enforced before send", OutgoingBound),
    ("Custom response mapping and server error retained", CustomMapping),
    ("Slow callback queue overflow fails pending without deadlock", CallbackOverflow),
    ("Every frame observed in wire order before RPC completion", OrderedFrames),
};
foreach (var test in tests)
{
    await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"All {tests.Length} standalone transport smoke checks passed (loopback only).");

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task<T> Fails<T>(Task task) where T : Exception
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (T ex) { return ex; }
    throw new Exception("Expected " + typeof(T).Name);
}
static Frame Reply(Frame request, byte[]? payload = null) => new(request.SessionId,
    (short)(request.Command + 1), request.Upsn, 500, 0, payload ?? request.Payload.ToArray());
static async Task Header()
{
    byte[] payload = [1, 2, 3];
    var frame = new Frame(-123456789012345, -1234, 987654321012345, -44444444444, -321, payload, 7, 8, 9);
    payload[0] = 99;
    byte[] wire = frame.Encode();
    Check(wire.Length == 38 && BinaryPrimitives.ReadInt32BigEndian(wire) == 3, "length offset");
    Check(BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(4)) == frame.SessionId, "session offset");
    Check(BinaryPrimitives.ReadInt16BigEndian(wire.AsSpan(12)) == frame.Command, "cmd offset");
    Check(wire[14] == 7 && wire[15] == 8 && wire[16] == 9, "versions offset");
    Check(BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(17)) == frame.Upsn, "upsn offset");
    Check(BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(25)) == frame.Downsn, "downsn offset");
    Check(BinaryPrimitives.ReadInt16BigEndian(wire.AsSpan(33)) == frame.Error && wire[35] == 1, "err/copy");
    Frame decoded = await Frame.ReadAsync(new MemoryStream(wire));
    Check(decoded.Payload.SequenceEqual(new byte[] { 1, 2, 3 }), "payload roundtrip");
}
static async Task SplitReads()
{
    await using var fixture = await Fixture.Create();
    var push = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.UnsolicitedFrame += f => { push.TrySetResult(f); return Task.CompletedTask; };
    fixture.Client.SessionId = 1234;
    Task<Frame> call = fixture.Client.CallAsync(5191, [10, 20]);
    Frame request = await fixture.Read();
    Check(request.Upsn == 101 && request.SessionId == 1234 && request.Downsn == 0 && request.Error == 0, "outgoing defaults");
    Check(request.Version1 == 1 && request.Version2 == 0 && request.Version3 == 0, "outgoing versions");
    byte[] bytes = Reply(request, [3, 4, 5]).Encode();
    foreach (byte b in bytes) await fixture.Stream.WriteAsync(new byte[] { b });
    Frame response = await call;
    Check(response.Command == 5192 && response.Payload.SequenceEqual(new byte[] { 3, 4, 5 }), "split response");
    await fixture.Send(new Frame(999, 1002, 0, 42, 0, []));
    Check((await push.Task).Downsn == 42 && fixture.Client.SessionId == 1234, "unsolicited/session explicit");
}
static async Task Malformed(int length)
{
    await using var fixture = await Fixture.Create();
    Task<Frame> call = fixture.Client.CallAsync(5001, []);
    await fixture.Read();
    var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length);
    await fixture.Stream.WriteAsync(header);
    await Fails<InvalidDataException>(call);
    Check(fixture.Client.TerminalError is InvalidDataException, "malformed not terminal");
}
static async Task Truncated(int bytes)
{
    await using var fixture = await Fixture.Create();
    Task<Frame> call = fixture.Client.CallAsync(5001, []);
    Frame request = await fixture.Read();
    await fixture.Stream.WriteAsync(Reply(request, [1, 2, 3, 4, 5]).Encode().AsMemory(0, bytes));
    fixture.Peer.Dispose();
    await Fails<EndOfStreamException>(call);
}
static async Task Concurrent()
{
    await using var fixture = await Fixture.Create();
    var calls = Enumerable.Range(0, 32).Select(i =>
    {
        var payload = Enumerable.Repeat((byte)i, 32 * 1024).ToArray();
        return fixture.Client.CallAsync((short)(5101 + i * 2), payload);
    }).ToArray();
    var requests = new List<Frame>();
    for (int i = 0; i < calls.Length; i++) requests.Add(await fixture.Read());
    Check(requests.Select(f => f.Upsn).Distinct().Count() == 32, "duplicate UPSN");
    foreach (Frame f in requests)
        Check(f.Payload.All(b => b == (f.Command - 5101) / 2), "interleaved payload");
    foreach (Frame f in requests.AsEnumerable().Reverse()) await fixture.Send(Reply(f));
    Frame[] results = await Task.WhenAll(calls);
    for (int i = 0; i < results.Length; i++)
        Check(results[i].Command == 5102 + i * 2 && results[i].Payload.All(b => b == i), "wrong correlation");
}
static async Task WrongCommand()
{
    await using var fixture = await Fixture.Create();
    Task<Frame> a = fixture.Client.CallAsync(5191, []), b = fixture.Client.CallAsync(5193, []);
    Frame request = await fixture.Read(); await fixture.Read();
    await fixture.Send(new Frame(0, 1002, request.Upsn, 0, 0, []));
    await Fails<InvalidDataException>(a); await Fails<InvalidDataException>(b);
}
static async Task Disconnect()
{
    await using var fixture = await Fixture.Create();
    var calls = Enumerable.Range(0, 4).Select(_ => fixture.Client.CallAsync(5001, [])).ToArray();
    for (int i = 0; i < calls.Length; i++) await fixture.Read();
    fixture.Peer.Dispose();
    foreach (Task call in calls) await Fails<IOException>(call);
    await Fails<IOException>(fixture.Client.CallAsync(5001, []));
    await Fails<IOException>(Task.Run(() => fixture.Client.ConnectAsync("127.0.0.1", fixture.Port)));
}
static async Task TimeoutAndCancellation()
{
    await using var fixture = await Fixture.Create(TimeSpan.FromMilliseconds(200));
    var late = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.UnsolicitedFrame += f => { late.TrySetResult(f); return Task.CompletedTask; };
    Task<Frame> timeout = fixture.Client.CallAsync(5001, []);
    Frame request = await fixture.Read();
    await Fails<TimeoutException>(timeout);
    await fixture.Send(Reply(request));
    Check((await late.Task).Upsn == request.Upsn, "late reply not unsolicited");
    using var cancel = new CancellationTokenSource();
    Task<Frame> canceled = fixture.Client.CallAsync(5001, [], cancel.Token);
    await fixture.Read(); cancel.Cancel();
    await Fails<OperationCanceledException>(canceled);
    Task<Frame> good = fixture.Client.CallAsync(5001, []);
    Frame next = await fixture.Read(); await fixture.Send(Reply(next)); await good;
    Check(fixture.Client.TerminalError == null, "timeout/cancellation terminated completed write");
}
static async Task CallbackRpc()
{
    await using var fixture = await Fixture.Create();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.UnsolicitedFrame += async _ =>
    {
        await fixture.Client.CallAsync(5001, []);
        await fixture.Client.DisposeAsync();
        done.SetResult();
    };
    await fixture.Send(new Frame(0, 1002, 0, 1, 0, []));
    Frame request = await fixture.Read(); await fixture.Send(Reply(request));
    await done.Task;
}
static async Task CallbackError()
{
    await using var fixture = await Fixture.Create();
    fixture.Client.UnsolicitedFrame += _ => throw new InvalidOperationException("fixture callback failure");
    Task<Frame> call = fixture.Client.CallAsync(5001, []);
    await fixture.Read(); await fixture.Send(new Frame(0, 1002, 0, 1, 0, []));
    await Fails<InvalidOperationException>(call);
}
static async Task Disposal()
{
    await using var fixture = await Fixture.Create();
    Task<Frame> call = fixture.Client.CallAsync(5001, []); await fixture.Read();
    await Task.WhenAll(fixture.Client.DisposeAsync().AsTask(), fixture.Client.DisposeAsync().AsTask());
    await Fails<Exception>(call);
}
static async Task OutgoingBound()
{
    await using var fixture = await Fixture.Create();
    await Fails<ArgumentOutOfRangeException>(fixture.Client.CallAsync(5001, new byte[Frame.MaxPayloadLength + 1]));
    Task<Frame> valid = fixture.Client.CallAsync(5001, []);
    Frame request = await fixture.Read(); await fixture.Send(Reply(request)); await valid;
}

static async Task CustomMapping()
{
    await using var fixture = await Fixture.Create(responseCommand: _ => 5126);
    Task<Frame> call = fixture.Client.CallAsync(5001, []);
    Frame request = await fixture.Read();
    await fixture.Send(new Frame(0, 5126, request.Upsn, 22, -7, [1]));
    Frame response = await call;
    Check(response.Command == 5126 && response.Error == -7, "custom mapping/error dropped");
}

static async Task CallbackOverflow()
{
    await using var fixture = await Fixture.Create();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.UnsolicitedFrame += async _ => { entered.TrySetResult(); await release.Task; };
    Task<Frame> call = fixture.Client.CallAsync(5001, []); await fixture.Read();
    try
    {
        var push = new Frame(0, 1002, 0, 1, 0, []);
        await fixture.Send(push); await entered.Task;
        byte[] overflow = Enumerable.Range(0, 65).SelectMany(_ => push.Encode()).ToArray();
        await fixture.Stream.WriteAsync(overflow);
        await Fails<IOException>(call);
        Check(fixture.Client.TerminalError?.Message.Contains("capacity") == true, "queue did not terminate explicitly");
    }
    finally { release.TrySetResult(); }
}
static async Task OrderedFrames()
{
    await using var fixture = await Fixture.Create();
    var seen = new System.Collections.Concurrent.ConcurrentQueue<short>();
    var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<Frame>? call = null;
    fixture.Client.FrameReceived += frame =>
    {
        if (frame.Upsn > 0) Check(call?.IsCompleted == false, "RPC completed before observation");
        seen.Enqueue(frame.Command);
        if (frame.Command == 1003) final.TrySetResult();
    };
    call = fixture.Client.CallAsync(5001, []);
    Frame request = await fixture.Read();
    byte[] bytes = new Frame(0, 1002, 0, 1, 0, []).Encode()
        .Concat(Reply(request).Encode()).Concat(new Frame(0, 1003, 0, 2, 0, []).Encode()).ToArray();
    await fixture.Stream.WriteAsync(bytes); await call; await final.Task;
    Check(seen.SequenceEqual(new short[] { 1002, 5002, 1003 }), "wire order lost");
}

sealed class Fixture : IAsyncDisposable
{
    public NativeTcpClient Client { get; }
    public TcpClient Peer { get; }
    public NetworkStream Stream => Peer.GetStream();
    public int Port { get; }
    private Fixture(NativeTcpClient client, TcpClient peer, int port) { Client = client; Peer = peer; Port = port; }
    public static async Task<Fixture> Create(TimeSpan? timeout = null, Func<short, short>? responseCommand = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new NativeTcpClient(timeout, responseCommand);
        try
        {
            Task connect = client.ConnectAsync("127.0.0.1", port);
            TcpClient peer = await listener.AcceptTcpClientAsync(); peer.NoDelay = true;
            await connect;
            return new Fixture(client, peer, port);
        }
        catch { await client.DisposeAsync(); throw; }
        finally { listener.Stop(); }
    }
    public Task<Frame> Read() => Frame.ReadAsync(Stream);
    public ValueTask Send(Frame frame) => Stream.WriteAsync(frame.Encode());
    public async ValueTask DisposeAsync() { Peer.Dispose(); await Client.DisposeAsync(); }
}
