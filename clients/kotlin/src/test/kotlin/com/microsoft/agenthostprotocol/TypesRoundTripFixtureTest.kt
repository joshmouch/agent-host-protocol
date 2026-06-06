package com.microsoft.agenthostprotocol

import com.microsoft.agenthostprotocol.generated.ActionEnvelope
import com.microsoft.agenthostprotocol.generated.ChangesetOperationTarget
import com.microsoft.agenthostprotocol.generated.Customization
import com.microsoft.agenthostprotocol.generated.CustomizationDirectory
import com.microsoft.agenthostprotocol.generated.CustomizationPlugin
import com.microsoft.agenthostprotocol.generated.CustomizationUnknown
import com.microsoft.agenthostprotocol.generated.JsonRpcErrorResponse
import com.microsoft.agenthostprotocol.generated.JsonRpcNotification
import com.microsoft.agenthostprotocol.generated.JsonRpcRequest
import com.microsoft.agenthostprotocol.generated.JsonRpcSuccessResponse
import com.microsoft.agenthostprotocol.generated.PartialSessionSummary
import com.microsoft.agenthostprotocol.generated.PROTOCOL_VERSION
import com.microsoft.agenthostprotocol.generated.SUPPORTED_PROTOCOL_VERSIONS
import com.microsoft.agenthostprotocol.generated.SessionAddedParams
import com.microsoft.agenthostprotocol.generated.SessionInputQuestion
import com.microsoft.agenthostprotocol.generated.SessionInputQuestionBoolean
import com.microsoft.agenthostprotocol.generated.SessionInputQuestionMultiSelect
import com.microsoft.agenthostprotocol.generated.SessionInputQuestionNumber
import com.microsoft.agenthostprotocol.generated.SessionInputQuestionSingleSelect
import com.microsoft.agenthostprotocol.generated.SessionInputQuestionText
import com.microsoft.agenthostprotocol.generated.SessionStatus
import com.microsoft.agenthostprotocol.generated.SessionSummary
import com.microsoft.agenthostprotocol.generated.StateAction
import com.microsoft.agenthostprotocol.generated.StateActionSessionTitleChanged
import com.microsoft.agenthostprotocol.generated.StateActionUnknown
import com.microsoft.agenthostprotocol.generated.StringOrMarkdown
import java.io.File
import kotlinx.serialization.KSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Data-driven wire round-trip parity for the Kotlin client.
 *
 * Loads the SHARED, language-agnostic round-trip corpus under
 * the `types/test-cases/round-trips/` directory (the `.json` fixtures) — the
 * very same fixtures the .NET
 * client runs (`clients/dotnet/tests/.../TypesRoundTripTests.cs`) and the
 * Swift reference client runs
 * (`clients/swift/.../TypesRoundTripFixtureTests.swift`) — and asserts each
 * via REAL kotlinx-serialization decode/encode of the corresponding generated
 * wire type (no mocks, no faked SUT).
 *
 * Fixture-directory resolution mirrors [FixtureDrivenReducerTest]:
 *   1. the `ahp.roundTripFixturesDir` system property if set (wired by
 *      `build.gradle.kts` for Gradle runs); else
 *   2. walking upward from `user.dir` for `types/test-cases/round-trips/`.
 *
 * The corpus carries language-neutral discriminators; each maps onto a Kotlin
 * accessor here:
 *   - `wire` / `wireRaw`      — exactly one; the bytes that get decoded.
 *   - `expect`                — dotted JSON paths checked against the
 *                               RE-ENCODED wire (so a field that decodes but
 *                               fails to re-emit is caught).
 *   - `expectVariant`         — { accessor: ConcreteTypeName }; "" means the
 *                               whole decoded union's active case. The .NET
 *                               concrete type name is mapped to the Kotlin
 *                               sealed-interface variant that carries the same
 *                               payload. The corpus name "JsonElement" is the
 *                               forward-compat passthrough case
 *                               (`*Unknown(raw: JsonObject)`).
 *   - `expectJsonRpcVariant`  request|notification|success|error. Kotlin has no
 *                               single `JsonRpcMessage` union (the four shapes
 *                               are distinct generated data classes), so the
 *                               wire is classified by the JSON-RPC 2.0 envelope
 *                               rule AND decoded into the REAL generated type
 *                               for that variant to prove it round-trips.
 *   - `expectBitset`          SessionStatus flag membership + numeric value.
 *   - `expectNumberAbove`     a re-encoded numeric field exceeds a 64-bit bound.
 *   - `expectReencodedAbsent` keys that must NOT appear in the re-encoded wire.
 *   - `reencodes`             re-encode is structure-exact with the input.
 *   - `roundTripStable`       decode→encode→decode→encode is a fixed point (and
 *                             any `expect` paths still hold on the 2nd pass).
 *   - `expectConstant`        ProtocolVersion constants (no wire decode).
 *
 * To run only this class:
 * ```
 * ./gradlew test --tests com.microsoft.agenthostprotocol.TypesRoundTripFixtureTest
 * ```
 */
