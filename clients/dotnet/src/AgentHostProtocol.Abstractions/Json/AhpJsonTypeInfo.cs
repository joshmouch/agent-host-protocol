#nullable enable

using System.Text.Json.Serialization.Metadata;

namespace Microsoft.AgentHostProtocol;

internal static class AhpJsonTypeInfo
{
    public static JsonTypeInfo<T> Get<T>(JsonSerializerOptions options) =>
        options.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"No JSON metadata is registered for {typeof(T)}.");
}
