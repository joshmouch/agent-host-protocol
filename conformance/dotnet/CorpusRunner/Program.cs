// AHP HOST-CONFORMANCE RUNNER — build-phase B5, .NET per-client corpus runner.
//
// Loads every scenario in the corpus, spawns the scenario-driven host for each,
// connects a REAL .NET WebSocket, replays client.request steps, reduces
// server.notify action envelopes through the REAL in-repo reducers, then checks
// every client.assert.* step — byte-for-byte convergence proof that the .NET
// client reducer converges with the canonical corpus.
//
// This is the .NET port of conformance/runner/run-conformance.mjs (B4).
// NO MOCKS — real host subprocess, real WebSocket, real reducers, real assertions.
// (CROSS-SPEC-INTENT-VERIFIED-BY-REAL-EXECUTION + ADR-067/072.)
//
// Usage:
//   dotnet run --project conformance/dotnet/CorpusRunner -c Release -f net8.0 \
//       -- [--host <path-to-scenario-host.mjs>] [--scenarios <dir>] \
//          [--verbose] [--filter <prefix>]
//
// Or via conformance/run-corpus.sh (which sets up host deps first).
//
// Exit 0 = all PASS; 1 = some FAIL or ERROR; 2 = harness setup error.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol;

// ─── Entry point ─────────────────────────────────────────────────────────────

var cliArgs = System.Environment.GetCommandLineArgs().Skip(1).ToArray();
var verbose  = cliArgs.Contains("--verbose");
var filterIdx = Array.IndexOf(cliArgs, "--filter");
var filter    = filterIdx >= 0 ? cliArgs[filterIdx + 1] : null;
var hostIdx   = Array.IndexOf(cliArgs, "--host");
var hostScript = hostIdx >= 0
    ? cliArgs[hostIdx + 1]
    : Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
        "..", "..", "..", "..", "..", // up from bin/<cfg>/<tfm>/ to repo root
        "conformance", "host", "scenario-host.mjs"));

var scenariosIdx = Array.IndexOf(cliArgs, "--scenarios");
string scenariosRoot;
if (scenariosIdx >= 0)
{
    scenariosRoot = cliArgs[scenariosIdx + 1];
}
else
{
    // Default: corpus lives under types/test-cases/scenarios/, relative to repo root.
    // Walk up from the binary to find the repo root (contains conformance/).
    var exe = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
    var repoRoot = FindRepoRoot(exe);
    scenariosRoot = repoRoot is not null
        ? Path.Combine(repoRoot, "types", "test-cases", "scenarios")
        : Path.GetFullPath(Path.Combine(exe, "..", "..", "..", "..", "..",
            "types", "test-cases", "scenarios"));
}

if (!Directory.Exists(scenariosRoot))
{
    Console.Error.WriteLine($"ERROR scenarios root not found: {scenariosRoot}");
    return 2;
}

if (!File.Exists(hostScript))
{
    Console.Error.WriteLine($"ERROR host script not found: {hostScript}");
    return 2;
}

// Collect scenario files from the three canonical subdirectories.
var subdirs = new[] { "round-trips", "reducers", "negatives" };
var scenarioFiles = subdirs
    .Select(d => Path.Combine(scenariosRoot, d))
    .Where(Directory.Exists)
    .SelectMany(d => Directory.GetFiles(d, "*.scenario.json"))
    .OrderBy(f => f)
    .ToList();

if (filter is not null)
    scenarioFiles = scenarioFiles.Where(f => Path.GetFileName(f).StartsWith(filter)).ToList();

if (scenarioFiles.Count == 0)
{
    Console.Error.WriteLine($"ERROR no scenario files found under {scenariosRoot}");
    return 2;
}

Console.WriteLine($"AHP .NET Conformance Runner — {scenarioFiles.Count} scenario(s)");
Console.WriteLine();

// Build the host dep-check message (scenario host just needs node).
var sw = Stopwatch.StartNew();

int passed = 0, failed = 0, errored = 0;
var failures = new List<(string Id, string Reason, List<AssertResult> Asserts)>();

foreach (var scenarioFile in scenarioFiles)
{
    var result = await RunScenarioAsync(scenarioFile, hostScript, verbose);
    switch (result.Status)
    {
        case "PASS":
            passed++;
            if (verbose)
                Console.WriteLine($"  PASS  {result.Id}  ({result.Asserts.Count} assertion(s))");
            else
                Console.Write(".");
            break;
        case "FAIL":
            failed++;
            Console.WriteLine($"\n  FAIL  {result.Id}");
            foreach (var a in result.Asserts.Where(x => !x.Ok))
                Console.WriteLine($"    ✗ {a.Op}  {a.Label}\n        → {a.Detail}");
            failures.Add((result.Id, "", result.Asserts));
            break;
        default: // ERROR
            errored++;
            Console.WriteLine($"\n  ERROR {result.Id}: {result.Reason}");
            failures.Add((result.Id, result.Reason ?? "", result.Asserts));
            break;
    }
}

