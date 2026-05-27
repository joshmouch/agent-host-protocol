package com.microsoft.agenthostprotocol

import kotlinx.serialization.json.Json

/**
 * Top-level entry point for the Agent Host Protocol Kotlin client.
 *
 * Exposes a [Json] instance pre-configured for AHP wire-format compatibility.
 * Consumers should use [Ahp.json] (or build their own [Json] with the same
 * settings) when encoding/decoding protocol messages — particularly
 * [com.microsoft.agenthostprotocol.generated.StateAction] and other
 * discriminated unions whose custom serializers require a JSON-aware
 * encoder/decoder.
 */
public object Ahp {
    /**
     * AHP-tuned JSON serializer.
     *
     * - `ignoreUnknownKeys = true` — server may add new fields in future
     *   protocol versions; clients must not break when they appear.
     * - `encodeDefaults = true` — required so action data classes emit their
     *   discriminator `type` field even when it equals the data class's
     *   default value (matching the wire format of the Swift / TS clients).
     * - `explicitNulls = false` — combined with `encodeDefaults = true`,
     *   this skips serializing nullable optional fields whose value is
     *   `null` (e.g. `meta`, `editedToolInput`), matching Swift's
     *   `JSONEncoder` default of omitting nil values.
     * - `classDiscriminator = "_ahp_kotlin_default_unused"` — discriminator
     *   handling for AHP discriminated unions is implemented by per-union
     *   custom serializers (see `StateAction`, `ResponsePart`, etc.). The
     *   kotlinx default of `"type"` would clash with real `type` fields on
     *   generated data classes; setting it to a sentinel value avoids any
     *   accidental polymorphic-mode collision.
     */
    public val json: Json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
        classDiscriminator = "_ahp_kotlin_default_unused"
    }
}

