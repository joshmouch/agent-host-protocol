// Multi-host registry + reconnect supervisor.
// Faithful port of clients/go/ahp/hosts/hosts.go + multi_host_state_mirror.go.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Hosts;

// ─── HostId ──────────────────────────────────────────────────────────────────

/// <summary>Opaque, stable identifier for a host registered with <see cref="MultiHostClient"/>.</summary>
public sealed class HostId : IEquatable<HostId>
{
    private readonly string _value;

    /// <summary>Creates a host ID from a string. The empty string is invalid.</summary>
    public HostId(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("HostId must not be empty.", nameof(value));
        _value = value;
    }

    /// <inheritdoc />
    public override string ToString() => _value;

    /// <inheritdoc />
    public bool Equals(HostId? other) => other is not null && _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HostId h && Equals(h);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator HostId(string s) => new(s);
}

// ─── HostState ───────────────────────────────────────────────────────────────

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

// ─── ReconnectPolicy ─────────────────────────────────────────────────────────

/// <summary>Controls reconnect behaviour after an unexpected transport drop.</summary>
public sealed class ReconnectPolicy
{
    /// <summary>
    /// Caps consecutive retry attempts. Zero means unlimited.
    /// </summary>
    public uint MaxAttempts { get; init; }

    /// <summary>Wait before the first retry.</summary>
    public TimeSpan InitialBackoff { get; init; }

    /// <summary>Caps the exponential backoff.</summary>
    public TimeSpan MaxBackoff { get; init; }

    /// <summary>Scales each successive backoff. Use 2.0 for exponential.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>If true, resets the attempt counter after a successful reconnect.</summary>
    public bool ResetOnSuccess { get; init; }

    /// <summary>
    /// Randomizes each backoff by ±this fraction (clamped to 0–1) to avoid
    /// reconnect storms when many hosts drop at once ("thundering herd"). The
    /// default 0 disables jitter — matching the other AHP clients' behavior.
    /// 0.2 is a reasonable production value. This is the dependency-free
    /// equivalent of the "exponential backoff with jitter" that the .NET
    /// resilience libraries recommend; see docs/decisions/reconnect.md.
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>Whether reconnection is effectively disabled (zero initial backoff).</summary>
    public bool IsDisabled => InitialBackoff <= TimeSpan.Zero;

    /// <summary>
    /// Returns a policy with 1 s → 2 s → 4 s → … capped at 30 s, unlimited, reset on success.
    /// </summary>
    public static ReconnectPolicy Default { get; } = new()
    {
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        ResetOnSuccess = true,
    };

    /// <summary>Returns a policy that disables reconnection.</summary>
    public static ReconnectPolicy Disabled { get; } = new()
    {
        MaxAttempts = 0,
        InitialBackoff = TimeSpan.Zero,
    };

    /// <summary>Computes the wait before attempt number <paramref name="attempt"/> (1-based).</summary>
    internal TimeSpan BackoffFor(uint attempt)
    {
        if (IsDisabled) return TimeSpan.Zero;
        var b = (double)InitialBackoff.Ticks;
        var mult = BackoffMultiplier <= 0 ? 1.0 : BackoffMultiplier;
        for (uint i = 1; i < attempt; i++) b *= mult;
        var result = TimeSpan.FromTicks((long)b);
        if (MaxBackoff > TimeSpan.Zero && result > MaxBackoff) result = MaxBackoff;

        if (Jitter > 0)
        {
            // Symmetric jitter: result * (1 ± Jitter), never negative and never
            // above MaxBackoff. Random.Shared is thread-safe.
            var j = Math.Clamp(Jitter, 0.0, 1.0);
            var factor = 1.0 + (Random.Shared.NextDouble() * 2.0 - 1.0) * j;
            var ticks = Math.Max(0L, (long)(result.Ticks * factor));
            result = TimeSpan.FromTicks(ticks);
            if (MaxBackoff > TimeSpan.Zero && result > MaxBackoff) result = MaxBackoff;
        }

        return result;
    }
}

// ─── HostConfig ──────────────────────────────────────────────────────────────

/// <summary>Factory delegate that opens a fresh transport for a given host.</summary>
public delegate Task<ITransport> HostTransportFactory(HostId hostId, CancellationToken cancellationToken);

/// <summary>Everything <see cref="MultiHostClient.AddHostAsync"/> needs to supervise a single host.</summary>
public sealed class HostConfig
{
    /// <summary>Stable host identifier. Required.</summary>
    public HostId Id { get; init; } = new("host");

    /// <summary>Human-readable name surfaced on <see cref="HostHandle.Label"/>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Stable AHP client ID. Leave empty to auto-generate and persist.</summary>
    public string? ClientId { get; init; }

    /// <summary>URIs to subscribe to on <c>initialize</c>. Defaults to <c>["ahp-root://"]</c>.</summary>
    public IReadOnlyList<string>? InitialSubscriptions { get; init; }

    /// <summary>Tunes the underlying <see cref="AhpClient"/> driver.</summary>
    public ClientConfig? ClientConfig { get; init; }

    /// <summary>Opens a transport for this host. Required.</summary>
    public HostTransportFactory? TransportFactory { get; init; }

    /// <summary>Controls reconnect behaviour on drops. Defaults to <see cref="ReconnectPolicy.Default"/>.</summary>
    public ReconnectPolicy? ReconnectPolicy { get; init; }

    /// <summary>Protocol versions advertised on <c>initialize</c>. Defaults to <see cref="ProtocolVersion.Supported"/>.</summary>
    public IReadOnlyList<string>? ProtocolVersions { get; init; }
}

// ─── HostHandle ──────────────────────────────────────────────────────────────

/// <summary>
/// Immutable snapshot of a registered host's observable state. Obtain a fresh
/// copy via <see cref="MultiHostClient.Host(HostId)"/> to see updates.
/// </summary>
public sealed class HostHandle
{
    /// <summary>The host's stable identifier.</summary>
    public HostId Id { get; init; } = new("host");

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

// ─── Aggregated view types ─────────────────────────────────────────────────────

/// <summary>
/// Aggregated session summary tagged with its host of origin. Returned by
/// <see cref="MultiHostClient.AggregatedSessions"/>. URIs are per-host scoped,
/// so two hosts can legitimately advertise the same <c>Summary.Resource</c>;
/// consumers should treat <c>(HostId, Summary.Resource)</c> as the compound key.
/// Port of Swift's <c>HostedSessionSummary</c>.
/// </summary>
public sealed class HostedSessionSummary
{
    /// <summary>Host that owns this summary.</summary>
    public HostId HostId { get; }

    /// <summary>Human-readable label of the owning host.</summary>
    public string HostLabel { get; }

    /// <summary>The underlying session summary.</summary>
    public SessionSummary Summary { get; }

    /// <summary>Creates a host-tagged session summary.</summary>
    public HostedSessionSummary(HostId hostId, string hostLabel, SessionSummary summary)
    {
        HostId = hostId; HostLabel = hostLabel; Summary = summary;
    }
}

/// <summary>
/// Aggregated agent descriptor tagged with its host of origin. Returned by
/// <see cref="MultiHostClient.AggregatedAgents"/>. Port of Swift's
/// <c>HostedAgent</c>.
/// </summary>
public sealed class HostedAgent
{
    /// <summary>Host that owns this agent.</summary>
    public HostId HostId { get; }

    /// <summary>Human-readable label of the owning host.</summary>
    public string HostLabel { get; }

    /// <summary>The underlying agent descriptor.</summary>
    public AgentInfo Agent { get; }