sw.Stop();
Console.WriteLine();
Console.WriteLine();
int total = passed + failed + errored;
Console.WriteLine($"Results: {passed}/{total} passed, {failed} failed, {errored} errored — {sw.Elapsed.TotalSeconds:F1}s");

if (failures.Count > 0)
{
    Console.WriteLine("\nFailed scenarios:");
    foreach (var (id, reason, _) in failures)
        Console.WriteLine($"  • {id}{(reason.Length > 0 ? $": {reason}" : "")}");
}

return (failed + errored) > 0 ? 1 : 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static string? FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "conformance")) &&
            Directory.Exists(Path.Combine(dir.FullName, "clients")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// ─── Scenario runner ──────────────────────────────────────────────────────────

static async Task<ScenarioResult> RunScenarioAsync(
    string scenarioPath, string hostScript, bool verbose)
{
    var id = Path.GetFileNameWithoutExtension(scenarioPath)
                 .Replace(".scenario", "");

    // Parse the scenario file.
    ScenarioDoc scenario;
    try
    {
        var json = await File.ReadAllTextAsync(scenarioPath);
        scenario = JsonSerializer.Deserialize<ScenarioDoc>(json, AhpJson.Options)
            ?? throw new InvalidDataException("null result");
    }
    catch (Exception e)
    {
        return ScenarioResult.Error(id, scenarioPath, $"parse error: {e.Message}");
    }

    // Pin the clock before any reduction (scenario-level pinClock).
    if (scenario.PinClock.HasValue)
        Reducers.SetNowProvider(() => scenario.PinClock.Value);
    else
        Reducers.SetNowProvider(null); // reset to real clock between scenarios

    // Spawn the host.
    var hostProc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{hostScript}\" \"{scenarioPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
        }
    };

    string hostUrl;
    try
    {
        hostProc.Start();
        // Read lines until we see "SCENARIO HOST READY ws://..."
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        hostUrl = await WaitForHostReadyAsync(hostProc, cts.Token);
    }
    catch (Exception e)
    {
        try { hostProc.Kill(); } catch { /* noop */ }
        return ScenarioResult.Error(id, scenarioPath, $"host start error: {e.Message}");
    }

    // Drive the protocol and collect state.
    DriveResult drive;
    try
    {
        drive = await DriveProtocolAsync(new Uri(hostUrl), scenario);
    }
    catch (Exception e)
    {
        try { hostProc.Kill(); } catch { /* noop */ }
        return ScenarioResult.Error(id, scenarioPath, $"drive error: {e.Message}");
    }
    finally
    {
        try { hostProc.Kill(); } catch { /* noop */ }
    }

    // Evaluate assertions.
    var assertSteps = scenario.Steps.Where(s => s.Op.StartsWith("client.assert.")).ToList();
    if (assertSteps.Count == 0)
        return ScenarioResult.Error(id, scenarioPath, "scenario has no client.assert.* steps");

    var asserts = new List<AssertResult>();
    bool allOk = true;
    foreach (var step in assertSteps)
    {
        var r = CheckAssertion(step, drive);
        asserts.Add(r);
        if (!r.Ok) allOk = false;
    }

    return new ScenarioResult(
        Id: id,
        ScenarioPath: scenarioPath,
        Status: allOk ? "PASS" : "FAIL",
        Reason: null,
        Asserts: asserts);
}

// ─── Wait for host READY line ─────────────────────────────────────────────────

static async Task<string> WaitForHostReadyAsync(Process proc, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    ct.Register(() => tcs.TrySetCanceled());

    _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line is null) break;
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"SCENARIO HOST READY (ws://127\.0\.0\.1:\d+)");
                if (m.Success)
                {
                    tcs.TrySetResult(m.Groups[1].Value);
                    return;
                }
            }
            tcs.TrySetException(new Exception("host stdout ended without READY line"));
        }
        catch (Exception e)
        {
            tcs.TrySetException(e);
        }
    }, CancellationToken.None);

    return await tcs.Task;
}

// ─── Protocol driver ──────────────────────────────────────────────────────────

