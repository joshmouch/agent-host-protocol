#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    /// <summary>The canonical, read-only serializer options used by the default serializer.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    internal static JsonSerializerOptions CreateOptions(JsonSerializerOptions? source = null)
    {
        JsonSerializerOptions options = source is null
            ? new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            }
            : new JsonSerializerOptions(source);

        options.TypeInfoResolverChain.Insert(0, AhpJsonMetadata.Default);
        if (CreateReflectionFallback() is { } reflectionResolver)
        {
            options.TypeInfoResolverChain.Add(reflectionResolver);
        }
        options.MakeReadOnly();
        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection resolver is unreachable when System.Text.Json reflection is disabled; Native AOT substitutes IsReflectionEnabledByDefault with false.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection resolver is unreachable when System.Text.Json reflection is disabled; Native AOT substitutes IsReflectionEnabledByDefault with false.")]
    private static DefaultJsonTypeInfoResolver? CreateReflectionFallback() =>
        JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;
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
    /// <param name="options">
    /// Override options; defaults to <see cref="AhpJson.Options"/>. Custom options
    /// are copied, extended with the generated AHP metadata, and frozen so later
    /// caller mutation cannot change serializer behavior while requests are in
    /// flight. Add a custom <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/>
    /// to serialize non-AHP types when reflection is disabled.
    /// </param>
    public SystemTextJsonAhpSerializer(JsonSerializerOptions? options = null)
    {
        if (options is null)
        {
            _options = AhpJson.Options;
            return;
        }

        _options = AhpJson.CreateOptions(options);
    }

    /// <summary>A shared, reusable instance using the default options.</summary>
    public static SystemTextJsonAhpSerializer Default { get; } = new();

    /// <inheritdoc />
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, GetTypeInfo<T>());

    /// <inheritdoc />
    public JsonElement SerializeToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, GetTypeInfo<T>());

    /// <inheritdoc />
    public T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, GetTypeInfo<T>())
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, GetTypeInfo<T>())
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    public T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize(element, GetTypeInfo<T>())
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}");

    /// <inheritdoc />
    public JsonRpcMessage DecodeMessage(TransportMessage message) =>
        message.Frame == TransportFrame.Text
            ? Deserialize<JsonRpcMessage>(message.Text ?? string.Empty)
            : Deserialize<JsonRpcMessage>(message.Binary.Span);

    /// <inheritdoc />
    public TransportMessage EncodeMessage(JsonRpcMessage message) =>
        TransportMessage.FromText(Serialize(message));

    private JsonTypeInfo<T> GetTypeInfo<T>() =>
        _options.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"No JSON metadata is registered for {typeof(T)}. "
            + $"Add a JsonSerializerContext for custom types to {nameof(JsonSerializerOptions.TypeInfoResolverChain)}.");
}
