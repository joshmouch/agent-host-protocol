// AHP Kotlin Conformance Runner — build-phase B5.
//
// Drives the REAL Kotlin client (reducer + types + KSerializer) against the
// scenario-driven host (B3, `conformance/host/scenario-host.mjs`) over a REAL
// WebSocket, and checks every `client.assert.*` step in each scenario file.
//
// NO MOCKS: real host subprocess, real ws transport, real reducers, real JSON
// codec, real assertions. (CROSS-SPEC-INTENT-VERIFIED-BY-REAL-EXECUTION + ADR-067/072.)
//
// ── Architecture ────────────────────────────────────────────────────────────
//   • @TestFactory generates one JUnit 5 DynamicTest per scenario in the tranche.
//   • For each scenario:
//       1. Pin the client timestamp to scenario.pinClock (same determinism
//          contract as the JS/TS runner).
//       2. Spawn `node scenario-host.mjs <scenario.json>` via ProcessBuilder;
//          parse the `SCENARIO HOST READY ws://127.0.0.1:<port>` line.
//       3. Open a java.net.http.WebSocket; replay client.request steps; collect
//          all incoming frames; fold server.notify action envelopes through the
//          Kotlin reducers; accumulate surfaced errors.
//       4. Check every client.assert.state | client.assert.event | client.assert.error
//          step using the SAME convergence rules as the JS runner (canonicalize:
//          drop null-valued keys, sort keys; deepEqual / deepContains).
//
// ── Reducer dispatch ────────────────────────────────────────────────────────
//   By action-type PREFIX (root/ session/ terminal/ changeset/ resource/),
//   not channel scheme — mirrors run-conformance.mjs §"Reducer dispatch" note.
//
// ── Convergence equality ────────────────────────────────────────────────────
//   Encode final state → JsonElement; encode expected state through the same
//   KSerializer (normalises null/optional parity); then canonicalize both
//   (drop null-valued keys, sort keys) and compare. This is the exact same rule
//   as FixtureDrivenReducerTest.compareFixture and the JS runner's canonicalize().

package com.microsoft.agenthostprotocol.conformance

import com.microsoft.agenthostprotocol.Ahp
import com.microsoft.agenthostprotocol.changesetReducer
import com.microsoft.agenthostprotocol.currentTimestampProvider
import com.microsoft.agenthostprotocol.resourceWatchReducer
import com.microsoft.agenthostprotocol.rootReducer
import com.microsoft.agenthostprotocol.sessionReducer
import com.microsoft.agenthostprotocol.terminalReducer
import com.microsoft.agenthostprotocol.generated.ChangesetState
import com.microsoft.agenthostprotocol.generated.ResourceWatchState
import com.microsoft.agenthostprotocol.generated.RootState
import com.microsoft.agenthostprotocol.generated.SessionState
import com.microsoft.agenthostprotocol.generated.StateAction
import com.microsoft.agenthostprotocol.generated.TerminalState
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.TestFactory
import org.junit.jupiter.api.fail
import java.io.File
import java.net.URI
import java.net.http.HttpClient
import java.net.http.WebSocket
import java.util.TreeMap
import java.util.concurrent.CompletionStage
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

// ── Configuration (from system properties set by build.gradle.kts) ──────────

private val SCENARIO_HOST_SCRIPT: String =
    System.getProperty("ahp.scenarioHostScript")
        ?: error("System property ahp.scenarioHostScript not set")

private val SCENARIOS_ROOT: File =
    File(
        System.getProperty("ahp.scenariosRoot")
            ?: error("System property ahp.scenariosRoot not set"),
    )

private val NODE_EXECUTABLE: String =
    System.getProperty("ahp.nodeExecutable") ?: "node"

private val TRANCHE: String =
    System.getProperty("ahp.tranche") ?: "brief"

private val SCENARIO_TIMEOUT_MS: Long =
    System.getProperty("ahp.scenarioTimeoutMs")?.toLongOrNull() ?: 10_000L

// ── JSON codec ───────────────────────────────────────────────────────────────

private val json: Json = Ahp.json

