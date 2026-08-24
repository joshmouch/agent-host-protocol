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
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(source);

        // These settings define the AHP wire contract. Caller options may add
        // resolvers, converters, encoders, and other behavior, but cannot change
        // generated property names or required-null handling.
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
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
    private static readonly JsonElement s_jsonNull = CreateJsonNull();
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates the serializer.</summary>
    /// <param name="options">
    /// Custom options; defaults to <see cref="AhpJson.Options"/>. Custom options
    /// are copied, extended with the generated AHP metadata, and frozen so later
    /// caller mutation cannot change serializer behavior while requests are in
    /// flight. The AHP camel-case naming and null-handling settings are always
    /// enforced. Add a custom
    /// <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/>
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
    public string Serialize<T>(T value)
    {
        if (typeof(T) != typeof(object))
        {
            return JsonSerializer.Serialize(value, GetTypeInfo<T>());
        }

        return value is null
            ? "null"
            : JsonSerializer.Serialize(value, GetTypeInfo(value.GetType()));
    }

    /// <inheritdoc />
    public JsonElement SerializeToElement<T>(T value)
    {
        if (typeof(T) != typeof(object))
        {
            return JsonSerializer.SerializeToElement(value, GetTypeInfo<T>());
        }

        if (value is not null)
        {
            return JsonSerializer.SerializeToElement(value, GetTypeInfo(value.GetType()));
        }

        return s_jsonNull;
    }

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
        GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"No JSON metadata is registered for {typeof(T)}. "
            + $"Add a JsonSerializerContext for custom types to {nameof(JsonSerializerOptions.TypeInfoResolverChain)}.");

    private JsonTypeInfo GetTypeInfo(Type type) =>
        _options.GetTypeInfo(type)
        ?? throw new NotSupportedException(
            $"No JSON metadata is registered for {type}. "
            + $"Add a JsonSerializerContext for custom types to {nameof(JsonSerializerOptions.TypeInfoResolverChain)}.");

    private static JsonElement CreateJsonNull()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}
