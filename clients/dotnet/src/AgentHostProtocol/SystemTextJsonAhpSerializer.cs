#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for the Agent Host Protocol.
/// The camelCase naming policy maps PascalCase C# properties to their
/// camelCase wire names by default; the generated types carry an explicit
/// <c>[JsonPropertyName]</c> only where the wire name diverges from that
/// (the <c>jsonrpc</c> envelope field and the snake_case <c>_meta</c> /
/// OAuth resource-metadata fields).
/// </summary>
public static class AhpJson
{
    /// <summary>The canonical serializer options used by the default serializer.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // Most wire names are camelCase(PropertyName); generated types carry an
        // explicit [JsonPropertyName] only where they aren't.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Optional fields opt into omission per-property via
        // [JsonIgnore(WhenWritingNull)]; the global default stays Never so
        // required fields still serialize their null/zero values.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    static AhpJson()
    {
        // Freeze the shared options so consumer mutation fails fast rather than
        // poisoning the global wire config. IL2026/IL3050: populateMissingResolver
        // wires the reflection-based default resolver (this library targets
        // reflection-based STJ until a JsonSerializerContext lands, per
        // docs/decisions/serialization.md).
#pragma warning disable IL2026, IL3050
        Options.MakeReadOnly(populateMissingResolver: true);
#pragma warning restore IL2026, IL3050
    }
}

/// <summary>
/// The default <see cref="IAhpSerializer"/>, backed by System.Text.Json. This
/// is the swap seam: an alternative serializer (a different engine, or a
/// schema-validating decorator over this one) can be supplied to the client
/// without changing any other code.
/// </summary>
public sealed class SystemTextJsonAhpSerializer : IAhpSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates the serializer.</summary>
    /// <param name="options">Override options; defaults to <see cref="AhpJson.Options"/>.</param>
    public SystemTextJsonAhpSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? AhpJson.Options;
    }

    /// <summary>A shared, reusable instance using the default options.</summary>
    public static SystemTextJsonAhpSerializer Default { get; } = new();

    // This serializer is the reflection-based System.Text.Json path (source-gen
    // deferred per docs/decisions/serialization.md), so every (de)serialize entry
    // point is genuinely trim/AOT-unsafe: STJ may need types that cannot be
    // statically analyzed (under trimming) or runtime code generation (under
    // Native AOT). The reflection unsafety is declared on the contract via these
    // attributes, matching the same attributes on the IAhpSerializer interface —
    // the honest interim state until a JsonSerializerContext lands. (The messages
    // mirror IAhpSerializer's SerializerTrimWarnings; the trim analyzer only
    // requires the attribute to be PRESENT on both, not message-identical, and
    // that constant is internal to the Abstractions assembly.)
    private const string TrimUnreferencedCode =
        "JSON (de)serialization here is reflection-based and may reference types that cannot be statically analyzed when trimming. Provide a JsonSerializerContext or preserve the wire types.";
    private const string TrimDynamicCode =
        "JSON (de)serialization here is reflection-based and may require runtime code generation under Native AOT. Use System.Text.Json source generation for AOT.";

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public JsonElement SerializeToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, _options);

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _options)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, _options)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(_options)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public JsonRpcMessage DecodeMessage(TransportMessage message) =>
        message.Frame == TransportFrame.Text
            ? Deserialize<JsonRpcMessage>(message.Text ?? string.Empty)
            : Deserialize<JsonRpcMessage>(message.Binary.Span);

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimUnreferencedCode)]
    [RequiresDynamicCode(TrimDynamicCode)]
    public TransportMessage EncodeMessage(JsonRpcMessage message) =>
        TransportMessage.FromText(Serialize(message));
}