// ── Scenario collection ──────────────────────────────────────────────────────

private fun listScenarios(dir: File): List<File> =
    (dir.listFiles { f -> f.isFile && f.name.endsWith(".scenario.json") }
        ?: emptyArray())
        .sortedBy { it.name }

/** Deterministic stride-sample of [n] items from [list], spanning the whole list. */
private fun <T> strideSample(list: List<T>, n: Int): List<T> {
    if (n >= list.size) return list.toList()
    val out = mutableListOf<T>()
    val stride = list.size.toDouble() / n
    for (i in 0 until n) out.add(list[(i * stride).toInt()])
    val seen = mutableSetOf<T>()
    val deduped = mutableListOf<T>()
    for (item in out) if (seen.add(item)) deduped.add(item)
    @Suppress("UNCHECKED_CAST")
    return (deduped.take(n) as List<File>).sortedBy { it.name } as List<T>
}

private fun buildTranche(): List<File> {
    val roundTrips = listScenarios(File(SCENARIOS_ROOT, "round-trips"))
    val negatives = listScenarios(File(SCENARIOS_ROOT, "negatives"))
    val allReducers = listScenarios(File(SCENARIOS_ROOT, "reducers"))
    val reducers: List<File> = if (TRANCHE == "full") allReducers else strideSample(allReducers, 30)
    return roundTrips + reducers + negatives
}

// ── Reducer dispatch by action-type prefix ───────────────────────────────────

private enum class ReducerKind { ROOT, SESSION, TERMINAL, CHANGESET, RESOURCE }

private fun reducerKindForActionType(type: String): ReducerKind? = when {
    type.startsWith("root/") -> ReducerKind.ROOT
    type.startsWith("session/") -> ReducerKind.SESSION
    type.startsWith("terminal/") -> ReducerKind.TERMINAL
    type.startsWith("changeset/") -> ReducerKind.CHANGESET
    type.startsWith("resource/") -> ReducerKind.RESOURCE
    else -> null
}

private fun reducerKindForChannelUri(uri: String): ReducerKind? = when {
    uri.startsWith("ahp-root:") -> ReducerKind.ROOT
    uri.startsWith("ahp-session:") -> ReducerKind.SESSION
    uri.startsWith("ahp-terminal:") -> ReducerKind.TERMINAL
    uri.startsWith("ahp-changeset:") -> ReducerKind.CHANGESET
    uri.startsWith("ahp-resource:") || uri.startsWith("ahp-watch:") -> ReducerKind.RESOURCE
    else -> null
}

// ── Clock pin ────────────────────────────────────────────────────────────────

private fun pinClock(epochMs: Long?) {
    if (epochMs != null) currentTimestampProvider = { epochMs }
}

private fun restoreClock() {
    currentTimestampProvider = { System.currentTimeMillis() }
}

// ── Per-channel reduced state ─────────────────────────────────────────────────
//
// IMPORTANT: The corpus routes terminal-reducer fixtures onto `ahp-session:/…`
// channels (the scenario generator has no terminal-channel entry). When seeding
// a snapshot we do not know whether the raw JSON is a TerminalState or a
// SessionState yet — we find out only when the first action arrives (by its
// type prefix). To handle this correctly we mirror the JS runner: we store the
// raw snapshot JsonElement alongside the typed state so that if the first
// action's reducer kind disagrees with how we initially decoded the snapshot,
// we can re-decode from the raw element rather than from an empty default.

private sealed class ChannelState {
    /** Raw snapshot element retained for cross-reducer re-decode on first action. */
    abstract val rawSnapshot: JsonElement?

    data class Root(val state: RootState, override val rawSnapshot: JsonElement? = null) : ChannelState()
    data class Session(val state: SessionState, override val rawSnapshot: JsonElement? = null) : ChannelState()
    data class Terminal(val state: TerminalState, override val rawSnapshot: JsonElement? = null) : ChannelState()
    data class Changeset(val state: ChangesetState, override val rawSnapshot: JsonElement? = null) : ChannelState()
    data class ResourceWatch(val state: ResourceWatchState, override val rawSnapshot: JsonElement? = null) : ChannelState()
    /**
     * Snapshot whose JSON could not be decoded to a typed state with the initial
     * kind guess (e.g. TerminalState JSON seeded onto an ahp-session: channel).
     * The raw element is kept so [applyAction] can re-decode with the correct
     * serializer once the action type reveals the true reducer kind.
     */
    data class Raw(override val rawSnapshot: JsonElement) : ChannelState()
}

