// Async JSON-RPC client over ITransport + IAhpSerializer.
// Faithful port of clients/go/ahp/client.go.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

// ─── Configuration ────────────────────────────────────────────────────────────

/// <summary>
/// Optional transport liveness policy for an <see cref="AhpClient"/>. Port of the
/// Swift <c>AHPKeepAlivePolicy</c> (clients/swift/.../AHPClientConfig.swift).
/// <para>
/// Keep-alive is disabled by default. When enabled, the client sends periodic
/// transport-level pings if the configured transport implements
/// <see cref="IKeepAliveTransport"/>; ping failures are treated as transport
/// failures and tear the client down.
/// </para>
/// </summary>
public sealed class KeepAlivePolicy
{
    private KeepAlivePolicy(bool isEnabled, TimeSpan interval, TimeSpan timeout)
    {
        IsEnabled = isEnabled;
        Interval = interval;
        Timeout = timeout;
    }

    /// <summary>Whether the keep-alive ping loop runs.</summary>
    public bool IsEnabled { get; }

    /// <summary>How often a ping is sent (only meaningful when <see cref="IsEnabled"/>).</summary>
    public TimeSpan Interval { get; }

    /// <summary>How long each ping waits for its pong before failing.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Do not run a keep-alive task. Mirrors Swift <c>.disabled</c>.</summary>
    public static KeepAlivePolicy Disabled { get; } =
        new(isEnabled: false, interval: TimeSpan.Zero, timeout: TimeSpan.Zero);

    /// <summary>
    /// Periodically send a transport-level ping. Mirrors Swift
    /// <c>.ping(interval:timeout:)</c>.
    /// </summary>
    public static KeepAlivePolicy Ping(TimeSpan interval, TimeSpan timeout) =>
        new(isEnabled: true, interval: interval, timeout: timeout);

    /// <summary>
    /// Convenience for the common WebSocket ping policy (30 s interval, 5 s
    /// timeout by default). Mirrors Swift <c>.enabled(interval:timeout:)</c>.
    /// </summary>
    public static KeepAlivePolicy Enabled(TimeSpan? interval = null, TimeSpan? timeout = null) =>
        Ping(interval ?? TimeSpan.FromSeconds(30), timeout ?? TimeSpan.FromSeconds(5));
}

/// <summary>Tuning knobs for an <see cref="AhpClient"/>.</summary>
public sealed class ClientConfig
{
    /// <summary>
    /// How long <see cref="AhpClient.RequestAsync{TParams,TResult}"/> waits for a
    /// response. Zero disables the timeout. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Capacity of each subscription's event channel. Excess events are dropped
    /// on a full channel (mirrors Go's <c>SubscriptionBuffer</c>). Defaults to 256.
    /// </summary>
    public int SubscriptionBufferCapacity { get; set; } = 256;

    /// <summary>
    /// Optional transport liveness policy. Defaults to
    /// <see cref="KeepAlivePolicy.Disabled"/>. Mirrors the Swift
    /// <c>AHPClientConfig.keepAlive</c> field.
    /// </summary>
    public KeepAlivePolicy KeepAlive { get; set; } = KeepAlivePolicy.Disabled;

    /// <summary>Returns a config with sensible defaults (30 s timeout, 256-message buffer).</summary>
    public static ClientConfig Default => new();
}

// ─── Connection state ──────────────────────────────────────────────────────────

/// <summary>
/// Connection state observable on <see cref="AhpClient.ConnectionState"/> and the
/// <see cref="AhpClient.CreateStateChangeStream"/> multicast stream. Port of the
/// Swift <c>ConnectionState</c> enum (clients/swift/.../AHPClientEvents.swift).
/// </summary>
public enum ConnectionState
{
    /// <summary>No active receive loop; the transport may or may not be open.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>The receive loop is running; the transport is treated as live.</summary>
    Connected,
}

// ─── Server-initiated request handling ─────────────────────────────────────────

/// <summary>
/// Handles a server-initiated JSON-RPC request. Return the result object to
/// reply with success; throw <see cref="AhpRpcException"/> to reply with that
/// JSON-RPC error. Receives the raw method name and the raw params element.
/// </summary>
/// <param name="method">The JSON-RPC method the server invoked.</param>
/// <param name="parameters">The raw params element, or <see langword="null"/> if absent.</param>
/// <returns>The result object to serialize into the success reply (may be null).</returns>
public delegate Task<object?> ServerRequestHandler(string method, JsonElement? parameters);

// ─── AhpClient ────────────────────────────────────────────────────────────────

/// <summary>
/// Async JSON-RPC client over a pluggable <see cref="ITransport"/>.
/// <para>
/// Create with <see cref="Connect"/> which spawns a background read loop.
/// All public methods are safe to call from multiple threads.
/// </para>
/// </summary>
public sealed class AhpClient : IAhpClient
{
    // ── State that lives for the client lifetime ──────────────────────────

    private readonly ITransport _transport;
    private readonly IAhpSerializer _serializer;
    private readonly ClientConfig _cfg;

    // Outbound queue (reader goroutine in Go; here driven by a Task).
    private readonly Channel<OutboundMessage> _outbound;

    // In-flight request correlation keyed by JSON-RPC id.
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<JsonElement>> _pending = new();

    // Per-URI subscription fan-out.
    private readonly object _subsLock = new();
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new();
    private readonly List<EventStream> _eventListeners = new();

