// Serializer seam — the pluggable boundary that lets the AHP client use a
// different JSON engine (or layer schema validation on top) without changing
// the client or transport. Hand-written.
#nullable enable

using System;
using System.Text.Json;

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

    /// <summary>
    /// Serializes <paramref name="value"/> directly to a <see cref="JsonElement"/>,
    /// avoiding the intermediate string + <see cref="JsonDocument"/> parse (and the
    /// undisposed-document leak that <c>JsonDocument.Parse(Serialize(x)).RootElement</c>
    /// incurs). The returned element owns its backing memory and is safe to retain.
    /// </summary>
    JsonElement SerializeToElement<T>(T value);

    /// <summary>Deserializes a JSON string into <typeparamref name="T"/>.</summary>
    T Deserialize<T>(string json);

    /// <summary>Deserializes UTF-8 JSON bytes into <typeparamref name="T"/>.</summary>
    T Deserialize<T>(ReadOnlySpan<byte> utf8Json);

    /// <summary>
    /// Deserializes an already-parsed <see cref="JsonElement"/> into
    /// <typeparamref name="T"/>, binding directly off the element's backing buffer
    /// with no intermediate string materialization and no re-tokenize. Symmetric
    /// with <see cref="SerializeToElement{T}"/>; prefer this over
    /// <c>Deserialize&lt;T&gt;(element.GetRawText())</c> on hot paths (inbound
    /// notifications, request results) where the element is already in hand.
    /// </summary>
    T Deserialize<T>(JsonElement element);

    /// <summary>
    /// Decodes a transport frame into a <see cref="JsonRpcMessage"/>, picking the
    /// correct variant (request / notification / success / error) from its shape.
    /// </summary>
    JsonRpcMessage DecodeMessage(TransportMessage message);

    /// <summary>Encodes a <see cref="JsonRpcMessage"/> into a text transport frame.</summary>
    TransportMessage EncodeMessage(JsonRpcMessage message);
}
