// Serializer seam — the pluggable boundary that lets the AHP client use a
// different JSON engine (or layer schema validation on top) without changing
// the client or transport. Hand-written.
#nullable enable

using System;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Abstracts the JSON engine the AHP client uses to encode outbound payloads
/// and decode inbound frames. The default implementation
/// (<c>SystemTextJsonAhpSerializer</c>, in <c>Microsoft.AgentHostProtocol</c>)
/// is backed by System.Text.Json; alternative implementations may swap the
/// engine or decorate it with JSON-Schema validation against the schemas the
/// repository generates under <c>schema/</c>.
/// </summary>
public interface IAhpSerializer
{
    /// <summary>Serializes <paramref name="value"/> to a JSON string.</summary>
    string Serialize<T>(T value);

    /// <summary>Deserializes a JSON string into <typeparamref name="T"/>.</summary>
    T Deserialize<T>(string json);

    /// <summary>Deserializes UTF-8 JSON bytes into <typeparamref name="T"/>.</summary>
    T Deserialize<T>(ReadOnlySpan<byte> utf8Json);

    /// <summary>
    /// Decodes a transport frame into a <see cref="JsonRpcMessage"/>, picking the
    /// correct variant (request / notification / success / error) from its shape.
    /// </summary>
    JsonRpcMessage DecodeMessage(TransportMessage message);

    /// <summary>Encodes a <see cref="JsonRpcMessage"/> into a text transport frame.</summary>
    TransportMessage EncodeMessage(JsonRpcMessage message);
}