class TypesRoundTripFixtureTest {

    // ── Known representational gaps (documented, not silent) ────────────────
    //
    // Fixtures the current Kotlin generated types cannot represent. Each is a
    // REAL, named gap reported out of the suite — never a silent skip. The
    // `knownGapsAreExactlyTheFailures` test asserts the set of fixtures that
    // actually fail-to-represent equals THIS set, so a future type change that
    // closes a gap (or opens a new one) fails loudly and forces this list to be
    // updated (the drift tripwire, mirroring Swift's knownRepresentationalGaps).
    //
    // 019 channel-scoped-notification-uri:
    //     SCHEMA-INVALID fixture. `schema/notifications.schema.json` declares
    //     BOTH `channel` and `summary` required on SessionAddedParams, but the
    //     fixture's wire is { channel, session } with NO `summary`. Kotlin's
    //     SessionAddedParams.summary is a NON-optional SessionSummary
    //     (Notifications.generated.kt — the spec-faithful modeling), so decode
    //     throws "missing summary". This is the spec-correct rejection of an
    //     off-spec payload, NOT a Kotlin fidelity defect. The fixture itself is
    //     being repaired separately (the .NET track owns it); until then it is
    //     pinned here as a known gap. See
    //     types/test-cases/round-trips/KNOWN-FIDELITY-GAPS.md Gap 5.
    //
    // NOTE: 002/003 (unknown StateAction / Customization passthrough) and
    // 012/013 (ChangesetOperationTarget `kind` re-emit) are Swift gaps but NOT
    // Kotlin gaps: Kotlin already models `*Unknown(raw: JsonObject)` passthrough
    // cases that re-encode verbatim, and its `kind` discriminators are real
    // stored fields emitted under `encodeDefaults = true`. They run as real
    // assertions here.
    private val knownRepresentationalGaps: Set<String> = setOf(
        "019-channel-scoped-notification-uri",
    )

    // ── Corpus presence ─────────────────────────────────────────────────────

    @Test
    fun `round-trip corpus is present`() {
        val files = fixtureFiles()
        assertTrue(
            files.isNotEmpty(),
            "No round-trip fixtures found at ${fixtureDir().absolutePath}. " +
                "Ensure the checkout includes types/test-cases/round-trips/.",
        )
    }

    // ── Per-fixture dynamic tests (one node per file) ────────────────────────

    @TestFactory
    fun roundTripCorpus(): List<DynamicTest> {
        val files = fixtureFiles()
        assertTrue(files.isNotEmpty(), "round-trip corpus is empty at ${fixtureDir().absolutePath}")
        return files.map { file ->
            val stem = file.nameWithoutExtension
            DynamicTest.dynamicTest(file.name) {
                val root = Ahp.json.parseToJsonElement(file.readText()).jsonObject
                if (stem in knownRepresentationalGaps) {
                    // Assert the gap STILL reproduces (decode/assert throws). If
                    // it no longer throws, the gap closed → fail loudly so the
                    // gap list gets updated.
                    var threw = false
                    try {
                        runFixture(file.name, root)
                    } catch (_: Throwable) {
                        threw = true
                    }
                    assertTrue(
                        threw,
                        "${file.name}: declared a known representational gap, but it now decodes+asserts " +
                            "cleanly. Remove it from knownRepresentationalGaps (and ideally let it run as a " +
                            "real assertion).",
                    )
                } else {
                    runFixture(file.name, root)
                }
            }
        }
    }