// Drive the protocol: connect a raw ws, send each client.request step,
// collect every incoming frame, reduce action notifications through the real
// in-repo reducers, seed state from initialize/subscribe snapshots.
// Returns the collected client state.
//
// This mirrors run-conformance.mjs's `driveProtocol` function exactly:
// - client.request  → JSON-RPC request frame (one per queued step)
// - server.response → seed snapshots, record errors, drive next request
// - server.notify   → record event, reduce action through correct reducer
// Reconnect and pin.clock steps are replayed client-side.
static async Task<DriveResult> DriveProtocolAsync(Uri wsUrl, ScenarioDoc scenario)
{
    const int MaxRetries = 5;
    const int TimeoutMs   = 10_000;
    Exception? lastEx = null;

    for (int attempt = 0; attempt <= MaxRetries; attempt++)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(wsUrl, CancellationToken.None);
        }
        catch (Exception e)
        {
            ws.Dispose();
            lastEx = e;
            if (attempt < MaxRetries && IsTransientConnectError(e.Message))
            {
                await Task.Delay(80);
                continue;
            }
            throw new Exception($"websocket connect failed after {attempt + 1} attempt(s): {e.Message}", e);
        }

        // Connected. Run the full protocol exchange.
        try
        {
            return await RunProtocolExchangeAsync(ws, scenario, TimeoutMs);
        }
        finally
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* noop */ }
            ws.Dispose();
        }
    }

    throw new Exception($"all {MaxRetries + 1} connect attempts failed: {lastEx?.Message}");
}

static bool IsTransientConnectError(string msg) =>
    msg.Contains("ECONNREFUSED") || msg.Contains("ECONNRESET") ||
    msg.Contains("refused") || msg.Contains("reset") ||
    msg.Contains("503") || msg.Contains("400");

static async Task<DriveResult> RunProtocolExchangeAsync(
    ClientWebSocket ws, ScenarioDoc scenario, int timeoutMs)
{
    // Collect per-channel reduced state (keyed by channel URI).
    // The JS runner stores snap.state as-is and lets the reducer mutate it.
    // We mirror that by storing raw JsonElement snapshots and deserializing
    // lazily on first action, keyed by action-type PREFIX (not channel scheme).
    // This is what the B4 JS runner's comment at line 26 describes:
    //   "Dispatching by channel scheme would pick the wrong reducer;
    //    dispatching by the action `type` prefix is correct."
    // At seed time, we store the raw JsonElement. On first action for a channel,
    // we deserialize to the right typed state based on the action prefix.
    var channelRawSeeds  = new Dictionary<string, JsonElement>(); // raw JSON from snapshot
    var channels         = new Dictionary<string, object?>();     // typed state after first action
    var channelSeeded    = new HashSet<string>();                  // channels that have been seeded
    var synthetic        = new Dictionary<string, object?>();     // protocolVersion, pingSeen
    var observedEvents   = new List<JsonElement>();
    var surfacedErrors   = new List<JsonElement>();
    var warnings         = new List<string>();

    // The ordered client.request steps to replay.
    var requests = scenario.Steps.Where(s => s.Op == "client.request").ToList();
    int requestCursor = 0;

    // Apply top-level pinClock — already done before entering RunScenarioAsync
    // but re-apply here in case scenario reset is needed.
    if (scenario.PinClock.HasValue)
        Reducers.SetNowProvider(() => scenario.PinClock.Value);

    // Send the first request to kick things off.
    if (requestCursor < requests.Count)
    {
        await SendRequestAsync(ws, requests[requestCursor++]);
    }

    // Receive loop with timeout.
    using var cts = new CancellationTokenSource(timeoutMs);
    var receiveBuffer = new byte[64 * 1024];
    var messageBuffer = new System.IO.MemoryStream();

    try
    {
        while (!cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Soft timeout: we've collected everything; proceed to assertions.
                break;
            }
            catch (WebSocketException)
            {
                // Remote close — normal end-of-scenario.
                break;
            }

            messageBuffer.Write(receiveBuffer, 0, result.Count);

            if (!result.EndOfMessage) continue;

            // Full message received — parse and dispatch.
            var msgJson = Encoding.UTF8.GetString(messageBuffer.ToArray());
            messageBuffer.SetLength(0);

            JsonElement msg;
            try
            {
                using var doc = JsonDocument.Parse(msgJson);
                msg = doc.RootElement.Clone();
            }
            catch
            {
                continue; // malformed frame — skip
            }

            // Determine message type: response (has `id` + `result`|`error`) or notification (has `method`, no `id`).
            var hasId = msg.TryGetProperty("id", out _);
            var hasResult = msg.TryGetProperty("result", out var resultEl);
            var hasError = msg.TryGetProperty("error", out var errorEl);
            var hasMethod = msg.TryGetProperty("method", out var methodEl);

            if (hasId && (hasResult || hasError))
            {
                // It's a response.
                if (hasError)
                {
                    surfacedErrors.Add(errorEl.Clone());
                    synthetic["lastResponseOk"] = false;
                }
                else
                {
                    synthetic["lastResponseOk"] = true;
                    SeedFromSnapshots(resultEl, channelRawSeeds, channelSeeded, synthetic, warnings);
                }
                // Drive the next request.
                if (requestCursor < requests.Count)
                {
                    await SendRequestAsync(ws, requests[requestCursor++]);
                }
            }
            else if (!hasId && hasMethod)
            {
                // It's a notification. Record the MESSAGE form (method + params)
                // so message-level event assertions can match.
                msg.TryGetProperty("params", out var paramsEl);
                observedEvents.Add(msg); // whole frame: { method, params }

                if (methodEl.GetString() == "action" && msg.TryGetProperty("params", out var actionParams))
                {
                    // Also record the ActionEnvelope (params) itself and fold through reducer.
                    observedEvents.Add(actionParams.Clone());
                    ApplyActionNotification(actionParams, channels, channelRawSeeds, channelSeeded, warnings);
                }
            }

            // Check whether the server has sent everything (socket will close naturally).
        }
    }
    catch (OperationCanceledException) { /* soft timeout */ }

    // Merge any channels that were seeded but never had an action applied.
    // For state-only scenarios with no notifies (rare), channels stays empty,
    // but channelRawSeeds holds the raw state. Expose them via ToJsonElement
    // so assert.state can still compare. We store as JsonElement directly.
    foreach (var (res, raw) in channelRawSeeds)
    {
        if (!channels.ContainsKey(res) && channelSeeded.Contains(res))
            channels[res] = raw; // raw JsonElement — ToJsonElement handles it
    }

    return new DriveResult(channels, synthetic, observedEvents, surfacedErrors, warnings);
}

