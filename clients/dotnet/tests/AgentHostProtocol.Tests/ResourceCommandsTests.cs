// Coverage for the typed resource* convenience surface added to parity with the
// TypeScript/Rust/Go/Swift clients (upstream #321):
//   • the 10 send-wrappers force the channel to the root resource URI, whatever
//     channel the caller set on the params record, and surface the typed result;
//   • the inbound typed ResourceRequestHandlers registry decodes params, invokes
//     the matching handler, and reports an unset method as MethodNotFound.
// Everything runs the REAL AhpClient over the REAL MemTransport — nothing mocked.
#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class ResourceCommandsTests
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // A send-wrapper overrides whatever channel the caller passed with the root
    // resource URI, and returns the server's typed result.
    [Fact]
    public async Task ResourceRead_ForcesRootChannel_AndReturnsResult()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        ResourceReadParams? received = null;
        var serverTask = Task.Run(() => FakeHost.New()
            .On("resourceRead", async (req, side, ct) =>
            {
                received = Ser.Deserialize<ResourceReadParams>(req.Params!.Value);
                await FakeHost.RespondResultAsync(
                    side, req.Id,
                    new ResourceReadResult { Data = "aGVsbG8=", Encoding = ContentEncoding.Base64, ContentType = "text/plain" },
                    ct);
            })
            .RunAsync(serverSide, cts.Token), cts.Token);

        await using var client = AhpClient.Connect(clientSide);

        // Caller deliberately passes a NON-root channel; the wrapper must override it.
        var result = await client.ResourceReadAsync(
            new ResourceReadParams { Channel = "ahp-session:/ignored", Uri = "file:///x.txt" },
            cts.Token);

        Assert.Equal("aGVsbG8=", result.Data);
        Assert.Equal(ContentEncoding.Base64, result.Encoding);
        Assert.NotNull(received);
        Assert.Equal(ProtocolVersion.RootResourceUri, received!.Channel);
        Assert.Equal("file:///x.txt", received.Uri);
    }

    // createResourceWatch behaves the same and returns the assigned watch channel.
    [Fact]
    public async Task CreateResourceWatch_ForcesRootChannel_AndReturnsWatchChannel()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        CreateResourceWatchParams? received = null;
        var serverTask = Task.Run(() => FakeHost.New()
            .On("createResourceWatch", async (req, side, ct) =>
            {
                received = Ser.Deserialize<CreateResourceWatchParams>(req.Params!.Value);
                await FakeHost.RespondResultAsync(
                    side, req.Id,
                    new CreateResourceWatchResult { Channel = "ahp-resource-watch:/42" },
                    ct);
            })
            .RunAsync(serverSide, cts.Token), cts.Token);

        await using var client = AhpClient.Connect(clientSide);

        var result = await client.CreateResourceWatchAsync(
            new CreateResourceWatchParams { Channel = "ahp-session:/ignored", Uri = "file:///dir", Recursive = true },
            cts.Token);

        Assert.Equal("ahp-resource-watch:/42", result.Channel);
        Assert.NotNull(received);
        Assert.Equal(ProtocolVersion.RootResourceUri, received!.Channel);
        Assert.Equal("file:///dir", received.Uri);
        Assert.True(received.Recursive);
    }

    // The typed inbound registry decodes the server's params into the right record
    // and replies with the handler's typed result.
    [Fact]
    public async Task ResourceRequestHandlers_TypedHandler_DecodesParamsAndReplies()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = AhpClient.Connect(clientSide);

        string? seenUri = null;
        client.SetResourceRequestHandlers(new ResourceRequestHandlers
        {
            OnResourceRead = p =>
            {
                seenUri = p.Uri;
                return Task.FromResult(new ResourceReadResult { Data = "d29ybGQ=", Encoding = ContentEncoding.Base64 });
            },
        });

        // Server invokes resourceRead ON the client (symmetrical direction).
        var req = new JsonRpcMessage
        {
            Request = new JsonRpcRequest
            {
                Id = 11,
                Method = "resourceRead",
                Params = Ser.SerializeToElement(new ResourceReadParams { Channel = "ahp-root://", Uri = "file:///srv.txt" }),
            },
        };
        await serverSide.SendAsync(Ser.EncodeMessage(req), cts.Token);

        var replyFrame = await serverSide.ReceiveAsync(cts.Token);
        var reply = Ser.DecodeMessage(replyFrame);
        Assert.NotNull(reply.SuccessResponse);
        Assert.Equal(11UL, reply.SuccessResponse!.Id);
        var decoded = Ser.Deserialize<ResourceReadResult>(reply.SuccessResponse.Result);
        Assert.Equal("d29ybGQ=", decoded.Data);
        Assert.Equal("file:///srv.txt", seenUri);
    }

    // A method with no registered handler is reported to the peer as MethodNotFound,
    // exactly like the no-handler path.
    [Fact]
    public async Task ResourceRequestHandlers_UnsetMethod_RepliesMethodNotFound()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = AhpClient.Connect(clientSide);
        client.SetResourceRequestHandlers(new ResourceRequestHandlers
        {
            OnResourceRead = p => Task.FromResult(new ResourceReadResult { Data = "", Encoding = ContentEncoding.Utf8 }),
            // OnResourceWrite intentionally unset.
        });

        var req = new JsonRpcMessage
        {
            Request = new JsonRpcRequest
            {
                Id = 12,
                Method = "resourceWrite",
                Params = Ser.SerializeToElement(new ResourceWriteParams
                {
                    Channel = "ahp-root://", Uri = "file:///w.txt", Data = "eA==", Encoding = ContentEncoding.Base64,
                }),
            },
        };
        await serverSide.SendAsync(Ser.EncodeMessage(req), cts.Token);

        var replyFrame = await serverSide.ReceiveAsync(cts.Token);
        var reply = Ser.DecodeMessage(replyFrame);
        Assert.NotNull(reply.ErrorResponse);
        Assert.Equal(12UL, reply.ErrorResponse!.Id);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, reply.ErrorResponse.Error.Code);
    }

    // Passing null clears the installed handler, restoring the MethodNotFound default.
    [Fact]
    public async Task ResourceRequestHandlers_Null_ClearsHandler()
    {
        var (clientSide, serverSide) = MemTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = AhpClient.Connect(clientSide);
        client.SetResourceRequestHandlers(new ResourceRequestHandlers
        {
            OnResourceRead = p => Task.FromResult(new ResourceReadResult { Data = "", Encoding = ContentEncoding.Utf8 }),
        });
        client.SetResourceRequestHandlers(null);

        var req = new JsonRpcMessage
        {
            Request = new JsonRpcRequest
            {
                Id = 13,
                Method = "resourceRead",
                Params = Ser.SerializeToElement(new ResourceReadParams { Channel = "ahp-root://", Uri = "file:///x" }),
            },
        };
        await serverSide.SendAsync(Ser.EncodeMessage(req), cts.Token);

        var replyFrame = await serverSide.ReceiveAsync(cts.Token);
        var reply = Ser.DecodeMessage(replyFrame);
        Assert.NotNull(reply.ErrorResponse);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, reply.ErrorResponse!.Error.Code);
    }
}