    /**
     * Whole-corpus tripwire: the set of fixtures that actually fail to
     * represent must equal [knownRepresentationalGaps] exactly. Closing a gap
     * shrinks the failing set → mismatch → forces the list to shrink. A new
     * un-representable fixture lands in the failing set but not the declared set
     * → loud failure.
     */
    @Test
    fun `known gaps are exactly the failing fixtures`() {
        val files = fixtureFiles()
        val failing = sortedSetOf<String>()
        var ranReal = 0
        for (file in files) {
            val stem = file.nameWithoutExtension
            val root = Ahp.json.parseToJsonElement(file.readText()).jsonObject
            try {
                runFixture(file.name, root)
                ranReal++
            } catch (t: Throwable) {
                failing.add(stem)
            }
        }
        assertEquals(
            knownRepresentationalGaps.toSortedSet(),
            failing,
            "Known-gap set drifted. Actually-failing: $failing; declared: " +
                "${knownRepresentationalGaps.toSortedSet()}. A gap that no longer reproduces must be " +
                "removed from knownRepresentationalGaps; a newly-failing fixture is a real regression.",
        )
        // Every non-gap fixture must have run a real assertion (no silent pass).
        assertEquals(
            files.size - knownRepresentationalGaps.size,
            ranReal,
            "Expected ${files.size - knownRepresentationalGaps.size} fixtures to decode+assert for real; " +
                "only $ranReal did.",
        )
    }

    // ── Per-fixture dispatch ────────────────────────────────────────────────

    private fun runFixture(file: String, root: JsonObject) {
        val type = root["type"]?.jsonPrimitive?.contentOrNull
            ?: error("$file: missing `type`")

        // ProtocolVersion fixtures assert constants, not wire decode.
        if (type == "ProtocolVersion") {
            verifyProtocolConstant(file, root)
            return
        }

        val inputJson = readInputJson(file, root)
        val (decoded, reencoded) = decodeAndReencode(type, inputJson)

        var assertedSomething = false

        (root["expect"] as? JsonObject)?.let { expect ->
            val reObj = Ahp.json.parseToJsonElement(reencoded)
            for ((path, want) in expect) {
                val got = resolvePath(reObj, path, file)
                assertJsonEquals(want, got, "$file: expect[\"$path\"]")
                assertedSomething = true
            }
        }

        (root["expectVariant"] as? JsonObject)?.let { variants ->
            verifyVariant(file, decoded, variants)
            assertedSomething = true
        }

        root["expectJsonRpcVariant"]?.jsonPrimitive?.contentOrNull?.let { kind ->
            // JsonRpc fixtures are dispatched in decodeAndReencode (type ==
            // "JsonRpcMessage"); the decoded value already carries the verdict.
            verifyJsonRpcVariant(file, decoded, kind)
            assertedSomething = true
        }

        (root["expectBitset"] as? JsonObject)?.let { bitset ->
            verifyBitset(file, decoded, reencoded, bitset)
            assertedSomething = true
        }

        (root["expectNumberAbove"] as? JsonObject)?.let { above ->
            val reObj = Ahp.json.parseToJsonElement(reencoded)
            for ((path, boundEl) in above) {
                val got = resolvePath(reObj, path, file)
                val bound = (boundEl as? JsonPrimitive)?.longOrNull
                    ?: error("$file: expectNumberAbove[\"$path\"] — non-numeric bound")
                val gotN = (got as? JsonPrimitive)?.longOrNull
                    ?: error("$file: expectNumberAbove[\"$path\"] — re-encoded value is non-numeric: $got")
                assertTrue(
                    gotN > bound,
                    "$file: expectNumberAbove[\"$path\"] — $gotN is not > $bound",
                )
                assertedSomething = true
            }
        }

        (root["expectReencodedAbsent"] as? kotlinx.serialization.json.JsonArray)?.let { absent ->
            val reObj = Ahp.json.parseToJsonElement(reencoded) as? JsonObject ?: JsonObject(emptyMap())
            for (keyEl in absent) {
                val key = (keyEl as? JsonPrimitive)?.contentOrNull ?: continue
                assertTrue(
                    !reObj.containsKey(key),
                    "$file: re-encoded JSON must NOT contain key \"$key\" but it does. Re-encoded: $reencoded",
                )
                assertedSomething = true
            }
        }

        if ((root["reencodes"] as? JsonPrimitive)?.booleanOrNull == true) {
            assertCanonicalEqual(inputJson, reencoded, "$file: reencodes (structure-exact)")
            assertedSomething = true
        }

        if ((root["roundTripStable"] as? JsonPrimitive)?.booleanOrNull == true) {
            val (_, reencoded2) = decodeAndReencode(type, reencoded)
            val expect = root["expect"] as? JsonObject
            if (expect != null) {
                val re2Obj = Ahp.json.parseToJsonElement(reencoded2)
                for ((path, want) in expect) {
                    val got = resolvePath(re2Obj, path, file)
                    assertJsonEquals(want, got, "$file: roundTripStable expect[\"$path\"] (2nd decode)")
                }
            } else {
                assertCanonicalEqual(reencoded, reencoded2, "$file: roundTripStable fixed-point")
            }
            assertedSomething = true
        }

        assertTrue(
            assertedSomething,
            "$file: fixture made no assertions — coverage theater.",
        )
    }

