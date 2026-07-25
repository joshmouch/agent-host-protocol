// Supporting types: IClientIdStore, host events, and subscription events.
#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Hosts;

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

/// <summary>
/// A connection-level event for a registered host. Two shapes exist, mirroring
/// the relevant cases of Swift's <c>HostEvent</c> enum:
/// <list type="bullet">
/// <item>a <b>state change</b> (<see cref="IsRemoved"/> is <c>false</c>) carries
/// the host's new <see cref="State"/> — Swift's <c>stateChanged</c>; and</item>
/// <item>a <b>removal</b> (<see cref="IsRemoved"/> is <c>true</c>), emitted when
/// the host is removed via <see cref="MultiHostClient.RemoveHostAsync"/> —
/// Swift's <c>removed(HostId)</c>. A removal carries a sentinel
/// <see cref="State"/> of kind <see cref="HostStateKind.Disconnected"/> (the host
/// is gone), so consumers should branch on <see cref="IsRemoved"/> first.</item>
/// </list>
/// </summary>
public sealed class HostEvent
{
    /// <summary>Which host this event belongs to.</summary>
    public HostId HostId { get; }

    /// <summary>The new state. For a removal event this is a sentinel
    /// (<see cref="HostStateKind.Disconnected"/>); branch on <see cref="IsRemoved"/>.</summary>
    public HostState State { get; }

    /// <summary>
    /// True when this event signals the host was removed from the registry
    /// (mirrors Swift's <c>HostEvent.removed(id)</c>). False for ordinary state
    /// transitions (mirrors Swift's <c>HostEvent.stateChanged</c>).
    /// </summary>
    public bool IsRemoved { get; }

    /// <summary>Creates a host state-change event (<see cref="IsRemoved"/> = false).</summary>
    public HostEvent(HostId hostId, HostState state) { HostId = hostId; State = state; IsRemoved = false; }

    private HostEvent(HostId hostId, HostState state, bool isRemoved)
    {
        HostId = hostId; State = state; IsRemoved = isRemoved;
    }

    /// <summary>
    /// Creates a host <b>removed</b> event for <paramref name="hostId"/>, mirroring
    /// Swift's <c>HostEvent.removed(id)</c>. Carries a sentinel
    /// <see cref="HostStateKind.Disconnected"/> state since the host is gone.
    /// </summary>
    public static HostEvent Removed(HostId hostId) =>
        new(hostId, new HostState { Kind = HostStateKind.Disconnected }, isRemoved: true);
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
