// Port of clients/go/ahptypes/ahptypes_test.go.
// Tests wire-type round-trips without mocking the JSON engine.
//
// These methods are now THIN, NAMED wrappers over the language-agnostic round-
// trip corpus under types/test-cases/round-trips/*.json. Each wrapper loads its
// fixture by name and delegates to TypesRoundTripFixtures, which decodes with the
// REAL serializer + REAL generated types and asserts the fixture's expectations.
//
// Why keep the named wrappers instead of a single [Theory]?
//   * The cross-language test master matrix (OpenAgency plan
//     2026-06-04-0137-ahp-dotnet-client-test-parity) and the executable parity
//     manifest (clients/dotnet/tests/parity-manifest.txt) reference these method
//     names (e.g. ActionEnvelope_RoundTrip_*, Customization_UnknownType_*). The
//     parity gate greps for each name; collapsing them into one [Theory] would
//     drop the named rows AND the [Fact]/[Theory] count below the floor
//     (clients/dotnet/tests/MIN_TEST_COUNT). The named wrappers preserve both.
//   * The corpus is ALSO run as a whole via TypesRoundTripFixtures.CorpusFixture
//     ([Theory] over the dir), so every fixture file is exercised even before a
//     named wrapper exists — the named wrappers are the parity-stable surface,
//     the dir-walk theory is the completeness guard.
#nullable enable

using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class TypesRoundTripTests
{
    // ── ProtocolVersion ───────────────────────────────────────────────────

    [Fact]
    public void ProtocolVersion_CurrentIsNonEmpty()
        => TypesRoundTripFixtures.RunFixtureByName("021");

    [Fact]
    public void ProtocolVersion_SupportedIsNonEmpty()
        => TypesRoundTripFixtures.RunFixtureByName("022");

    [Fact]
    public void ProtocolVersion_FirstSupportedEqualsCurrentVersion()
        => TypesRoundTripFixtures.RunFixtureByName("023");

    // ── ActionEnvelope round-trip ─────────────────────────────────────────

    [Fact]
    public void ActionEnvelope_RoundTrip_SessionTitleChanged()
        => TypesRoundTripFixtures.RunFixtureByName("001");

    // ── Unknown discriminator preserved verbatim ──────────────────────────

    [Fact]
    public void StateAction_UnknownVariant_PreservedVerbatim()
        => TypesRoundTripFixtures.RunFixtureByName("002");

    // ── SessionStatus bitset ──────────────────────────────────────────────

    [Fact]
    public void SessionStatus_HasFlag_Works()
        => TypesRoundTripFixtures.RunFixtureByName("004");

    [Fact]
    public void SessionStatus_UnknownBitsSurviveRoundTrip()
        => TypesRoundTripFixtures.RunFixtureByName("005");

    // ── StringOrMarkdown plain and object forms ───────────────────────────

    [Theory]
    [InlineData("plain", "006")]
    [InlineData("object", "007")]
    public void StringOrMarkdown_RoundTrip(string _, string fixture)
        => TypesRoundTripFixtures.RunFixtureByName(fixture);

    // ── JsonRpcMessage all four shapes ────────────────────────────────────

    [Theory]
    [InlineData("request", "008")]
    [InlineData("notification", "009")]
    [InlineData("success", "010")]
    [InlineData("error", "011")]
    public void JsonRpcMessage_Discriminator(string _, string fixture)
        => TypesRoundTripFixtures.RunFixtureByName(fixture);

    // ── Customization unknown discriminator does not throw ─────────────────

    [Fact]
    public void Customization_UnknownType_DoesNotThrow()
        => TypesRoundTripFixtures.RunFixtureByName("003");

    // ── Changeset-operation target dispatches on its `kind` discriminator ──

    [Fact]
    public void ChangesetOperationTarget_DispatchesOnKind()
    {
        // The original test exercised BOTH the resource and range variants in one
        // method; the corpus splits them into two fixtures (012 resource, 013
        // range). Run both here so this manifest-named method covers the same
        // ground, and the dir-walk theory covers each fixture independently too.
        TypesRoundTripFixtures.RunFixtureByName("012");
        TypesRoundTripFixtures.RunFixtureByName("013");
    }

    // ── SessionInputQuestion "number" and "integer" both decode typed ─────

    [Fact]
    public void SessionInputQuestion_NumberAndIntegerKinds()
    {
        // Original method asserted both kinds; fixtures 014 (number) + 015
        // (integer) preserve the same two vectors.
        TypesRoundTripFixtures.RunFixtureByName("014");
        TypesRoundTripFixtures.RunFixtureByName("015");
    }

    // ── 64-bit numeric field survives values above Int32.MaxValue ─────────

    [Fact]
    public void Number_LongAboveInt32Max_Preserved()
        => TypesRoundTripFixtures.RunFixtureByName("016");

    // ── Unknown wire keys are ignored on decode ───────────────────────────

    [Fact]
    public void UnknownWireKeys_IgnoredOnDecode()
        => TypesRoundTripFixtures.RunFixtureByName("017");

    // ── Nested optional struct round-trips when null ──────────────────────

    [Fact]
    public void NestedOptionalStruct_RoundTripsWhenNull()
        => TypesRoundTripFixtures.RunFixtureByName("018");

    // ── Channel-scoped notification preserves its channel URI ─────────────

    [Fact]
    public void ChannelScopedNotification_CarriesUri()
        => TypesRoundTripFixtures.RunFixtureByName("019");

    // ── Partial summary with all-null payload round-trips ─────────────────

    [Fact]
    public void PartialSummary_AllNullPayload_RoundTrips()
        => TypesRoundTripFixtures.RunFixtureByName("020");
}