private fun encodeChannelState(cs: ChannelState): JsonElement = when (cs) {
    is ChannelState.Root -> json.encodeToJsonElement(RootState.serializer(), cs.state)
    is ChannelState.Session -> json.encodeToJsonElement(SessionState.serializer(), cs.state)
    is ChannelState.Terminal -> json.encodeToJsonElement(TerminalState.serializer(), cs.state)
    is ChannelState.Changeset -> json.encodeToJsonElement(ChangesetState.serializer(), cs.state)
    is ChannelState.ResourceWatch -> json.encodeToJsonElement(ResourceWatchState.serializer(), cs.state)
    // A Raw channel has never had a reducer applied — return the raw JSON directly.
    is ChannelState.Raw -> cs.rawSnapshot
}

/**
 * Decode a snapshot state element into the correct [ChannelState] variant.
 * Uses the channel URI prefix as the initial guess; the raw element is retained
 * for re-decoding when the first action's prefix disagrees.
 *
 * If the initial decode fails (the JSON shape doesn't match the channel-prefix
 * guess, e.g. TerminalState JSON seeded onto an ahp-session:/ channel), the
 * snapshot is stored as [ChannelState.Raw] so the action path can re-decode it.
 */
private fun decodeSnapshotState(stateEl: JsonElement, kind: ReducerKind?): ChannelState = try {
    when (kind) {
        ReducerKind.ROOT -> ChannelState.Root(json.decodeFromJsonElement(RootState.serializer(), stateEl), stateEl)
        ReducerKind.TERMINAL -> ChannelState.Terminal(json.decodeFromJsonElement(TerminalState.serializer(), stateEl), stateEl)
        ReducerKind.CHANGESET -> ChannelState.Changeset(json.decodeFromJsonElement(ChangesetState.serializer(), stateEl), stateEl)
        ReducerKind.RESOURCE -> ChannelState.ResourceWatch(json.decodeFromJsonElement(ResourceWatchState.serializer(), stateEl), stateEl)
        // SESSION or null: try Session first; fall through to Raw on failure.
        else -> ChannelState.Session(json.decodeFromJsonElement(SessionState.serializer(), stateEl), stateEl)
    }
} catch (_: Exception) {
    // Initial decode failed (cross-reducer scenario: channel URI prefix disagrees
    // with actual JSON shape). Store raw so the action path can re-decode.
    ChannelState.Raw(stateEl)
}

/**
 * Apply a decoded [StateAction] to the current per-channel state.
 *
 * When the stored [ChannelState] type disagrees with [kind] (e.g. the channel
 * was seeded as Session but the first action is terminal/X), re-decode the
 * retained [ChannelState.rawSnapshot] element with the correct serializer.
 * This is the same cross-reducer path the JS runner takes implicitly (it stores
 * raw JSON and lets each reducer handle the initial undefined-state seed).
 */