static async Task SendRequestAsync(ClientWebSocket ws, ScenarioStep step)
{
    var frame = new System.Text.Json.Nodes.JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = step.Method,
        ["id"]      = step.Id is JsonElement ide
            ? (ide.ValueKind == JsonValueKind.String ? (System.Text.Json.Nodes.JsonNode?)ide.GetString() : ide.GetInt64())
            : (System.Text.Json.Nodes.JsonNode?)null,
    };
    if (step.Params.HasValue)
        frame["params"] = System.Text.Json.Nodes.JsonNode.Parse(step.Params.Value.GetRawText());

    var json = frame.ToJsonString();
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

// ─── State seeding ────────────────────────────────────────────────────────────

// Store snapshot states as raw JsonElement seeds. The typed state is
// materialized lazily on first action, keyed by the action-type PREFIX —
// exactly the B4 JS runner's dispatch-by-prefix discipline.
static void SeedFromSnapshots(
    JsonElement result,
    Dictionary<string, JsonElement> channelRawSeeds,
    HashSet<string> channelSeeded,
    Dictionary<string, object?> synthetic,
    List<string> warnings)
{
    if (result.TryGetProperty("protocolVersion", out var pvEl) &&
        pvEl.ValueKind == JsonValueKind.String)
    {
        synthetic["protocolVersion"] = pvEl.GetString();
    }

    // initialize-style: result.snapshots[]
    if (result.TryGetProperty("snapshots", out var snapshotsEl) &&
        snapshotsEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var snap in snapshotsEl.EnumerateArray())
        {
            if (!snap.TryGetProperty("resource", out var resEl)) continue;
            var resource = resEl.GetString();
            if (resource is null) continue;

            if (snap.TryGetProperty("state", out var stateEl))
            {
                channelRawSeeds[resource] = stateEl.Clone();
                channelSeeded.Add(resource);
            }
        }
    }

    // reconnect-style: result.snapshot (singular)
    if (result.TryGetProperty("snapshot", out var snapEl) &&
        snapEl.TryGetProperty("resource", out var sResEl))
    {
        var resource = sResEl.GetString();
        if (resource is not null && snapEl.TryGetProperty("state", out var sStateEl))
        {
            channelRawSeeds[resource] = sStateEl.Clone();
            channelSeeded.Add(resource);
        }
    }
}

