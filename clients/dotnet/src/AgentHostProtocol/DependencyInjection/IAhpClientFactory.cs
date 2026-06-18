// DI-resolvable factory for the connect-then-use client: a live ITransport is
// required before an AhpClient exists, so consumers resolve this factory and call
// Connect(transport) rather than injecting an IAhpClient singleton directly.
#nullable enable

using System;
using Microsoft.Extensions.Options;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Creates <see cref="IAhpClient"/> instances over a caller-supplied, already-connected
/// <see cref="ITransport"/>, using the serializer and <see cref="ClientConfig"/> registered
/// via <c>AddAgentHostProtocol</c>. Registered as a singleton by that extension.
/// </summary>
public interface IAhpClientFactory
{
    /// <summary>
    /// Wires the AHP protocol over <paramref name="transport"/> and returns the client.
    /// Synchronous by design: the transport is already connected, so this only wires up
    /// state and starts the background reader/writer loops (no I/O). The async work — the
    /// transport's own connect and the client's InitializeAsync handshake — is awaited
    /// separately by the caller, mirroring the Go and TypeScript clients.
    /// </summary>
    IAhpClient Connect(ITransport transport);
}

internal sealed class AhpClientFactory(IAhpSerializer serializer, IOptions<ClientConfig> options) : IAhpClientFactory
{
    public IAhpClient Connect(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return AhpClient.Connect(transport, options.Value, serializer);
    }
}