    // Multicast connection-state fan-out. Guarded by `_subsLock` (same lock as the
    // event listeners — every fan-out path already takes it).
    private readonly List<StateChangeStream> _stateListeners = new();

    // Current connection state. `volatile` supplies the visibility a lock would
    // otherwise give for the lock-free `ConnectionState` reader.
    private volatile ConnectionStateBox _connectionState = new(AgentHostProtocol.ConnectionState.Connected);

    // Boxes the enum so it can live behind a `volatile` field (enums aren't valid
    // `volatile` targets directly).
    private sealed class ConnectionStateBox
    {
        public ConnectionState Value { get; }
        public ConnectionStateBox(ConnectionState value) => Value = value;
    }

    // Keep-alive ping loop (null when disabled or the transport isn't ping-capable).
    private readonly CancellationTokenSource _keepAliveCts = new();
    private readonly Task? _keepAliveTask;

    // Monotonically incrementing counters (no lock needed — Interlocked).
    private ulong _nextId = 1;
    private long _nextClientSeq = 1;

    // ── Test-only accessors (InternalsVisibleTo the test assembly) ─────────
    // Mirror the Swift client's `_pendingCount()` test hook so the cancellation
    // parity tests can observe the real pending-request bookkeeping (1 -> 0 on a
    // cancelled in-flight request) without widening the public API.

    /// <summary>The number of in-flight requests awaiting a response.</summary>
    internal int PendingRequestCount => _pending.Count;

    /// <summary>The number of subscriptions currently registered across all URIs —
    /// lets the lifecycle tests prove a direct Close()/Dispose() detaches from the
    /// registry (not just UnsubscribeAsync).</summary>
    internal int SubscriptionCount
    {
        get
        {
            lock (_subsLock)
            {
                int n = 0;
                foreach (var list in _subscriptions.Values) n += list.Count;
                return n;
            }
        }
    }

    internal int EventListenerCount { get { lock (_subsLock) { return _eventListeners.Count; } } }

    internal int StateListenerCount { get { lock (_subsLock) { return _stateListeners.Count; } } }

    /// <summary>
    /// The next JSON-RPC request id that would be minted. Lets the fast-fail
    /// parity test prove a pre-cancelled request did NOT mint an id (the counter
    /// is unchanged).
    /// </summary>
    internal ulong NextRequestId => Volatile.Read(ref _nextId);

    // Optional handler for server-initiated requests. Published reference, read
    // lock-free; `volatile` supplies the visibility a lock would otherwise give.
    private volatile ServerRequestHandler? _serverRequestHandler;

    // Lifecycle
    private readonly TaskCompletionSource _doneTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _shutdownStarted;  // 0 = running, 1 = shut down
    private Exception? _closeErr;

    private readonly Task _readerTask;
    private readonly Task _writerTask;

    // ── Inner types ───────────────────────────────────────────────────────

    private sealed class OutboundMessage
    {
        public JsonRpcMessage Message { get; }
        public TaskCompletionSource<bool>? Sent { get; }

        public OutboundMessage(JsonRpcMessage message, TaskCompletionSource<bool>? sent = null)
        {
            Message = message;
            Sent = sent;
        }
    }

    // ── Constructor / factory ─────────────────────────────────────────────