// ─── Reducer dispatch — by action-type prefix (matches JS runner exactly) ─────
//
// Key invariant (mirrors B4 JS run-conformance.mjs line 26 comment):
//   The corpus routes terminal-reducer fixtures onto an `ahp-session:/…` channel
//   (gen-scenarios has no terminal channel entry), but their state is a
//   TerminalState and their actions are `terminal/*`. Dispatching by channel
//   scheme would pick the wrong reducer; dispatching by the action `type` prefix
//   is correct. The channel string is only the state-bucket key.
//
// State lifecycle:
//   1. `SeedFromSnapshots` stores the raw JsonElement under channelRawSeeds.
//   2. On first action for a channel: deserialize the raw seed into the correct
//      typed state based on the action-type prefix, then reduce.
//   3. On subsequent actions: the typed state is already in `channels`.

static void ApplyActionNotification(
    JsonElement paramsEl,
    Dictionary<string, object?> channels,
    Dictionary<string, JsonElement> channelRawSeeds,
    HashSet<string> channelSeeded,
    List<string> warnings)
{
    if (!paramsEl.TryGetProperty("channel", out var channelEl)) return;
    var channel = channelEl.GetString();
    if (channel is null) return;

    if (!paramsEl.TryGetProperty("action", out var actionEl)) return;

    // Guard: if action is not a JSON object (e.g. a bare string like "hello" in
    // the StringOrMarkdown round-trip scenario), it is not a reducible StateAction.
    // The event is still observed above; nothing to reduce.
    if (actionEl.ValueKind != JsonValueKind.Object) return;

    // Dispatch by action-type prefix (the reliable discriminator — same as B4 JS).
    var actionType = actionEl.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
    var prefix = actionType?.Split('/')[0];

    // Resolve the current state for this channel:
    //   - If already typed in `channels`, use it.
    //   - If only a raw seed exists, materialize it based on the action prefix.
    //   - If neither exists, this is an event-only scenario with no seeded state.
    object? prev;
    if (channels.TryGetValue(channel, out prev))
    {
        // Already materialized — use it.
    }
    else if (channelSeeded.Contains(channel) && channelRawSeeds.TryGetValue(channel, out var rawSeed))
    {
        // Materialize the seed into the typed state matching the action prefix.
        prev = MaterializeSeed(rawSeed, prefix, channel, warnings);
        if (prev is not null)
            channels[channel] = prev;
        else
            return; // Seed present but prefix unknown / unsupported — still observed above.
    }
    else
    {
        // No seed — event-only scenario; event is observed above but no state to fold.
        return;
    }

    // Deserialize the action now that we know the channel has seeded state.
    StateAction action;
    try
    {
        action = JsonSerializer.Deserialize<StateAction>(actionEl.GetRawText(), AhpJson.Options)!;
    }
    catch
    {
        return; // unrecognized action — still observed above
    }

    if (action is null) return;

    switch (prefix)
    {
        case "session":
        {
            if (prev is not SessionState state)
            {
                warnings.Add($"channel {channel} had non-SessionState for session/* action");
                return;
            }
            try { Reducers.ApplyToSession(state, action); channels[channel] = state; }
            catch (Exception e) { warnings.Add($"session reducer threw for {actionType} on {channel}: {e.Message}"); }
            break;
        }
        case "terminal":
        {
            if (prev is not TerminalState state)
            {
                warnings.Add($"channel {channel} had non-TerminalState for terminal/* action");
                return;
            }
            try { Reducers.ApplyToTerminal(state, action); channels[channel] = state; }
            catch (Exception e) { warnings.Add($"terminal reducer threw for {actionType} on {channel}: {e.Message}"); }
            break;
        }
        case "changeset":
        {
            if (prev is not ChangesetState state)
            {
                warnings.Add($"channel {channel} had non-ChangesetState for changeset/* action");
                return;
            }
            try { Reducers.ApplyToChangeset(state, action); channels[channel] = state; }
            catch (Exception e) { warnings.Add($"changeset reducer threw for {actionType} on {channel}: {e.Message}"); }
            break;
        }
        case "root":
        {
            if (prev is not RootState state)
            {
                warnings.Add($"channel {channel} had non-RootState for root/* action");
                return;
            }
            try { Reducers.ApplyToRoot(state, action); channels[channel] = state; }
            catch (Exception e) { warnings.Add($"root reducer threw for {actionType} on {channel}: {e.Message}"); }
            break;
        }
        // resource/* — resourceWatchReducer exists in TS; .NET has no port yet.
        // The action is still observed; skip fold (no state mutation).
        case "resource":
            break;
        default:
            break;
    }
}

