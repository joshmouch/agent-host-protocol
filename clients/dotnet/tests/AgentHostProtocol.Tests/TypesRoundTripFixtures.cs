// Data-driven loader for the shared wire round-trip corpus under
// types/test-cases/round-trips/*.json. Decodes each fixture with the REAL
// System.Text.Json serializer (SystemTextJsonAhpSerializer.Default) and the
// REAL generated wire types, then checks the fixture's assertions against the
// decoded value and the value re-encoded by the same real serializer.
//
// The corpus is language-agnostic (see types/test-cases/round-trips/README.md);
// the same fixtures are intended to drive the Swift / TS / Go / Rust / Kotlin
// clients. This file is the .NET adapter: a logical `type` string → real decode
// dispatch plus a JSON-path / variant / bitset assertion engine.
//
// Two entry points exercise the same corpus:
//   * RunFixtureByName(name) — called by the named [Fact] wrappers in
//     TypesRoundTripTests.cs so the cross-language master-matrix method names
//     (and the parity-manifest rows) survive the move to data-driven fixtures.
//   * the [Theory] CorpusFixture below — iterates EVERY fixture in the dir, so
//     adding a fixture file is automatically run even before a named wrapper
//     exists, and a stray/garbled fixture fails loudly.
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AgentHostProtocol;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class TypesRoundTripFixtures
{
    private static readonly SystemTextJsonAhpSerializer Ser = SystemTextJsonAhpSerializer.Default;

    // ── Public entry points ───────────────────────────────────────────────

    /// <summary>
    /// Runs one fixture identified by its file's leading number-or-name. Used by
    /// the thin named [Fact] wrappers so the master-matrix / parity-manifest
    /// method names are preserved while the behavior lives in shared fixtures.
    /// Internal (not public) so xUnit does not mistake it for a parameterless
    /// test method (xUnit1013); the named wrappers live in the same assembly.
    /// </summary>
    internal static void RunFixtureByName(string fixturePrefix)
    {
        string path = ResolveFixturePath(fixturePrefix);
        VerifyFixture(path);
    }

    public static IEnumerable<object[]> AllFixtures()
    {
        foreach (string path in EnumerateFixtureFiles())
        {
            yield return new object[] { Path.GetFileName(path), path };
        }
    }

    /// <summary>
    /// Iterates the whole corpus. This is additive to the named wrappers (it
    /// re-decodes the same fixtures through the dir-walk entry shape that the
    /// other-language loaders use), and it is the loud guard that every fixture
    /// file on disk is real, parseable, and asserts something.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void CorpusFixture(string name, string path)
    {
        _ = name;
        VerifyFixture(path);
    }

    // ── Verifier ──────────────────────────────────────────────────────────

    private static void VerifyFixture(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        string type = root.GetProperty("type").GetString()
            ?? throw new Xunit.Sdk.XunitException($"{Path.GetFileName(path)}: missing `type`");

        // ProtocolVersion fixtures assert constants, not wire decode.
        if (type == "ProtocolVersion")
        {
            VerifyProtocolConstant(path, root);
            return;
        }

        // Build the exact input bytes: `wireRaw` (verbatim string) wins; else the
        // compact form of the `wire` object.
        string inputJson = ReadInputJson(path, root);

        var (decoded, reencoded) = DecodeAndReencode(type, inputJson);

        bool assertedSomething = false;

        if (root.TryGetProperty("expect", out JsonElement expect))
        {
            using JsonDocument re = JsonDocument.Parse(reencoded);
            foreach (JsonProperty p in expect.EnumerateObject())
            {
                JsonElement actual = ResolvePath(re.RootElement, p.Name, path);
                AssertJsonEquals(p.Value, actual, $"{Path.GetFileName(path)}: expect[\"{p.Name}\"]");
                assertedSomething = true;
            }
        }

        if (root.TryGetProperty("expectVariant", out JsonElement variants))
        {
            VerifyVariant(path, decoded, variants);
            assertedSomething = true;
        }

        if (root.TryGetProperty("expectJsonRpcVariant", out JsonElement jrpcVariant))
        {
            VerifyJsonRpcVariant(path, decoded, jrpcVariant.GetString()!);
            assertedSomething = true;
        }

        if (root.TryGetProperty("expectBitset", out JsonElement bitset))
        {
            VerifyBitset(path, decoded, reencoded, bitset);
            assertedSomething = true;
        }

        if (root.TryGetProperty("expectNumberAbove", out JsonElement above))
        {
            using JsonDocument re = JsonDocument.Parse(reencoded);
            foreach (JsonProperty p in above.EnumerateObject())
            {
                JsonElement actual = ResolvePath(re.RootElement, p.Name, path);
                long bound = p.Value.GetInt64();
                long got = actual.GetInt64();
                Assert.True(
                    got > bound,
                    $"{Path.GetFileName(path)}: expectNumberAbove[\"{p.Name}\"] — {got} is not > {bound}");
                assertedSomething = true;
            }
        }

        if (root.TryGetProperty("expectReencodedAbsent", out JsonElement absent))
        {
            using JsonDocument re = JsonDocument.Parse(reencoded);
            foreach (JsonElement keyEl in absent.EnumerateArray())
            {
                string key = keyEl.GetString()!;
                Assert.False(
                    re.RootElement.TryGetProperty(key, out _),
                    $"{Path.GetFileName(path)}: re-encoded JSON must NOT contain key \"{key}\" but it does. Re-encoded: {reencoded}");
                assertedSomething = true;
            }
        }

        if (root.TryGetProperty("reencodes", out JsonElement reencodesEl) && reencodesEl.GetBoolean())
        {
            Assert.True(
                reencoded == inputJson,
                $"{Path.GetFileName(path)}: re-encode is not byte-exact.\n  input:      {inputJson}\n  re-encoded: {reencoded}");
            assertedSomething = true;
        }

        if (root.TryGetProperty("roundTripStable", out JsonElement stableEl) && stableEl.GetBoolean())
        {
            // Decode the re-encoded JSON a second time and re-assert `expect`.
            var (_, reencoded2) = DecodeAndReencode(type, reencoded);
            if (root.TryGetProperty("expect", out JsonElement expect2))
            {
                using JsonDocument re2 = JsonDocument.Parse(reencoded2);
                foreach (JsonProperty p in expect2.EnumerateObject())
                {
                    JsonElement actual = ResolvePath(re2.RootElement, p.Name, path);
                    AssertJsonEquals(p.Value, actual,
                        $"{Path.GetFileName(path)}: roundTripStable expect[\"{p.Name}\"] (2nd decode)");
                }
            }
            else
            {
                // No `expect` to recheck — at minimum the second re-encode must
                // equal the first (stable fixed point).
                Assert.True(
                    reencoded2 == reencoded,
                    $"{Path.GetFileName(path)}: round-trip is not a fixed point.\n  1st: {reencoded}\n  2nd: {reencoded2}");
            }
            assertedSomething = true;
        }

        Assert.True(
            assertedSomething,
            $"{Path.GetFileName(path)}: fixture made no assertions (no expect/expectVariant/expectJsonRpcVariant/expectBitset/expectNumberAbove/expectReencodedAbsent/reencodes/roundTripStable). A fixture that checks nothing is coverage theater.");
    }

    /// <summary>
    /// Neutral JSON-RPC variant check. The fixture names the logical variant
    /// ("request"/"notification"/"success"/"error"); each language maps it to its
    /// own JsonRpcMessage accessor (here, the C# property). Asserts that variant is
    /// present and the other three absent — verifying the decoder's wire-shape
    /// dispatch without baking .NET property names into the shared corpus.
    /// </summary>
    private static void VerifyJsonRpcVariant(string path, object decoded, string kind)
    {
        string fname = Path.GetFileName(path);
        string? present = kind switch
        {
            "request" => "Request",
            "notification" => "Notification",
            "success" => "SuccessResponse",
            "error" => "ErrorResponse",
            _ => null,
        };
        Assert.True(present is not null,
            $"{fname}: expectJsonRpcVariant \"{kind}\" is not one of request/notification/success/error");
        foreach (string prop in new[] { "Request", "Notification", "SuccessResponse", "ErrorResponse" })
        {
            object? val = decoded.GetType().GetProperty(prop)?.GetValue(decoded);
            bool shouldBePresent = prop == present;
            Assert.True(
                (val is not null) == shouldBePresent,
                $"{fname}: expectJsonRpcVariant \"{kind}\" — {prop} is {(val is null ? "absent" : "present")}, expected {(shouldBePresent ? "present" : "absent")}");
        }
    }

    // ── Real decode dispatch ──────────────────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="inputJson"/> into the real generated type named by
    /// <paramref name="type"/> using the real serializer, then re-encodes it with
    /// the same serializer. Returns both so assertions can inspect the decoded
    /// object (variant identity, flag bits) and the re-encoded wire (field paths,
    /// byte-exactness). Adding a wire type to the corpus is a deliberate edit
    /// here — the corpus never decodes arbitrary types reflectively.
    /// </summary>
    private static (object decoded, string reencoded) DecodeAndReencode(string type, string inputJson)
    {
        switch (type)
        {
            case "ActionEnvelope":
                return Wrap(Ser.Deserialize<ActionEnvelope>(inputJson));
            case "StateAction":
                return Wrap(Ser.Deserialize<StateAction>(inputJson));
            case "Customization":
                return Wrap(Ser.Deserialize<Customization>(inputJson));
            case "SessionStatus":
                return Wrap(Ser.Deserialize<SessionStatus>(inputJson));
            case "StringOrMarkdown":
                return Wrap(Ser.Deserialize<StringOrMarkdown>(inputJson));
            case "JsonRpcMessage":
                return Wrap(Ser.Deserialize<JsonRpcMessage>(inputJson));
            case "ChangesetOperationTarget":
                return Wrap(Ser.Deserialize<ChangesetOperationTarget>(inputJson));
            case "SessionInputQuestion":
                return Wrap(Ser.Deserialize<SessionInputQuestion>(inputJson));
            case "SessionSummary":
                return Wrap(Ser.Deserialize<SessionSummary>(inputJson));
            case "SessionAddedParams":
                return Wrap(Ser.Deserialize<SessionAddedParams>(inputJson));
            case "PartialSessionSummary":
                return Wrap(Ser.Deserialize<PartialSessionSummary>(inputJson));
            default:
                throw new Xunit.Sdk.XunitException(
                    $"round-trip fixture: unknown wire type \"{type}\". " +
                    "Add a decode entry to TypesRoundTripFixtures.DecodeAndReencode.");
        }

        (object, string) Wrap<T>(T value) => (value!, Ser.Serialize(value));
    }

    // ── Assertion helpers ─────────────────────────────────────────────────

    private static void VerifyVariant(string path, object decoded, JsonElement variants)
    {
        foreach (JsonProperty p in variants.EnumerateObject())
        {
            string accessor = p.Name;
            string want = p.Value.GetString()!;

            if (accessor.Length == 0)
            {
                // Whole-decoded-value union identity: the active .Value's runtime
                // type (e.g. StateAction / Customization decoded directly).
                object? value = RequireUnionValue(decoded, path);
                AssertVariantType(path, "", value, want);
                continue;
            }

            // Named accessor (case-insensitive, so a wire name like "action"
            // resolves the C# `Action` property).
            object? member = GetMember(decoded, accessor, path);

            if (want is "present" or "absent")
            {
                // Plain-union container with one nullable property per variant
                // (e.g. JsonRpcMessage.Request / .Notification).
                bool present = member is not null;
                bool wantPresent = want == "present";
                Assert.True(
                    present == wantPresent,
                    $"{Path.GetFileName(path)}: expectVariant[\"{accessor}\"] — {(present ? "present" : "absent")}, expected {want}");
            }
            else
            {
                // Property is itself an AhpUnion; assert its active .Value's
                // concrete type (e.g. ActionEnvelope.Action.Value).
                Assert.True(
                    member is AhpUnion,
                    $"{Path.GetFileName(path)}: expectVariant[\"{accessor}\"] expected a union value (type \"{want}\"), but property is {(member is null ? "null" : member.GetType().Name)}");
                object? inner = ((AhpUnion)member!).Value;
                AssertVariantType(path, accessor, inner, want);
            }
        }
    }

    private static void AssertVariantType(string path, string accessor, object? value, string want)
    {
        string actualType = value is null ? "null" : value.GetType().Name;
        string ctx = accessor.Length == 0 ? "expectVariant[\"\"]" : $"expectVariant[\"{accessor}\"]";
        Assert.True(
            actualType == want,
            $"{Path.GetFileName(path)}: {ctx} — active .Value is {actualType}, expected {want}");
    }

    private static void VerifyBitset(string path, object decoded, string reencoded, JsonElement bitset)
    {
        Assert.True(
            decoded is SessionStatus,
            $"{Path.GetFileName(path)}: expectBitset requires a SessionStatus, got {decoded.GetType().Name}");
        var status = (SessionStatus)decoded;

        if (bitset.TryGetProperty("has", out JsonElement has))
        {
            foreach (JsonElement nameEl in has.EnumerateArray())
            {
                SessionStatus flag = ParseStatusFlag(nameEl.GetString()!, path);
                Assert.True(
                    status.HasFlag(flag),
                    $"{Path.GetFileName(path)}: SessionStatus must have flag {nameEl.GetString()} but does not (value {(uint)status})");
            }
        }

        if (bitset.TryGetProperty("lacks", out JsonElement lacks))
        {
            foreach (JsonElement nameEl in lacks.EnumerateArray())
            {
                SessionStatus flag = ParseStatusFlag(nameEl.GetString()!, path);
                Assert.False(
                    status.HasFlag(flag),
                    $"{Path.GetFileName(path)}: SessionStatus must NOT have flag {nameEl.GetString()} but does (value {(uint)status})");
            }
        }

        if (bitset.TryGetProperty("numeric", out JsonElement numericEl))
        {
            ulong want = numericEl.GetUInt64();
            Assert.Equal(want, (ulong)(uint)status);

            // The re-encoded wire form must also be the same numeric value.
            using JsonDocument re = JsonDocument.Parse(reencoded);
            Assert.True(
                re.RootElement.ValueKind == JsonValueKind.Number,
                $"{Path.GetFileName(path)}: SessionStatus must re-encode as a JSON number, got {re.RootElement.ValueKind}");
            Assert.Equal(want, re.RootElement.GetUInt64());
        }
    }

    private static void VerifyProtocolConstant(string path, JsonElement root)
    {
        JsonElement c = root.GetProperty("expectConstant");
        bool asserted = false;

        if (c.TryGetProperty("current", out JsonElement cur))
        {
            Assert.Equal("non-empty", cur.GetString());
            Assert.False(
                string.IsNullOrWhiteSpace(ProtocolVersion.Current),
                $"{Path.GetFileName(path)}: ProtocolVersion.Current must be non-empty");
            asserted = true;
        }

        if (c.TryGetProperty("supported", out JsonElement sup))
        {
            Assert.Equal("non-empty-list", sup.GetString());
            Assert.NotEmpty(ProtocolVersion.Supported);
            asserted = true;
        }

        if (c.TryGetProperty("firstSupportedEqualsCurrent", out JsonElement first) && first.GetBoolean())
        {
            Assert.NotEmpty(ProtocolVersion.Supported);
            Assert.Equal(ProtocolVersion.Current, ProtocolVersion.Supported[0]);
            asserted = true;
        }

        Assert.True(asserted, $"{Path.GetFileName(path)}: ProtocolVersion fixture asserted no constant");
    }

    // ── Reflection plumbing for unions ────────────────────────────────────

    private static object? RequireUnionValue(object decoded, string path)
    {
        if (decoded is AhpUnion u)
        {
            return u.Value;
        }

        throw new Xunit.Sdk.XunitException(
            $"{Path.GetFileName(path)}: expectVariant[\"\"] requires a union (AhpUnion), got {decoded.GetType().Name}");
    }

    private static object? GetMember(object decoded, string name, string path)
    {
        // Case-insensitive so a wire field name ("action") resolves the C#
        // PascalCase property ("Action").
        var prop = decoded.GetType().GetProperty(
            name,
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);
        if (prop is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Path.GetFileName(path)}: type {decoded.GetType().Name} has no property \"{name}\" (case-insensitive) for expectVariant");
        }

        return prop.GetValue(decoded);
    }

    private static SessionStatus ParseStatusFlag(string name, string path)
    {
        if (Enum.TryParse<SessionStatus>(name, ignoreCase: false, out SessionStatus flag))
        {
            return flag;
        }

        throw new Xunit.Sdk.XunitException(
            $"{Path.GetFileName(path)}: unknown SessionStatus flag \"{name}\"");
    }

    // ── JSON path + equality ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a dotted path against a JSON element. The empty path returns the
    /// element itself (for scalar unions whose whole value is the payload).
    /// </summary>
    private static JsonElement ResolvePath(JsonElement root, string path, string fixturePath)
    {
        if (path.Length == 0)
        {
            return root;
        }

        JsonElement cur = root;
        foreach (string seg in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out JsonElement next))
            {
                throw new Xunit.Sdk.XunitException(
                    $"{Path.GetFileName(fixturePath)}: path \"{path}\" — segment \"{seg}\" not found in {cur.GetRawText()}");
            }

            cur = next;
        }

        return cur;
    }

    private static void AssertJsonEquals(JsonElement want, JsonElement got, string ctx)
    {
        switch (want.ValueKind)
        {
            case JsonValueKind.String:
                Assert.True(
                    got.ValueKind == JsonValueKind.String && got.GetString() == want.GetString(),
                    $"{ctx} — expected string \"{want.GetString()}\", got {Describe(got)}");
                break;
            case JsonValueKind.Number:
                // Compare numerically so 64-bit values above Int32 are exact and
                // 0 == 0.0 etc. Use decimal when both fit; fall back to raw text.
                Assert.True(
                    got.ValueKind == JsonValueKind.Number && NumbersEqual(want, got),
                    $"{ctx} — expected number {want.GetRawText()}, got {Describe(got)}");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.True(
                    got.ValueKind == want.ValueKind,
                    $"{ctx} — expected {want.ValueKind}, got {Describe(got)}");
                break;
            case JsonValueKind.Null:
                Assert.True(
                    got.ValueKind == JsonValueKind.Null,
                    $"{ctx} — expected null, got {Describe(got)}");
                break;
            default:
                // Objects / arrays: compare canonical raw text.
                Assert.True(
                    got.GetRawText() == want.GetRawText(),
                    $"{ctx} — expected {want.GetRawText()}, got {got.GetRawText()}");
                break;
        }
    }

    private static bool NumbersEqual(JsonElement a, JsonElement b)
    {
        if (a.TryGetInt64(out long la) && b.TryGetInt64(out long lb))
        {
            return la == lb;
        }

        if (a.TryGetUInt64(out ulong ua) && b.TryGetUInt64(out ulong ub))
        {
            return ua == ub;
        }

        if (a.TryGetDecimal(out decimal da) && b.TryGetDecimal(out decimal db))
        {
            return da == db;
        }

        if (a.TryGetDouble(out double dda) && b.TryGetDouble(out double ddb))
        {
            return dda == ddb;
        }

        return a.GetRawText() == b.GetRawText();
    }

    private static string Describe(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.String => $"string \"{e.GetString()}\"",
            JsonValueKind.Number => $"number {e.GetRawText()}",
            JsonValueKind.Object or JsonValueKind.Array => e.GetRawText(),
            _ => e.ValueKind.ToString(),
        };

    // ── Fixture file plumbing ─────────────────────────────────────────────

    private static string ReadInputJson(string path, JsonElement root)
    {
        bool hasRaw = root.TryGetProperty("wireRaw", out JsonElement rawEl);
        bool hasWire = root.TryGetProperty("wire", out JsonElement wireEl);

        if (hasRaw == hasWire)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Path.GetFileName(path)}: exactly one of `wire` / `wireRaw` is required for a decode fixture (found wire={hasWire}, wireRaw={hasRaw}).");
        }

        if (hasRaw)
        {
            // `wireRaw` is a JSON string whose CONTENT is the exact bytes to decode.
            return rawEl.GetString()
                ?? throw new Xunit.Sdk.XunitException($"{Path.GetFileName(path)}: `wireRaw` is null");
        }

        // `wire` is a JSON value; compact-serialize it to bytes.
        return JsonSerializer.Serialize(wireEl);
    }

    private static string ResolveFixturePath(string prefix)
    {
        string dir = FindFixtureDir();
        var matches = Directory
            .EnumerateFiles(dir, "*.json")
            .Where(p =>
            {
                string fn = Path.GetFileNameWithoutExtension(p);
                return fn == prefix || fn.StartsWith(prefix + "-", StringComparison.Ordinal);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            throw new FileNotFoundException(
                $"no round-trip fixture matches prefix \"{prefix}\" in {dir}");
        }

        if (matches.Count > 1)
        {
            throw new Xunit.Sdk.XunitException(
                $"prefix \"{prefix}\" is ambiguous — matched {matches.Count}: " +
                string.Join(", ", matches.Select(Path.GetFileName)));
        }

        return matches[0];
    }

    private static IEnumerable<string> EnumerateFixtureFiles() =>
        Directory
            .EnumerateFiles(FindFixtureDir(), "*.json")
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string FindFixtureDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "types", "test-cases", "round-trips");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException(
            "could not locate types/test-cases/round-trips walking upward from the test assembly");
    }
}