private fun applyAction(current: ChannelState?, action: StateAction, kind: ReducerKind): ChannelState {
    return when (kind) {
        ReducerKind.ROOT -> {
            val prev: RootState = when (current) {
                is ChannelState.Root -> current.state
                else -> {
                    val raw = current?.rawSnapshot
                    if (raw != null) json.decodeFromJsonElement(RootState.serializer(), raw)
                    else json.decodeFromJsonElement(RootState.serializer(), JsonObject(emptyMap()))
                }
            }
            ChannelState.Root(rootReducer(prev, action))
        }
        ReducerKind.SESSION -> {
            val prev: SessionState = when (current) {
                is ChannelState.Session -> current.state
                else -> {
                    val raw = current?.rawSnapshot
                    if (raw != null) json.decodeFromJsonElement(SessionState.serializer(), raw)
                    else json.decodeFromJsonElement(SessionState.serializer(), JsonObject(emptyMap()))
                }
            }
            ChannelState.Session(sessionReducer(prev, action))
        }
        ReducerKind.TERMINAL -> {
            val prev: TerminalState = when (current) {
                is ChannelState.Terminal -> current.state
                else -> {
                    // This is the cross-reducer case: ahp-session:/… channel seeded as
                    // Session but first action is terminal/X. Re-decode from raw.
                    val raw = current?.rawSnapshot
                    if (raw != null) json.decodeFromJsonElement(TerminalState.serializer(), raw)
                    else json.decodeFromJsonElement(TerminalState.serializer(), JsonObject(emptyMap()))
                }
            }
            ChannelState.Terminal(terminalReducer(prev, action))
        }
        ReducerKind.CHANGESET -> {
            val prev: ChangesetState = when (current) {
                is ChannelState.Changeset -> current.state
                else -> {
                    val raw = current?.rawSnapshot
                    if (raw != null) json.decodeFromJsonElement(ChangesetState.serializer(), raw)
                    else json.decodeFromJsonElement(ChangesetState.serializer(), JsonObject(emptyMap()))
                }
            }
            ChannelState.Changeset(changesetReducer(prev, action))
        }
        ReducerKind.RESOURCE -> {
            val prev: ResourceWatchState = when (current) {
                is ChannelState.ResourceWatch -> current.state
                else -> {
                    val raw = current?.rawSnapshot
                    if (raw != null) json.decodeFromJsonElement(ResourceWatchState.serializer(), raw)
                    else json.decodeFromJsonElement(ResourceWatchState.serializer(), JsonObject(emptyMap()))
                }
            }
            ChannelState.ResourceWatch(resourceWatchReducer(prev, action))
        }
    }
}

// ── Deep equality and containment (JSON-element level) ───────────────────────

/**
 * Canonicalize a [JsonElement]: recursively drop null-valued object keys and
 * sort keys. Mirrors run-conformance.mjs's canonicalize().
 */
private fun canonicalize(el: JsonElement): JsonElement = when (el) {
    is JsonNull -> el
    is JsonPrimitive -> el
    is JsonArray -> JsonArray(el.map { canonicalize(it) })
    is JsonObject -> {
        val out = TreeMap<String, JsonElement>()
        for ((k, v) in el) {
            if (v is JsonNull) continue
            out[k] = canonicalize(v)
        }
        JsonObject(out)
    }
}

/**
 * Deep-containment: every key in [expected] must be present in [actual] with a
 * matching value (recursively). Extra keys in [actual] are ignored.
 * Arrays compare element-wise with the same containment rule.
 * Mirrors run-conformance.mjs's deepContains().
 */
private fun deepContains(actual: JsonElement, expected: JsonElement): Boolean {
    if (expected is JsonNull || expected is JsonPrimitive) return actual == expected
    if (actual is JsonNull || actual is JsonPrimitive) return false
    if (expected is JsonArray) {
        if (actual !is JsonArray) return false
        if (actual.size != expected.size) return false
        return actual.zip(expected).all { (a, e) -> deepContains(a, e) }
    }
    if (expected is JsonObject) {
        if (actual !is JsonObject) return false
        for ((k, ev) in expected) {
            val av = actual[k] ?: return false
            if (!deepContains(av, ev)) return false
        }
        return true
    }
    return false
}

/** Navigate a dotted path through a JsonElement. Returns null if not found. */
private fun navigate(el: JsonElement, path: String): JsonElement? {
    if (path.isEmpty()) return el
    var cur: JsonElement = el
    for (seg in path.split('.')) {
        cur = when {
            cur is JsonObject -> cur[seg] ?: return null
            cur is JsonArray && seg.all { it.isDigit() } -> cur.getOrNull(seg.toInt()) ?: return null
            else -> return null
        }
    }
    return cur
}

// ── Host process ─────────────────────────────────────────────────────────────

private data class HostProcess(val process: Process, val wsUrl: String)

