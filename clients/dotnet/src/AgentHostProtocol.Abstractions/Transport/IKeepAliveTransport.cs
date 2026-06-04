// Optional keep-alive capability for transports that can send protocol-level
// pings. Port of the Swift `AHPKeepAliveTransport` protocol
// (clients/swift/.../Transport/AHPTransport.swift).
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Optional capability for transports that can send protocol-level pings.
/// <para>
/// <see cref="AhpClient"/> uses this only when <see cref="ClientConfig.KeepAlive"/>
/// is enabled. Transports that do not support pings can simply not implement this
/// interface; keep-alive is then unavailable for those transports and the client
/// silently skips its ping loop.
/// </para>
/// </summary>
public interface IKeepAliveTransport : ITransport
{
    /// <summary>
    /// Sends a transport-level ping and completes after the matching pong arrives
    /// (or throws on timeout / transport failure). Mirrors the Swift
    /// <c>AHPKeepAliveTransport.sendPing(timeout:)</c>.
    /// </summary>
    /// <param name="timeout">How long to wait for the matching pong before failing.</param>
    /// <param name="cancellationToken">Cancels the ping wait.</param>
    ValueTask SendPingAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