    /// <summary>Creates a host-tagged agent descriptor.</summary>
    public HostedAgent(HostId hostId, string hostLabel, AgentInfo agent)
    {
        HostId = hostId; HostLabel = hostLabel; Agent = agent;
    }
}

// ─── HostClientHandle ──────────────────────────────────────────────────────────

/// <summary>
/// Generation-checked handle onto the underlying single-host <see cref="AhpClient"/>
/// for a host. Issued by <see cref="MultiHostClient.ClientFor"/>. Operations verify
/// the host is still registered and on the same <c>Generation</c> the handle was
/// minted at; if the host was removed/shut down they throw
/// <see cref="HostShutDownException"/>, and if a reconnect replaced the connection
/// they throw <see cref="HostNotConnectedException"/> (acquire a fresh handle).
/// Port of Swift's <c>HostClientHandle</c> (Swift surfaces the reconnect case as
/// <c>hostReconnected</c>; the .NET typed-error set folds that into "not the
/// connection you held — reacquire").
/// </summary>
public sealed class HostClientHandle
{
    private readonly MultiHostClient _owner;

    /// <summary>The host this handle was issued for.</summary>
    public HostId HostId { get; }

    /// <summary>The generation this handle was minted at.</summary>
    public ulong Generation { get; }

    internal HostClientHandle(MultiHostClient owner, HostId hostId, ulong generation)
    {
        _owner = owner; HostId = hostId; Generation = generation;
    }

    /// <summary>
    /// Validates this handle and returns the underlying live client. Throws
    /// <see cref="HostShutDownException"/> if the host is no longer registered
    /// (removed or the multi-host client shut down), or
    /// <see cref="HostNotConnectedException"/> if the host has reconnected (the
    /// generation moved) or currently has no live connection.
    /// </summary>
    private AhpClient CheckAlive()
    {
        var entry = _owner.TryGetEntry(HostId);
        if (entry is null) throw new HostShutDownException(HostId);
        var snap = entry.Snapshot();
        if (snap.Generation != Generation) throw new HostNotConnectedException(HostId);
        var client = entry.CurrentClient;
        if (client is null) throw new HostNotConnectedException(HostId);
        return client;
    }

    /// <summary>
    /// Throws if this handle is no longer valid (host removed →
    /// <see cref="HostShutDownException"/>; reconnected/disconnected →
    /// <see cref="HostNotConnectedException"/>). Mirrors Swift's <c>checkAlive()</c>.
    /// </summary>
    public void CheckAliveOrThrow() => CheckAlive();