    // ── Real decode dispatch ────────────────────────────────────────────────
    //
    // Mirrors the .NET / Swift dispatch switches. Adding a wire type to the
    // corpus is a deliberate edit here; the corpus never decodes arbitrary
    // types reflectively. Returns a [Decoded] so variant assertions can inspect
    // the active case, plus the re-encoded bytes (via the AHP-tuned
    // [Ahp.json]).

    private sealed interface Decoded {
        data class Envelope(val value: ActionEnvelope) : Decoded
        data class Action(val value: StateAction) : Decoded
        data class Custom(val value: Customization) : Decoded
        data class Status(val value: SessionStatus) : Decoded
        data class StrOrMd(val value: StringOrMarkdown) : Decoded
        data class JsonRpc(val variant: String) : Decoded
        data class ChangesetTarget(val value: ChangesetOperationTarget) : Decoded
        data class InputQuestion(val value: SessionInputQuestion) : Decoded
        data class Summary(val value: SessionSummary) : Decoded
        data class AddedParams(val value: SessionAddedParams) : Decoded
        data class PartialSummary(val value: PartialSessionSummary) : Decoded
    }

    private fun decodeAndReencode(type: String, inputJson: String): Pair<Decoded, String> {
        fun <T> roundtrip(serializer: KSerializer<T>, wrap: (T) -> Decoded): Pair<Decoded, String> {
            val value = Ahp.json.decodeFromString(serializer, inputJson)
            val reencoded = Ahp.json.encodeToString(serializer, value)
            return wrap(value) to reencoded
        }
        return when (type) {
            "ActionEnvelope" -> roundtrip(ActionEnvelope.serializer()) { Decoded.Envelope(it) }
            "StateAction" -> roundtrip(StateAction.serializer()) { Decoded.Action(it) }
            "Customization" -> roundtrip(Customization.serializer()) { Decoded.Custom(it) }
            "SessionStatus" -> roundtrip(SessionStatus.serializer()) { Decoded.Status(it) }
            "StringOrMarkdown" -> roundtrip(StringOrMarkdown.serializer()) { Decoded.StrOrMd(it) }
            "ChangesetOperationTarget" ->
                roundtrip(ChangesetOperationTarget.serializer()) { Decoded.ChangesetTarget(it) }
            "SessionInputQuestion" ->
                roundtrip(SessionInputQuestion.serializer()) { Decoded.InputQuestion(it) }
            "SessionSummary" -> roundtrip(SessionSummary.serializer()) { Decoded.Summary(it) }
            "SessionAddedParams" -> roundtrip(SessionAddedParams.serializer()) { Decoded.AddedParams(it) }
            "PartialSessionSummary" ->
                roundtrip(PartialSessionSummary.serializer()) { Decoded.PartialSummary(it) }
            "JsonRpcMessage" -> decodeJsonRpc(inputJson)
            else -> error(
                "round-trip fixture: unknown wire type \"$type\". Add a decode entry to decodeAndReencode.",
            )
        }
    }

