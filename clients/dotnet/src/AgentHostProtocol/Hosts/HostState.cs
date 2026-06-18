// Host lifecycle state.
#nullable enable

using System;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>Lifecycle states a host can be in.</summary>
public enum HostStateKind
{
    /// <summary>Added but no transport is open.</summary>
    Disconnected,
    /// <summary>Transport is being opened or <c>initialize</c> is in flight.</summary>
    Connecting,
    /// <summary>Fully connected and serving subscriptions.</summary>
    Connected,
    /// <summary>Previous connection dropped; supervisor is retrying.</summary>
    Reconnecting,
    /// <summary>Reconnect attempts exhausted (or disabled).</summary>
    Failed,
}

/// <summary>Current lifecycle state of a host.</summary>
public sealed class HostState
{
    /// <summary>The state kind.</summary>
    public HostStateKind Kind { get; init; }

    /// <summary>Consecutive reconnect attempt counter.</summary>
    public uint Attempt { get; init; }

    /// <summary>The error that put the host into its current state, if any.</summary>
    public Exception? Error { get; init; }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        HostStateKind.Disconnected => "disconnected",
        HostStateKind.Connecting => "connecting",
        HostStateKind.Connected => "connected",
        HostStateKind.Reconnecting => "reconnecting",
        HostStateKind.Failed => "failed",
        _ => "unknown",
    };
}
