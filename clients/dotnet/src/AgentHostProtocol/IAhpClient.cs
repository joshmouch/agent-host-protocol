// The public protocol surface of AhpClient, extracted so consumers can depend on
// an interface — mock it in tests, substitute it behind their own abstractions —
// rather than the concrete sealed client.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// The Agent Host Protocol client surface. Implemented by <see cref="AhpClient"/>;
/// a live <see cref="ITransport"/> is required, so construction stays a factory
/// (<see cref="AhpClient.Connect"/>), not a
/// parameterless DI singleton. Depend on this interface to keep call sites
/// mockable and substitutable.
/// </summary>
public interface IAhpClient : IAsyncDisposable
{
    /// <summary>The current connection state, readable synchronously.</summary>
    ConnectionState ConnectionState { get; }

    /// <summary>Completes once the client begins teardown (via shutdown or a transport failure).</summary>
    Task Completion { get; }

    /// <summary>The error that caused teardown, or <see langword="null"/> after a clean shutdown.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Error matches the established AhpClient.Error public property; renaming would diverge the interface from its implementation.")]
    Exception? Error { get; }

    /// <summary>Gracefully tears down the client. Safe to call multiple times.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers a handler for server-initiated JSON-RPC requests (replaces any prior handler).</summary>
    void SetServerRequestHandler(ServerRequestHandler? handler);

    /// <summary>Returns a fresh multicast stream of future connection-state transitions.</summary>
    StateChangeStream CreateStateChangeStream();

    /// <summary>Returns a fresh top-level event stream over every inbound event.</summary>
    EventStream CreateEventStream();

    /// <summary>Issues a JSON-RPC request and awaits the typed result.</summary>
    Task<TResult?> RequestAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Sends a JSON-RPC notification (fire-and-forget).</summary>
    Task NotifyAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Issues the <c>initialize</c> handshake.</summary>
    Task<InitializeResult> InitializeAsync(
        string clientId,
        IReadOnlyList<string>? protocolVersions = null,
        IReadOnlyList<string>? initialSubscriptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Re-establishes a dropped connection via the <c>reconnect</c> flow.</summary>
    Task<ReconnectResult> ReconnectAsync(
        string clientId,
        long lastSeenServerSeq,
        IReadOnlyList<string>? subscriptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>subscribe</c> request and returns the snapshot plus a per-URI handle.</summary>
    Task<(SubscribeResult Result, Subscription Sub)> SubscribeAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>Returns a local subscription for <paramref name="uri"/> without sending a request.</summary>
    Subscription AttachSubscription(string uri);

    /// <summary>Sends an <c>unsubscribe</c> notification and drops local subscriptions for <paramref name="uri"/>.</summary>
    Task UnsubscribeAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>Fires a write-ahead <c>dispatchAction</c> notification.</summary>
    Task<DispatchHandle> DispatchAsync(
        string channel,
        StateAction action,
        long? clientSeq = null,
        CancellationToken cancellationToken = default);
}
