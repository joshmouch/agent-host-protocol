// The public runtime surface of MultiHostClient, extracted so consumers can
// depend on an interface — mock the multi-host runtime in their own tests,
// substitute it behind their own abstractions — rather than the concrete sealed
// facade. Construction stays on the concrete type (MultiHostClient.SingleAsync /
// the constructors + WithClientIdStore fluent builder), because wiring a live
// registry is a factory concern, not a DI-singleton one.
#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// The multi-host registry + reconnect-supervisor surface. Implemented by
/// <see cref="MultiHostClient"/>. Depend on this interface to keep consumer call
/// sites mockable and substitutable; construct a concrete instance via
/// <see cref="MultiHostClient.SingleAsync"/> or the <see cref="MultiHostClient"/>
/// constructors.
/// </summary>
public interface IMultiHostClient : IAsyncDisposable
{
    // ── Registry lifecycle ────────────────────────────────────────────────

    /// <summary>Registers + connects a host, returning its initial handle.</summary>
    Task<HostHandle> AddHostAsync(HostConfig config, CancellationToken cancellationToken = default);

    /// <summary>Returns the current handle for <paramref name="id"/>, or <see langword="null"/> if unregistered.</summary>
    HostHandle? Host(HostId id);

    /// <summary>Tears down + removes a host. No-op if the host is unknown.</summary>
    Task RemoveHostAsync(HostId id, CancellationToken cancellationToken = default);

    /// <summary>Tears down every host and releases registered event channels. Idempotent.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    // ── Event channels ────────────────────────────────────────────────────

    /// <summary>A fresh channel of <see cref="HostEvent"/> state transitions; slow consumers drop oldest.</summary>
    ChannelReader<HostEvent> Events();

    /// <summary>A fresh channel of every <see cref="HostSubscriptionEvent"/> from every host.</summary>
    ChannelReader<HostSubscriptionEvent> Subscriptions();

    /// <summary>A fresh per-(host, uri) event channel for <paramref name="host"/>; throws <see cref="UnknownHostException"/> if unregistered.</summary>
    ChannelReader<SubscriptionEvent> EventsForHost(HostId host, string uri);

    /// <summary>Observable stream of <see cref="HostHandle"/> snapshots for <paramref name="host"/>; throws <see cref="UnknownHostException"/> if unregistered.</summary>
    ChannelReader<HostHandle> HostSnapshots(HostId host);

    /// <summary>Observable stream of cached session summaries for <paramref name="host"/>; throws <see cref="UnknownHostException"/> if unregistered.</summary>
    ChannelReader<IReadOnlyList<SessionSummary>> SessionSummariesForHost(HostId host);

    // ── Aggregated views ──────────────────────────────────────────────────

    /// <summary>Aggregated session summaries across every host, newest first, carrying host attribution.</summary>
    List<HostedSessionSummary> AggregatedSessions();

    /// <summary>Aggregated agents across every host, in per-host registration order, carrying host attribution.</summary>
    List<HostedAgent> AggregatedAgents();

    // ── Manual reconnect ──────────────────────────────────────────────────

    /// <summary>Triggers a manual reconnect on <paramref name="id"/>; throws <see cref="UnknownHostException"/> if unregistered.</summary>
    Task ReconnectAsync(HostId id, CancellationToken cancellationToken = default);

    /// <summary>Triggers a manual reconnect on every host that is not Connected/Connecting; never throws.</summary>
    Dictionary<HostId, Exception> ReconnectAllUnavailable();

    // ── Per-host dispatch / subscribe ─────────────────────────────────────

    /// <summary>Dispatches <paramref name="action"/> on <paramref name="host"/> for <paramref name="channel"/>.</summary>
    Task<DispatchHandle> DispatchAsync(
        HostId host,
        StateAction action,
        string channel,
        long? clientSeq = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes to <paramref name="uri"/> on <paramref name="host"/>, tracking it for replay across reconnects.</summary>
    Task<SubscribeResult> SubscribeAsync(HostId host, string uri, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribes from <paramref name="uri"/> on <paramref name="host"/> and drops it from the replay set.</summary>
    Task UnsubscribeAsync(HostId host, string uri, CancellationToken cancellationToken = default);
}