/**
 * Spawn `node scenario-host.mjs <scenarioPath>` and wait for the
 * `SCENARIO HOST READY ws://127.0.0.1:<port>` line on stdout.
 */
private fun startHost(scenarioFile: File, timeoutMs: Long = 8_000L): HostProcess {
    val pb = ProcessBuilder(NODE_EXECUTABLE, SCENARIO_HOST_SCRIPT, scenarioFile.absolutePath)
    pb.redirectErrorStream(false)
    val proc = pb.start()

    val reader = proc.inputStream.bufferedReader()
    val deadline = System.currentTimeMillis() + timeoutMs
    val urlRegex = Regex("""SCENARIO HOST READY (ws://127\.0\.0\.1:\d+)""")
    while (System.currentTimeMillis() < deadline) {
        if (!proc.isAlive && proc.exitValue() != 0) {
            val stderr = proc.errorStream.bufferedReader().readText()
            throw IllegalStateException("host exited with code ${proc.exitValue()} before READY.\nstderr: $stderr")
        }
        val line = reader.readLine() ?: run {
            Thread.sleep(5)
            continue
        }
        val m = urlRegex.find(line)
        if (m != null) return HostProcess(proc, m.groupValues[1])
    }
    proc.destroyForcibly()
    throw IllegalStateException("host did not print READY within ${timeoutMs}ms")
}

// ── Drive result ─────────────────────────────────────────────────────────────

private class DriveResult(
    val channels: Map<String, ChannelState>,
    val synthetic: Map<String, JsonElement>,
    val observedEvents: List<JsonElement>,
    val surfacedErrors: List<JsonObject>,
    val warnings: List<String>,
)

// ── WebSocket driver ──────────────────────────────────────────────────────────

/**
 * Connect to [wsUrl], replay the scenario's client.request steps, collect all
 * frames, fold action notifications through reducers.
 */
