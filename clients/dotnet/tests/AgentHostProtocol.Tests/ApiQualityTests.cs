#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol.Hosts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class ApiQualityTests
{
    [Fact]
    public void AhpJson_OptionsAreReadOnly()
    {
        Assert.True(AhpJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => AhpJson.Options.WriteIndented = true);
    }

    [Fact]
    public void GeneratedJsonContext_IsNotPublicApi()
    {
        Type? contextType = typeof(Implementation).Assembly.GetType(
            "Microsoft.AgentHostProtocol.AgentHostProtocolJsonContext");

        Assert.NotNull(contextType);
        Assert.False(contextType.IsPublic || contextType.IsNestedPublic);
    }

    [Fact]
    public async Task AhpClient_ConnectSnapshotsCallerConfiguration()
    {
        var originalTimeProvider = new FakeTimeProvider();
        var config = new ClientConfig
        {
            SubscriptionBufferCapacity = 1,
            TimeProvider = originalTimeProvider,
        };
        var snapshot = ClientConfig.Snapshot(config);
        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = AhpClient.Connect(clientSide, config);

        config.SubscriptionBufferCapacity = 2;
        config.TimeProvider = new FakeTimeProvider();
        using var subscription = client.AttachSubscription("ahp-test://snapshot");
        var progress = new ProgressParams { Channel = "ahp-test://snapshot", ProgressToken = "token" };
        subscription.TrySend(new SubscriptionEventProgress(progress));
        subscription.TrySend(new SubscriptionEventProgress(progress));

        Assert.True(subscription.Events.TryRead(out _));
        Assert.False(subscription.Events.TryRead(out _));
        Assert.Same(originalTimeProvider, snapshot.TimeProvider);
    }

    [Fact]
    public void SystemTextJsonAhpSerializer_SnapshotsCallerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        var serializer = new SystemTextJsonAhpSerializer(options);

        options.WriteIndented = true;

        Assert.Equal(
            "{\"name\":\"test\",\"version\":\"1.0\"}",
            serializer.Serialize(new Implementation { Name = "test", Version = "1.0" }));
    }

    [Fact]
    public void StringOrMarkdown_FactoriesRejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => StringOrMarkdown.FromPlain(null!));
        Assert.Throws<ArgumentNullException>(() => StringOrMarkdown.FromMarkdown(null!));
    }

    [Fact]
    public void HostId_OperatorsUseValueEquality()
    {
        HostId first = "host-a";
        HostId second = "host-a";
        HostId other = "host-b";

        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first != other);
    }

    [Fact]
    public void HostConfig_SnapshotOwnsMutableConfiguration()
    {
        var subscriptions = new List<string> { "ahp-test://one" };
        var protocolVersions = new List<string> { "2026-01-01" };
        var timeProvider = new FakeTimeProvider();
        var clientConfig = new ClientConfig
        {
            SubscriptionBufferCapacity = 1,
            TimeProvider = timeProvider,
        };
        var config = new HostConfig
        {
            Id = new HostId("host-a"),
            InitialSubscriptions = subscriptions,
            ProtocolVersions = protocolVersions,
            ClientConfig = clientConfig,
            TransportFactory = (_, _) => throw new InvalidOperationException(),
        };

        var snapshot = config.Snapshot("client-a");
        subscriptions[0] = "ahp-test://changed";
        protocolVersions[0] = "changed";
        clientConfig.SubscriptionBufferCapacity = 2;

        Assert.Equal("ahp-test://one", Assert.Single(snapshot.InitialSubscriptions!));
        Assert.Equal("2026-01-01", Assert.Single(snapshot.ProtocolVersions!));
        Assert.Equal(1, snapshot.ClientConfig!.SubscriptionBufferCapacity);
        Assert.Same(timeProvider, snapshot.ClientConfig.TimeProvider);
    }
}
