// HostError — typed exceptions specific to the multi-host SDK layer.
//
// Faithful port of clients/swift/AgentHostProtocol/Sources/AgentHostProtocolClient/Hosts/HostError.swift.
// Swift models these as one `HostError` enum (unknownHost / hostReconnected /
// hostShutDown / duplicateHost / client). .NET prefers a small set of typed
// exception classes — one per case — each carrying the offending HostId so
// callers can `catch (DuplicateHostException ex)` and read `ex.HostId`.
//
// Errors from the underlying single-host AhpClient are NOT re-wrapped here:
// they propagate as the existing AhpException hierarchy (Errors.cs), mirroring
// Swift's `HostError.client(AHPClientError)` pass-through.
#nullable enable

using System;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Base exception for errors specific to the multi-host SDK layer
/// (<see cref="MultiHostClient"/>). Carries the <see cref="HostId"/> the error
/// pertains to. Port of Swift's <c>HostError</c> enum; each Swift case maps to
/// a concrete subclass here.
/// </summary>
public abstract class HostException : Exception
{
    /// <summary>The host this error pertains to.</summary>
    public HostId HostId { get; }

    /// <summary>Creates a host exception for <paramref name="hostId"/>.</summary>
    protected HostException(HostId hostId, string message) : base(message)
    {
        HostId = hostId;
    }
}

/// <summary>
/// Thrown when <see cref="MultiHostClient.AddHostAsync"/> is called with a host
/// id that is already registered (or mid-add from a concurrent caller). Port of
/// Swift's <c>HostError.duplicateHost(HostId)</c>.
/// </summary>
public sealed class DuplicateHostException : HostException
{
    /// <summary>Creates a duplicate-host exception.</summary>
    public DuplicateHostException(HostId hostId)
        : base(hostId, $"hosts: host {hostId} is already registered; remove it first") { }
}

/// <summary>
/// Thrown when an operation references a host id that is not currently
/// registered. Port of Swift's <c>HostError.unknownHost(HostId)</c>.
/// </summary>
public sealed class UnknownHostException : HostException
{
    /// <summary>Creates an unknown-host exception.</summary>
    public UnknownHostException(HostId hostId)
        : base(hostId, $"hosts: no host registered with id {hostId}") { }
}

/// <summary>
/// Thrown when an operation requires a live connection but the host has no
/// connected client (e.g. dispatching while disconnected/failed). The
/// distinction from <see cref="HostShutDownException"/> is that the host is
/// still registered — it just isn't connected right now.
/// </summary>
public sealed class HostNotConnectedException : HostException
{
    /// <summary>Creates a host-not-connected exception.</summary>
    public HostNotConnectedException(HostId hostId)
        : base(hostId, $"hosts: host {hostId} is not connected") { }
}

/// <summary>
/// Thrown when a host's runtime has been torn down (the host was removed, or the
/// multi-host client was shut down). Outstanding handles for the host surface
/// this. Port of Swift's <c>HostError.hostShutDown(HostId)</c>.
/// </summary>
public sealed class HostShutDownException : HostException
{
    /// <summary>Creates a host-shut-down exception.</summary>
    public HostShutDownException(HostId hostId)
        : base(hostId, $"hosts: host {hostId} runtime is no longer active") { }
}