private fun driveProtocol(
    wsUrl: String,
    scenarioObj: JsonObject,
    timeoutMs: Long = SCENARIO_TIMEOUT_MS,
): DriveResult {
    val steps = scenarioObj["steps"]?.jsonArray ?: JsonArray(emptyList())
    val clientRequests: List<JsonObject> = steps
        .map { it.jsonObject }
        .filter { it["op"]?.jsonPrimitive?.contentOrNull == "client.request" }

    val channels = mutableMapOf<String, ChannelState>()
    val synthetic = mutableMapOf<String, JsonElement>()
    val observedEvents = mutableListOf<JsonElement>()
    val surfacedErrors = mutableListOf<JsonObject>()
    val warnings = mutableListOf<String>()
    val requestCursor = AtomicInteger(0)

    val doneLatch = CountDownLatch(1)
    val settled = AtomicBoolean(false)
    // Text frames may arrive in chunks (last=false); accumulate until last=true.
    val frameBuf = StringBuilder()

    fun finish() {
        if (settled.compareAndSet(false, true)) doneLatch.countDown()
    }

    val httpClient = HttpClient.newHttpClient()
    var wsRef: java.net.http.WebSocket? = null

    fun sendNextRequest(ws: java.net.http.WebSocket) {
        val idx = requestCursor.getAndIncrement()
        if (idx >= clientRequests.size) return
        val step = clientRequests[idx]
        val frameMap = mutableMapOf<String, JsonElement>()
        frameMap["jsonrpc"] = JsonPrimitive("2.0")
        step["method"]?.let { frameMap["method"] = it }
        step["id"]?.let { if (it !is JsonNull) frameMap["id"] = it }
        step["params"]?.let { if (it !is JsonNull) frameMap["params"] = it }
        ws.sendText(JsonObject(frameMap).toString(), true)
    }

    fun handleIncomingFrame(raw: String, ws: java.net.http.WebSocket) {
        val msg: JsonObject = try {
            json.parseToJsonElement(raw).jsonObject
        } catch (_: Exception) { return }

        val id = msg["id"]
        val result = msg["result"]
        val error = msg["error"]
        val method = msg["method"]

        if (id != null && id !is JsonNull && (result != null || error != null)) {
            // JSON-RPC response.
            if (error != null && error !is JsonNull) {
                surfacedErrors.add(error.jsonObject)
                synthetic["lastResponseOk"] = JsonPrimitive(false)
            } else if (result != null) {
                synthetic["lastResponseOk"] = JsonPrimitive(true)
                if (result !is JsonNull) {
                    seedFromResult(result.jsonObject, channels, warnings)
                    (result.jsonObject["protocolVersion"] as? JsonPrimitive)?.contentOrNull
                        ?.let { synthetic["protocolVersion"] = JsonPrimitive(it) }
                }
            }
            sendNextRequest(ws)
        } else if (method != null && method !is JsonNull && (id == null || id is JsonNull)) {
            // Server notification.
            observedEvents.add(msg)
            val methodStr = method.jsonPrimitive.contentOrNull
            if (methodStr == "action") {
                val params = msg["params"]
                if (params != null && params !is JsonNull) {
                    observedEvents.add(params)
                    applyActionNotification(params.jsonObject, channels, warnings)
                }
            }
        }
    }

    val listener = object : WebSocket.Listener {
        override fun onOpen(webSocket: java.net.http.WebSocket) {
            wsRef = webSocket
            webSocket.request(1)
            if (clientRequests.isNotEmpty()) sendNextRequest(webSocket)
        }

        override fun onText(
            webSocket: java.net.http.WebSocket,
            data: CharSequence,
            last: Boolean,
        ): CompletionStage<*>? {
            frameBuf.append(data)
            if (last) {
                val text = frameBuf.toString()
                frameBuf.clear()
                handleIncomingFrame(text, webSocket)
            }
            webSocket.request(1)
            return null
        }

        override fun onClose(webSocket: java.net.http.WebSocket, statusCode: Int, reason: String): CompletionStage<*>? {
            finish()
            return null
        }

        override fun onError(webSocket: java.net.http.WebSocket, error: Throwable) {
            warnings.add("websocket error: ${error.message}")
            finish()
        }
    }

    // Build the WebSocket and wait for the handshake (onOpen fires synchronously
    // on the HttpClient dispatcher thread once the upgrade completes).
    httpClient.newWebSocketBuilder()
        .buildAsync(URI.create(wsUrl), listener)
        .join()

    // Soft timeout: some scenarios leave the socket open after the last expected frame.
    doneLatch.await(timeoutMs, TimeUnit.MILLISECONDS)
    finish()
    try { wsRef?.abort() } catch (_: Exception) {}

    return DriveResult(channels, synthetic, observedEvents, surfacedErrors, warnings)
}

private fun seedFromResult(
    result: JsonObject,
    channels: MutableMap<String, ChannelState>,
    warnings: MutableList<String>,
) {
    // Multi-snapshot response (initialize result).
    (result["snapshots"] as? JsonArray)?.forEach { snap ->
        val snapObj = snap.jsonObject
        val resource = (snapObj["resource"] as? JsonPrimitive)?.contentOrNull ?: return@forEach
        val stateEl = snapObj["state"] ?: return@forEach
        val kind = reducerKindForChannelUri(resource)
        channels[resource] = decodeSnapshotState(stateEl, kind)
    }
    // Reconnect shape: result.snapshot (singular).
    (result["snapshot"] as? JsonObject)?.let { snapObj ->
        val resource = (snapObj["resource"] as? JsonPrimitive)?.contentOrNull ?: return
        val stateEl = snapObj["state"] ?: return
        val kind = reducerKindForChannelUri(resource)
        channels[resource] = decodeSnapshotState(stateEl, kind)
    }
}

