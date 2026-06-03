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
    /// Classifies the failure: <c>"closed"</c>, <c>"io"</c>, or <c>"protocol"</c>.
    /// Mirrors the Go <c>TransportError.Kind</c> field.
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

/// <summary>
/// Thrown when an action envelope arrives out of sequence and the client
/// cannot reconcile without a new snapshot. The caller should resubscribe.
/// </summary>
public sealed class AhpSequenceGapException : AhpException
{
    /// <summary>Creates a sequence-gap exception.</summary>
    public AhpSequenceGapException()
        : base("ahp: sequence gap detected; resubscribe required") { }
}

/// <summary>
/// Thrown by <see cref="AhpClient.UnsubscribeAsync"/> when the URI is not
/// tracked by this client.
/// </summary>
public sealed class UnknownSubscriptionException : AhpException
{
    /// <summary>The URI that was not found.</summary>
    public string Uri { get; }

    /// <summary>Creates an unknown-subscription exception.</summary>
    public UnknownSubscriptionException(string uri)
        : base($"ahp: no such subscription: {uri}")
    {
        Uri = uri;
    }
}
