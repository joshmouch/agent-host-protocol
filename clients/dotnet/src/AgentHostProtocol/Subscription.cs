// Per-URI subscription handle and top-level event stream — port of the Go
// client's Subscription, SubscriptionEvent, EventStream, ClientEvent types.
// Mirrors: ahp/client.go (SubscriptionEvent variants, Subscription, EventStream).
#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace Microsoft.AgentHostProtocol;

// ─── Subscription events ─────────────────────────────────────────────────────

/// <summary>Marker interface for all subscription event variants.</summary>
public abstract class SubscriptionEvent { }

/// <summary>A write-ahead action envelope delivered to a subscription.</summary>
public sealed class SubscriptionEventAction : SubscriptionEvent
{
    /// <summary>The action envelope from the server.</summary>
    public ActionEnvelope Envelope { get; }

    /// <summary>Creates a new action event.</summary>
    public SubscriptionEventAction(ActionEnvelope envelope) => Envelope = envelope;
}

/// <summary>Mirrors the <c>root/sessionAdded</c> notification.</summary>
public sealed class SubscriptionEventSessionAdded : SubscriptionEvent
{
    /// <summary>The notification parameters.</summary>
    public SessionAddedParams Params { get; }

    /// <summary>Creates a new session-added event.</summary>
    public SubscriptionEventSessionAdded(SessionAddedParams @params) => Params = @params;
}

/// <summary>Mirrors the <c>root/sessionRemoved</c> notification.</summary>
public sealed class SubscriptionEventSessionRemoved : SubscriptionEvent
{
    /// <summary>The notification parameters.</summary>
    public SessionRemovedParams Params { get; }

    /// <summary>Creates a new session-removed event.</summary>
    public SubscriptionEventSessionRemoved(SessionRemovedParams @params) => Params = @params;
}

/// <summary>Mirrors the <c>root/sessionSummaryChanged</c> notification.</summary>
public sealed class SubscriptionEventSessionSummaryChanged : SubscriptionEvent
{
    /// <summary>The notification parameters.</summary>
    public SessionSummaryChangedParams Params { get; }

    /// <summary>Creates a new session-summary-changed event.</summary>
    public SubscriptionEventSessionSummaryChanged(SessionSummaryChangedParams @params) => Params = @params;
}

/// <summary>Mirrors the <c>auth/required</c> notification.</summary>
public sealed class SubscriptionEventAuthRequired : SubscriptionEvent
{
    /// <summary>The notification parameters.</summary>
    public AuthRequiredParams Params { get; }

    /// <summary>Creates a new auth-required event.</summary>
    public SubscriptionEventAuthRequired(AuthRequiredParams @params) => Params = @params;
}

/// <summary>
/// A <see cref="SubscriptionEvent"/> tagged with the channel URI it was
/// scoped to. Returned by <see cref="AhpClient.CreateEventStream"/>.
/// </summary>
public sealed class ClientEvent
{
    /// <summary>The channel URI the event belongs to.</summary>
    public string Channel { get; }

    /// <summary>The underlying subscription event.</summary>
    public SubscriptionEvent Event { get; }

    /// <summary>Creates a client event.</summary>
    public ClientEvent(string channel, SubscriptionEvent @event)
    {
        Channel = channel;
        Event = @event;
    }
}

// ─── Subscription handle ─────────────────────────────────────────────────────

/// <summary>
/// Per-URI fan-out handle returned by <see cref="AhpClient.SubscribeAsync"/> and
/// <see cref="AhpClient.AttachSubscription"/>. Drop the handle by calling
/// <see cref="Close"/> or let <see cref="AhpClient.ShutdownAsync"/> tear it down.
/// </summary>
public sealed class Subscription
{
    private readonly Channel<SubscriptionEvent> _channel;
    private int _closed;

    /// <summary>The channel URI this subscription is bound to.</summary>
    public string Uri { get; }

    /// <summary>Creates a new subscription.</summary>
    internal Subscription(string uri, int bufferCapacity)
    {
        Uri = uri;
        _channel = Channel.CreateBounded<SubscriptionEvent>(
            new BoundedChannelOptions(bufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// The reader side of the subscription's event channel. Read from this
    /// to receive events as they arrive.
    /// </summary>
    public ChannelReader<SubscriptionEvent> Events => _channel.Reader;

    /// <summary>
    /// Stops the subscription locally without notifying the server.
    /// Safe to call multiple times.
    /// </summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Attempts to deliver an event. Drops the event if the channel is full
    /// (overflow protection mirrors the Go <c>trySend</c>).
    /// </summary>
    internal void TrySend(SubscriptionEvent ev)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        _channel.Writer.TryWrite(ev);
    }
}

// ─── Top-level event stream ───────────────────────────────────────────────────

/// <summary>
/// Top-level fan-in receiver over every inbound event from an <see cref="AhpClient"/>,
/// tagged with the channel URI. Multiple streams may exist concurrently.
/// Returned by <see cref="AhpClient.CreateEventStream"/>.
/// </summary>
public sealed class EventStream
{
    private readonly Channel<ClientEvent> _channel;
    private int _closed;

    /// <summary>Creates a new event stream.</summary>
    internal EventStream(int bufferCapacity)
    {
        _channel = Channel.CreateBounded<ClientEvent>(
            new BoundedChannelOptions(bufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// The reader side of the event stream. Read from this to receive
    /// <see cref="ClientEvent"/>s as they arrive.
    /// </summary>
    public ChannelReader<ClientEvent> Events => _channel.Reader;

    /// <summary>
    /// Stops the stream. Safe to call multiple times.
    /// </summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Attempts to deliver an event. Drops it on full (mirrors Go <c>trySend</c>).
    /// </summary>
    internal void TrySend(ClientEvent ev)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        _channel.Writer.TryWrite(ev);
    }
}

/// <summary>
/// The receipt returned by <see cref="AhpClient.DispatchAsync"/>, recording
/// the client-assigned sequence number for the dispatched action.
/// </summary>
public sealed class DispatchHandle
{
    /// <summary>The client-assigned sequence number.</summary>
    public long ClientSeq { get; }

    /// <summary>Creates a dispatch handle.</summary>
    public DispatchHandle(long clientSeq) => ClientSeq = clientSeq;
}
