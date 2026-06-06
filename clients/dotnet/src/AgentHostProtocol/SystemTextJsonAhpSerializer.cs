#nullable enable

using System;
using System.Text.Json;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for the Agent Host Protocol.
/// Wire names and converters are declared by attributes on the generated
/// types, so the options carry no naming policy or converter registrations.
/// </summary>
public static class AhpJson
{
    /// <summary>The canonical serializer options used by the default serializer.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // Generated types use explicit [JsonPropertyName]; no naming policy.
        PropertyNamingPolicy = null,
        // Optional fields opt into omission per-property via
        // [JsonIgnore(WhenWritingNull)]; the global default stays Never so
        // required fields still serialize their null/zero values.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
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

    /// <inheritdoc />
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    /// <inheritdoc />
    public T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _options)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, _options)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    public JsonRpcMessage DecodeMessage(TransportMessage message) =>
        message.Frame == TransportFrame.Text
            ? Deserialize<JsonRpcMessage>(message.Text ?? string.Empty)
            : Deserialize<JsonRpcMessage>(message.Binary.Span);

    /// <inheritdoc />
    public TransportMessage EncodeMessage(JsonRpcMessage message) =>
        TransportMessage.FromText(Serialize(message));
}
