// Client error hierarchy — port of the Go client's error.go.
// Mirrors: ahp/error.go (TransportError, RPCError, UnknownSubscriptionError,
//          ErrClosed, ErrShutdown, ErrSequenceGap).
#nullable enable

using System;
using System.Text.Json;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Base exception for all Agent Host Protocol client errors.
/// </summary>
public abstract class AhpException : Exception
{
    /// <inheritdoc />
    protected AhpException(string message) : base(message) { }

    /// <inheritdoc />
    protected AhpException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <see cref="ITransport"/> implementations when the underlying
/// connection experiences a transport-level fault.
/// </summary>
public sealed class AhpTransportException : AhpException
{
    /// <summary>
    /// Classifies the failure. Mirrors the Go <c>TransportError.Kind</c> field, whose
    /// vocabulary is <c>"closed"</c>, <c>"io"</c>, and <c>"protocol"</c>. This client
    /// raises <c>"closed"</c> and <c>"io"</c>; it deliberately does not raise
    /// <c>"protocol"</c> — where Go surfaces a protocol error on a frame it cannot
    /// decode, this client skips the malformed frame and resyncs (counted by the
    /// <c>ahp.client.frames.malformed</c> metric). A <c>"protocol"</c> value may still
    /// be observed if a server reports one.
    /// </summary>
    public string Kind { get; }

    /// <summary>Creates a transport exception.</summary>
    public AhpTransportException(string kind, string? message = null, Exception? inner = null)
        : base(message ?? $"ahp: transport {kind}", inner)
    {
        Kind = kind;
    }
}

/// <summary>
/// Thrown when a JSON-RPC request completes with an error response from the server.
/// </summary>
public sealed class AhpRpcException : AhpException
{
    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>The JSON-RPC error data, if present.</summary>
    public JsonElement? ErrorData { get; }

    /// <summary>Creates an RPC exception from the server error response.</summary>
    public AhpRpcException(int code, string message, JsonElement? data = null)
        : base($"ahp: rpc error {code}: {message}")
    {
        Code = code;
        ErrorData = data;
    }
}

/// <summary>
/// Thrown by <see cref="AhpClient"/> methods when the client (or its
/// background driver) has been shut down.
/// </summary>
public sealed class AhpClientClosedException : AhpException
{
    /// <summary>Creates a client-closed exception.</summary>
    public AhpClientClosedException(string? message = null)
        : base(message ?? "ahp: client shut down") { }
}
