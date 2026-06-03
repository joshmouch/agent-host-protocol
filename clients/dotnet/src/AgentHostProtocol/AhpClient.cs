// Async JSON-RPC client over ITransport + IAhpSerializer.
// Faithful port of clients/go/ahp/client.go.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

// ─── Configuration ────────────────────────────────────────────────────────────

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

    /// <summary>Returns a config with sensible defaults (30 s timeout, 256-message buffer).</summary>
    public static ClientConfig Default { get; } = new();
}

// ─── AhpClient ────────────────────────────────────────────────────────────────

/// <summary>
/// Async JSON-RPC client over a pluggable <see cref="ITransport"/>.
/// <para>
/// Create with <see cref="Connect"/> which spawns a background read loop.
/// All public methods are safe to call from multiple threads.
/// </para>
/// </summary>
public sealed class AhpClient : IAsyncDisposable
{
    // ── State that lives for the client lifetime ──────────────────────────

    private readonly ITransport _transport;
    private readonly IAhpSerializer _serializer;
    private readonly ClientConfig _cfg;

    // Outbound queue (reader goroutine in Go; here driven by a Task).
    private readonly System.Threading.Channels.Channel<OutboundMessage> _outbound;

    // In-flight request correlation keyed by JSON-RPC id.
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<JsonElement>> _pending = new();

    // Per-URI subscription fan-out.
    private readonly object _subsLock = new();
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new();
    private readonly List<EventStream> _eventListeners = new();