    /**
     * Kotlin has no single `JsonRpcMessage` union; the four JSON-RPC shapes are
     * distinct generated data classes (`JsonRpcRequest<P>`,
     * `JsonRpcNotification<P>`, `JsonRpcSuccessResponse<R>`,
     * `JsonRpcErrorResponse`). Classify the wire by the JSON-RPC 2.0 envelope
     * rule, then decode into the REAL generated type for that variant — a
     * decode failure (wrong shape) surfaces as a thrown exception, so this is a
     * real round-trip assertion, not a shape-only sniff. Returns the canonical
     * variant verdict and the re-encoded bytes for that concrete type.
     */
    private fun decodeJsonRpc(inputJson: String): Pair<Decoded, String> {
        val obj = Ahp.json.parseToJsonElement(inputJson).jsonObject
        val hasId = obj.containsKey("id") && obj["id"] !is JsonNull
        val hasMethod = obj.containsKey("method")
        val hasResult = obj.containsKey("result")
        val hasError = obj.containsKey("error")

        // Decode through the REAL generated type so the params/result/error
        // payloads actually parse (JsonElement params keep arbitrary bodies).
        val pSer = JsonElement.serializer()
        return when {
            hasError && hasId -> {
                val v = Ahp.json.decodeFromString(JsonRpcErrorResponse.serializer(), inputJson)
                Decoded.JsonRpc("error") to Ahp.json.encodeToString(JsonRpcErrorResponse.serializer(), v)
            }
            hasResult && hasId -> {
                val ser = JsonRpcSuccessResponse.serializer(pSer)
                val v = Ahp.json.decodeFromString(ser, inputJson)
                Decoded.JsonRpc("success") to Ahp.json.encodeToString(ser, v)
            }
            hasMethod && hasId -> {
                val ser = JsonRpcRequest.serializer(pSer)
                val v = Ahp.json.decodeFromString(ser, inputJson)
                Decoded.JsonRpc("request") to Ahp.json.encodeToString(ser, v)
            }
            hasMethod && !hasId -> {
                val ser = JsonRpcNotification.serializer(pSer)
                val v = Ahp.json.decodeFromString(ser, inputJson)
                Decoded.JsonRpc("notification") to Ahp.json.encodeToString(ser, v)
            }
            else -> error("JsonRpcMessage: wire does not match any JSON-RPC 2.0 variant: $inputJson")
        }
    }

    // ── Variant identity (maps .NET concrete-type names → Kotlin cases) ──────

    private fun verifyVariant(file: String, decoded: Decoded, variants: JsonObject) {
        for ((accessor, wantEl) in variants) {
            val want = (wantEl as? JsonPrimitive)?.contentOrNull ?: continue
            val actual = if (accessor.isEmpty()) {
                wholeVariantTypeName(decoded)
            } else {
                namedAccessorVariantTypeName(decoded, accessor, file)
            }
            assertEquals(
                want,
                actual,
                "$file: expectVariant[\"$accessor\"] — active variant is $actual, expected $want",
            )
        }
    }

    /** Maps the active case of a top-level decoded union to the corpus's .NET concrete type name. */
    private fun wholeVariantTypeName(decoded: Decoded): String? = when (decoded) {
        is Decoded.Action -> stateActionVariantName(decoded.value)
        is Decoded.Custom -> customizationVariantName(decoded.value)
        is Decoded.ChangesetTarget -> changesetTargetVariantName(decoded.value)
        is Decoded.InputQuestion -> inputQuestionVariantName(decoded.value)
        is Decoded.StrOrMd -> when (decoded.value) {
            is StringOrMarkdown.Plain -> "String"
            is StringOrMarkdown.Markdown -> "MarkdownString"
        }
        else -> null
    }

