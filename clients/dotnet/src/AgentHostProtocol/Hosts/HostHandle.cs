// Immutable snapshot of a registered host's observable state.
#nullable enable

using System;
using System.Collections.Generic;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Immutable snapshot of a registered host's observable state. Obtain a fresh
/// copy via <see cref="MultiHostClient.Host(HostId)"/> to see updates.
/// </summary>
public sealed class HostHandle
{
    /// <summary>
    /// The host's stable identifier. Required (the only producer is
    /// <see cref="HostEntry.Snapshot"/>, which always sets it) — no misleading
    /// sentinel default, consistent with <see cref="HostConfig.Id"/>.
    /// </summary>
    public required HostId Id { get; init; }

    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>The stable AHP client ID sent on <c>initialize</c>.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>Current lifecycle state.</summary>
    public HostState State { get; init; } = new() { Kind = HostStateKind.Disconnected };

    /// <summary>Protocol version negotiated on the last successful <c>initialize</c>.</summary>
    public string ProtocolVersion { get; init; } = "";

    /// <summary>Snapshot time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    // ── Swift-parity observable fields (mirrors HostHandle.swift) ──────────
    // These mirror the Swift `HostHandle`'s richer surface so aggregated views
    // and per-host streams have a per-host data source. They are populated by
    // the supervisor from `initialize`'s root snapshot, an opportunistic
    // `listSessions` seed, and session-summary notifications.

    /// <summary>
    /// Agents currently advertised by the host (mirrored from the root-state
    /// snapshot returned on <c>initialize</c>). Empty until the host first
    /// connects.
    /// </summary>
    public IReadOnlyList<AgentInfo> Agents { get; init; } = Array.Empty<AgentInfo>();

    /// <summary>
    /// Cached session summaries, sorted by <c>ModifiedAt</c> descending. Seeded
    /// by <c>listSessions</c> after each connect and kept fresh by
    /// <c>root/sessionAdded</c> / <c>root/sessionRemoved</c> /
    /// <c>root/sessionSummaryChanged</c> notifications.
    /// </summary>
    public IReadOnlyList<SessionSummary> SessionSummaries { get; init; } = Array.Empty<SessionSummary>();

    /// <summary>Active session count from root state, when present.</summary>
    public long? ActiveSessions { get; init; }

    /// <summary>URIs the supervisor will (re-)subscribe to across reconnects.</summary>
    public IReadOnlyList<string> Subscriptions { get; init; } = Array.Empty<string>();

    /// <summary>Highest <c>serverSeq</c> observed on this host.</summary>
    public long ServerSeq { get; init; }

    /// <summary>
    /// Wall-clock time of the most recent successful <c>initialize</c> /
    /// <c>reconnect</c>. Null until the host first connects.
    /// </summary>
    public DateTimeOffset? LastConnectedAt { get; init; }

    /// <summary>
    /// Generation counter — bumped on every connect or reconnect. Lets callers
    /// detect that the host reconnected since a snapshot was taken.
    /// </summary>
    public ulong Generation { get; init; }
}
