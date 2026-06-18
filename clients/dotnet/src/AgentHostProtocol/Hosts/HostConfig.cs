// Configuration supplied to MultiHostClient.AddHostAsync.
#nullable enable

using System.Collections.Generic;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>Everything <see cref="MultiHostClient.AddHostAsync"/> needs to supervise a single host.</summary>
public sealed class HostConfig
{
    /// <summary>
    /// Stable host identifier. Required — declared with the C# <c>required</c>
    /// modifier so the compiler forces every caller to supply it, rather than a
    /// silent default that would register an unconfigured host under a sentinel id
    /// (and collide with any other host that also forgot to set it).
    /// </summary>
    public required HostId Id { get; init; }

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
