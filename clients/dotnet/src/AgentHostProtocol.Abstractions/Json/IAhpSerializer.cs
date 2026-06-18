// Serializer seam — the pluggable boundary that lets the AHP client use a
// different JSON engine (or layer schema validation on top) without changing
// the client or transport. Hand-written.
#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
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
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    string Serialize<T>(T value);

    /// <summary>
    /// Serializes <paramref name="value"/> directly to a <see cref="JsonElement"/>,
    /// avoiding the intermediate string + <see cref="JsonDocument"/> parse (and the
    /// undisposed-document leak that <c>JsonDocument.Parse(Serialize(x)).RootElement</c>
    /// incurs). The returned element owns its backing memory and is safe to retain.
    /// </summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    JsonElement SerializeToElement<T>(T value);

    /// <summary>Deserializes a JSON string into <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    T Deserialize<T>(string json);

    /// <summary>Deserializes UTF-8 JSON bytes into <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    T Deserialize<T>(ReadOnlySpan<byte> utf8Json);

    /// <summary>
    /// Deserializes an already-parsed <see cref="JsonElement"/> into
    /// <typeparamref name="T"/>, binding directly off the element's backing buffer
    /// with no intermediate string materialization and no re-tokenize. Symmetric
    /// with <see cref="SerializeToElement{T}"/>; prefer this over
    /// <c>Deserialize&lt;T&gt;(element.GetRawText())</c> on hot paths (inbound
    /// notifications, request results) where the element is already in hand.
    /// </summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    T Deserialize<T>(JsonElement element);

    /// <summary>
    /// Decodes a transport frame into a <see cref="JsonRpcMessage"/>, picking the
    /// correct variant (request / notification / success / error) from its shape.
    /// </summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    JsonRpcMessage DecodeMessage(TransportMessage message);

    /// <summary>Encodes a <see cref="JsonRpcMessage"/> into a text transport frame.</summary>
    [RequiresUnreferencedCode(SerializerTrimWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializerTrimWarnings.DynamicCode)]
    TransportMessage EncodeMessage(JsonRpcMessage message);
}

/// <summary>
/// Shared <see cref="RequiresUnreferencedCodeAttribute"/> /
/// <see cref="RequiresDynamicCodeAttribute"/> messages for the serializer seam.
/// The default <c>SystemTextJsonAhpSerializer</c> is reflection-based (source-gen
/// is deferred per <c>docs/decisions/serialization.md</c>), so every
/// (de)serialization entry point declares the trim/AOT unsafety on the contract.
/// </summary>
internal static class SerializerTrimWarnings
{
    public const string UnreferencedCode =
        "JSON (de)serialization here is reflection-based and may reference types that cannot be statically analyzed when trimming. Provide a JsonSerializerContext or preserve the wire types.";

    public const string DynamicCode =
        "JSON (de)serialization here is reflection-based and may require runtime code generation under Native AOT. Use System.Text.Json source generation for AOT.";
}
