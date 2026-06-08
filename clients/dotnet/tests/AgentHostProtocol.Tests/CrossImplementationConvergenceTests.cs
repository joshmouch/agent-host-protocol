#nullable enable

using System.IO;
using System.Text.Json;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

/// <summary>
/// Cross-implementation convergence: replays a real session trace produced by an
/// INDEPENDENT AHP host (a separate WebSocket host running the canonical
/// TypeScript <c>sessionReducer</c>) through the C# reducer and asserts the
/// resulting state is byte-for-byte identical to the host's authoritative state.
///
/// The trace under <c>interop/</c> was captured over a real WebSocket; this test
/// replays it offline so it runs in CI with no external dependency. It is
/// complementary to the shared per-action fixtures: this is a multi-action
/// session exercising the <c>serverSeq</c> + host-authoritative <c>modifiedAt</c>
/// overlay model (microsoft/agent-host-protocol#186).
/// </summary>
public sealed class CrossImplementationConvergenceTests
{
    [Fact]
    public void ConvergesWithCapturedCanonicalHostTrace()
    {
        var path = Path.Combine(System.AppContext.BaseDirectory, "interop", "independent-host-session-convergence.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var opts = AhpJson.Options;

        var state = root.GetProperty("initial").Deserialize<SessionState>(opts)!;
        foreach (var env in root.GetProperty("envelopes").EnumerateArray())
        {
            var action = env.GetProperty("action").Deserialize<StateAction>(opts)!;
            Reducers.ApplyToSession(state, action);

            // Host-authoritative modifiedAt overlay — the same step every AHP
            // client mirror applies so the impure reducer's clock converges.
            if (env.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("modifiedAt", out var m) && m.ValueKind == JsonValueKind.Number)
                state.Summary.ModifiedAt = m.GetInt64();
        }

        var got = JsonCanon.Of(JsonSerializer.SerializeToElement(state, opts));
        var want = JsonCanon.Of(root.GetProperty("final"));
        Assert.Equal(want, got);
    }
}
