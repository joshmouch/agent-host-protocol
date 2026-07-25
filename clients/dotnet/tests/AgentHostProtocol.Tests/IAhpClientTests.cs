// Proves the client is substitutable behind IAhpClient — consumers depend on the
// interface (and can mock it) rather than the concrete sealed AhpClient.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class IAhpClientTests
{
    [Fact]
    public async Task AhpClient_IsSubstitutable_BehindIAhpClient()
    {
        var (clientSide, _) = MemTransport.CreatePair();
        await using var client = AhpClient.Connect(clientSide);

        IAhpClient asInterface = Assert.IsAssignableFrom<IAhpClient>(client);
        Assert.Equal(ConnectionState.Connected, asInterface.ConnectionState);
    }
}