private fun applyActionNotification(
    params: JsonObject,
    channels: MutableMap<String, ChannelState>,
    warnings: MutableList<String>,
) {
    val channel = (params["channel"] as? JsonPrimitive)?.contentOrNull ?: return
    val actionEl = params["action"] ?: return
    if (actionEl is JsonNull) return
    val actionObj = actionEl.jsonObject
    val type = (actionObj["type"] as? JsonPrimitive)?.contentOrNull ?: return

    val kind = reducerKindForActionType(type) ?: return
    val action: StateAction = try {
        json.decodeFromJsonElement(StateAction.serializer(), actionEl)
    } catch (_: Exception) {
        // Non-decodable action (forward-compat) — event already recorded.
        return
    }

    val current = channels[channel]
    val hadPrev = current != null
    try {
        channels[channel] = applyAction(current, action, kind)
    } catch (e: Exception) {
        if (hadPrev) {
            warnings.add("reducer for $type on channel $channel threw with seeded state: ${e.message}")
        }
        // else: event-only scenario (no snapshot seeded), ignore fold error.
    }
}

// ── Assertion evaluation ─────────────────────────────────────────────────────

private data class AssertResult(val ok: Boolean, val detail: String = "")

private fun checkAssertion(step: JsonObject, result: DriveResult): AssertResult {
    val op = step["op"]?.jsonPrimitive?.contentOrNull ?: return AssertResult(false, "missing op")

    when (op) {
        "client.assert.state" -> {
            val channel = step["channel"]?.jsonPrimitive?.contentOrNull
            val path = step["path"]?.jsonPrimitive?.contentOrNull ?: ""
            val expected = step["equals"] ?: JsonNull

            val (target: JsonElement, bucketLabel: String) = when {
                channel != null -> {
                    val cs = result.channels[channel]
                        ?: return AssertResult(false, "no reduced state for channel $channel; known: [${result.channels.keys.joinToString()}]")
                    encodeChannelState(cs) to "channel $channel"
                }
                path.isNotEmpty() -> {
                    // Path with no channel → synthetic top-level state.
                    JsonObject(result.synthetic) to "synthetic top-level state"
                }
                else -> {
                    // No channel, no path → whole-state convergence over single channel.
                    when (result.channels.size) {
                        0 -> return AssertResult(false, "no channels reduced; whole-state convergence impossible")
                        1 -> {
                            val (k, v) = result.channels.entries.first()
                            encodeChannelState(v) to "the single channel ($k)"
                        }
                        else -> return AssertResult(false, "whole-state assertion with ${result.channels.size} channels; specify channel explicitly")
                    }
                }
            }

            val actual: JsonElement? = if (path.isNotEmpty()) navigate(target, path) else target
            if (actual == null) {
                // For synthetic paths, treat absent == null.
                return if (bucketLabel == "synthetic top-level state" && expected is JsonNull) {
                    AssertResult(true)
                } else {
                    AssertResult(false, "path '$path' not found in $bucketLabel")
                }
            }

            val actualCanon = canonicalize(actual)
            val expectedCanon = canonicalize(expected)
            return if (actualCanon == expectedCanon) {
                AssertResult(true)
            } else {
                AssertResult(
                    false,
                    "$op @ $bucketLabel${if (path.isNotEmpty()) " path '$path'" else " (whole state)"}: expected ${prettyPrint(expectedCanon)}, got ${prettyPrint(actualCanon)}",
                )
            }
        }

        "client.assert.event" -> {
            val matches: JsonElement = step["matches"] ?: JsonObject(emptyMap())
            // Try event under several views, same as JS runner:
            //   • the event itself
            //   • event.action
            //   • event.params
            for (ev in result.observedEvents) {
                val views = buildList {
                    add(ev)
                    if (ev is JsonObject) {
                        ev["action"]?.takeIf { it !is JsonNull }?.let { add(it) }
                        ev["params"]?.takeIf { it !is JsonNull }?.let { add(it) }
                    }
                }
                for (view in views) {
                    if (deepContains(view, matches)) return AssertResult(true)
                }
            }
            return AssertResult(
                false,
                "$op: no observed event (or its .action/.params view) deep-contains $matches. observed ${result.observedEvents.size} event(s)",
            )
        }

        "client.assert.error" -> {
            val code = step["code"]?.jsonPrimitive?.intOrNull
                ?: return AssertResult(false, "assert.error missing code field")
            val msgSubstring = step["message"]?.jsonPrimitive?.contentOrNull

            for (err in result.surfacedErrors) {
                val errCode = err["code"]?.jsonPrimitive?.intOrNull ?: continue
                if (errCode != code) continue
                if (msgSubstring != null) {
                    val errMsg = err["message"]?.jsonPrimitive?.contentOrNull ?: ""
                    if (!errMsg.contains(msgSubstring)) continue
                }
                return AssertResult(true)
            }
            return AssertResult(
                false,
                "$op: no surfaced error with code $code${if (msgSubstring != null) " + message '$msgSubstring'" else ""}. surfaced: ${result.surfacedErrors}",
            )
        }

        else -> return AssertResult(false, "unknown assertion op: $op")
    }
}

