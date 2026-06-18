// Aggregated view types that tag host-owned items with host id + label.
#nullable enable

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Aggregated session summary tagged with its host of origin. Returned by
/// <see cref="MultiHostClient.AggregatedSessions"/>. URIs are per-host scoped,
/// so two hosts can legitimately advertise the same <c>Summary.Resource</c>;
/// consumers should treat <c>(HostId, Summary.Resource)</c> as the compound key.
/// Port of Swift's <c>HostedSessionSummary</c>.
/// </summary>
public sealed class HostedSessionSummary
{
    /// <summary>Host that owns this summary.</summary>
    public HostId HostId { get; }

    /// <summary>Human-readable label of the owning host.</summary>
    public string HostLabel { get; }

    /// <summary>The underlying session summary.</summary>
    public SessionSummary Summary { get; }

    /// <summary>Creates a host-tagged session summary.</summary>
    public HostedSessionSummary(HostId hostId, string hostLabel, SessionSummary summary)
    {
        HostId = hostId; HostLabel = hostLabel; Summary = summary;
    }
}

/// <summary>
/// Aggregated agent descriptor tagged with its host of origin. Returned by
/// <see cref="MultiHostClient.AggregatedAgents"/>. Port of Swift's
/// <c>HostedAgent</c>.
/// </summary>
public sealed class HostedAgent
{
    /// <summary>Host that owns this agent.</summary>
    public HostId HostId { get; }

    /// <summary>Human-readable label of the owning host.</summary>
    public string HostLabel { get; }

    /// <summary>The underlying agent descriptor.</summary>
    public AgentInfo Agent { get; }

    /// <summary>Creates a host-tagged agent descriptor.</summary>
    public HostedAgent(HostId hostId, string hostLabel, AgentInfo agent)
    {
        HostId = hostId; HostLabel = hostLabel; Agent = agent;
    }
}