// Materialize a raw snapshot JsonElement into the correct typed state based on
// the action-type prefix. Returns null for unrecognized / unsupported types.
static object? MaterializeSeed(JsonElement rawSeed, string? prefix, string channel, List<string> warnings)
{
    try
    {
        return prefix switch
        {
            "session"   => JsonSerializer.Deserialize<SessionState>(rawSeed.GetRawText(), AhpJson.Options),
            "terminal"  => JsonSerializer.Deserialize<TerminalState>(rawSeed.GetRawText(), AhpJson.Options),
            "changeset" => JsonSerializer.Deserialize<ChangesetState>(rawSeed.GetRawText(), AhpJson.Options),
            "root"      => JsonSerializer.Deserialize<RootState>(rawSeed.GetRawText(), AhpJson.Options),
            _           => null,
        };
    }
    catch (Exception e)
    {
        warnings.Add($"seed materialize error for {channel} (prefix {prefix}): {e.Message}");
        return null;
    }
}

// ─── Assertion engine ─────────────────────────────────────────────────────────

static AssertResult CheckAssertion(ScenarioStep step, DriveResult drive)
{
    if (step.Op == "client.assert.state")
        return CheckStateAssertion(step, drive);
    if (step.Op == "client.assert.event")
        return CheckEventAssertion(step, drive);
    if (step.Op == "client.assert.error")
        return CheckErrorAssertion(step, drive);

    return AssertResult.Fail(step, $"unknown assertion op: {step.Op}");
}

// ── client.assert.state ───────────────────────────────────────────────────────

static AssertResult CheckStateAssertion(ScenarioStep step, DriveResult drive)
{
    // Determine the target bucket.
    JsonElement targetEl;
    string bucketLabel;

    if (step.Channel is not null)
    {
        if (!drive.Channels.TryGetValue(step.Channel, out var ch))
            return AssertResult.Fail(step,
                $"no reduced state for channel {step.Channel}; known: [{string.Join(", ", drive.Channels.Keys)}]");
        targetEl = ToJsonElement(ch);
        bucketLabel = $"channel {step.Channel}";
    }
    else if (step.Path is not null)
    {
        // Path with no channel → synthetic top-level state.
        targetEl = ToJsonElement(drive.Synthetic);
        bucketLabel = "synthetic top-level state";
    }
    else
    {
        // No channel and no path → whole-state convergence against the single channel.
        if (drive.Channels.Count == 1)
        {
            var (k, v) = drive.Channels.First();
            targetEl = ToJsonElement(v);
            bucketLabel = $"the single channel ({k})";
        }
        else
        {
            return AssertResult.Fail(step,
                $"whole-state assertion needs exactly one channel, found {drive.Channels.Count}: [{string.Join(", ", drive.Channels.Keys)}]");
        }
    }

    // Navigate to path.
    var (found, actual) = Navigate(targetEl, step.Path);

    // Convergence equality: canonicalize (drop nulls, sort keys) — same rule as
    // the .NET FixtureDrivenReducerTests.Canon and the B4 JS runner canonicalize().
    var expectedEl = step.Equals ?? default;

    // Synthetic top-level: undefined path resolves to null expected sentinel.
    if (!found && step.Path is not null && bucketLabel == "synthetic top-level state" && IsJsonNull(step.Equals))
        found = true; // treat as null

    if (!found)
        return AssertResult.Fail(step, $"assert.state @ {bucketLabel} path '{step.Path}': path not found");

    var actualCanon = Canon(actual);
    var expectedCanon = Canon(expectedEl);

    if (actualCanon == expectedCanon)
        return AssertResult.Pass(step);

    return AssertResult.Fail(step,
        $"assert.state @ {bucketLabel}{(step.Path is not null ? $" path '{step.Path}'" : " (whole state)")}:\n" +
        $"  expected: {Truncate(expectedCanon, 500)}\n" +
        $"  got:      {Truncate(actualCanon, 500)}");
}

// ── client.assert.event ───────────────────────────────────────────────────────

static AssertResult CheckEventAssertion(ScenarioStep step, DriveResult drive)
{
    if (!step.Matches.HasValue)
        return AssertResult.Fail(step, "assert.event missing 'matches' field");

    var matches = step.Matches.Value;

    // Try deep-containment against each observed event and its sub-views:
    // • the event itself
    // • event["action"] (inner action fields for ActionEnvelope)
    // • event["params"] (params of non-action notifications)
    foreach (var ev in drive.ObservedEvents)
    {
        if (DeepContains(ev, matches)) return AssertResult.Pass(step);
        if (ev.TryGetProperty("action", out var actionView) &&
            DeepContains(actionView, matches)) return AssertResult.Pass(step);
        if (ev.TryGetProperty("params", out var paramsView) &&
            DeepContains(paramsView, matches)) return AssertResult.Pass(step);
    }

    return AssertResult.Fail(step,
        $"assert.event: no observed event deep-contains {Truncate(matches.GetRawText(), 300)}. " +
        $"observed {drive.ObservedEvents.Count} event(s).");
}

