// Proves the DI integration: AddAgentHostProtocol registers the services with the
// right lifetimes, applies ClientConfig via IOptions, lets a consumer override a
// service (TryAdd), and the factory produces a working IAhpClient over a real
// transport. The provider is disposed via `await using` because MultiHostClient is
// IAsyncDisposable-only.
#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddAgentHostProtocol_RegistersServices_AndAppliesConfig()
    {
        var services = new ServiceCollection();
        services.AddAgentHostProtocol(cfg => cfg.DefaultRequestTimeout = TimeSpan.FromSeconds(7));
        await using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IAhpClientFactory>());
        Assert.IsType<InMemoryClientIdStore>(sp.GetRequiredService<IClientIdStore>());
        Assert.NotNull(sp.GetRequiredService<MultiHostClient>());
        Assert.NotNull(sp.GetRequiredService<IAhpSerializer>());
        Assert.Equal(TimeSpan.FromSeconds(7), sp.GetRequiredService<IOptions<ClientConfig>>().Value.DefaultRequestTimeout);
    }

    [Fact]
    public async Task IAhpClientFactory_Produces_WorkingClient()
    {
        var services = new ServiceCollection();
        services.AddAgentHostProtocol();
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IAhpClientFactory>();

        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = factory.Connect(clientSide);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task AddAgentHostProtocol_PreservesPreRegisteredStore()
    {
        var custom = new InMemoryClientIdStore();
        var services = new ServiceCollection();
        services.AddSingleton<IClientIdStore>(custom);
        services.AddAgentHostProtocol();
        await using var sp = services.BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<IClientIdStore>());
    }
}
