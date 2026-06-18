// Reconnect policy + transport factory delegate.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>Factory delegate that opens a fresh transport for a given host.</summary>
public delegate Task<ITransport> HostTransportFactory(HostId hostId, CancellationToken cancellationToken);

/// <summary>Controls reconnect behaviour after an unexpected transport drop.</summary>
public sealed class ReconnectPolicy
{
    /// <summary>
    /// Caps consecutive retry attempts. Zero means unlimited.
    /// </summary>
    public uint MaxAttempts { get; init; }

    /// <summary>Wait before the first retry.</summary>
    public TimeSpan InitialBackoff { get; init; }

    /// <summary>Caps the exponential backoff.</summary>
    public TimeSpan MaxBackoff { get; init; }

    /// <summary>Scales each successive backoff. Use 2.0 for exponential.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>If true, resets the attempt counter after a successful reconnect.</summary>
    public bool ResetOnSuccess { get; init; }

    /// <summary>
    /// Randomizes each backoff by ±this fraction (clamped to 0–1) to avoid
    /// reconnect storms when many hosts drop at once ("thundering herd"). The
    /// default 0 disables jitter — matching the other AHP clients' behavior.
    /// 0.2 is a reasonable production value. This is the dependency-free
    /// equivalent of the "exponential backoff with jitter" that the .NET
    /// resilience libraries recommend; see docs/decisions/reconnect.md.
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>Whether reconnection is effectively disabled (zero initial backoff).</summary>
    public bool IsDisabled => InitialBackoff <= TimeSpan.Zero;

    /// <summary>
    /// Returns a policy with 1 s → 2 s → 4 s → … capped at 30 s, unlimited, reset on success.
    /// </summary>
    public static ReconnectPolicy Default { get; } = new()
    {
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        ResetOnSuccess = true,
    };

    /// <summary>Returns a policy that disables reconnection.</summary>
    public static ReconnectPolicy Disabled { get; } = new()
    {
        MaxAttempts = 0,
        InitialBackoff = TimeSpan.Zero,
    };

    /// <summary>Computes the wait before attempt number <paramref name="attempt"/> (1-based).</summary>
    internal TimeSpan BackoffFor(uint attempt)
    {
        if (IsDisabled) return TimeSpan.Zero;
        var b = (double)InitialBackoff.Ticks;
        var mult = BackoffMultiplier <= 0 ? 1.0 : BackoffMultiplier;
        for (uint i = 1; i < attempt; i++) b *= mult;
        var result = TimeSpan.FromTicks((long)b);
        if (MaxBackoff > TimeSpan.Zero && result > MaxBackoff) result = MaxBackoff;

        if (Jitter > 0)
        {
            // Symmetric jitter: result * (1 ± Jitter), never negative and never
            // above MaxBackoff. Random.Shared is thread-safe.
            var j = Math.Clamp(Jitter, 0.0, 1.0);
            var factor = 1.0 + (Random.Shared.NextDouble() * 2.0 - 1.0) * j;
            var ticks = Math.Max(0L, (long)(result.Ticks * factor));
            result = TimeSpan.FromTicks(ticks);
            if (MaxBackoff > TimeSpan.Zero && result > MaxBackoff) result = MaxBackoff;
        }

        return result;
    }
}