    // Monotonically incrementing counters (no lock needed — Interlocked).
    private ulong _nextId = 1;
    private long _nextClientSeq = 1;

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
        _outbound = System.Threading.Channels.Channel.CreateBounded<OutboundMessage>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });
        _readerTask = Task.Run(RunReaderAsync);
        _writerTask = Task.Run(RunWriterAsync);
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
        var cfg = config ?? ClientConfig.Default;
        if (cfg.SubscriptionBufferCapacity <= 0) cfg.SubscriptionBufferCapacity = 256;
        return new AhpClient(transport, cfg, serializer ?? SystemTextJsonAhpSerializer.Default);
    }

    /// <summary>
    /// Wires <paramref name="transport"/> to a new <see cref="AhpClient"/> and
    /// starts the background reader / writer tasks. The client owns the transport
    /// from this point.
    /// </summary>
    /// <remarks>
    /// Kept for source compatibility. Prefer the synchronous <see cref="Connect"/> factory.
    /// </remarks>
    public static Task<AhpClient> ConnectAsync(
        ITransport transport,
        ClientConfig? config = null,
        IAhpSerializer? serializer = null)
        => Task.FromResult(Connect(transport, config, serializer));

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
    private async Task ShutdownWithErrorAsync(Exception? cause)
    {
        if (Interlocked.CompareExchange(ref _shutdownStarted, 1, 0) != 0)
        {
            return; // Already shutting down.
        }

        Volatile.Write(ref _closeErr, cause);

        // Signal the done task so Done-waiters unblock.
        _doneTcs.TrySetResult();

        // Close the transport so any blocked ReceiveAsync unblocks.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _transport.CloseAsync(shutdownCts.Token).ConfigureAwait(false);
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
            _subscriptions.Clear();

            allListeners = new List<EventStream>(_eventListeners);
            _eventListeners.Clear();
        }
        foreach (var sub in allSubs) sub.Close();
        foreach (var lst in allListeners) lst.Close();
    }

    // ── Writer loop ───────────────────────────────────────────────────────

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
                    await ShutdownWithErrorAsync(new Exception($"ahp: transport send: {ex.Message}", ex)).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShutdownWithErrorAsync(new Exception($"ahp: writer: {ex.Message}", ex)).ConfigureAwait(false);
        }
    }

    // ── Reader loop ───────────────────────────────────────────────────────

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
                    await ShutdownWithErrorAsync(new Exception($"ahp: transport recv: {ex.Message}", ex)).ConfigureAwait(false);
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
                    continue;
                }

                Dispatch(msg);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShutdownWithErrorAsync(new Exception($"ahp: reader: {ex.Message}", ex)).ConfigureAwait(false);
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
        // Server-initiated requests aren't supported in v0.1; drop.
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

    private void HandleNotification(JsonRpcNotification n)
    {
        if (n.Params is null) return;
        var paramsEl = n.Params.Value;

        switch (n.Method)
        {
            case "action":
            {
                ActionEnvelope env;
                try { env = _serializer.Deserialize<ActionEnvelope>(paramsEl.GetRawText()); }
                catch { return; }
                FanOut(env.Channel, new SubscriptionEventAction(env));
                break;
            }
            case "root/sessionAdded":
            {
                SessionAddedParams p;
                try { p = _serializer.Deserialize<SessionAddedParams>(paramsEl.GetRawText()); }
                catch { return; }
                FanOut(p.Channel, new SubscriptionEventSessionAdded(p));
                break;
            }
            case "root/sessionRemoved":
            {
                SessionRemovedParams p;
                try { p = _serializer.Deserialize<SessionRemovedParams>(paramsEl.GetRawText()); }
                catch { return; }
                FanOut(p.Channel, new SubscriptionEventSessionRemoved(p));
                break;
            }
            case "root/sessionSummaryChanged":
            {
                SessionSummaryChangedParams p;
                try { p = _serializer.Deserialize<SessionSummaryChangedParams>(paramsEl.GetRawText()); }
                catch { return; }
                FanOut(p.Channel, new SubscriptionEventSessionSummaryChanged(p));
                break;
            }
            case "auth/required":
            {
                AuthRequiredParams p;
                try { p = _serializer.Deserialize<AuthRequiredParams>(paramsEl.GetRawText()); }
                catch { return; }
                FanOut(p.Channel, new SubscriptionEventAuthRequired(p));
                break;
            }
        }
    }

    private void FanOut(string channel, SubscriptionEvent ev)
    {
        List<Subscription> subs;
        List<EventStream> listeners;
        lock (_subsLock)
        {
            subs = _subscriptions.TryGetValue(channel, out var list)
                ? new List<Subscription>(list)
                : new List<Subscription>();
            listeners = new List<EventStream>(_eventListeners);
        }
        foreach (var sub in subs) sub.TrySend(ev);
        foreach (var lst in listeners) lst.TrySend(new ClientEvent(channel, ev));
    }

    // ── Request / Notify ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a JSON-RPC request and decodes the result. Applies the configured
    /// default timeout whenever <see cref="ClientConfig.DefaultRequestTimeout"/> is
    /// positive, composing it with any caller-supplied cancellation token.
    /// </summary>
    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method,
        TParams @params,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shutdownStarted) == 1)
            throw new AhpClientClosedException();

        var id = Interlocked.Increment(ref _nextId) - 1;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        // Re-check shutdown after inserting into _pending so a request registered
        // during shutdown cannot hang.
        if (Volatile.Read(ref _shutdownStarted) == 1)
        {
            _pending.TryRemove(id, out _);
            throw new AhpClientClosedException();
        }

        JsonElement? paramsEl;
        if (@params is null)
        {
            paramsEl = null;
        }
        else
        {
            using var doc = JsonDocument.Parse(_serializer.Serialize(@params));
            paramsEl = doc.RootElement.Clone();
        }

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

        // Always apply the configured default timeout when positive, composing it
        // with any caller-supplied cancellation token.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_cfg.DefaultRequestTimeout > TimeSpan.Zero)
            linkedCts.CancelAfter(_cfg.DefaultRequestTimeout);

        try
        {
            var resultEl = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            var json = resultEl.GetRawText();
            if (json == "null" || string.IsNullOrEmpty(json))
                return default!;
            return _serializer.Deserialize<TResult>(json);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (AhpRpcException)
        {
            throw;
        }
        catch (AhpClientClosedException)
        {
            throw;
        }
    }

    /// <summary>
    /// Sends a JSON-RPC notification (fire-and-forget).
    /// </summary>
    public async Task NotifyAsync<TParams>(
        string method,
        TParams @params,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shutdownStarted) == 1)
            throw new AhpClientClosedException();

        JsonElement? paramsEl;
        if (@params is null)
        {
            paramsEl = null;
        }
        else
        {
            using var doc = JsonDocument.Parse(_serializer.Serialize(@params));
            paramsEl = doc.RootElement.Clone();
        }

        var notif = new JsonRpcMessage
        {
            Notification = new JsonRpcNotification
            {
                Method = method,
                Params = paramsEl,
            }
        };

        await SendMessageAsync(notif, cancellationToken).ConfigureAwait(false);
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

        // Wait for the writer goroutine to actually send the frame.
        await sentTcs.Task.ConfigureAwait(false);
    }

    // ── Protocol surface ──────────────────────────────────────────────────

    /// <summary>Issues the <c>initialize</c> handshake.</summary>
    public Task<InitializeResult> InitializeAsync(
        string clientId,
        IReadOnlyList<string>? protocolVersions = null,
        IReadOnlyList<string>? initialSubscriptions = null,
        CancellationToken cancellationToken = default)
    {
        var versions = protocolVersions is not null
            ? new System.Collections.Generic.List<string>(protocolVersions)
            : new System.Collections.Generic.List<string>(ProtocolVersion.Supported);

        var @params = new InitializeParams
        {
            Channel = ProtocolVersion.RootResourceUri,
            ProtocolVersions = versions,
            ClientId = clientId,
            InitialSubscriptions = initialSubscriptions is { Count: > 0 }
                ? new System.Collections.Generic.List<string>(initialSubscriptions)
                : null,
        };

        return RequestAsync<InitializeParams, InitializeResult>("initialize", @params, cancellationToken);
    }

    /// <summary>Re-establishes a dropped connection via the <c>reconnect</c> flow.</summary>
    public Task<ReconnectResult> ReconnectAsync(
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
                ? new System.Collections.Generic.List<string>(subscriptions)
                : new System.Collections.Generic.List<string>(),
        };

        return RequestAsync<ReconnectParams, ReconnectResult>("reconnect", @params, cancellationToken);
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
            var result = await RequestAsync<SubscribeParams, SubscribeResult>(
                "subscribe", new SubscribeParams { Channel = uri }, cancellationToken)
                .ConfigureAwait(false);
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
        return sub;
    }

    /// <summary>
    /// Sends an <c>unsubscribe</c> notification and drops every local
    /// <see cref="Subscription"/> for <paramref name="uri"/>.
    /// </summary>
    public async Task UnsubscribeAsync(string uri, CancellationToken cancellationToken = default)
    {
        List<Subscription> subs;
        lock (_subsLock)
        {
            if (!_subscriptions.Remove(uri, out subs!))
                subs = new List<Subscription>();
        }
        foreach (var sub in subs) sub.Close();
        await NotifyAsync("unsubscribe", new UnsubscribeParams { Channel = uri }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fires a write-ahead <c>dispatchAction</c> notification with a
    /// client-assigned sequence number.
    /// </summary>
    public async Task<DispatchHandle> DispatchAsync(
        string channel,
        StateAction action,
        CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _nextClientSeq) - 1;
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
        return stream;
    }
}