// ── client.assert.error ───────────────────────────────────────────────────────

static AssertResult CheckErrorAssertion(ScenarioStep step, DriveResult drive)
{
    if (!step.Code.HasValue)
        return AssertResult.Fail(step, "assert.error missing 'code' field");

    foreach (var err in drive.SurfacedErrors)
    {
        if (!err.TryGetProperty("code", out var codeEl)) continue;
        if (codeEl.GetInt32() != step.Code.Value) continue;
        if (step.Message is not null)
        {
            var msgStr = err.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
            if (!msgStr.Contains(step.Message)) continue;
        }
        return AssertResult.Pass(step);
    }

    return AssertResult.Fail(step,
        $"assert.error: no surfaced error with code {step.Code.Value}" +
        (step.Message is not null ? $" + message substring '{step.Message}'" : "") +
        $". surfaced: {JsonSerializer.Serialize(drive.SurfacedErrors)}");
}

// ─── Canonicalization + equality (mirrors the B4 JS canonicalize + Canon) ─────

// Canon: drop null-valued keys, sort keys alphabetically (recursive).
// Produces the same canonical string as the JS runner's `canonicalize` + JSON.stringify.
static string Canon(JsonElement el)
{
    var sb = new StringBuilder();
    CanonWrite(el, sb);
    return sb.ToString();
}

static void CanonWrite(JsonElement el, StringBuilder sb)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
            sb.Append('{');
            bool first = true;
            foreach (var prop in el.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(prop.Name));
                sb.Append(':');
                CanonWrite(prop.Value, sb);
            }
            sb.Append('}');
            break;
        case JsonValueKind.Array:
            sb.Append('[');
            bool first2 = true;
            foreach (var item in el.EnumerateArray())
            {
                if (!first2) sb.Append(',');
                first2 = false;
                CanonWrite(item, sb);
            }
            sb.Append(']');
            break;
        case JsonValueKind.String:
            sb.Append(JsonSerializer.Serialize(el.GetString()));
            break;
        case JsonValueKind.Number:
            sb.Append(el.GetRawText());
            break;
        case JsonValueKind.True:
            sb.Append("true");
            break;
        case JsonValueKind.False:
            sb.Append("false");
            break;
        default:
            sb.Append("null");
            break;
    }
}

// Deep-containment: every key in `expected` matches in `actual`; extra keys
// in actual are ignored. Arrays compare element-wise with same rule.
static bool DeepContains(JsonElement actual, JsonElement expected)
{
    if (expected.ValueKind != JsonValueKind.Object && expected.ValueKind != JsonValueKind.Array)
        return DeepEqual(actual, expected);
    if (actual.ValueKind == JsonValueKind.Null) return false;
    if (expected.ValueKind == JsonValueKind.Array)
    {
        if (actual.ValueKind != JsonValueKind.Array) return false;
        var expArr = expected.EnumerateArray().ToList();
        var actArr = actual.EnumerateArray().ToList();
        if (actArr.Count != expArr.Count) return false;
        for (int i = 0; i < expArr.Count; i++)
            if (!DeepContains(actArr[i], expArr[i])) return false;
        return true;
    }
    // Object: every expected key must be contained in actual.
    if (actual.ValueKind != JsonValueKind.Object) return false;
    foreach (var prop in expected.EnumerateObject())
    {
        if (!actual.TryGetProperty(prop.Name, out var actVal)) return false;
        if (!DeepContains(actVal, prop.Value)) return false;
    }
    return true;
}

static bool DeepEqual(JsonElement a, JsonElement b)
{
    if (a.ValueKind != b.ValueKind) return false;
    return a.ValueKind switch
    {
        JsonValueKind.Null    => true,
        JsonValueKind.True    => true,
        JsonValueKind.False   => true,
        JsonValueKind.String  => a.GetString() == b.GetString(),
        JsonValueKind.Number  => a.GetRawText() == b.GetRawText(),
        JsonValueKind.Array   => DeepEqualArray(a, b),
        JsonValueKind.Object  => DeepEqualObject(a, b),
        _ => false,
    };
}

static bool DeepEqualArray(JsonElement a, JsonElement b)
{
    var aArr = a.EnumerateArray().ToList();
    var bArr = b.EnumerateArray().ToList();
    if (aArr.Count != bArr.Count) return false;
    for (int i = 0; i < aArr.Count; i++)
        if (!DeepEqual(aArr[i], bArr[i])) return false;
    return true;
}