    /// <summary>
    /// Dispatches an action through this connection on <paramref name="channel"/>,
    /// refusing (throwing) if the host was removed or the connection has been
    /// replaced. Mirrors Swift's <c>HostClientHandle.dispatch</c>.
    /// </summary>
    public async Task<DispatchHandle> DispatchAsync(
        StateAction action,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var client = CheckAlive();
        return await client.DispatchAsync(channel, action, cancellationToken).ConfigureAwait(false);
    }
}

// ─── IClientIdStore ──────────────────────────────────────────────────────────

/// <summary>Persists the stable <c>clientId</c> used in AHP's reconnect flow.</summary>
public interface IClientIdStore
{
    /// <summary>Returns the stored client ID for <paramref name="host"/>, or null if absent.</summary>
    Task<string?> LoadAsync(HostId host, CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="clientId"/> for <paramref name="host"/>.</summary>
    Task StoreAsync(HostId host, string clientId, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory <see cref="IClientIdStore"/>. Suitable for tests and short-lived processes.</summary>
public sealed class InMemoryClientIdStore : IClientIdStore
{
    private readonly ConcurrentDictionary<string, string> _data = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<string?> LoadAsync(HostId host, CancellationToken cancellationToken = default) =>
        Task.FromResult(_data.TryGetValue(host.ToString(), out var v) ? v : null);

    /// <inheritdoc />
    public Task StoreAsync(HostId host, string clientId, CancellationToken cancellationToken = default)
    {
        _data[host.ToString()] = clientId;
        return Task.CompletedTask;
    }
}

// ─── Events ──────────────────────────────────────────────────────────────────

/// <summary>A connection-level event for a registered host.</summary>
public sealed class HostEvent
{
    /// <summary>Which host the state change belongs to.</summary>
    public HostId HostId { get; }

    /// <summary>The new state.</summary>
    public HostState State { get; }

    /// <summary>Creates a host event.</summary>
    public HostEvent(HostId hostId, HostState state) { HostId = hostId; State = state; }
}

/// <summary>An <see cref="AhpClient"/> subscription event tagged with host + URI.</summary>
public sealed class HostSubscriptionEvent
{
    /// <summary>Which host emitted this event.</summary>
    public HostId HostId { get; }

    /// <summary>The channel URI the event belongs to.</summary>
    public string Channel { get; }

    /// <summary>The underlying subscription event.</summary>
    public SubscriptionEvent Event { get; }

    /// <summary>Creates a host subscription event.</summary>
    public HostSubscriptionEvent(HostId hostId, string channel, SubscriptionEvent @event)
    {
        HostId = hostId; Channel = channel; Event = @event;
    }
}

// ─── Internal per-host bookkeeping ───────────────────────────────────────────

internal sealed class HostEntry
{
    public HostId Id { get; }
    public HostConfig Config { get; }
    public string ClientId { get; set; }

    private readonly Gate _gate = new();
    // Published reference, read lock-free via CurrentClient. A reference read is
    // atomic; `volatile` supplies the visibility a lock would otherwise provide.
    private volatile AhpClient? _client;
    private HostState _state = new() { Kind = HostStateKind.Disconnected };
    private string _protoVer = "";
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    // ── Swift-parity observable per-host state (guarded by _gate) ──────────
    // Session summaries are keyed by their `Resource` URI so add/remove/change
    // notifications mutate them by id, mirroring Swift's `sessionSummaries` dict
    // in HostRuntime.swift. Snapshot() materializes them sorted by ModifiedAt
    // descending. The rest mirror HostHandle.swift's richer fields.
    private readonly Dictionary<string, SessionSummary> _sessionSummaries = new(StringComparer.Ordinal);
    private List<AgentInfo> _agents = new();
    private long? _activeSessions;
    private readonly List<string> _subscriptions;
    private long _serverSeq;
    private DateTimeOffset? _lastConnectedAt;
    private ulong _generation;

    public CancellationTokenSource LifetimeCts { get; } = new();
    public Task SupervisorTask { get; set; } = Task.CompletedTask;

    /// <summary>Task for the fire-and-forget pump loop started in OpenHostAsync.</summary>
    public Task PumpTask { get; set; } = Task.CompletedTask;

    // ── Manual-reconnect signaling (Swift `manualReconnect` parity) ────────
    // `_manualReconnect` is a wake counter: ReconnectAsync releases it; the
    // supervisor waits on it to short-circuit a backoff sleep or to wake from
    // the `.failed` park (where the policy is exhausted/disabled). `_attemptCts`
    // is the cancellation source for the CURRENT connect attempt — ReconnectAsync
    // (and removal) cancels it so a slow `connectOnce`/transport-factory is
    // aborted promptly rather than blocking the next attempt.
    private readonly SemaphoreSlim _manualReconnect = new(0);
    private volatile CancellationTokenSource? _attemptCts;

    /// <summary>
    /// Request a manual reconnect: wake any backoff sleep / failed-park, and
    /// abort a slow in-flight connect attempt so the next attempt starts fresh.
    /// Mirrors Swift `HostRuntime.reconnect()`.
    /// </summary>
    public void SignalManualReconnect()
    {
        // Abort the in-flight attempt (slow factory / hung handshake) first…
        try { _attemptCts?.Cancel(); } catch (ObjectDisposedException) { }
        // …then wake the supervisor's wait so it loops back to a fresh attempt.
        try { _manualReconnect.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Awaits a manual-reconnect request or <paramref name="ct"/> cancellation.
    /// Returns true if a manual reconnect was requested, false if cancelled.
    /// </summary>
    public async Task<bool> WaitForManualReconnectAsync(CancellationToken ct)
    {
        try { await _manualReconnect.WaitAsync(ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Awaits EITHER the current client's completion (a transport drop) OR a
    /// manual-reconnect request, whichever happens first. Returns true if a
    /// manual reconnect won the race, false if the connection dropped (or ct
    /// cancelled).
    /// </summary>
    public async Task<bool> WaitForDropOrManualReconnectAsync(Task completion, CancellationToken ct)
    {
        var manual = _manualReconnect.WaitAsync(ct);
        var winner = await Task.WhenAny(completion, manual).ConfigureAwait(false);
        if (winner == manual)
        {
            // Observe the result so a faulted/cancelled wait doesn't go unhandled.
            try { await manual.ConfigureAwait(false); } catch { }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Establishes a fresh per-attempt CancellationTokenSource linked to the
    /// host lifetime token, returning its token. SignalManualReconnect() cancels
    /// whatever attempt CTS is current, aborting a slow factory.
    /// </summary>
    public CancellationToken BeginAttempt()
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(LifetimeCts.Token);
        var prev = Interlocked.Exchange(ref _attemptCts, linked);
        prev?.Dispose();
        return linked.Token;
    }

    /// <summary>Disposes the current per-attempt CTS once an attempt concludes.</summary>
    public void EndAttempt()
    {
        var prev = Interlocked.Exchange(ref _attemptCts, null);
        prev?.Dispose();
    }

    /// <summary>Drain any pending manual-reconnect signals (after a connect lands).</summary>
    public void DrainManualReconnectSignals()
    {
        while (_manualReconnect.CurrentCount > 0)
        {
            try { _manualReconnect.Wait(0); } catch { break; }
        }
    }

    public HostEntry(HostId id, HostConfig config, string clientId)
    {
        Id = id; Config = config; ClientId = clientId;
        // Seed the replay subscription set from the normalized config so it
        // survives reconnects (mirrors Swift HostRuntime seeding `subscriptions`
        // from `config.initialSubscriptions`).
        _subscriptions = config.InitialSubscriptions is { Count: > 0 }
            ? new List<string>(config.InitialSubscriptions)
            : new List<string>();
    }

    /// <summary>
    /// The current client, or null if not connected. Lock-free: a reference read
    /// is atomic and <c>_client</c> is <c>volatile</c>, so no lock is needed just
    /// to read one published reference.
    /// </summary>
    public AhpClient? CurrentClient => _client;

    public void SetClient(AhpClient? client, string protoVer)
    {
        // _protoVer is read together with _state/_updatedAt by Snapshot(), so the
        // write stays under the lock; the _client write is a volatile publish.
        lock (_gate) { _client = client; _protoVer = protoVer; }
    }

    public void SetState(HostState state)
    {
        lock (_gate) { _state = state; _updatedAt = DateTimeOffset.UtcNow; }
    }

    /// <summary>An immutable, consistent snapshot of this host's public state.</summary>
    public HostHandle Snapshot()
    {
        lock (_gate)
        {
            // Materialize summaries sorted by ModifiedAt descending (newest
            // first), matching Swift's `sessionSummaries` sort contract.
            var summaries = new List<SessionSummary>(_sessionSummaries.Values);
            summaries.Sort(static (a, b) =>
            {
                var byTime = b.ModifiedAt.CompareTo(a.ModifiedAt);
                if (byTime != 0) return byTime;
                // Stable tie-break on resource so equal timestamps are
                // deterministic across calls.
                return string.CompareOrdinal(a.Resource, b.Resource);
            });

            return new HostHandle
            {
                Id = Id,
                Label = Config.Label,
                ClientId = ClientId,
                State = _state,
                ProtocolVersion = _protoVer,
                UpdatedAt = _updatedAt,
                Agents = new List<AgentInfo>(_agents),
                SessionSummaries = summaries,
                ActiveSessions = _activeSessions,
                Subscriptions = new List<string>(_subscriptions),
                ServerSeq = _serverSeq,
                LastConnectedAt = _lastConnectedAt,
                Generation = _generation,
            };
        }
    }

    // ── Swift-parity observable mutators (all take _gate) ──────────────────

    /// <summary>
    /// Records a successful (re)connect: bumps the generation, stamps the
    /// connect time, and applies the root snapshot (agents + activeSessions)
    /// when present. Mirrors the `state.generation &amp;+= 1` / root-snapshot block
    /// in Swift's <c>completeHandshake</c>.
    /// </summary>
    public ulong ApplyConnected(RootState? root, long serverSeq)
    {
        lock (_gate)
        {
            _generation += 1;
            _lastConnectedAt = DateTimeOffset.UtcNow;
            _serverSeq = serverSeq;
            if (root is not null)
            {
                _agents = root.Agents is { } a ? new List<AgentInfo>(a) : new List<AgentInfo>();
                _activeSessions = root.ActiveSessions;
            }
            return _generation;
        }
    }

    /// <summary>
    /// Replaces the cached session summaries with the <c>listSessions</c> seed.
    /// Mirrors the `state.sessionSummaries.removeAll()` + repopulate block in
    /// Swift's <c>completeHandshake</c>.
    /// </summary>
    public void SeedSessionSummaries(IEnumerable<SessionSummary> items)
    {
        lock (_gate)
        {
            _sessionSummaries.Clear();
            foreach (var item in items) _sessionSummaries[item.Resource] = item;
        }
    }

    /// <summary>Adds or replaces a single cached summary (root/sessionAdded).</summary>
    public void PutSessionSummary(SessionSummary summary)
    {
        lock (_gate) { _sessionSummaries[summary.Resource] = summary; }
    }

    /// <summary>Removes a cached summary by URI (root/sessionRemoved).</summary>
    public void RemoveSessionSummary(string uri)
    {
        lock (_gate) { _sessionSummaries.Remove(uri); }
    }

    /// <summary>
    /// Applies a partial summary patch in place (root/sessionSummaryChanged).
    /// Identity fields (resource/provider/createdAt) are ignored per spec —
    /// mirrors Swift's <c>applySummaryChanges</c>.
    /// </summary>
    public void ApplySummaryChange(string uri, PartialSessionSummary changes)
    {
        lock (_gate)
        {
            if (!_sessionSummaries.TryGetValue(uri, out var existing)) return;
            if (changes.Title is { } title) existing.Title = title;
            if (changes.Status is { } status) existing.Status = status;
            if (changes.Activity is { } activity) existing.Activity = activity;
            if (changes.ModifiedAt is { } modifiedAt) existing.ModifiedAt = modifiedAt;
            if (changes.Project is { } project) existing.Project = project;
            if (changes.Model is { } model) existing.Model = model;
            if (changes.WorkingDirectory is { } wd) existing.WorkingDirectory = wd;
            if (changes.Changesets is { } changesets) existing.Changesets = changesets;
            _sessionSummaries[uri] = existing;
        }
    }

    /// <summary>Tracks a URI in the replay subscription set (idempotent).</summary>
    public void AppendSubscription(string uri)
    {
        lock (_gate) { if (!_subscriptions.Contains(uri)) _subscriptions.Add(uri); }
    }

    /// <summary>Drops a URI from the replay subscription set.</summary>
    public void RemoveSubscription(string uri)
    {
        lock (_gate) { _subscriptions.Remove(uri); }
    }
}

// ─── MultiHostClient ─────────────────────────────────────────────────────────

/// <summary>
/// Multi-host registry + reconnect supervisor. Manages N independent AHP hosts,
/// fans in their inbound events, and supervises reconnects per-host policy.
/// </summary>
public sealed class MultiHostClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostEntry> _hosts = new(StringComparer.Ordinal);
    private volatile IClientIdStore _store;

    private readonly List<System.Threading.Channels.Channel<HostEvent>> _eventChannels = new();
    private readonly Gate _eventsLock = new();

    private readonly List<System.Threading.Channels.Channel<HostSubscriptionEvent>> _subChannels = new();
    private readonly Gate _subsLock = new();

    // ── Per-host listener registries (Swift-parity, MultiHostClient-owned) ──
    // These live on the facade (NOT on any single AhpClient), so they survive
    // reconnects: replayed envelopes the supervisor fans out on reconnect reach
    // them too. Mirrors `perResourceListeners` / hostSnapshots / sessionSummaries
    // ownership in Swift's MultiHostClient.swift.
    private readonly Gate _perHostLock = new();
    // Per-(hostId) bucket of per-(uri) event listeners for EventsForHost.
    private readonly Dictionary<string, List<PerResourceListener>> _perResourceListeners = new(StringComparer.Ordinal);
    // Per-(hostId) bucket of HostHandle-snapshot listeners for HostSnapshots.
    private readonly Dictionary<string, List<System.Threading.Channels.Channel<HostHandle>>> _snapshotListeners = new(StringComparer.Ordinal);
    // Per-(hostId) bucket of session-summary-list listeners for SessionSummaries.
    private readonly Dictionary<string, List<System.Threading.Channels.Channel<IReadOnlyList<SessionSummary>>>> _summaryListeners = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _rootCts = new();

    // Set once ShutdownAsync has begun. Guards AddHostAsync (which throws
    // HostShutDownException afterward, mirroring Swift's `add` post-shutdown
    // behavior) and makes Shutdown idempotent. Read/written under _perHostLock.
    private bool _didShutDown;

    // ── Construction ──────────────────────────────────────────────────────

    /// <summary>Creates a multi-host registry backed by an <see cref="InMemoryClientIdStore"/>.</summary>
    public MultiHostClient() : this(new InMemoryClientIdStore()) { }

    /// <summary>Creates a multi-host registry backed by the given store.</summary>
    public MultiHostClient(IClientIdStore store) => _store = store;

    /// <summary>Swaps the <see cref="IClientIdStore"/>. Call before any <see cref="AddHostAsync"/>.</summary>
    public MultiHostClient WithClientIdStore(IClientIdStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    // ── Single-host convenience ───────────────────────────────────────────

    /// <summary>
    /// One-line constructor for the common "I just want one host" case.
    /// Returns the client and the initial host handle.
    /// </summary>
    public static async Task<(MultiHostClient Client, HostHandle Handle)> SingleAsync(
        HostConfig config,
        CancellationToken cancellationToken = default)
    {
        var m = new MultiHostClient();
        try
        {
            var handle = await m.AddHostAsync(config, cancellationToken).ConfigureAwait(false);
            return (m, handle);
        }
        catch
        {
            await m.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── Host management ───────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="config"/>, opens its initial transport, runs the
    /// <c>initialize</c> handshake, and starts the reconnect supervisor. Returns a
    /// fresh <see cref="HostHandle"/> snapshot.
    /// </summary>
    public async Task<HostHandle> AddHostAsync(
        HostConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.Id is null) throw new ArgumentException("HostConfig.Id is required.");
        if (config.TransportFactory is null)
            throw new ArgumentException($"HostConfig.TransportFactory is required for {config.Id}.");

        // After shutdown, adding a host is rejected with HostShutDownException
        // carrying the would-be host id (mirrors Swift `add` throwing
        // `.hostShutDown(id)` once `didShutDown`).
        lock (_perHostLock)
        {
            if (_didShutDown) throw new HostShutDownException(config.Id);
        }

        var policy = config.ReconnectPolicy ?? ReconnectPolicy.Default;
        var initialSubs = config.InitialSubscriptions is { Count: > 0 }
            ? config.InitialSubscriptions
            : new[] { ProtocolVersion.RootResourceUri };
        var protoVersions = config.ProtocolVersions is { Count: > 0 }
            ? config.ProtocolVersions
            : ProtocolVersion.Supported;

        // Resolve or mint a clientId.
        var clientId = config.ClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            clientId = await _store.LoadAsync(config.Id, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(clientId))
                clientId = GenerateClientId();
        }
        await _store.StoreAsync(config.Id, clientId, cancellationToken).ConfigureAwait(false);

        var normalizedConfig = new HostConfig
        {
            Id = config.Id,
            Label = config.Label,
            ClientId = clientId,
            InitialSubscriptions = initialSubs,
            ClientConfig = config.ClientConfig,
            TransportFactory = config.TransportFactory,
            ReconnectPolicy = policy,
            ProtocolVersions = protoVersions,
        };

        var entry = new HostEntry(config.Id, normalizedConfig, clientId);

        // Atomic add-if-absent: TryAdd is the check-then-act done correctly,
        // with no separate lock and no race window. Duplicate ids surface the
        // typed DuplicateHostException carrying the offending id (mirrors Swift
        // `add` throwing `.duplicateHost(id)`).
        if (!_hosts.TryAdd(config.Id.ToString(), entry))
            throw new DuplicateHostException(config.Id);

        // Initial connect; on failure remove the host and propagate.
        try
        {
            await OpenHostAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetHostState(entry, new HostState { Kind = HostStateKind.Failed, Error = ex });
            _hosts.TryRemove(entry.Id.ToString(), out _);
            throw;
        }

        // Start supervisor.
        entry.SupervisorTask = Task.Run(() => SuperviseAsync(entry));

        return entry.Snapshot();
    }

    /// <summary>Returns a fresh snapshot of the host with <paramref name="id"/>, or null if not registered.</summary>
    public HostHandle? Host(HostId id) =>
        _hosts.TryGetValue(id.ToString(), out var entry) ? entry.Snapshot() : null;

    /// <summary>Returns a fresh snapshot of every registered host.</summary>
    public List<HostHandle> Hosts()
    {
        // ConcurrentDictionary.Values is a moment-in-time snapshot — safe to
        // enumerate without external locking.
        var result = new List<HostHandle>();
        foreach (var e in _hosts.Values) result.Add(e.Snapshot());
        return result;
    }

    /// <summary>
    /// Acquires a generation-checked client handle for <paramref name="id"/>, or
    /// null if the host is not registered or has no live connection. The handle
    /// refuses to operate once the host has been removed (throwing
    /// <see cref="HostShutDownException"/>) or once a reconnect has replaced the
    /// connection it was minted against. Mirrors Swift's <c>client(for:)</c>.
    /// </summary>
    public HostClientHandle? ClientFor(HostId id)
    {
        if (!_hosts.TryGetValue(id.ToString(), out var entry)) return null;
        var snap = entry.Snapshot();
        if (entry.CurrentClient is null) return null;
        return new HostClientHandle(this, id, snap.Generation);
    }

    // Internal accessor used by HostClientHandle to validate liveness against
    // the live registry (returns null once the host is removed/shut down).
    internal HostEntry? TryGetEntry(HostId id) =>
        _hosts.TryGetValue(id.ToString(), out var entry) ? entry : null;

    /// <summary>
    /// Unregisters a host and tears down its supervisor and client. Throws
    /// <see cref="UnknownHostException"/> if no host with <paramref name="id"/>
    /// is registered. Per-host streams (<see cref="EventsForHost"/>,
    /// <see cref="HostSnapshots"/>, <see cref="SessionSummariesForHost"/>) for
    /// this host are finished so their <c>await foreach</c> loops exit cleanly.
    /// </summary>
    public async Task RemoveHostAsync(HostId id, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryRemove(id.ToString(), out var entry))
            throw new UnknownHostException(id);

        // Finish per-host listener streams first so consumers observing them
        // exit their loops as soon as the host is gone (mirrors Swift's
        // `finishPerResourceListeners(for:)` on `remove(_:)`).
        FinishPerHostListeners(id.ToString());

        entry!.LifetimeCts.Cancel();
        var client = entry.CurrentClient;
        if (client is not null)
        {
            try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        try { await entry.SupervisorTask.ConfigureAwait(false); } catch { }
        try { await entry.PumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        entry.LifetimeCts.Dispose();
    }

    // ── Event channels ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a channel that receives <see cref="HostEvent"/> state transitions.
    /// Each call returns an independent channel; slow consumers drop events.
    /// </summary>
    public System.Threading.Channels.ChannelReader<HostEvent> Events()
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<HostEvent>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
        lock (_eventsLock) { _eventChannels.Add(ch); }
        return ch.Reader;
    }

    /// <summary>
    /// Returns a channel that receives every <see cref="HostSubscriptionEvent"/> from
    /// every registered host.
    /// </summary>
    public System.Threading.Channels.ChannelReader<HostSubscriptionEvent> Subscriptions()
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<HostSubscriptionEvent>(
            new System.Threading.Channels.BoundedChannelOptions(256)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
        lock (_subsLock) { _subChannels.Add(ch); }
        return ch.Reader;
    }

    // ── Shutdown ──────────────────────────────────────────────────────────

    /// <summary>Tears down every host and releases registered event channels. Idempotent.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_perHostLock)
        {
            if (_didShutDown) return;
            _didShutDown = true;
        }

        _rootCts.Cancel();

        var entries = new List<HostEntry>(_hosts.Values);
        _hosts.Clear();

        // Finish per-host listener streams for every host so their consumers'
        // `await foreach` loops exit (mirrors the perResourceListeners finish in
        // Swift's shutdown()).
        foreach (var entry in entries) FinishPerHostListeners(entry.Id.ToString());

        foreach (var entry in entries)
        {
            entry.LifetimeCts.Cancel();
            // Wake a parked (failed/disabled) supervisor so it observes the
            // cancellation and exits rather than blocking on its manual-reconnect
            // wait forever.
            entry.SignalManualReconnect();
            var client = entry.CurrentClient;
            if (client is not null)
            {
                try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        // Wait for all supervisors and pump tasks.
        foreach (var entry in entries)
        {
            try { await entry.SupervisorTask.ConfigureAwait(false); } catch { }
            try { await entry.PumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
            entry.LifetimeCts.Dispose();
        }

        // Complete all event/subscription channel writers so consumers' await foreach terminates.
        lock (_eventsLock)
        {
            foreach (var ch in _eventChannels) ch.Writer.TryComplete();
        }
        lock (_subsLock)
        {
            foreach (var ch in _subChannels) ch.Writer.TryComplete();
        }

        _rootCts.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    // ── Internal: openHost, supervisor, pumpEvents ────────────────────────

    private async Task OpenHostAsync(HostEntry entry, CancellationToken cancellationToken)
    {
        SetHostState(entry, new HostState { Kind = HostStateKind.Connecting });

        var transport = await entry.Config.TransportFactory!(entry.Id, cancellationToken).ConfigureAwait(false);
        var client = AhpClient.Connect(
            transport,
            entry.Config.ClientConfig,
            null);

        InitializeResult result;
        try
        {
            result = await client.InitializeAsync(
                entry.ClientId,
                entry.Config.ProtocolVersions,
                entry.Config.InitialSubscriptions,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }

        entry.SetClient(client, result.ProtocolVersion);

        // Extract the root-state snapshot (agents + activeSessions) that the
        // server returned for the root channel, mirroring the
        // `init1.snapshots.first(where: resource == RootResourceURI)` block in
        // Swift's completeHandshake.
        var root = ExtractRootSnapshot(result);
        var generation = entry.ApplyConnected(root, result.ServerSeq);

        // Opportunistic `listSessions` seed. Cheap on first connect; kept in
        // sync by notifications afterward. Non-fatal: a host that doesn't
        // answer (or is slow) leaves the cache untouched, exactly like Swift's
        // `try? await client.request("listSessions", ...)`. We bound the wait
        // with a short timeout so hosts that never answer don't stall the
        // connect (the default request timeout is 30s).
        await SeedSessionSummariesAsync(entry, client, cancellationToken).ConfigureAwait(false);

        SetHostState(entry, new HostState { Kind = HostStateKind.Connected });

        // Emit a post-connect snapshot + summary list to per-host stream
        // listeners (the connect transition is the first "observable change"
        // after listSessions lands), mirroring the `.connected` re-yields in
        // Swift's hostSnapshots / sessionSummaries watchers.
        NotifyPerHostSnapshot(entry);
        NotifyPerHostSummaries(entry);
        _ = generation; // bumped for parity; surfaced via HostHandle.Generation

        // Fan events out to subscribers.
        entry.PumpTask = Task.Run(() => PumpEventsAsync(entry, client));
    }

    /// <summary>
    /// Pulls the <see cref="RootState"/> out of the root-channel snapshot in an
    /// <see cref="InitializeResult"/>, or null if no root snapshot is present.
    /// </summary>
    private static RootState? ExtractRootSnapshot(InitializeResult result)
    {
        if (result.Snapshots is null) return null;
        foreach (var snap in result.Snapshots)
        {
            if (snap.Resource == ProtocolVersion.RootResourceUri && snap.State?.Root is { } root)
                return root;
        }
        return null;
    }

    /// <summary>
    /// Issues a best-effort <c>listSessions</c> on the root channel and seeds the
    /// host's summary cache. Bounded by a short timeout and fully non-fatal —
    /// failures/timeouts leave the cache as-is.
    /// </summary>
    private static async Task SeedSessionSummariesAsync(HostEntry entry, AhpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(750));
            var listed = await client.RequestAsync<ListSessionsParams, ListSessionsResult>(
                "listSessions",
                new ListSessionsParams { Channel = ProtocolVersion.RootResourceUri },
                timeoutCts.Token)
                .ConfigureAwait(false);
            if (listed?.Items is { } items)
                entry.SeedSessionSummaries(items);
        }
        catch
        {
            // Non-fatal: host did not answer listSessions in time, or returned
            // an error. Cache stays as-is (matches Swift `try?`).
        }
    }

    private async Task PumpEventsAsync(HostEntry entry, AhpClient client)
    {
        var stream = client.CreateEventStream();
        try
        {
            await foreach (var ev in stream.Events.ReadAllAsync().ConfigureAwait(false))
            {
                // Update per-host observable state BEFORE broadcasting so any
                // observer reading the next snapshot sees the post-event state
                // (mirrors the ordering in Swift HostRuntime.handleEvent).
                var summaryTouched = ApplyEventToHostState(entry, ev.Event);

                var hostEv = new HostSubscriptionEvent(entry.Id, ev.Channel, ev.Event);
                List<System.Threading.Channels.Channel<HostSubscriptionEvent>> channels;
                lock (_subsLock) { channels = new List<System.Threading.Channels.Channel<HostSubscriptionEvent>>(_subChannels); }
                foreach (var ch in channels) ch.Writer.TryWrite(hostEv);

                // Fan to per-(host,uri) listeners scoped to this channel
                // (reducer-critical reliable path, runtime-owned so it survives
                // reconnect — Swift's perResourceListeners).
                BroadcastPerResourceEvent(entry.Id, ev.Channel, ev.Event);

                // A session-summary-shaped notification advanced the cache:
                // re-yield the snapshot + summary list to per-host listeners.
                if (summaryTouched)
                {
                    NotifyPerHostSnapshot(entry);
                    NotifyPerHostSummaries(entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via LifetimeCts.
        }
        catch (Exception ex)
        {
            // Unexpected pump failure — mark the host as failed.
            SetHostState(entry, new HostState
            {
                Kind = HostStateKind.Failed,
                Error = ex,
            });
        }
        finally
        {
            stream.Close();
        }
    }

    private async Task SuperviseAsync(HostEntry entry)
    {
        var policy = entry.Config.ReconnectPolicy ?? ReconnectPolicy.Default;
        var ct = entry.LifetimeCts.Token;

        while (true)
        {
            if (ct.IsCancellationRequested) return;
            var client = entry.CurrentClient;
            if (client is null) return;

            // Wait for either a transport drop (client.Completion) OR a manual
            // reconnect request. A manual reconnect on a connected host wins the
            // race and forces a fresh connect cycle below (mirrors Swift's
            // `manualReconnect` case interrupting `runConnection`).
            bool manualWhileConnected;
            try
            {
                manualWhileConnected = await entry.WaitForDropOrManualReconnectAsync(client.Completion, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            // Tear the old client down before reconnecting (whether it dropped
            // or we're forcing a manual reconnect).
            try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            entry.SetClient(null, "");

            // A manual reconnect bypasses the reconnect policy entirely — even a
            // `.disabled` policy reconnects on explicit request. A spontaneous
            // drop on a disabled policy parks in `.failed` (then waits for a
            // manual reconnect to wake).
            if (!manualWhileConnected && policy.IsDisabled)
            {
                SetHostState(entry, new HostState
                {
                    Kind = HostStateKind.Failed,
                    Error = new Exception("hosts: transport closed and reconnect disabled"),
                });
                if (!await ParkUntilManualReconnectAsync(entry, ct).ConfigureAwait(false)) return;
                manualWhileConnected = true; // woken explicitly; bypass backoff
            }

            // Reconnect attempt loop. A manual reconnect skips the backoff sleep
            // for the first attempt (immediate). Per-attempt cancellation lets a
            // later manual reconnect / removal abort a slow transport factory.
            uint attempt = 1;
            bool immediate = manualWhileConnected;
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                SetHostState(entry, new HostState { Kind = HostStateKind.Reconnecting, Attempt = attempt });

                if (!immediate)
                {
                    var delay = policy.BackoffFor(attempt);
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                immediate = false;

                var attemptCt = entry.BeginAttempt();
                try
                {
                    await OpenHostAsync(entry, attemptCt).ConfigureAwait(false);
                    entry.EndAttempt();
                    entry.DrainManualReconnectSignals();
                    break; // reconnected successfully
                }
                catch (OperationCanceledException)
                {
                    entry.EndAttempt();
                    // Distinguish a lifetime cancel (shut down → exit) from an
                    // attempt-scoped cancel triggered by a manual reconnect /
                    // removal aborting a slow factory.
                    if (ct.IsCancellationRequested) return;
                    // Manual reconnect aborted this attempt: retry immediately.
                    entry.DrainManualReconnectSignals();
                    immediate = true;
                    continue;
                }
                catch
                {
                    entry.EndAttempt();
                    /* retry after backoff */
                }

                attempt++;
                if (policy.MaxAttempts > 0 && attempt > policy.MaxAttempts)
                {
                    SetHostState(entry, new HostState
                    {
                        Kind = HostStateKind.Failed,
                        Error = new Exception($"hosts: exceeded {policy.MaxAttempts} reconnect attempts"),
                    });
                    // Park in `.failed` until a manual reconnect wakes us (a
                    // manual reconnect bypasses the exhausted policy), mirroring
                    // Swift's `waitForManualReconnectOrShutdown`.
                    if (!await ParkUntilManualReconnectAsync(entry, ct).ConfigureAwait(false)) return;
                    attempt = 1;
                    immediate = true;
                }
            }
        }
    }

    /// <summary>
    /// Parks a host in its terminal (<c>.failed</c>) state until a manual
    /// reconnect is requested or the host lifetime is cancelled. Returns true if
    /// a manual reconnect woke it (caller should re-attempt), false on
    /// cancellation (caller should exit). Mirrors Swift's
    /// <c>waitForManualReconnectOrShutdown</c>.
    /// </summary>
    private static async Task<bool> ParkUntilManualReconnectAsync(HostEntry entry, CancellationToken ct)
    {
        entry.DrainManualReconnectSignals();
        return await entry.WaitForManualReconnectAsync(ct).ConfigureAwait(false);
    }

    private void SetHostState(HostEntry entry, HostState state)
    {
        entry.SetState(state);

        var ev = new HostEvent(entry.Id, state);
        List<System.Threading.Channels.Channel<HostEvent>> channels;
        lock (_eventsLock) { channels = new List<System.Threading.Channels.Channel<HostEvent>>(_eventChannels); }
        foreach (var ch in channels) ch.Writer.TryWrite(ev);

        // A state transition is an observable change for hostSnapshots
        // consumers (Swift re-yields a fresh snapshot on `.stateChanged`).
        NotifyPerHostSnapshot(entry);
    }

    private static string GenerateClientId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Per-host observable plumbing ──────────────────────────────────────

    /// <summary>
    /// Applies a subscription event to a host's cached observable state. Returns
    /// true if the event mutated the session-summary cache (so per-host snapshot
    /// / summary listeners should be re-yielded). Mirrors the cache mutations in
    /// Swift's <c>HostRuntime.handleEvent</c> + <c>applyAction</c>.
    /// </summary>
    private static bool ApplyEventToHostState(HostEntry entry, SubscriptionEvent ev)
    {
        switch (ev)
        {
            case SubscriptionEventSessionAdded added:
                entry.PutSessionSummary(added.Params.Summary);
                return true;
            case SubscriptionEventSessionRemoved removed:
                entry.RemoveSessionSummary(removed.Params.Session);
                return true;
            case SubscriptionEventSessionSummaryChanged changed:
                entry.ApplySummaryChange(changed.Params.Session, changed.Params.Changes);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Fans an event scoped to <paramref name="channel"/> to every per-(host,uri)
    /// listener whose URI matches. Listeners are runtime-owned so they survive
    /// reconnect. Mirrors the per-channel fan-out in Swift's
    /// <c>broadcastSubscriptionEvent</c>.
    /// </summary>
    private void BroadcastPerResourceEvent(HostId hostId, string channel, SubscriptionEvent ev)
    {
        List<PerResourceListener>? listeners;
        lock (_perHostLock)
        {
            if (!_perResourceListeners.TryGetValue(hostId.ToString(), out var bucket)) return;
            listeners = new List<PerResourceListener>(bucket);
        }
        foreach (var l in listeners)
        {
            if (l.Uri == channel) l.Channel.Writer.TryWrite(ev);
        }
    }

    /// <summary>Re-yields a fresh <see cref="HostHandle"/> snapshot to per-host snapshot listeners.</summary>
    private void NotifyPerHostSnapshot(HostEntry entry)
    {
        List<System.Threading.Channels.Channel<HostHandle>>? listeners;
        lock (_perHostLock)
        {
            if (!_snapshotListeners.TryGetValue(entry.Id.ToString(), out var bucket) || bucket.Count == 0) return;
            listeners = new List<System.Threading.Channels.Channel<HostHandle>>(bucket);
        }
        var snap = entry.Snapshot();
        foreach (var ch in listeners) ch.Writer.TryWrite(snap);
    }

    /// <summary>Re-yields the current sorted summary list to per-host summary listeners.</summary>
    private void NotifyPerHostSummaries(HostEntry entry)
    {
        List<System.Threading.Channels.Channel<IReadOnlyList<SessionSummary>>>? listeners;
        lock (_perHostLock)
        {
            if (!_summaryListeners.TryGetValue(entry.Id.ToString(), out var bucket) || bucket.Count == 0) return;
            listeners = new List<System.Threading.Channels.Channel<IReadOnlyList<SessionSummary>>>(bucket);
        }
        var summaries = entry.Snapshot().SessionSummaries;
        foreach (var ch in listeners) ch.Writer.TryWrite(summaries);
    }

    /// <summary>
    /// Finishes (completes) every per-host listener stream for <paramref name="hostId"/>
    /// and drops the buckets, so consumers' <c>await foreach</c> loops exit. Called
    /// on host removal and shutdown. Mirrors Swift's <c>finishPerResourceListeners</c>.
    /// </summary>
    private void FinishPerHostListeners(string hostId)
    {
        List<PerResourceListener>? perResource = null;
        List<System.Threading.Channels.Channel<HostHandle>>? snapshots = null;
        List<System.Threading.Channels.Channel<IReadOnlyList<SessionSummary>>>? summaries = null;
        lock (_perHostLock)
        {
            if (_perResourceListeners.Remove(hostId, out var b1)) perResource = b1;
            if (_snapshotListeners.Remove(hostId, out var b2)) snapshots = b2;
            if (_summaryListeners.Remove(hostId, out var b3)) summaries = b3;
        }
        if (perResource is not null) foreach (var l in perResource) l.Channel.Writer.TryComplete();
        if (snapshots is not null) foreach (var ch in snapshots) ch.Writer.TryComplete();
        if (summaries is not null) foreach (var ch in summaries) ch.Writer.TryComplete();
    }

    // ── Per-host streams (Swift-parity public API) ────────────────────────

    /// <summary>
    /// Per-<c>(host, uri)</c> event stream — the reliable channel for
    /// reducer-critical action envelopes. Delivers every event scoped to
    /// <paramref name="uri"/> on <paramref name="host"/>, both live and replayed
    /// across reconnects (the listener is owned by this facade, not by any single
    /// <see cref="AhpClient"/>). The stream finishes when the host is removed or
    /// the client shuts down. Mirrors Swift's <c>events(host:uri:)</c>.
    ///
    /// <para>Throws <see cref="UnknownHostException"/> if no host with
    /// <paramref name="host"/> is registered. (Swift returns nil here; the .NET
    /// surface throws a typed error, per the parity test contract.)</para>
    /// </summary>
    public System.Threading.Channels.ChannelReader<SubscriptionEvent> EventsForHost(HostId host, string uri)
    {
        lock (_perHostLock)
        {
            if (!_hosts.ContainsKey(host.ToString())) throw new UnknownHostException(host);
            var ch = System.Threading.Channels.Channel.CreateUnbounded<SubscriptionEvent>();
            var listener = new PerResourceListener(uri, ch);
            if (!_perResourceListeners.TryGetValue(host.ToString(), out var bucket))
            {
                bucket = new List<PerResourceListener>();
                _perResourceListeners[host.ToString()] = bucket;
            }
            bucket.Add(listener);
            return ch.Reader;
        }
    }

    /// <summary>
    /// Observable stream of <see cref="HostHandle"/> snapshots for
    /// <paramref name="host"/>. Yields the current snapshot immediately, then a
    /// fresh snapshot whenever the host's observable state changes (connection
    /// state transitions, reconnect completion, session-summary updates). The
    /// stream finishes when the host is removed. Mirrors Swift's
    /// <c>hostSnapshots(host:)</c>.
    ///
    /// <para>Throws <see cref="UnknownHostException"/> if no host with
    /// <paramref name="host"/> is registered.</para>
    /// </summary>
    public System.Threading.Channels.ChannelReader<HostHandle> HostSnapshots(HostId host)
    {
        lock (_perHostLock)
        {
            if (!_hosts.TryGetValue(host.ToString(), out var entry)) throw new UnknownHostException(host);
            // bufferingNewest(1)-equivalent: only the latest snapshot matters to
            // a UI consumer, so slow consumers drop intermediate snapshots.
            var ch = System.Threading.Channels.Channel.CreateBounded<HostHandle>(
                new System.Threading.Channels.BoundedChannelOptions(1)
                { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            if (!_snapshotListeners.TryGetValue(host.ToString(), out var bucket))
            {
                bucket = new List<System.Threading.Channels.Channel<HostHandle>>();
                _snapshotListeners[host.ToString()] = bucket;
            }
            bucket.Add(ch);
            // Dispatch the initial snapshot as the first stream element.
            ch.Writer.TryWrite(entry.Snapshot());
            return ch.Reader;
        }
    }

    /// <summary>
    /// Observable stream of cached session summaries for <paramref name="host"/>,
    /// sorted by <c>ModifiedAt</c> descending. Yields the current cache
    /// immediately, then a fresh sorted list whenever the cache changes
    /// (<c>listSessions</c> refresh on connect, or session add/remove/summary-change
    /// notifications). The stream finishes when the host is removed. Mirrors
    /// Swift's <c>sessionSummaries(host:)</c>.
    ///
    /// <para>Throws <see cref="UnknownHostException"/> if no host with
    /// <paramref name="host"/> is registered.</para>
    /// </summary>
    public System.Threading.Channels.ChannelReader<IReadOnlyList<SessionSummary>> SessionSummariesForHost(HostId host)
    {
        lock (_perHostLock)
        {
            if (!_hosts.TryGetValue(host.ToString(), out var entry)) throw new UnknownHostException(host);
            var ch = System.Threading.Channels.Channel.CreateBounded<IReadOnlyList<SessionSummary>>(
                new System.Threading.Channels.BoundedChannelOptions(1)
                { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            if (!_summaryListeners.TryGetValue(host.ToString(), out var bucket))
            {
                bucket = new List<System.Threading.Channels.Channel<IReadOnlyList<SessionSummary>>>();
                _summaryListeners[host.ToString()] = bucket;
            }
            bucket.Add(ch);
            ch.Writer.TryWrite(entry.Snapshot().SessionSummaries);
            return ch.Reader;
        }
    }

    // ── Aggregated views (Swift-parity public API) ────────────────────────

    /// <summary>
    /// Aggregated session summaries across every registered host, sorted by
    /// <c>Summary.ModifiedAt</c> descending. Each row carries the originating
    /// host id + label so consumers render a unified inbox without losing host
    /// attribution. Tie-break for equal timestamps: host registration order,
    /// then <c>Summary.Resource</c>. Mirrors Swift's <c>aggregatedSessions()</c>.
    /// </summary>
    public List<HostedSessionSummary> AggregatedSessions()
    {
        // Registration order for the secondary tie-break, captured once.
        var order = new List<HostEntry>(_hosts.Values);
        var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) orderIndex[order[i].Id.ToString()] = i;

        var rows = new List<HostedSessionSummary>();
        foreach (var entry in order)
        {
            var snap = entry.Snapshot();
            foreach (var summary in snap.SessionSummaries)
                rows.Add(new HostedSessionSummary(snap.Id, snap.Label, summary));
        }

        rows.Sort((a, b) =>
        {
            if (a.Summary.ModifiedAt != b.Summary.ModifiedAt)
                return b.Summary.ModifiedAt.CompareTo(a.Summary.ModifiedAt); // newest first
            var ai = orderIndex.TryGetValue(a.HostId.ToString(), out var x) ? x : int.MaxValue;
            var bi = orderIndex.TryGetValue(b.HostId.ToString(), out var y) ? y : int.MaxValue;
            if (ai != bi) return ai.CompareTo(bi);
            return string.CompareOrdinal(a.Summary.Resource, b.Summary.Resource);
        });
        return rows;
    }

    /// <summary>
    /// Aggregated agents across every registered host, in registration order per
    /// host. Each row carries the originating host id + label. Mirrors Swift's
    /// <c>aggregatedAgents()</c>.
    /// </summary>
    public List<HostedAgent> AggregatedAgents()
    {
        var rows = new List<HostedAgent>();
        foreach (var entry in _hosts.Values)
        {
            var snap = entry.Snapshot();
            foreach (var agent in snap.Agents)
                rows.Add(new HostedAgent(snap.Id, snap.Label, agent));
        }
        return rows;
    }

    // ── Manual reconnect (Swift-parity public API) ────────────────────────

    /// <summary>
    /// Triggers a manual reconnect on <paramref name="id"/>. Cancels any in-flight
    /// backoff sleep (or slow transport factory) and forces a fresh connect
    /// attempt — even when the host is in <c>Failed</c> with an exhausted/disabled
    /// reconnect policy. Mirrors Swift's <c>reconnect(_:)</c>.
    ///
    /// <para>Throws <see cref="UnknownHostException"/> if no host with
    /// <paramref name="id"/> is registered.</para>
    /// </summary>
    public Task ReconnectAsync(HostId id, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(id.ToString(), out var entry))
            throw new UnknownHostException(id);
        entry.SignalManualReconnect();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Triggers a manual reconnect on every registered host that is NOT currently
    /// <c>Connected</c> or <c>Connecting</c> (i.e. <c>Disconnected</c>,
    /// <c>Reconnecting</c>, or <c>Failed</c>). Connected / actively-connecting
    /// hosts are skipped. Returns a map of host id → error for hosts whose
    /// reconnect request could not be dispatched; the call itself does not throw.
    /// Mirrors Swift's <c>reconnectAllUnavailable()</c>.
    /// </summary>
    public Task<Dictionary<HostId, Exception>> ReconnectAllUnavailableAsync(CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<HostId, Exception>();
        foreach (var entry in _hosts.Values)
        {
            var snap = entry.Snapshot();
            switch (snap.State.Kind)
            {
                case HostStateKind.Connected:
                case HostStateKind.Connecting:
                    continue; // skip — already connected or actively connecting
                default:
                    try { entry.SignalManualReconnect(); }
                    catch (Exception ex) { errors[entry.Id] = ex; }
                    break;
            }
        }
        return Task.FromResult(errors);
    }

    // ── Per-host dispatch / subscribe (typed-error surface) ───────────────

    /// <summary>
    /// Dispatches <paramref name="action"/> on <paramref name="host"/> for
    /// <paramref name="channel"/>. Throws <see cref="UnknownHostException"/> if no
    /// such host is registered, or <see cref="HostNotConnectedException"/> if the
    /// host has no live connection. Mirrors Swift's <c>dispatch(host:…)</c>.
    /// </summary>
    public async Task<DispatchHandle> DispatchAsync(
        HostId host,
        StateAction action,
        string channel,
        CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(host.ToString(), out var entry))
            throw new UnknownHostException(host);
        var client = entry.CurrentClient;
        if (client is null)
            throw new HostNotConnectedException(host);
        return await client.DispatchAsync(channel, action, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes to <paramref name="uri"/> on <paramref name="host"/>, tracking
    /// the URI for replay across reconnects. Throws <see cref="UnknownHostException"/>
    /// if no such host is registered, or <see cref="HostNotConnectedException"/> if
    /// the host has no live connection. Mirrors Swift's <c>subscribe(host:uri:)</c>.
    /// </summary>
    public async Task<SubscribeResult> SubscribeAsync(
        HostId host,
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(host.ToString(), out var entry))
            throw new UnknownHostException(host);
        var client = entry.CurrentClient;
        if (client is null)
            throw new HostNotConnectedException(host);
        var sub = client.AttachSubscription(uri);
        try
        {
            // Issue the subscribe RPC; track the URI for replay on success.
            var result = await client.RequestAsync<SubscribeParams, SubscribeResult>(
                "subscribe",
                new SubscribeParams { Channel = uri },
                cancellationToken).ConfigureAwait(false);
            entry.AppendSubscription(uri);
            return result;
        }
        catch
        {
            sub.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One per-<c>(host, uri)</c> listener registered via
    /// <see cref="EventsForHost"/>. Held in the facade's
    /// <c>_perResourceListeners</c> registry so it outlives any single
    /// <see cref="AhpClient"/> and survives reconnects. Mirrors Swift's
    /// <c>PerResourceListener</c>.
    /// </summary>
    private sealed class PerResourceListener
    {
        public string Uri { get; }
        public System.Threading.Channels.Channel<SubscriptionEvent> Channel { get; }

        public PerResourceListener(string uri, System.Threading.Channels.Channel<SubscriptionEvent> channel)
        {
            Uri = uri; Channel = channel;
        }
    }
}

// ─── MultiHostStateMirror ─────────────────────────────────────────────────────

/// <summary>
/// Thread-safe map of (hostId, URI) → state snapshot. Port of
/// <c>multi_host_state_mirror.go</c>. Writes snapshots in; reads them back;
/// drops them when the host or resource disappears.
/// </summary>
public sealed class MultiHostStateMirror
{
    // Independent per-key snapshots: ConcurrentDictionary gives lock-free
    // reads and fine-grained writes, which is exactly this access pattern.
    // The per-resource maps key by HostedResourceKey (host + URI value type) so a
    // host id and a URI compose into one collision-free key with value equality —
    // no ad-hoc tuple delimiter to confuse with reserved URI characters.
    private readonly ConcurrentDictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<HostedResourceKey, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<HostedResourceKey, TerminalState> _terminals = new();
    private readonly ConcurrentDictionary<HostedResourceKey, ChangesetState> _changesets = new();

    /// <summary>Stores <paramref name="root"/> for <paramref name="hostId"/>.</summary>
    public void PutRoot(string hostId, RootState root) => _roots[hostId] = root;

    /// <summary>Returns the root snapshot for <paramref name="hostId"/>, or (default, false) if absent.</summary>
    public (RootState? Value, bool Found) Root(string hostId) =>
        _roots.TryGetValue(hostId, out var v) ? (v, true) : (default, false);

    /// <summary>Stores a session snapshot under (hostId, uri).</summary>
    public void PutSession(string hostId, string uri, SessionState state) => _sessions[new HostedResourceKey(hostId, uri)] = state;

    /// <summary>Returns the session snapshot at (hostId, uri), or (default, false) if absent.</summary>
    public (SessionState? Value, bool Found) Session(string hostId, string uri) =>
        _sessions.TryGetValue(new HostedResourceKey(hostId, uri), out var v) ? (v, true) : (default, false);

    /// <summary>Stores a terminal snapshot under (hostId, uri).</summary>
    public void PutTerminal(string hostId, string uri, TerminalState state) => _terminals[new HostedResourceKey(hostId, uri)] = state;

    /// <summary>Returns the terminal snapshot at (hostId, uri), or (default, false) if absent.</summary>
    public (TerminalState? Value, bool Found) Terminal(string hostId, string uri) =>
        _terminals.TryGetValue(new HostedResourceKey(hostId, uri), out var v) ? (v, true) : (default, false);

    /// <summary>Stores a changeset snapshot under (hostId, uri).</summary>
    public void PutChangeset(string hostId, string uri, ChangesetState state) => _changesets[new HostedResourceKey(hostId, uri)] = state;

    /// <summary>Returns the changeset snapshot at (hostId, uri), or (default, false) if absent.</summary>
    public (ChangesetState? Value, bool Found) Changeset(string hostId, string uri) =>
        _changesets.TryGetValue(new HostedResourceKey(hostId, uri), out var v) ? (v, true) : (default, false);

    /// <summary>Removes every snapshot belonging to <paramref name="hostId"/>.</summary>
    public void DropHost(string hostId)
    {
        _roots.TryRemove(hostId, out _);
        foreach (var k in _sessions.Keys) if (k.HostId.ToString() == hostId) _sessions.TryRemove(k, out _);
        foreach (var k in _terminals.Keys) if (k.HostId.ToString() == hostId) _terminals.TryRemove(k, out _);
        foreach (var k in _changesets.Keys) if (k.HostId.ToString() == hostId) _changesets.TryRemove(k, out _);
    }

    /// <summary>Removes the snapshot at (hostId, uri) across every resource kind.</summary>
    public void DropResource(string hostId, string uri)
    {
        var key = new HostedResourceKey(hostId, uri);
        _sessions.TryRemove(key, out _);
        _terminals.TryRemove(key, out _);
        _changesets.TryRemove(key, out _);
    }
}