    private AhpClient(ITransport transport, ClientConfig cfg, IAhpSerializer serializer)
    {
        _transport = transport;
        _cfg = cfg;
        _serializer = serializer;
        _outbound = Channel.CreateBounded<OutboundMessage>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
            });
        _readerTask = Task.Run(RunReaderAsync);
        _writerTask = Task.Run(RunWriterAsync);

        // Start the keep-alive ping loop iff a ping policy is configured AND the
        // transport advertises the ping capability. Mirrors the Swift
        // `startKeepAliveIfNeeded()` guard (`case .ping` + `as? AHPKeepAliveTransport`).
        if (_cfg.KeepAlive.IsEnabled && _transport is IKeepAliveTransport pingTransport)
        {
            _keepAliveTask = Task.Run(() => RunKeepAliveAsync(pingTransport));
        }
    }

    /// <summary>
    /// Wires <paramref name="transport"/> to a new <see cref="AhpClient"/> and
    /// starts the background reader / writer tasks. The client owns the transport
    /// from this point.
    /// </summary>
    public static AhpClient Connect(
        ITransport transport,
        ClientConfig? config = null,
        IAhpSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var cfg = config ?? ClientConfig.Default;
        if (cfg.SubscriptionBufferCapacity <= 0) cfg.SubscriptionBufferCapacity = 256;
        return new AhpClient(transport, cfg, serializer ?? SystemTextJsonAhpSerializer.Default);
    }


    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="Task"/> that completes once the client begins teardown (either
    /// via <see cref="ShutdownAsync"/> or a transport failure).
    /// </summary>
    public Task Completion => _doneTcs.Task;

    /// <summary>
    /// The first error that triggered teardown, or <see langword="null"/> if the
    /// client is still running or was shut down cleanly.
    /// </summary>
    public Exception? Error => Volatile.Read(ref _closeErr);

    /// <summary>
    /// Gracefully tears down the client. In-flight requests complete with
    /// <see cref="AhpClientClosedException"/>. Subscriptions and event streams are
    /// closed. The underlying transport is closed too.
    /// Safe to call multiple times.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await ShutdownWithErrorAsync(null).ConfigureAwait(false);
        // Wait for both background tasks to exit.
        await Task.WhenAll(_readerTask, _writerTask).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Centralised idempotent teardown path. All shutdown paths funnel through here.
    /// </summary>
    /// <param name="cause">The failure that triggered teardown, or null for a clean shutdown.</param>
    /// <param name="fromKeepAlive">
    /// True when called from the keep-alive loop's own failure path (a ping failure).
    /// In that case the keep-alive task must NOT be awaited here — it is the very task
    /// running this teardown, so awaiting it would deadlock. The loop is already
    /// unwinding, so skipping the await is safe.
    /// </param>
    private async Task ShutdownWithErrorAsync(Exception? cause, bool fromKeepAlive = false)
    {
        if (Interlocked.CompareExchange(ref _shutdownStarted, 1, 0) != 0)
        {
            return; // Already shutting down.
        }

        Volatile.Write(ref _closeErr, cause);

        // Signal the done task so Done-waiters unblock.
        _doneTcs.TrySetResult();

        // Stop the keep-alive loop (no more pings once we're tearing down). Mirrors
        // the Swift `keepAliveTask?.cancel()` in both shutdown and failure paths.
        // Cancel, then await the loop to completion so the still-running loop can no
        // longer read `_keepAliveCts.Token`, then dispose the CTS — deterministic
        // teardown that disposes the CTS safely. When teardown was *triggered by* the
        // keep-alive loop itself (a ping failure flows into ShutdownWithErrorAsync from
        // inside `_keepAliveTask` with fromKeepAlive=true), awaiting `_keepAliveTask`
        // here would deadlock (the task would await itself), so skip the await in that
        // case — the loop is already unwinding and the captured token struct stays
        // readable even after the CTS is disposed.
        _keepAliveCts.Cancel();
        if (_keepAliveTask is not null && !fromKeepAlive)
        {
            try { await _keepAliveTask.ConfigureAwait(false); }
            catch { /* the loop's own failure path already ran; nothing to observe */ }
        }
        _keepAliveCts.Dispose();

        // Dispose the transport (the client owns it): DisposeAsync runs the close
        // handshake so any blocked ReceiveAsync unblocks AND releases the transport's
        // unmanaged handles (e.g. the ClientWebSocket socket + its send semaphore),
        // which CloseAsync alone does not. Bounded to 2s.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _transport.DisposeAsync().AsTask().WaitAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }

        // Complete the outbound channel so the writer exits.
        _outbound.Writer.TryComplete();

        // Fail every in-flight request.
        var shutdownEx = cause is null
            ? new AhpClientClosedException()
            : new AhpClientClosedException($"ahp: client shut down: {cause.Message}");

        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetException(shutdownEx);
            }
        }

        // Close every subscription and listener.
        List<Subscription> allSubs;
        List<EventStream> allListeners;
        lock (_subsLock)
        {
            allSubs = new List<Subscription>();
            foreach (var list in _subscriptions.Values)
                allSubs.AddRange(list);

            allListeners = new List<EventStream>(_eventListeners);
            _eventListeners.Clear();
        }
        // Each Close() runs the subscription's detach hook, which removes it from the
        // registry and decrements the active-subscriptions gauge (single teardown path).
        foreach (var sub in allSubs) sub.Close();
        foreach (var lst in allListeners) lst.Close();

        // Fan out a final `.Disconnected` transition, then finish the state-change
        // streams. Mirrors the Swift shutdown tail: `transition(to: .disconnected)`
        // immediately followed by `finishAllStateListeners()`. State streams (unlike
        // the event taps) deliver this terminal transition before completing, so a
        // consumer awaiting the stream sees `Disconnected` as the last item.
        Transition(AgentHostProtocol.ConnectionState.Disconnected);
        List<StateChangeStream> allStateListeners;
        lock (_subsLock)
        {
            allStateListeners = new List<StateChangeStream>(_stateListeners);
            _stateListeners.Clear();
        }
        foreach (var st in allStateListeners) st.Close();
    }

    // ── Connection state ──────────────────────────────────────────────────

    /// <summary>
    /// The current connection state, readable synchronously. Mirrors the Swift
    /// <c>connectionState</c> property. The client is <see cref="ConnectionState.Connected"/>
    /// from construction (the read/write loops start immediately in <see cref="Connect"/>)
    /// and transitions to <see cref="ConnectionState.Disconnected"/> on shutdown or
    /// transport failure.
    /// </summary>
    public ConnectionState ConnectionState => _connectionState.Value;

    /// <summary>
    /// Returns a fresh multicast <see cref="StateChangeStream"/> of future
    /// <see cref="ConnectionState"/> transitions. Mirrors the Swift
    /// <c>stateChanges</c> stream: each call returns an independent stream that
    /// delivers only transitions occurring after attachment; the current value is
    /// available synchronously via <see cref="ConnectionState"/>.
    /// </summary>
    public StateChangeStream CreateStateChangeStream()
    {
        var stream = new StateChangeStream(Math.Max(8, _cfg.SubscriptionBufferCapacity));
        lock (_subsLock)
        {
            _stateListeners.Add(stream);
        }
        // Detach on dispose so an abandoned stream is removed from the fan-out list.
        stream.OnClose(() => { lock (_subsLock) { _stateListeners.Remove(stream); } });
        return stream;
    }

    /// <summary>
    /// Records a new connection state and fans it out to every attached
    /// <see cref="StateChangeStream"/>. Mirrors the Swift <c>transition(to:)</c>.
    /// Idempotent on repeated identical states is NOT enforced (Swift fans out on
    /// every call); callers transition only on real edges.
    /// </summary>
    private void Transition(ConnectionState newState)
    {
        _connectionState = new ConnectionStateBox(newState);
        List<StateChangeStream> listeners;
        lock (_subsLock)
        {
            listeners = new List<StateChangeStream>(_stateListeners);
        }
        foreach (var st in listeners) st.TrySend(newState);
    }

    // ── Keep-alive ────────────────────────────────────────────────────────

    /// <summary>
    /// The keep-alive ping loop. Sleeps for the configured interval, then sends a
    /// transport-level ping; a ping failure is treated as a transport failure and
    /// tears the client down. Port of the Swift <c>keepAliveTask</c> loop in
    /// <c>startKeepAliveIfNeeded()</c>.
    /// </summary>
    private async Task RunKeepAliveAsync(IKeepAliveTransport pingTransport)
    {
        var policy = _cfg.KeepAlive;
        var ct = _keepAliveCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(policy.Interval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                await pingTransport.SendPingAsync(policy.Timeout, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal teardown — the cancellation came from our own shutdown path.
        }
        catch (Exception ex)
        {
            // A ping failure (or pong timeout) is a transport failure: tear down
            // exactly as the receive/writer loops do on error. Mirrors the Swift
            // `handleTransportFailure(error)` call from the keep-alive loop.
            // fromKeepAlive: true so the teardown does not await THIS task (which is
            // the keep-alive task) — that would deadlock.
            await ShutdownWithErrorAsync(
                new AhpTransportException("io", $"ahp: keep-alive ping: {ex.Message}", ex),
                fromKeepAlive: true).ConfigureAwait(false);
        }
    }

    // ── Writer loop ───────────────────────────────────────────────────────

    // The client routes all JSON through the injected IAhpSerializer, whose
    // contract is [RequiresUnreferencedCode]/[RequiresDynamicCode] (the default
    // SystemTextJsonAhpSerializer is reflection-based). The trim/AOT unsafety is
    // declared at that contract; re-declaring it on this internal loop — or
    // propagating the attribute up through the constructor / Connect() / the
    // entire client + multi-host public surface — is out of scope here, so the
    // serializer-call warnings are suppressed at the call site with that
    // contract named as the owner.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    private async Task RunWriterAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var frame = _serializer.EncodeMessage(item.Message);
                try
                {
                    await _transport.SendAsync(frame).ConfigureAwait(false);
                    item.Sent?.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    item.Sent?.TrySetException(ex);
                    await ShutdownWithErrorAsync(new AhpTransportException("io", $"ahp: transport send: {ex.Message}", ex)).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShutdownWithErrorAsync(new AhpTransportException("io", $"ahp: writer: {ex.Message}", ex)).ConfigureAwait(false);
        }
    }

    // ── Reader loop ───────────────────────────────────────────────────────

    // See RunWriterAsync: JSON decode goes through the [RequiresUnreferencedCode]/
    // [RequiresDynamicCode] IAhpSerializer, which owns the trim/AOT-unsafety
    // declaration.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    private async Task RunReaderAsync()
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _shutdownStarted) == 1) return;

                TransportMessage frame;
                try
                {
                    frame = await _transport.ReceiveAsync().ConfigureAwait(false);
                }
                catch (TransportClosedException)
                {
                    // A clean remote close is not an error: shut down without a cause.
                    await ShutdownWithErrorAsync(null).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    await ShutdownWithErrorAsync(new AhpTransportException("io", $"ahp: transport recv: {ex.Message}", ex)).ConfigureAwait(false);
                    return;
                }

                JsonRpcMessage msg;
                try
                {
                    msg = _serializer.DecodeMessage(frame);
                }
                catch
                {
                    // Skip malformed frames; protocol resync is the server's responsibility.
                    AhpTelemetry.MalformedFrames.Add(1);
                    continue;
                }

                AhpTelemetry.MessagesReceived.Add(1);
                Dispatch(msg);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShutdownWithErrorAsync(new AhpTransportException("io", $"ahp: reader: {ex.Message}", ex)).ConfigureAwait(false);
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────

    private void Dispatch(JsonRpcMessage msg)
    {
        if (msg.SuccessResponse is not null)
        {
            Deliver(msg.SuccessResponse.Id, msg.SuccessResponse.Result, null);
        }
        else if (msg.ErrorResponse is not null)
        {
            var err = msg.ErrorResponse.Error;
            Deliver(msg.ErrorResponse.Id, default, new AhpRpcException(err.Code, err.Message, err.Data));
        }
        else if (msg.Notification is not null)
        {
            HandleNotification(msg.Notification);
        }
        else if (msg.Request is not null)
        {
            // Fire-and-forget: server-initiated request. Reply async so the reader
            // loop is never blocked by handler work. (Lifts the v0.1 "drop server
            // requests" limitation; mirrors the TS client's handleServerRequest.)
            _ = HandleServerRequestAsync(msg.Request);
        }
    }

    private void Deliver(ulong id, JsonElement result, AhpRpcException? rpcError)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            if (rpcError is not null)
                tcs.TrySetException(rpcError);
            else
                tcs.TrySetResult(result);
        }
    }

    // ── Server-initiated requests ─────────────────────────────────────────

    /// <summary>
    /// Installs a handler for server-initiated requests. If none is installed, the
    /// client auto-replies <c>MethodNotFound</c> so the server does not leak a
    /// pending request. Pass <see langword="null"/> to clear.
    /// </summary>
    public void SetServerRequestHandler(ServerRequestHandler? handler) => _serverRequestHandler = handler;

    /// <summary>
    /// Replies to an inbound server-initiated request: <c>MethodNotFound</c> if no
    /// handler is installed, otherwise the handler's result (or its thrown error).
    /// Mirrors the TS client's <c>handleServerRequest</c>.
    /// </summary>
    // See RunWriterAsync: the handler's result serialize goes through the
    // [RequiresUnreferencedCode]/[RequiresDynamicCode] IAhpSerializer contract.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    private async Task HandleServerRequestAsync(JsonRpcRequest req)
    {
        var handler = _serverRequestHandler;
        if (handler is null)
        {
            await ReplyErrorAsync(req.Id, JsonRpcErrorCodes.MethodNotFound,
                $"no handler for server method \"{req.Method}\"").ConfigureAwait(false);
            return;
        }
        try
        {
            var result = await handler(req.Method, req.Params).ConfigureAwait(false);
            // SerializeToElement(null) yields a JSON `null` element; no separate
            // JsonDocument.Parse("null") (and its undisposed leak) is needed.
            JsonElement resultEl = _serializer.SerializeToElement(result);
            await ReplyResultAsync(req.Id, resultEl).ConfigureAwait(false);
        }
        catch (AhpRpcException rpc)
        {
            await ReplyErrorAsync(req.Id, rpc.Code, rpc.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReplyErrorAsync(req.Id, JsonRpcErrorCodes.InternalError, ex.Message).ConfigureAwait(false);
        }
    }

    private Task ReplyResultAsync(ulong id, JsonElement result) =>
        EnqueueReplyAsync(new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse { Id = id, Result = result },
        });

    private Task ReplyErrorAsync(ulong id, int code, string message) =>
        EnqueueReplyAsync(new JsonRpcMessage
        {
            ErrorResponse = new JsonRpcErrorResponse
            {
                Id = id,
                Error = new JsonRpcErrorObject { Code = code, Message = message },
            },
        });

    // Enqueue a reply frame on the existing outbound channel. Best-effort: if the
    // client is shutting down, the reply is dropped (the transport is gone anyway).
    private async Task EnqueueReplyAsync(JsonRpcMessage msg)
    {
        if (Volatile.Read(ref _shutdownStarted) == 1) return;
        try
        {
            await _outbound.Writer.WriteAsync(new OutboundMessage(msg)).ConfigureAwait(false);
        }
        catch { /* shutting down — best effort */ }
    }

    // See RunWriterAsync: the per-notification deserialize calls go through the
    // [RequiresUnreferencedCode]/[RequiresDynamicCode] IAhpSerializer contract.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    private void HandleNotification(JsonRpcNotification n)
    {
        if (n.Params is null) return;
        var paramsEl = n.Params.Value;

        switch (n.Method)
        {
            case "action":
                {
                    ActionEnvelope env;
                    try { env = _serializer.Deserialize<ActionEnvelope>(paramsEl); }
                    catch { return; }
                    FanOut(env.Channel, new SubscriptionEventAction(env));
                    break;
                }
            case "root/sessionAdded":
                {
                    SessionAddedParams p;
                    try { p = _serializer.Deserialize<SessionAddedParams>(paramsEl); }
                    catch { return; }
                    FanOut(p.Channel, new SubscriptionEventSessionAdded(p));
                    break;
                }
            case "root/sessionRemoved":
                {
                    SessionRemovedParams p;
                    try { p = _serializer.Deserialize<SessionRemovedParams>(paramsEl); }
                    catch { return; }
                    FanOut(p.Channel, new SubscriptionEventSessionRemoved(p));
                    break;
                }
            case "root/sessionSummaryChanged":
                {
                    SessionSummaryChangedParams p;
                    try { p = _serializer.Deserialize<SessionSummaryChangedParams>(paramsEl); }
                    catch { return; }
                    FanOut(p.Channel, new SubscriptionEventSessionSummaryChanged(p));
                    break;
                }
            case "root/progress":
                {
                    ProgressParams p;
                    try { p = _serializer.Deserialize<ProgressParams>(paramsEl); }
                    catch { return; }
                    FanOut(p.Channel, new SubscriptionEventProgress(p));
                    break;
                }
            case "auth/required":
                {
                    AuthRequiredParams p;
                    try { p = _serializer.Deserialize<AuthRequiredParams>(paramsEl); }
                    catch { return; }
                    FanOut(p.Channel, new SubscriptionEventAuthRequired(p));
                    break;
                }
        }
    }

    private void FanOut(string channel, SubscriptionEvent ev)
    {
        // Snapshot each bucket under the lock (so TrySend runs outside it), but
        // only allocate when there is something to fan to — the common per-message
        // case has zero matching subscriptions and/or zero event listeners, and a
        // shared empty array avoids the per-message List churn on that path.
        IReadOnlyList<Subscription> subs;
        IReadOnlyList<EventStream> listeners;
        lock (_subsLock)
        {
            subs = _subscriptions.TryGetValue(channel, out var list) && list.Count > 0
                ? new List<Subscription>(list)
                : Array.Empty<Subscription>();
            listeners = _eventListeners.Count > 0
                ? new List<EventStream>(_eventListeners)
                : Array.Empty<EventStream>();
        }

        for (var i = 0; i < subs.Count; i++) subs[i].TrySend(ev);

        if (listeners.Count > 0)
        {
            // Allocate the tagged ClientEvent once, only when a listener exists.
            var clientEvent = new ClientEvent(channel, ev);
            for (var i = 0; i < listeners.Count; i++) listeners[i].TrySend(clientEvent);
        }
    }

    // ── Request / Notify ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a JSON-RPC request and decodes the result. Applies the configured
    /// default timeout whenever <see cref="ClientConfig.DefaultRequestTimeout"/> is
    /// positive, composing it with any caller-supplied cancellation token.
    /// <para>
    /// Returns <see langword="default"/> (for a reference-type <typeparamref name="TResult"/>,
    /// <see langword="null"/>) when the server replies with a JSON <c>null</c> or an
    /// empty result — the result type is nullable so callers are forced by NRT to
    /// handle that case rather than dereferencing a result the contract promised
    /// would be non-null.
    /// </para>
    /// </summary>
    // See RunWriterAsync: param serialize + result deserialize go through the
    // [RequiresUnreferencedCode]/[RequiresDynamicCode] IAhpSerializer contract.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    public async Task<TResult?> RequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (Volatile.Read(ref _shutdownStarted) == 1)
            throw new AhpClientClosedException();

        // Fast-fail when the caller's token is already cancelled. Mirrors the
        // Swift client's `Task.checkCancellation()` at the top of `request`:
        // avoid minting a request id and pushing wire bytes for a request whose
        // result would be thrown away immediately. Must run BEFORE the id is
        // minted and the pending entry is registered.
        cancellationToken.ThrowIfCancellationRequested();

        // Gate the span-name string-build on HasListeners so the no-listener path
        // stays allocation-free; the method goes in the span name (OTel "{op} {target}")
        // and as the rpc.method tag.
        using var activity = AhpTelemetry.ActivitySource.HasListeners()
            ? AhpTelemetry.ActivitySource.StartActivity($"{AhpTelemetryNames.RequestSpan} {method}", ActivityKind.Client)
            : null;
        activity?.SetTag(AhpTelemetryNames.AttrRpcSystem, AhpTelemetryNames.RpcSystemJsonrpc);
        activity?.SetTag(AhpTelemetryNames.AttrRpcMethod, method);
        var startTimestamp = Stopwatch.GetTimestamp();
        string outcome = AhpTelemetryNames.OutcomeError;

        var id = Interlocked.Increment(ref _nextId) - 1;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        activity?.SetTag(AhpTelemetryNames.AttrRequestId, id);
        AhpTelemetry.InflightRequests.Add(1);

        try
        {
            // Re-check shutdown after inserting into _pending so a request registered
            // during shutdown cannot hang.
            if (Volatile.Read(ref _shutdownStarted) == 1)
            {
                _pending.TryRemove(id, out _);
                throw new AhpClientClosedException();
            }

            JsonElement? paramsEl = parameters is null ? null : _serializer.SerializeToElement(parameters);

            var req = new JsonRpcMessage
            {
                Request = new JsonRpcRequest
                {
                    Id = id,
                    Method = method,
                    Params = paramsEl,
                }
            };

            try
            {
                await SendMessageAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }
            AhpTelemetry.MessagesSent.Add(1, new KeyValuePair<string, object?>(AhpTelemetryNames.AttrMessageKind, AhpTelemetryNames.MessageKindRequest));

            // Always apply the configured default timeout when positive, composing it
            // with any caller-supplied cancellation token.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_cfg.DefaultRequestTimeout > TimeSpan.Zero)
                linkedCts.CancelAfter(_cfg.DefaultRequestTimeout);

            try
            {
                var resultEl = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                outcome = AhpTelemetryNames.OutcomeOk;
                if (resultEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return default;
                return _serializer.Deserialize<TResult>(resultEl);
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }
        catch (Exception ex)
        {
            // Distinguish caller cancellation (the caller's token) from a request
            // timeout (the configured default-timeout fired its linked token) from a
            // genuine error — the metric and the span status differ for each.
            bool callerCancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            outcome = ex switch
            {
                OperationCanceledException when callerCancelled => AhpTelemetryNames.OutcomeCancelled,
                OperationCanceledException => AhpTelemetryNames.OutcomeTimeout,
                _ => AhpTelemetryNames.OutcomeError,
            };
            // A deliberate caller cancellation is not an operation failure.
            activity?.SetStatus(callerCancelled ? ActivityStatusCode.Unset : ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            AhpTelemetry.InflightRequests.Add(-1);
            AhpTelemetry.RequestDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>(AhpTelemetryNames.AttrRpcMethod, method),
                new KeyValuePair<string, object?>(AhpTelemetryNames.AttrOutcome, outcome));
        }
    }

    /// <summary>
    /// Sends a JSON-RPC notification (fire-and-forget).
    /// </summary>
    // See RunWriterAsync: param serialize goes through the
    // [RequiresUnreferencedCode]/[RequiresDynamicCode] IAhpSerializer contract.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JSON goes through the [RequiresUnreferencedCode] IAhpSerializer, which declares the reflection unsafety on its contract.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JSON goes through the [RequiresDynamicCode] IAhpSerializer, which declares the AOT unsafety on its contract.")]
    public async Task NotifyAsync<TParams>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (Volatile.Read(ref _shutdownStarted) == 1)
            throw new AhpClientClosedException();

        JsonElement? paramsEl = parameters is null ? null : _serializer.SerializeToElement(parameters);

        var notif = new JsonRpcMessage
        {
            Notification = new JsonRpcNotification
            {
                Method = method,
                Params = paramsEl,
            }
        };

        await SendMessageAsync(notif, cancellationToken).ConfigureAwait(false);
        AhpTelemetry.MessagesSent.Add(1, new KeyValuePair<string, object?>(AhpTelemetryNames.AttrMessageKind, AhpTelemetryNames.MessageKindNotification));
    }

    private async Task SendMessageAsync(JsonRpcMessage msg, CancellationToken cancellationToken)
    {
        var sentTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new OutboundMessage(msg, sentTcs);

        try
        {
            await _outbound.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _shutdownStarted) == 1)
                throw new AhpClientClosedException();
            throw new AhpTransportException("io", null, ex);
        }

        // Wait for the writer goroutine to actually send the frame. Honor the
        // caller's token so a cancelled send does not hang indefinitely waiting
        // for the writer to pick it up.
        await sentTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Protocol surface ──────────────────────────────────────────────────

    /// <summary>Issues the <c>initialize</c> handshake.</summary>
    public async Task<InitializeResult> InitializeAsync(
        string clientId,
        IReadOnlyList<string>? protocolVersions = null,
        IReadOnlyList<string>? initialSubscriptions = null,
        CancellationToken cancellationToken = default)
    {
        var versions = protocolVersions is not null
            ? new List<string>(protocolVersions)
            : new List<string>(ProtocolVersion.Supported);

        var @params = new InitializeParams
        {
            Channel = ProtocolVersion.RootResourceUri,
            ProtocolVersions = versions,
            ClientId = clientId,
            InitialSubscriptions = initialSubscriptions is { Count: > 0 }
                ? new List<string>(initialSubscriptions)
                : null,
        };

        // The protocol requires a result for `initialize`; a null/empty result is
        // a protocol violation, surfaced loudly rather than returned as null.
        return await RequestAsync<InitializeParams, InitializeResult>("initialize", @params, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AhpRpcException(JsonRpcErrorCodes.InternalError, "ahp: initialize returned no result");
    }

    /// <summary>Re-establishes a dropped connection via the <c>reconnect</c> flow.</summary>
    public async Task<ReconnectResult> ReconnectAsync(
        string clientId,
        long lastSeenServerSeq,
        IReadOnlyList<string>? subscriptions = null,
        CancellationToken cancellationToken = default)
    {
        var @params = new ReconnectParams
        {
            Channel = ProtocolVersion.RootResourceUri,
            ClientId = clientId,
            LastSeenServerSeq = lastSeenServerSeq,
            Subscriptions = subscriptions is not null
                ? new List<string>(subscriptions)
                : new List<string>(),
        };

        try
        {
            // As with `initialize`, the protocol mandates a `reconnect` result; a null
            // result is a protocol violation surfaced loudly.
            var result = await RequestAsync<ReconnectParams, ReconnectResult>("reconnect", @params, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new AhpRpcException(JsonRpcErrorCodes.InternalError, "ahp: reconnect returned no result");
            AhpTelemetry.Reconnects.Add(1, new KeyValuePair<string, object?>(AhpTelemetryNames.AttrOutcome, AhpTelemetryNames.OutcomeOk));
            return result;
        }
        catch
        {
            AhpTelemetry.Reconnects.Add(1, new KeyValuePair<string, object?>(AhpTelemetryNames.AttrOutcome, AhpTelemetryNames.OutcomeError));
            throw;
        }
    }

    /// <summary>
    /// Sends a <c>subscribe</c> request and returns the initial snapshot plus a
    /// per-URI <see cref="Subscription"/> handle.
    /// </summary>
    public async Task<(SubscribeResult Result, Subscription Sub)> SubscribeAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        var sub = AttachSubscription(uri);
        try
        {
            // The protocol mandates a `subscribe` result; a null result is a
            // protocol violation surfaced loudly rather than returned as null.
            var result = await RequestAsync<SubscribeParams, SubscribeResult>(
                "subscribe", new SubscribeParams { Channel = uri }, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new AhpRpcException(JsonRpcErrorCodes.InternalError, "ahp: subscribe returned no result");
            return (result, sub);
        }
        catch
        {
            sub.Close();
            throw;
        }
    }

    /// <summary>
    /// Returns a local <see cref="Subscription"/> for <paramref name="uri"/> without
    /// sending a <c>subscribe</c> request. Useful when the URI was included in
    /// <c>initialSubscriptions</c> during <see cref="InitializeAsync"/>.
    /// </summary>
    public Subscription AttachSubscription(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var sub = new Subscription(uri, _cfg.SubscriptionBufferCapacity);
        lock (_subsLock)
        {
            if (!_subscriptions.TryGetValue(uri, out var list))
            {
                list = new List<Subscription>();
                _subscriptions[uri] = list;
            }
            list.Add(sub);
        }
        AhpTelemetry.ActiveSubscriptions.Add(1);
        // Detach on Close()/Dispose() (whichever ends it) so the registry and the
        // active-subscriptions gauge stay accurate regardless of teardown path.
        sub.OnClose(() =>
        {
            lock (_subsLock)
            {
                if (_subscriptions.TryGetValue(uri, out var subsForUri)
                    && subsForUri.Remove(sub) && subsForUri.Count == 0)
                {
                    _subscriptions.Remove(uri);
                }
            }
            AhpTelemetry.ActiveSubscriptions.Add(-1);
        });
        return sub;
    }

    /// <summary>
    /// Sends an <c>unsubscribe</c> notification and drops every local
    /// <see cref="Subscription"/> for <paramref name="uri"/>.
    /// </summary>
    public async Task UnsubscribeAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        List<Subscription> subs;
        lock (_subsLock)
        {
            subs = _subscriptions.TryGetValue(uri, out var list) ? new List<Subscription>(list) : new();
        }
        // Close() runs each subscription's detach hook, which removes it from the
        // registry and decrements the gauge — the single teardown path.
        foreach (var sub in subs) sub.Close();
        await NotifyAsync("unsubscribe", new UnsubscribeParams { Channel = uri }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fires a write-ahead <c>dispatchAction</c> notification.
    /// </summary>
    /// <param name="channel">Channel URI the action targets.</param>
    /// <param name="action">The action to dispatch.</param>
    /// <param name="clientSeq">
    /// Optional caller-owned sequence number. When <c>null</c> (the default), the
    /// next auto-incrementing client sequence is assigned. When supplied, that
    /// exact value is sent on the wire and recorded on the returned handle — for
    /// an app-level outbox that needs stable sequence numbers across
    /// reconnect/replay. To keep later auto-assigned numbers from colliding, the
    /// internal counter is advanced past an explicit value that is at or beyond it
    /// (mirroring Swift's <c>dispatch(clientSeq:)</c>).
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task<DispatchHandle> DispatchAsync(
        string channel,
        StateAction action,
        long? clientSeq = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(action);
        long seq;
        if (clientSeq is { } explicitSeq)
        {
            seq = explicitSeq;
            // Advance _nextClientSeq to explicitSeq + 1 if the explicit value is at
            // or beyond the current counter, so a subsequent auto-assigned dispatch
            // won't reuse this number. CAS loop keeps this race-free under
            // concurrent dispatchers (mirrors Swift's `if clientSeq >= nextClientSeq
            // { nextClientSeq = clientSeq + 1 }`, done atomically).
            while (true)
            {
                var current = Interlocked.Read(ref _nextClientSeq);
                if (explicitSeq < current) break; // counter already ahead
                var desired = explicitSeq + 1;
                if (Interlocked.CompareExchange(ref _nextClientSeq, desired, current) == current) break;
            }
        }
        else
        {
            seq = Interlocked.Increment(ref _nextClientSeq) - 1;
        }

        await NotifyAsync("dispatchAction", new DispatchActionParams
        {
            Channel = channel,
            ClientSeq = seq,
            Action = action,
        }, cancellationToken).ConfigureAwait(false);
        return new DispatchHandle(seq);
    }

    /// <summary>
    /// Returns a new top-level <see cref="EventStream"/> that receives every
    /// inbound event from this client, tagged with the channel URI. Multiple
    /// streams may exist concurrently.
    /// </summary>
    public EventStream CreateEventStream()
    {
        var stream = new EventStream(_cfg.SubscriptionBufferCapacity);
        lock (_subsLock)
        {
            _eventListeners.Add(stream);
        }
        // Detach on dispose so an abandoned stream is removed from the fan-out
        // list rather than receiving (dropped) events for the client's lifetime.
        stream.OnClose(() => { lock (_subsLock) { _eventListeners.Remove(stream); } });
        return stream;
    }
}

// ─── Connection-state stream ────────────────────────────────────────────────────

/// <summary>
/// A multicast stream of <see cref="ConnectionState"/> transitions, returned by
/// <see cref="AhpClient.CreateStateChangeStream"/>. Port of the Swift
/// <c>stateChanges</c> AsyncStream.
/// <para>
/// Each stream delivers only transitions that occur after it was created; the
/// current value is available synchronously via <see cref="AhpClient.ConnectionState"/>.
/// On shutdown the client fans out a terminal <see cref="ConnectionState.Disconnected"/>
/// transition and then completes the stream, so a consumer draining the stream sees
/// <see cref="ConnectionState.Disconnected"/> as the last item.
/// </para>
/// </summary>
// CA1711: "Stream" names the AHP state-change stream concept (mirrors Swift's
// AsyncStream / stateChanges API), not a System.IO.Stream subclass. This is part
// of the established cross-SDK public API surface; renaming would break callers.
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "StateChangeStream names the AHP connection-state stream (mirrors Swift AsyncStream API), not a System.IO.Stream subclass.")]
public sealed class StateChangeStream : IDisposable
{
    private static readonly KeyValuePair<string, object?> DropTag = new(AhpTelemetryNames.AttrStream, AhpTelemetryNames.StreamState);
    private readonly BoundedDropOldestChannel<ConnectionState> _channel;
    private Action? _onClose;
    private int _closed;

    /// <summary>Creates a new state-change stream.</summary>
    internal StateChangeStream(int bufferCapacity)
    {
        _channel = new BoundedDropOldestChannel<ConnectionState>(
            bufferCapacity, _ => AhpTelemetry.DroppedEvents.Add(1, DropTag));
    }

    /// <summary>
    /// Sets the client's one-shot detach hook, run on the first <see cref="Close"/>
    /// so the stream is removed from the client's fan-out list no matter how it ends.
    /// </summary>
    internal void OnClose(Action onClose) => _onClose = onClose;

    /// <summary>
    /// The reader side of the stream. Read from this to receive
    /// <see cref="ConnectionState"/> transitions as they occur.
    /// </summary>
    public ChannelReader<ConnectionState> States => _channel.Reader;

    /// <summary>Stops the stream and detaches it from the client. Safe to call multiple times.</summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0) return;
        _channel.Close();
        _onClose?.Invoke();
    }

    /// <inheritdoc cref="Close"/>
    public void Dispose() => Close();

    internal void TrySend(ConnectionState state) => _channel.TrySend(state);
}
