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

    public CancellationTokenSource LifetimeCts { get; } = new();
    public Task SupervisorTask { get; set; } = Task.CompletedTask;

    /// <summary>Task for the fire-and-forget pump loop started in OpenHostAsync.</summary>
    public Task PumpTask { get; set; } = Task.CompletedTask;

    public HostEntry(HostId id, HostConfig config, string clientId)
    {
        Id = id; Config = config; ClientId = clientId;
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
            return new HostHandle
            {
                Id = Id,
                Label = Config.Label,
                ClientId = ClientId,
                State = _state,
                ProtocolVersion = _protoVer,
                UpdatedAt = _updatedAt,
            };
        }
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

    private readonly CancellationTokenSource _rootCts = new();

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
        // with no separate lock and no race window.
        if (!_hosts.TryAdd(config.Id.ToString(), entry))
            throw new InvalidOperationException($"hosts: host id already registered: {config.Id}");

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

    /// <summary>Unregisters a host and tears down its supervisor and client.</summary>
    public async Task RemoveHostAsync(HostId id, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryRemove(id.ToString(), out var entry))
            throw new InvalidOperationException($"hosts: unknown host id: {id}");

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

    /// <summary>Tears down every host and releases registered event channels.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _rootCts.Cancel();

        var entries = new List<HostEntry>(_hosts.Values);
        _hosts.Clear();

        foreach (var entry in entries)
        {
            entry.LifetimeCts.Cancel();
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
        SetHostState(entry, new HostState { Kind = HostStateKind.Connected });

        // Fan events out to subscribers.
        entry.PumpTask = Task.Run(() => PumpEventsAsync(entry, client));
    }

    private async Task PumpEventsAsync(HostEntry entry, AhpClient client)
    {
        var stream = client.CreateEventStream();
        try
        {
            await foreach (var ev in stream.Events.ReadAllAsync().ConfigureAwait(false))
            {
                var hostEv = new HostSubscriptionEvent(entry.Id, ev.Channel, ev.Event);
                List<System.Threading.Channels.Channel<HostSubscriptionEvent>> channels;
                lock (_subsLock) { channels = new List<System.Threading.Channels.Channel<HostSubscriptionEvent>>(_subChannels); }
                foreach (var ch in channels) ch.Writer.TryWrite(hostEv);
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
            var client = entry.CurrentClient;
            if (client is null) return;

            try { await client.Completion.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;
            if (policy.IsDisabled)
            {
                SetHostState(entry, new HostState
                {
                    Kind = HostStateKind.Failed,
                    Error = new Exception("hosts: transport closed and reconnect disabled"),
                });
                return;
            }

            // Ensure old client is torn down.
            try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

            uint attempt = 1;
            while (true)
            {
                SetHostState(entry, new HostState { Kind = HostStateKind.Reconnecting, Attempt = attempt });
                var delay = policy.BackoffFor(attempt);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    await OpenHostAsync(entry, ct).ConfigureAwait(false);
                    if (policy.ResetOnSuccess) attempt = 0;
                    break; // reconnected successfully
                }
                catch (OperationCanceledException) { return; }
                catch { /* retry */ }

                attempt++;
                if (policy.MaxAttempts > 0 && attempt > policy.MaxAttempts)
                {
                    SetHostState(entry, new HostState
                    {
                        Kind = HostStateKind.Failed,
                        Error = new Exception($"hosts: exceeded {policy.MaxAttempts} reconnect attempts"),
                    });
                    return;
                }
            }
        }
    }

    private void SetHostState(HostEntry entry, HostState state)
    {
        entry.SetState(state);

        var ev = new HostEvent(entry.Id, state);
        List<System.Threading.Channels.Channel<HostEvent>> channels;
        lock (_eventsLock) { channels = new List<System.Threading.Channels.Channel<HostEvent>>(_eventChannels); }
        foreach (var ch in channels) ch.Writer.TryWrite(ev);
    }

    private static string GenerateClientId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