    private fun namedAccessorVariantTypeName(decoded: Decoded, accessor: String, file: String): String? =
        when {
            decoded is Decoded.Envelope && accessor.equals("action", ignoreCase = true) ->
                stateActionVariantName(decoded.value.action)
            else -> error("$file: expectVariant accessor \"$accessor\" not wired for this decoded type")
        }

    private fun stateActionVariantName(a: StateAction): String? = when (a) {
        is StateActionSessionTitleChanged -> "SessionTitleChangedAction"
        is StateActionUnknown -> "JsonElement" // corpus name for the passthrough case
        else -> a::class.simpleName
            ?.removePrefix("StateAction")
            ?.let { it + "Action" }
    }

    private fun customizationVariantName(c: Customization): String? = when (c) {
        is CustomizationPlugin -> "PluginCustomization"
        is CustomizationDirectory -> "DirectoryCustomization"
        is CustomizationUnknown -> "JsonElement"
        else -> null
    }

    private fun changesetTargetVariantName(t: ChangesetOperationTarget): String = when (t) {
        is ChangesetOperationTarget.Resource -> "ChangesetOperationResourceTarget"
        is ChangesetOperationTarget.Range -> "ChangesetOperationRangeTarget"
    }

    private fun inputQuestionVariantName(q: SessionInputQuestion): String? = when (q) {
        is SessionInputQuestionText -> "SessionInputTextQuestion"
        // The corpus maps BOTH `number` and `integer` kinds to the same .NET
        // concrete type (SessionInputNumberQuestion); Kotlin wraps both wire
        // kinds in the single SessionInputQuestionNumber variant.
        is SessionInputQuestionNumber -> "SessionInputNumberQuestion"
        is SessionInputQuestionBoolean -> "SessionInputBooleanQuestion"
        is SessionInputQuestionSingleSelect -> "SessionInputSingleSelectQuestion"
        is SessionInputQuestionMultiSelect -> "SessionInputMultiSelectQuestion"
        else -> null
    }

    // ── JSON-RPC variant ─────────────────────────────────────────────────────

    private fun verifyJsonRpcVariant(file: String, decoded: Decoded, kind: String) {
        val actual = (decoded as? Decoded.JsonRpc)?.variant
            ?: error("$file: expectJsonRpcVariant requires a JsonRpcMessage wire type")
        val allowed = setOf("request", "notification", "success", "error")
        assertTrue(kind in allowed, "$file: expectJsonRpcVariant \"$kind\" is not one of $allowed")
        assertEquals(kind, actual, "$file: expectJsonRpcVariant — decoded as $actual, expected $kind")
    }

    // ── Bitset ───────────────────────────────────────────────────────────────

    private fun verifyBitset(file: String, decoded: Decoded, reencoded: String, bitset: JsonObject) {
        val status = (decoded as? Decoded.Status)?.value
            ?: error("$file: expectBitset requires a SessionStatus")

        (bitset["has"] as? kotlinx.serialization.json.JsonArray)?.forEach { nameEl ->
            val name = (nameEl as? JsonPrimitive)?.contentOrNull ?: return@forEach
            val flag = statusFlag(name, file)
            assertTrue(
                flag in status,
                "$file: SessionStatus must have flag $name but does not (value ${status.rawValue})",
            )
        }
        (bitset["lacks"] as? kotlinx.serialization.json.JsonArray)?.forEach { nameEl ->
            val name = (nameEl as? JsonPrimitive)?.contentOrNull ?: return@forEach
            val flag = statusFlag(name, file)
            assertTrue(
                flag !in status,
                "$file: SessionStatus must NOT have flag $name but does (value ${status.rawValue})",
            )
        }
        (bitset["numeric"] as? JsonPrimitive)?.longOrNull?.let { want ->
            assertEquals(
                want,
                status.rawValue,
                "$file: SessionStatus numeric — got ${status.rawValue}, expected $want",
            )
            // The re-encoded wire form must be the same bare number.
            val reNum = (Ahp.json.parseToJsonElement(reencoded) as? JsonPrimitive)?.longOrNull
                ?: error("$file: SessionStatus must re-encode as a JSON number, got $reencoded")
            assertEquals(want, reNum, "$file: SessionStatus re-encoded numeric — got $reNum, expected $want")
        }
    }

