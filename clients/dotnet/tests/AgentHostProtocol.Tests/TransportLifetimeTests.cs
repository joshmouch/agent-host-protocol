// Proves the client disposes the transport it owns: ShutdownAsync must call
// ITransport.DisposeAsync (which releases unmanaged handles like the
// ClientWebSocket socket), not just CloseAsync. MemTransport holds no OS handles
// so it can't catch this — a recording double is used instead.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class TransportLifetimeTests
{
    [Fact]
    public async Task ShutdownAsync_DisposesTheOwnedTransport()
    {
        var transport = new RecordingTransport();
        var client = AhpClient.Connect(transport);

        await client.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.True(transport.Disposed, "the client owns the transport and must DisposeAsync it on shutdown");
    }

    [Fact]
    public async Task ShutdownAsync_IsIdempotent()
    {
        var transport = new RecordingTransport();
        var client = AhpClient.Connect(transport);

        await client.ShutdownAsync(TestContext.Current.CancellationToken);
        await client.ShutdownAsync(TestContext.Current.CancellationToken);   // second call must be a safe no-op

        Assert.True(transport.Disposed);
    }

    private sealed class RecordingTransport : ITransport
    {
        private readonly CancellationTokenSource _closed = new();
        public bool Disposed { get; private set; }

        public ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            try { await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* fall through to the closed signal */ }
            throw new AhpTransportException("closed");
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            _closed.Cancel();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _closed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