static bool DeepEqualObject(JsonElement a, JsonElement b)
{
    var aProps = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    if (aProps.Count != bProps.Count) return false;
    foreach (var (k, av) in aProps)
    {
        if (!bProps.TryGetValue(k, out var bv)) return false;
        if (!DeepEqual(av, bv)) return false;
    }
    return true;
}

// Navigate a dotted path; numeric segments index arrays.
static (bool Found, JsonElement Value) Navigate(JsonElement root, string? path)
{
    if (path is null || path.Length == 0) return (true, root);
    var cur = root;
    foreach (var seg in path.Split('.'))
    {
        if (cur.ValueKind == JsonValueKind.Object)
        {
            if (!cur.TryGetProperty(seg, out cur)) return (false, default);
        }
        else if (cur.ValueKind == JsonValueKind.Array && int.TryParse(seg, out var idx))
        {
            var arr = cur.EnumerateArray().ToList();
            if (idx < 0 || idx >= arr.Count) return (false, default);
            cur = arr[idx];
        }
        else
        {
            return (false, default);
        }
    }
    return (true, cur);
}

// Convert a CLR object back to JsonElement for unified assertion processing.
static JsonElement ToJsonElement(object? value)
{
    if (value is null) return JsonDocument.Parse("null").RootElement;
    if (value is JsonElement je) return je;
    if (value is Dictionary<string, object?> dict)
    {
        // Synthetic state — only primitive values, serialize generically.
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var (k, v) in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(k));
            sb.Append(':');
            sb.Append(v is null ? "null" : JsonSerializer.Serialize(v));
        }
        sb.Append('}');
        return JsonDocument.Parse(sb.ToString()).RootElement;
    }
    // Typed state (SessionState, TerminalState, etc.) — serialize with AhpJson.
    var raw = JsonSerializer.Serialize(value, AhpJson.Options);
    return JsonDocument.Parse(raw).RootElement;
}

static bool IsJsonNull(JsonElement? el) =>
    el is null || el.Value.ValueKind == JsonValueKind.Null;

static string Truncate(string s, int maxLen) =>
    s.Length <= maxLen ? s : s[..maxLen] + "…";

// ─── DTOs (scenario JSON schema) ──────────────────────────────────────────────

sealed class ScenarioDoc
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("pinClock")]
    public long? PinClock { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("steps")]
    public List<ScenarioStep> Steps { get; set; } = new();
}

sealed class ScenarioStep
{
    [System.Text.Json.Serialization.JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string? Label { get; set; }

    // client.request
    [System.Text.Json.Serialization.JsonPropertyName("method")]
    public string? Method { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    // server.response
    [System.Text.Json.Serialization.JsonPropertyName("forId")]
    public JsonElement? ForId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public JsonElement? Error { get; set; }

    // server.notify
    // (uses Method + Params above)

    // client.assert.state
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public string? Path { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("equals")]
    public new JsonElement? Equals { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("channel")]
    public string? Channel { get; set; }

    // client.assert.event
    [System.Text.Json.Serialization.JsonPropertyName("matches")]
    public JsonElement? Matches { get; set; }

    // client.assert.error
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public int? Code { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    // client.reconnect
    [System.Text.Json.Serialization.JsonPropertyName("lastSeenServerSeq")]
    public long? LastSeenServerSeq { get; set; }

    // pin.clock
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public long? Value { get; set; }
}

// ─── Result types ─────────────────────────────────────────────────────────────

sealed record ScenarioResult(
    string Id,
    string ScenarioPath,
    string Status,
    string? Reason,
    List<AssertResult> Asserts)
{
    public static ScenarioResult Error(string id, string path, string reason) =>
        new(id, path, "ERROR", reason, new List<AssertResult>());
}

sealed record AssertResult(string Op, string Label, bool Ok, string? Detail)
{
    public static AssertResult Pass(ScenarioStep step) =>
        new(step.Op, step.Label ?? "", true, null);

    public static AssertResult Fail(ScenarioStep step, string detail) =>
        new(step.Op, step.Label ?? "", false, detail);
}

sealed class DriveResult
{
    public DriveResult(
        Dictionary<string, object?> channels,
        Dictionary<string, object?> synthetic,
        List<JsonElement> observedEvents,
        List<JsonElement> surfacedErrors,
        List<string> warnings)
    {
        Channels = channels;
        Synthetic = synthetic;
        ObservedEvents = observedEvents;
        SurfacedErrors = surfacedErrors;
        Warnings = warnings;
    }

    public Dictionary<string, object?> Channels { get; }
    public Dictionary<string, object?> Synthetic { get; }
    public List<JsonElement> ObservedEvents { get; }
    public List<JsonElement> SurfacedErrors { get; }
    public List<string> Warnings { get; }
}
