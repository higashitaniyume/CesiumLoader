using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StandaloneSpectator;

/// <summary>One TCP connection. No reconnect, retry, credential storage, or logging.</summary>
public sealed class NativeTcpClient : IAsyncDisposable
{
    private sealed record Pending(short Command, TaskCompletionSource<Frame> Result);
    private readonly object gate = new();
    private readonly Dictionary<long, Pending> pending = new();
    private readonly TcpClient tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Frame> pushes = Channel.CreateBounded<Frame>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly AsyncLocal<bool> inCallback = new();
    private readonly TimeSpan rpcTimeout;
    private readonly Func<short, short> responseCommand;
    private NetworkStream? stream;
    private Task connectTask = Task.CompletedTask;
    private Task readerTask = Task.CompletedTask;
    private Task callbackTask = Task.CompletedTask;
    private Exception? terminalError;
    private bool started;
    private long sequence = 100;
    private long sessionId;

    public NativeTcpClient(TimeSpan? rpcTimeout = null, Func<short, short>? responseCommand = null)
    {
        this.rpcTimeout = rpcTimeout ?? TimeSpan.FromSeconds(20);
        if (this.rpcTimeout <= TimeSpan.Zero || this.rpcTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(rpcTimeout));
        this.responseCommand = responseCommand ?? (cmd => checked((short)(cmd + 1)));
    }

    /// <summary>Explicitly assigned by the application; never inferred from incoming frames.</summary>
    public long SessionId { get => Interlocked.Read(ref sessionId); set => Interlocked.Exchange(ref sessionId, value); }
    /// <summary>Ordered unsolicited callbacks on a separate pump, allowing callbacks to await RPCs.
    /// Callback exceptions or overflow of the 64-frame callback queue terminate the connection.
    /// Awaited callbacks should honor application cancellation; disposal awaits running callbacks.</summary>
    public event Func<Frame, Task>? UnsolicitedFrame;
    /// <summary>Runs synchronously in wire order for every validated incoming frame, before RPC
    /// completion or unsolicited dispatch. Keep handlers short; never synchronously wait on RPCs
    /// or disposal here. Handler exceptions terminate the connection and all pending calls.</summary>
    public event Action<Frame>? FrameReceived;
    public Exception? TerminalError { get { lock (gate) return terminalError; } }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfTerminated();
            if (started) throw new InvalidOperationException("This client supports one connection attempt.");
            started = true;
            return connectTask = ConnectCoreAsync(host, port, cancellationToken);
        }
    }

    private async Task ConnectCoreAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfTerminated();
                stream = tcp.GetStream();
                // Dispatch outside the lifecycle lock even when buffered I/O completes synchronously.
                readerTask = Task.Run(ReadLoopAsync);
                callbackTask = Task.Run(CallbackLoopAsync);
            }
        }
        catch (Exception ex) { Terminate(ex); throw; }
    }

    public async Task<Frame> CallAsync(short cmd, byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        short expected = responseCommand(cmd);
        long upsn = Interlocked.Increment(ref sequence);
        if (upsn <= 100) throw new InvalidOperationException("UPSN sequence exhausted.");
        var frame = new Frame(SessionId, cmd, upsn, 0, 0, payload);
        var call = new Pending(expected, new(TaskCreationOptions.RunContinuationsAsynchronously));
        NetworkStream connection;
        lock (gate)
        {
            ThrowIfTerminated();
            connection = stream ?? throw new InvalidOperationException("ConnectAsync must complete before calling RPCs.");
            pending.Add(upsn, call);
        }
        using var timeout = new CancellationTokenSource(rpcTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, lifetime.Token);
        bool acquired = false;
        try
        {
            await writes.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (gate) ThrowIfTerminated();
            try { await connection.WriteAsync(frame.Encode(), linked.Token).ConfigureAwait(false); }
            catch (Exception ex) { Terminate(ex); throw; } // A partial write makes continuing unsafe.
            writes.Release(); acquired = false;
            return await call.Result.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new TimeoutException($"RPC command {cmd} timed out after {rpcTimeout}."); }
        catch (OperationCanceledException)
        {
            Exception? error;
            lock (gate) error = terminalError;
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            if (acquired) writes.Release();
            lock (gate) pending.Remove(upsn);
            // Observe faults if cancellation won the race against a connection failure.
            if (call.Result.Task.IsFaulted) _ = call.Result.Task.Exception;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                Frame frame = await Frame.ReadAsync(stream!, lifetime.Token).ConfigureAwait(false);
                Pending? call;
                lock (gate)
                {
                    pending.TryGetValue(frame.Upsn, out call);
                    if (call != null && frame.Command != call.Command)
                        throw new InvalidDataException($"RPC response command {frame.Command} does not match expected {call.Command}.");
                    }
                FrameReceived?.Invoke(frame);
                if (call != null)
                {
                    lock (gate) pending.Remove(frame.Upsn);
                    call.Result.TrySetResult(frame);
                }
                else if (!pushes.Writer.TryWrite(frame))
                    throw new IOException("Unsolicited callback queue capacity exceeded.");
            }
        }
        catch (Exception ex) { Terminate(ex); }
    }

    private async Task CallbackLoopAsync()
    {
        try
        {
            await foreach (Frame frame in pushes.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var callbacks = UnsolicitedFrame;
                if (callbacks == null) continue;
                inCallback.Value = true;
                try
                {
                    foreach (Func<Frame, Task> callback in callbacks.GetInvocationList())
                        await callback(frame).ConfigureAwait(false);
                }
                finally { inCallback.Value = false; }
            }
        }
        catch (Exception ex) { Terminate(ex); }
    }

    private void ThrowIfTerminated()
    {
        if (terminalError != null) throw new IOException("Transport has terminated.", terminalError);
    }

    private void Terminate(Exception error)
    {
        lock (gate)
        {
            if (terminalError != null) return;
            terminalError = error;
            foreach (Pending call in pending.Values) call.Result.TrySetException(error);
            pending.Clear();
        }
        lifetime.Cancel();
        tcp.Dispose();
        pushes.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Terminate(new ObjectDisposedException(nameof(NativeTcpClient)));
        Task connect, reader, callbacks;
        lock (gate) { connect = connectTask; reader = readerTask; callbacks = callbackTask; }
        try { await connect.ConfigureAwait(false); } catch { /* ConnectAsync reports its own failure. */ }
        await reader.ConfigureAwait(false);
        // A callback may itself dispose this client; never await that callback from itself.
        if (!inCallback.Value) await callbacks.ConfigureAwait(false);
        // Semaphore/CTS remain valid for concurrent CallAsync finally blocks and repeated disposal.
    }
}