    /** Maps a .NET SessionStatus flag name to the Kotlin bitset member. */
    private fun statusFlag(name: String, file: String): SessionStatus = when (name) {
        "Idle" -> SessionStatus.IDLE
        "Error" -> SessionStatus.ERROR
        "InProgress" -> SessionStatus.IN_PROGRESS
        "InputNeeded" -> SessionStatus.INPUT_NEEDED
        "IsRead" -> SessionStatus.IS_READ
        "IsArchived" -> SessionStatus.IS_ARCHIVED
        else -> error("$file: unknown SessionStatus flag \"$name\"")
    }

    // ── ProtocolVersion constants ────────────────────────────────────────────

    private fun verifyProtocolConstant(file: String, root: JsonObject) {
        val c = root["expectConstant"] as? JsonObject
            ?: error("$file: ProtocolVersion fixture missing expectConstant")
        var asserted = false

        (c["current"] as? JsonPrimitive)?.contentOrNull?.let { cur ->
            assertEquals("non-empty", cur, "$file: expectConstant.current must be \"non-empty\"")
            assertTrue(PROTOCOL_VERSION.isNotBlank(), "$file: PROTOCOL_VERSION must be non-empty")
            asserted = true
        }
        (c["supported"] as? JsonPrimitive)?.contentOrNull?.let { sup ->
            assertEquals("non-empty-list", sup, "$file: expectConstant.supported must be \"non-empty-list\"")
            assertTrue(SUPPORTED_PROTOCOL_VERSIONS.isNotEmpty(), "$file: SUPPORTED_PROTOCOL_VERSIONS must be non-empty")
            asserted = true
        }
        if ((c["firstSupportedEqualsCurrent"] as? JsonPrimitive)?.booleanOrNull == true) {
            val head = SUPPORTED_PROTOCOL_VERSIONS.firstOrNull()
                ?: error("$file: SUPPORTED_PROTOCOL_VERSIONS is empty")
            assertEquals(
                PROTOCOL_VERSION,
                head,
                "$file: first supported $head != current $PROTOCOL_VERSION",
            )
            asserted = true
        }
        assertTrue(asserted, "$file: ProtocolVersion fixture asserted no constant")
    }

    // ── Input bytes ──────────────────────────────────────────────────────────

    private fun readInputJson(file: String, root: JsonObject): String {
        val hasRaw = root.containsKey("wireRaw")
        val hasWire = root.containsKey("wire")
        require(hasRaw != hasWire) {
            "$file: exactly one of `wire` / `wireRaw` is required (wire=$hasWire, wireRaw=$hasRaw)."
        }
        return if (hasRaw) {
            (root["wireRaw"] as? JsonPrimitive)?.contentOrNull
                ?: error("$file: `wireRaw` is not a string")
        } else {
            // `wire` is an embedded JSON value; compact-serialize it.
            Ahp.json.encodeToString(JsonElement.serializer(), root["wire"]!!)
        }
    }

    // ── JSON path + equality ─────────────────────────────────────────────────

    /** Resolves a dotted path against a parsed JSON value. Empty path → the value itself. */
    private fun resolvePath(rootEl: JsonElement, path: String, file: String): JsonElement {
        if (path.isEmpty()) return rootEl
        var cur = rootEl
        for (seg in path.split(".")) {
            val obj = cur as? JsonObject
                ?: error("$file: path \"$path\" — segment \"$seg\" parent is not an object")
            cur = obj[seg] ?: error("$file: path \"$path\" — segment \"$seg\" not found")
        }
        return cur
    }

