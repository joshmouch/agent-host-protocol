#nullable enable

using System;
using Microsoft.AgentHostProtocol.Hosts;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

/// <summary>
/// Tests the reconnect backoff calculation, including the opt-in jitter that
/// avoids reconnect storms (the dependency-free equivalent of the .NET
/// resilience libraries' "exponential backoff with jitter").
/// </summary>
public sealed class ReconnectPolicyTests
{
    private static ReconnectPolicy Policy(double jitter = 0) => new()
    {
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        Jitter = jitter,
    };

    [Fact]
    public void BackoffIsDeterministicAndExponentialWithoutJitter()
    {
        var p = Policy();
        Assert.Equal(TimeSpan.FromSeconds(1), p.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), p.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), p.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(8), p.BackoffFor(4));
    }

    [Fact]
    public void BackoffCapsAtMaxBackoff()
    {
        var p = Policy();
        Assert.Equal(TimeSpan.FromSeconds(30), p.BackoffFor(20));
    }

    [Fact]
    public void DisabledPolicyReturnsZero()
    {
        Assert.True(ReconnectPolicy.Disabled.IsDisabled);
        Assert.Equal(TimeSpan.Zero, ReconnectPolicy.Disabled.BackoffFor(1));
    }

    [Fact]
    public void JitterStaysWithinTheSymmetricBand()
    {
        var p = Policy(jitter: 0.5);
        // attempt 3 base = 1s * 2 * 2 = 4s; ±50% jitter → [2s, 6s].
        for (var i = 0; i < 1000; i++)
        {
            var d = p.BackoffFor(3);
            Assert.InRange(d, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        }
    }

    [Fact]
    public void JitterNeverExceedsMaxBackoff()
    {
        var p = new ReconnectPolicy
        {
            InitialBackoff = TimeSpan.FromSeconds(30),
            MaxBackoff = TimeSpan.FromSeconds(30),
            BackoffMultiplier = 2.0,
            Jitter = 1.0,
        };
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(p.BackoffFor(1) <= TimeSpan.FromSeconds(30));
        }
    }
}
