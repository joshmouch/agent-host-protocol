// Shape C — consumer self-instrumentation. Wires the AHP client's
// OpenTelemetry-native instrumentation into a real OpenTelemetry pipeline with a
// console exporter, then drives ONE client operation so the resulting spans +
// metrics print to stdout.
//
// The AHP library takes NO OpenTelemetry dependency — it originates only BCL
// System.Diagnostics ActivitySource + Meter instruments, near-zero-cost when no
// listener is attached. A consumer "lights them up" exactly as shown below:
//
//   .AddSource(AhpTelemetry.Name)   // traces
//   .AddMeter(AhpTelemetry.Name)    // metrics
//
// (AhpTelemetry.Name == AhpServiceCollectionExtensions.TelemetrySourceName ==
//  AhpTelemetryNames.Source == "Microsoft.AgentHostProtocol").
//
// To keep the example self-contained (no external AHP server required), it talks
// to a tiny in-process loopback ITransport that answers the `initialize`
// handshake. The instrumentation that fires is identical to a real connection's.
//
// Usage: dotnet run --project examples/OtelExport
#nullable enable

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ── 1. Build the OpenTelemetry pipelines, wiring in the AHP instrumentation. ──
// AddSource/AddMeter take the AHP instrumentation-scope name; the console
// exporter prints every captured span + metric. A real app would swap the
// console exporter for OTLP/Jaeger/Prometheus — the AddSource/AddMeter lines are
// the only AHP-specific wiring.
var resource = ResourceBuilder.CreateDefault().AddService("ahp-otel-example");

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(AhpTelemetry.Name)
    .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(AhpTelemetry.Name)
    .AddConsoleExporter()
    .Build();

// ── 2. Drive one real client operation so the instrumentation fires. ─────────
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

var (clientTransport, serverTransport) = LoopbackTransport.CreatePair();
var serverTask = Task.Run(() => RunInitializeResponderAsync(serverTransport, cts.Token), cts.Token);

await using (var client = AhpClient.Connect(clientTransport))
{
    Console.WriteLine("→ running initialize handshake (instrumented)…");
    var init = await client.InitializeAsync("ahp-otel-example", cancellationToken: cts.Token);
    Console.WriteLine($"← negotiated protocol version: {init.ProtocolVersion}");
}

await serverTask;

// ── 3. Flush the exporters so the spans + metrics print before the process ───
//      exits. Disposing the providers (the `using` above) also flushes, but an
//      explicit ForceFlush makes the console output deterministic for the demo.
tracerProvider.ForceFlush();
meterProvider.ForceFlush();

Console.WriteLine();
Console.WriteLine("The 'Activity.TraceId'/'ahp.request initialize' span above is the AHP request span;");
Console.WriteLine("the 'ahp.client.*' instruments are the AHP metrics — both via .AddSource/.AddMeter(AhpTelemetry.Name).");
return 0;

// ── In-process responder ──────────────────────────────────────────────────
// Answers the single `initialize` request with a stub InitializeResult, so the
// client's request span settles Ok and the request.duration / messages.* /
// requests.in_flight metrics all record. Mirrors the test fake-server shape but
// uses only the PUBLIC serializer + transport surface.
static async Task RunInitializeResponderAsync(LoopbackTransport serverSide, CancellationToken ct)
{
    var serializer = SystemTextJsonAhpSerializer.Default;
    try
    {
        var frame = await serverSide.ReceiveAsync(ct).ConfigureAwait(false);
        var msg = serializer.DecodeMessage(frame);
        if (msg.Request is { Method: "initialize" } request)
        {
            var result = new InitializeResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Snapshots = new(),
            };
            var response = new JsonRpcMessage
            {
                SuccessResponse = new JsonRpcSuccessResponse
                {
                    Id = request.Id,
                    Result = serializer.SerializeToElement(result),
                },
            };
            await serverSide.SendAsync(serializer.EncodeMessage(response), ct).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException)
    {
        // Demo finished; the responder's wait was cancelled — expected.
    }
}

// ── Minimal in-memory ITransport pair ──────────────────────────────────────
// A self-contained loopback so the example needs no external AHP server. Frames
// written to one side appear on the other. Closing either side closes both.
// Demonstrates that ITransport is a small, public, pluggable seam.
internal sealed class LoopbackTransport : ITransport
{
    private readonly ChannelReader<TransportMessage> _inbox;
    private readonly ChannelWriter<TransportMessage> _outbox;
    private readonly CancellationTokenSource _closeCts;

    private LoopbackTransport(
        ChannelReader<TransportMessage> inbox,
        ChannelWriter<TransportMessage> outbox,
        CancellationTokenSource closeCts)
    {
        _inbox = inbox;
        _outbox = outbox;
        _closeCts = closeCts;
    }

    public static (LoopbackTransport Client, LoopbackTransport Server) CreatePair()
    {
        var c2s = Channel.CreateUnbounded<TransportMessage>();
        var s2c = Channel.CreateUnbounded<TransportMessage>();
        var cts = new CancellationTokenSource(); // closing either side closes both
        var client = new LoopbackTransport(s2c.Reader, c2s.Writer, cts);
        var server = new LoopbackTransport(c2s.Reader, s2c.Writer, cts);
        return (client, server);
    }

    public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        try { await _outbox.WriteAsync(message, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        { throw new TransportClosedException("loopback closed"); }
    }

    public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        try { return await _inbox.ReadAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        { throw new TransportClosedException("loopback closed"); }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        _closeCts.Cancel();
        _outbox.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