    private fun assertJsonEquals(want: JsonElement, got: JsonElement, ctx: String) {
        // Strings compare as strings; numbers numerically (so 0 == 0.0 and large
        // ints stay exact); bools as bools; null as null; objects/arrays as
        // canonical (key-order-independent) JSON.
        val wantP = want as? JsonPrimitive
        val gotP = got as? JsonPrimitive
        if (wantP != null && gotP != null) {
            if (wantP is JsonNull || gotP is JsonNull) {
                assertTrue(wantP is JsonNull && gotP is JsonNull, "$ctx — expected $want, got $got")
                return
            }
            // String (quoted) primitives.
            if (wantP.isString || gotP.isString) {
                assertEquals(wantP.contentOrNull, gotP.contentOrNull, "$ctx — string mismatch")
                return
            }
            // Boolean.
            val wantB = wantP.booleanOrNull
            val gotB = gotP.booleanOrNull
            if (wantB != null || gotB != null) {
                assertEquals(wantB, gotB, "$ctx — boolean mismatch")
                return
            }
            // Integral first (exact), else floating.
            val wantL = wantP.longOrNull
            val gotL = gotP.longOrNull
            if (wantL != null && gotL != null) {
                assertEquals(wantL, gotL, "$ctx — number mismatch")
                return
            }
            val wantD = wantP.doubleOrNull
            val gotD = gotP.doubleOrNull
            if (wantD != null && gotD != null) {
                assertEquals(wantD, gotD, "$ctx — number mismatch")
                return
            }
            assertEquals(wantP.content, gotP.content, "$ctx — primitive mismatch")
            return
        }
        // Objects / arrays — compare canonical JSON.
        assertEquals(canonical(want), canonical(got), "$ctx — structural mismatch")
    }

    /**
     * Compares two JSON documents structurally (key order independent, value
     * and key-presence sensitive). Used for `reencodes` / fixed-point checks.
     */
    private fun assertCanonicalEqual(lhs: String, rhs: String, ctx: String) {
        val lo = Ahp.json.parseToJsonElement(lhs)
        val ro = Ahp.json.parseToJsonElement(rhs)
        assertEquals(canonical(lo), canonical(ro), "$ctx\n  lhs: $lhs\n  rhs: $rhs")
    }

    /** Canonical string form: objects key-sorted recursively so key ORDER never matters. */
    private fun canonical(el: JsonElement): String = when (el) {
        is JsonObject -> el.entries
            .sortedBy { it.key }
            .joinToString(prefix = "{", postfix = "}", separator = ",") { (k, v) ->
                "\"$k\":${canonical(v)}"
            }
        is kotlinx.serialization.json.JsonArray -> el.joinToString(prefix = "[", postfix = "]", separator = ",") {
            canonical(it)
        }
        is JsonPrimitive ->
            if (el.isString) {
                "\"${el.content}\""
            } else {
                // Normalise integral doubles (0.0 → 0) so numeric equality holds
                // across encoders that emit `0` vs `0.0`.
                val asLong = el.longOrNull
                if (asLong != null) asLong.toString() else el.content
            }
        JsonNull -> "null"
    }

    // ── Fixture directory (mirrors FixtureDrivenReducerTest) ─────────────────

    private fun fixtureFiles(): List<File> =
        fixtureDir().listFiles { f -> f.isFile && f.name.endsWith(".json") }
            ?.sortedBy { it.name }
            ?: emptyList()

    private fun fixtureDir(): File {
        val fromProperty = System.getProperty("ahp.roundTripFixturesDir")?.let(::File)
        if (fromProperty != null) {
            assertTrue(
                fromProperty.isDirectory,
                "ahp.roundTripFixturesDir points to '${fromProperty.path}' which is not a directory",
            )
            return fromProperty
        }
        val cwd = File(System.getProperty("user.dir") ?: ".").absoluteFile
        var dir: File? = cwd
        while (dir != null) {
            val candidate = File(dir, "types/test-cases/round-trips")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        error(
            "Could not locate the round-trip fixtures directory. Set the " +
                "'ahp.roundTripFixturesDir' system property (Gradle does this automatically), or run tests " +
                "from somewhere inside the repo checkout containing 'types/test-cases/round-trips/'. " +
                "Searched upward from '${cwd.path}'.",
        )
    }
}
