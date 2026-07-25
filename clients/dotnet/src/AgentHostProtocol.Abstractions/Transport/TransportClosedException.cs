// Typed signal for a clean remote close of the transport.
#nullable enable

using System;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Thrown when the remote peer closes the transport cleanly.
/// Distinct from transport fault exceptions so callers can differentiate
/// between a clean remote close and an I/O error.
/// </summary>
public sealed class TransportClosedException : Exception
{
    /// <summary>Creates a <see cref="TransportClosedException"/> with a default message.</summary>
    public TransportClosedException()
        : base("The transport was closed by the remote peer.") { }

    /// <summary>Creates a <see cref="TransportClosedException"/> with the given message.</summary>
    /// <param name="message">A human-readable description of the close reason.</param>
    public TransportClosedException(string message)
        : base(message) { }

    /// <summary>Creates a <see cref="TransportClosedException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable description of the close reason.</param>
    /// <param name="inner">The exception that caused this one.</param>
    public TransportClosedException(string message, Exception inner)
        : base(message, inner) { }
}
