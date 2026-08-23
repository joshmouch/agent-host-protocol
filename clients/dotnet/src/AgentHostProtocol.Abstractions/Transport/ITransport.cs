// Transport seam — the pluggable boundary between the AHP client and the
// underlying byte stream (WebSocket, in-memory pipe, IPC, ...). Hand-written.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

/// <summary>The wire framing of a single transport message.</summary>
public enum TransportFrame
{
    /// <summary>A UTF-8 text frame (the common case for JSON-RPC).</summary>
    Text,

    /// <summary>A binary frame.</summary>
    Binary,
}

/// <summary>
/// A single message exchanged over an <see cref="ITransport"/>. A message is
/// either a UTF-8 text frame or a binary frame; the AHP client encodes and
/// decodes JSON-RPC payloads from these frames via an <see cref="IAhpSerializer"/>.
/// </summary>
public sealed class TransportMessage
{
    private TransportMessage(TransportFrame frame, string? text, ReadOnlyMemory<byte> binary)
    {
        Frame = frame;
        Text = text;
        Binary = binary;
    }

    /// <summary>The framing of this message.</summary>
    public TransportFrame Frame { get; }

    /// <summary>The text payload when <see cref="Frame"/> is <see cref="TransportFrame.Text"/>.</summary>
    public string? Text { get; }

    /// <summary>The binary payload when <see cref="Frame"/> is <see cref="TransportFrame.Binary"/>.</summary>
    public ReadOnlyMemory<byte> Binary { get; }

    /// <summary>Creates a UTF-8 text message.</summary>
    public static TransportMessage FromText(string text) =>
        new(TransportFrame.Text, text ?? throw new ArgumentNullException(nameof(text)), default);

    /// <summary>Creates a binary message.</summary>
    public static TransportMessage FromBinary(ReadOnlyMemory<byte> bytes) =>
        new(TransportFrame.Binary, null, bytes);
}

/// <summary>
/// A bidirectional, ordered, message-framed transport. Implementations are
/// responsible only for moving opaque frames; JSON-RPC encoding lives in the
/// client. A single transport instance is used by exactly one client.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Sends a single message.</summary>
    ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the next message. Implementations transparently handle and skip
    /// control frames (ping/pong). Throws when the transport is closed.
    /// </summary>
    ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the transport gracefully.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