// ── Pretty-print helper ───────────────────────────────────────────────────────

private fun prettyPrint(el: JsonElement): String =
    Json(Ahp.json) { prettyPrint = true }.encodeToString(JsonElement.serializer(), el)

// ── Main test factory ─────────────────────────────────────────────────────────

/**
 * JUnit 5 conformance suite.
 *
 * One [DynamicTest] per scenario in the tranche. Each test is self-contained:
 * it spawns the host, connects, drives, asserts, and tears down.
 */
class ScenarioConformanceTest {

    @TestFactory
    fun conformanceTranche(): List<DynamicTest> {
        val scenarios = buildTranche()
        check(scenarios.isNotEmpty()) { "No scenarios found under $SCENARIOS_ROOT" }

        println("[AHP-B5] Kotlin conformance tranche: ${scenarios.size} scenarios (TRANCHE=$TRANCHE)")
        println("[AHP-B5] Host script: $SCENARIO_HOST_SCRIPT")
        println("[AHP-B5] Node: $NODE_EXECUTABLE")

        return scenarios.map { scenarioFile ->
            DynamicTest.dynamicTest(scenarioFile.name) {
                runScenario(scenarioFile)
            }
        }
    }

    private fun runScenario(scenarioFile: File) {
        val scenarioObj: JsonObject = try {
            json.parseToJsonElement(scenarioFile.readText()).jsonObject
        } catch (e: Exception) {
            fail("${scenarioFile.name}: failed to parse scenario JSON: ${e.message}")
        }

        val pinClockMs = (scenarioObj["pinClock"] as? JsonPrimitive)?.longOrNull
        pinClock(pinClockMs)
        try {
            runScenarioInner(scenarioFile, scenarioObj)
        } finally {
            restoreClock()
        }
    }

    private fun runScenarioInner(scenarioFile: File, scenarioObj: JsonObject) {
        val steps = scenarioObj["steps"]?.jsonArray ?: JsonArray(emptyList())
        val assertSteps: List<JsonObject> = steps
            .map { it.jsonObject }
            .filter { it["op"]?.jsonPrimitive?.contentOrNull?.startsWith("client.assert.") == true }

        if (assertSteps.isEmpty()) {
            fail("${scenarioFile.name}: no client.assert.* steps — scenario is vacuous")
        }

        val host: HostProcess = try {
            startHost(scenarioFile)
        } catch (e: Exception) {
            fail("${scenarioFile.name}: failed to start host: ${e.message}")
        }

        val driveResult: DriveResult
        try {
            driveResult = try {
                driveProtocol(host.wsUrl, scenarioObj)
            } catch (e: Exception) {
                fail("${scenarioFile.name}: failed to drive protocol: ${e.message}")
            }
        } finally {
            host.process.destroyForcibly()
        }

        for (w in driveResult.warnings) {
            println("[AHP-B5 WARN] ${scenarioFile.name}: $w")
        }

        val failures = mutableListOf<String>()
        for (step in assertSteps) {
            val label = step["label"]?.jsonPrimitive?.contentOrNull ?: ""
            val res = checkAssertion(step, driveResult)
            if (!res.ok) {
                failures.add("  FAIL  ${step["op"]?.jsonPrimitive?.contentOrNull}  $label\n    → ${res.detail}")
            }
        }

        if (failures.isNotEmpty()) {
            fail(
                "${scenarioFile.name} FAILED (${failures.size}/${assertSteps.size} assertions):\n${failures.joinToString("\n")}",
            )
        }
    }
}
