// Shared fake-host test harness. Collapses the near-identical
//   while (true) { receive → decode → dispatch-by-method }
// server loops that the multi-host / client / hosts tests each hand-rolled
// into one declarative builder:
//
//   await FakeHost.New()
//       .OnInitialize((req, side, ct) => RespondInitializeAsync(side, req.Id, ct))
//       .On("listSessions", (req, side, ct) => RespondListSessionsAsync(side, req.Id, sessions, ct))
//       .OnReconnect((req, side, ct) => RespondReplayAsync(side, req.Id, ct))
//       .AfterInitialize((side, ct) => RepeatActionAsync(side, channel, seq, ct))  // optional
//       .RunAsync(serverSide, ct);
//
// The loop itself — ReceiveAsync, DecodeMessage, the swallow-and-exit on a
// closed transport, and the post-initialize side task — lives here exactly
// once. Drives the REAL serializer over the REAL MemTransport; nothing is
// mocked. Response helpers use SystemTextJsonAhpSerializer.SerializeToElement
// (no JsonDocument.Parse(...).RootElement leak).
#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Tests;

/// <summary>
/// Declarative fake AHP host: register per-method request handlers, then
/// <see cref="RunAsync"/> drives the single canonical receive→decode→dispatch
/// loop. Reused by the host / multi-host / client tests. Internal because it
/// takes the internal <c>MemTransport</c> test helper.
/// </summary>
internal sealed class FakeHost
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    /// <summary>A handler for one inbound JSON-RPC request.</summary>
    public delegate Task RequestHandler(JsonRpcRequest request, MemTransport serverSide, CancellationToken ct);

    /// <summary>A side task started once, right after the first `initialize`.</summary>
    public delegate Task PostInitialize(MemTransport serverSide, CancellationToken ct);

    private readonly Dictionary<string, RequestHandler> _handlers = new(StringComparer.Ordinal);
    private RequestHandler? _default;
    private PostInitialize? _afterInitialize;

    private FakeHost() { }

    /// <summary>Starts a new, empty fake-host definition.</summary>
    public static FakeHost New() => new();

    /// <summary>Registers the handler for <paramref name="method"/>.</summary>
    public FakeHost On(string method, RequestHandler handler)
    {
        _handlers[method] = handler;
        return this;
    }

    /// <summary>Registers the <c>initialize</c> handler.</summary>
    public FakeHost OnInitialize(RequestHandler handler) => On("initialize", handler);

    /// <summary>Registers the <c>listSessions</c> handler.</summary>
    public FakeHost OnListSessions(RequestHandler handler) => On("listSessions", handler);

    /// <summary>Registers the <c>reconnect</c> handler.</summary>
    public FakeHost OnReconnect(RequestHandler handler) => On("reconnect", handler);

    /// <summary>
    /// Registers the fallback handler for any request whose method has no
    /// explicit handler. Without one, unmatched requests are ignored.
    /// </summary>
    public FakeHost OnDefault(RequestHandler handler)
    {
        _default = handler;
        return this;
    }

    /// <summary>
    /// Convenience fallback: acknowledge any otherwise-unhandled request with an
    /// empty <c>{}</c> result so the client's pending entry resolves.
    /// </summary>
    public FakeHost AckUnmatchedWithEmpty() => OnDefault((req, side, ct) => RespondEmptyAsync(side, req.Id, ct));

    /// <summary>
    /// Registers a side task started once, immediately after the first
    /// <c>initialize</c> is answered (e.g. a repeated notification push). It is
    /// fire-and-forget and shares the loop's cancellation token.
    /// </summary>
    public FakeHost AfterInitialize(PostInitialize hook)
    {
        _afterInitialize = hook;
        return this;
    }

    /// <summary>
    /// Runs the canonical server loop against <paramref name="serverSide"/>:
    /// receive a frame, decode it, dispatch to the matching handler (or the
    /// default), and — once — fire the post-initialize hook. Returns when the
    /// transport closes or the token is cancelled.
    /// </summary>
    public async Task RunAsync(MemTransport serverSide, CancellationToken ct)
    {
        var firedAfterInit = false;
        try
        {
            while (true)
            {
                TransportMessage frame;
                try { frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false); }
                catch { return; }

                JsonRpcMessage msg;
                try { msg = Ser.DecodeMessage(frame); }
                catch { return; }

                if (msg.Request is not { } request)
                    continue;

                if (_handlers.TryGetValue(request.Method, out RequestHandler? handler))
                    await handler(request, serverSide, ct).ConfigureAwait(false);
                else if (_default is not null)
                    await _default(request, serverSide, ct).ConfigureAwait(false);

                if (request.Method == "initialize" && !firedAfterInit && _afterInitialize is { } hook)
                {
                    firedAfterInit = true;
                    _ = Task.Run(() => hook(serverSide, ct));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Shared response helpers (real serializer; no JsonDocument leak) ─────

    /// <summary>Replies to <paramref name="id"/> with <paramref name="result"/> serialized.</summary>
    public static async Task RespondResultAsync<T>(MemTransport serverSide, ulong id, T result, CancellationToken ct)
    {
        var response = new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse
            {
                Id = id,
                Result = Ser.SerializeToElement(result),
            },
        };
        try { await serverSide.SendAsync(Ser.EncodeMessage(response), ct).ConfigureAwait(false); }
        catch { /* peer gone */ }
    }

    /// <summary>Replies to <paramref name="id"/> with an empty <c>{}</c> result.</summary>
    public static async Task RespondEmptyAsync(MemTransport serverSide, ulong id, CancellationToken ct)
    {
        var response = new JsonRpcMessage
        {
            SuccessResponse = new JsonRpcSuccessResponse
            {
                Id = id,
                Result = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()),
            },
        };
        try { await serverSide.SendAsync(Ser.EncodeMessage(response), ct).ConfigureAwait(false); }
        catch { /* peer gone */ }
    }

    /// <summary>Sends a notification carrying <paramref name="params"/> as its params.</summary>
    public static async Task SendNotificationAsync<T>(MemTransport serverSide, string method, T @params, CancellationToken ct)
    {
        var notif = new JsonRpcMessage
        {
            Notification = new JsonRpcNotification
            {
                Method = method,
                Params = Ser.SerializeToElement(@params),
            },
        };
        try { await serverSide.SendAsync(Ser.EncodeMessage(notif), ct).ConfigureAwait(false); }
        catch { /* peer gone */ }
    }
}
