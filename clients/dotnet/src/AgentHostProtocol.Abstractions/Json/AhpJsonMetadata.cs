#nullable enable

using System.Text.Json.Serialization.Metadata;

namespace Microsoft.AgentHostProtocol;

/// <summary>Provides source-generated System.Text.Json metadata for the complete AHP wire model.</summary>
public static class AhpJsonMetadata
{
    /// <summary>Gets the source-generated resolver for AHP protocol types.</summary>
    public static IJsonTypeInfoResolver Default => AgentHostProtocolJsonContext.Default;
}
