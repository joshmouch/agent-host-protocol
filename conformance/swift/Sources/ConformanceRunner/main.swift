// AHP HOST-CONFORMANCE RUNNER — build-phase B5, Swift per-client runner.
//
// Ports the B4 (JS) replay loop to Swift:
//   • Spawns the scenario-driven host (scenario-host.mjs) per scenario.
//   • Opens a REAL URLSessionWebSocketTask connection.
//   • Replays client.request steps (JSON-RPC frames) in order.
//   • Seeds per-channel state from server.response snapshots.
//   • Applies server.notify ActionEnvelope frames through the REAL Swift
//     reducers (rootReducer / sessionReducer / terminalReducer from the
//     AgentHostProtocol package, plus inline changeset + resourceWatch
//     reducers whose action-type logic mirrors the TS originals).
//   • Checks every client.assert.state | event | error step.
//   • Prints a summary and exits 0 (all pass) or 1 (any fail/error).
//
// NO MOCKS. Real transport, real host subprocess, real reducers.
// (CROSS-SPEC-INTENT-VERIFIED-BY-REAL-EXECUTION + ADR-067/072.)
//
// Usage:
//   swift run --package-path conformance/swift ConformanceRunner [<scenario.json>…] [--verbose]
//   (with no arguments: runs all round-trips + reducers + negatives)

import Foundation
import AgentHostProtocol

// MARK: - Scenario schema ─────────────────────────────────────────────────────

struct Scenario: Decodable {
    let id: String
    let pinClock: Int?
    let steps: [Step]
}

struct Step: Decodable {
    let op: String
    let label: String?
    let method: String?
    let id: Int?
    let params: AnyJSON?
    let forId: Int?
    let result: AnyJSON?
    let error: AnyJSON?
    let channel: String?
    let equals: AnyJSON?
    let path: String?
    let matches: AnyJSON?
    let code: Int?
    let message: String?
}

// MARK: - AnyJSON ─────────────────────────────────────────────────────────────
// A type-erased JSON value (null | bool | number | string | array | object).

enum AnyJSON: Codable, Equatable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([AnyJSON])
    case object([String: AnyJSON])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null; return }
        if let b = try? c.decode(Bool.self) { self = .bool(b); return }
        if let i = try? c.decode(Int.self)  { self = .int(i);  return }
        if let d = try? c.decode(Double.self) { self = .double(d); return }
        if let s = try? c.decode(String.self) { self = .string(s); return }
        if let a = try? c.decode([AnyJSON].self) { self = .array(a); return }
        if let o = try? c.decode([String: AnyJSON].self) { self = .object(o); return }
        throw DecodingError.dataCorruptedError(in: c, debugDescription: "AnyJSON: unrecognized type")
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null:             try c.encodeNil()
        case .bool(let v):      try c.encode(v)
        case .int(let v):       try c.encode(v)
        case .double(let v):    try c.encode(v)
        case .string(let v):    try c.encode(v)
        case .array(let v):     try c.encode(v)
        case .object(let v):    try c.encode(v)
        }
    }

    // Convert to plain Foundation Any (for comparison helpers below).
    var asAny: Any {
        switch self {
        case .null:          return NSNull()
        case .bool(let v):   return v
        case .int(let v):    return v
        case .double(let v): return v
        case .string(let v): return v
        case .array(let v):  return v.map { $0.asAny }
        case .object(let v): return v.mapValues { $0.asAny }
        }
    }
}

// MARK: - Clock pin ───────────────────────────────────────────────────────────

func pinClock(_ epochMs: Int?) {
    guard let ms = epochMs else { return }
    currentTimestampProvider = { ms }
}

// MARK: - Reducer dispatch ────────────────────────────────────────────────────
// Route by action-type prefix — same contract as the B4 JS runner.

/// Channel-keyed raw JSON state (for channels whose reducer is not implemented
/// in the Swift library — changeset and resourceWatch). We apply the action
/// by decoding the typed Swift action, running our inline reducer, then
/// re-encoding back to AnyJSON for comparison.
struct ChannelState {
    enum Value {
        case root(RootState)
        case session(SessionState)
        case terminal(TerminalState)
        case changeset(ChangesetStateJSON)   // raw JSON bag; inline reducer
        case resourceWatch(AnyJSON)          // passthrough — state unchanged
        case unknown(AnyJSON)               // seeded but no reducer
    }
    var value: Value
}

// ─── Inline changeset reducer ────────────────────────────────────────────────
// The Swift AgentHostProtocol library does not export a standalone
// changesetReducer function, so we implement the subset used by the corpus
// here, using the decoded Swift types for full fidelity.

struct ChangesetStateJSON {
    var raw: AnyJSON   // the JSON we seeded from (for serialization back to AnyJSON)
    // Fully parsed fields for reduction:
    var status: String
    var error: [String: AnyJSON]?         // optional error object
    var files: [[String: AnyJSON]]        // array of file objects (id + edit + _meta)
    var operations: [[String: AnyJSON]]?  // optional

    static func from(_ json: AnyJSON) -> ChangesetStateJSON? {
        guard case .object(let obj) = json,
              case .string(let status) = obj["status"] else { return nil }
        var files: [[String: AnyJSON]] = []
        if case .array(let arr) = obj["files"] {
            for el in arr {
                if case .object(let f) = el { files.append(f) }
            }
        }
        var ops: [[String: AnyJSON]]? = nil
        if case .array(let arr) = obj["operations"] {
            ops = arr.compactMap { el -> [String: AnyJSON]? in
                if case .object(let o) = el { return o } else { return nil }
            }
        }
        var errorObj: [String: AnyJSON]? = nil
        if case .object(let e) = obj["error"] { errorObj = e }
        return ChangesetStateJSON(raw: json, status: status, error: errorObj, files: files, operations: ops)
    }

    func toAnyJSON() -> AnyJSON {
        var obj: [String: AnyJSON] = ["status": .string(status)]
        if let e = error { obj["error"] = .object(e) }
        obj["files"] = .array(files.map { .object($0) })
        if let ops = operations {
            obj["operations"] = .array(ops.map { .object($0) })
        }
        return .object(obj)
    }
}

func applyChangesetAction(_ state: inout ChangesetStateJSON, _ action: StateAction) {
    switch action {
    case .changesetStatusChanged(let a):
        state.status = a.status.rawValue
        if let err = a.error {
            // Build error field directly from the ErrorInfo fields.
            var errObj: [String: AnyJSON] = [
                "errorType": .string(err.errorType),
                "message": .string(err.message),
            ]
            if let stack = err.stack { errObj["stack"] = .string(stack) }
            state.error = errObj
        } else {
            state.error = nil
        }

    case .changesetFileSet(let a):
        let encoder = JSONEncoder()
        guard let data = try? encoder.encode(a.file),
              let fileJson = try? JSONDecoder().decode([String: AnyJSON].self, from: data)
        else { return }
        let fileId = a.file.id
        if let idx = state.files.firstIndex(where: { $0["id"] == .string(fileId) }) {
            state.files[idx] = fileJson
        } else {
            state.files.append(fileJson)
        }

    case .changesetFileRemoved(let a):
        state.files.removeAll { $0["id"] == .string(a.fileId) }

    case .changesetOperationsChanged(let a):
        if let ops = a.operations {
            let encoder = JSONEncoder()
            state.operations = ops.compactMap { op in
                guard let data = try? encoder.encode(op),
                      let j = try? JSONDecoder().decode([String: AnyJSON].self, from: data)
                else { return nil }
                return j
            }
        } else {
            state.operations = nil
        }

    case .changesetOperationStatusChanged(let a):
        guard var ops = state.operations else { return }
        for idx in ops.indices {
            if ops[idx]["id"] == .string(a.operationId) {
                ops[idx]["status"] = .string(a.status.rawValue)
                if let err = a.error {
                    let encoder = JSONEncoder()
                    if let data = try? encoder.encode(err),
                       let j = try? JSONDecoder().decode(AnyJSON.self, from: data) {
                        ops[idx]["error"] = j
                    }
                } else {
                    ops[idx].removeValue(forKey: "error")
                }
                break
            }
        }
        state.operations = ops

    case .changesetCleared:
        state.files = []

    default:
        break
    }
}

extension ChangesetStateJSON {
    func toAnyJSONObject() -> [String: AnyJSON] {
        var obj: [String: AnyJSON] = ["status": .string(status)]
        if let e = error { obj["error"] = .object(e) }
        obj["files"] = .array(files.map { .object($0) })
        if let ops = operations {
            obj["operations"] = .array(ops.map { .object($0) })
        }
        return obj
    }
}

// MARK: - Per-channel state store ─────────────────────────────────────────────

class ChannelStore {
    var channels: [String: ChannelState] = [:]

    func seed(resource: String, rawState: AnyJSON) {
        // Root channel matches both "ahp-root:/" and "ahp-root://" (corpus uses
        // double-slash form for the root resource URI).
        if resource.hasPrefix("ahp-root:") {
            if let state = decodeRoot(rawState) {
                channels[resource] = ChannelState(value: .root(state))
            } else {
                channels[resource] = ChannelState(value: .unknown(rawState))
            }
            return
        }
        let scheme = resource.components(separatedBy: ":").first ?? ""
        switch scheme {
        case "ahp-session":
            // Some corpus scenarios use ahp-session:/ for terminal state (the
            // state shape has title/cols/rows/content/claim but no summary).
            // Try session decode first; fall back to terminal decode.
            if let state = decodeSession(rawState) {
                channels[resource] = ChannelState(value: .session(state))
            } else if let state = decodeTerminal(rawState) {
                channels[resource] = ChannelState(value: .terminal(state))
            } else {
                channels[resource] = ChannelState(value: .unknown(rawState))
            }
        case "ahp-terminal":
            if let state = decodeTerminal(rawState) {
                channels[resource] = ChannelState(value: .terminal(state))
            } else {
                channels[resource] = ChannelState(value: .unknown(rawState))
            }
        case "ahp-changeset":
            if let state = ChangesetStateJSON.from(rawState) {
                channels[resource] = ChannelState(value: .changeset(state))
            } else {
                channels[resource] = ChannelState(value: .unknown(rawState))
            }
        case "ahp-resource-watch":
            channels[resource] = ChannelState(value: .resourceWatch(rawState))
        default:
            // Unknown resource scheme — try to figure out from state shape.
            if let state = decodeSession(rawState) {
                channels[resource] = ChannelState(value: .session(state))
            } else if let state = decodeTerminal(rawState) {
                channels[resource] = ChannelState(value: .terminal(state))
            } else if let cs = ChangesetStateJSON.from(rawState) {
                channels[resource] = ChannelState(value: .changeset(cs))
            } else {
                channels[resource] = ChannelState(value: .unknown(rawState))
            }
        }
    }

    func applyAction(channel: String, action: StateAction) {
        guard var cs = channels[channel] else { return }
        switch cs.value {
        case .root(let s):
            cs.value = .root(rootReducer(state: s, action: action))
        case .session(let s):
            cs.value = .session(sessionReducer(state: s, action: action))
        case .terminal(let s):
            cs.value = .terminal(terminalReducer(state: s, action: action))
        case .changeset(var cjs):
            applyChangesetAction(&cjs, action)
            cs.value = .changeset(cjs)
        case .resourceWatch(let s):
            // passthrough reducer: state unchanged on all actions
            cs.value = .resourceWatch(s)
        case .unknown:
            break
        }
        channels[channel] = cs
    }

    func stateAsAnyJSON(_ channel: String) -> AnyJSON? {
        guard let cs = channels[channel] else { return nil }
        switch cs.value {
        case .root(let s):         return encode(s)
        case .session(let s):      return encode(s)
        case .terminal(let s):     return encode(s)
        case .changeset(let cjs):  return cjs.toAnyJSON()
        case .resourceWatch(let s): return s
        case .unknown(let s):      return s
        }
    }
}

// MARK: - Codable helpers ─────────────────────────────────────────────────────

private let decoder = JSONDecoder()
private let encoder = JSONEncoder()

func decodeRoot(_ json: AnyJSON) -> RootState? {
    guard let data = try? encoder.encode(json) else { return nil }
    return try? decoder.decode(RootState.self, from: data)
}
func decodeSession(_ json: AnyJSON) -> SessionState? {
    guard let data = try? encoder.encode(json) else { return nil }
    return try? decoder.decode(SessionState.self, from: data)
}
func decodeTerminal(_ json: AnyJSON) -> TerminalState? {
    guard let data = try? encoder.encode(json) else { return nil }
    return try? decoder.decode(TerminalState.self, from: data)
}
func decodeStateAction(from json: AnyJSON) -> StateAction? {
    guard let data = try? encoder.encode(json) else { return nil }
    return try? decoder.decode(StateAction.self, from: data)
}
func encode<T: Encodable>(_ v: T) -> AnyJSON? {
    guard let data = try? encoder.encode(v) else { return nil }
    return try? decoder.decode(AnyJSON.self, from: data)
}

// MARK: - Comparison helpers ──────────────────────────────────────────────────

/// Canonicalize: drop null-valued object keys, sort keys, recurse.
/// Same rule as the JS B4 runner ("omitted optional == explicit null").
func canonicalize(_ v: AnyJSON) -> AnyJSON {
    switch v {
    case .null, .bool, .int, .double, .string:
        return v
    case .array(let arr):
        return .array(arr.map { canonicalize($0) })
    case .object(let obj):
        var out: [String: AnyJSON] = [:]
        for k in obj.keys.sorted() {
            let val = obj[k]!
            if case .null = val { continue }  // drop null values
            out[k] = canonicalize(val)
        }
        return .object(out)
    }
}

func deepEqual(_ a: AnyJSON, _ b: AnyJSON) -> Bool {
    return a == b
}

/// deep-CONTAINS: every key in `expected` matches in `actual`; extra keys
/// in `actual` are ignored. Arrays compare element-wise with containment.
func deepContains(_ actual: AnyJSON, _ expected: AnyJSON) -> Bool {
    switch (actual, expected) {
    case (.null, .null): return true
    case (.bool(let a), .bool(let b)): return a == b
    case (.int(let a), .int(let b)): return a == b
    case (.double(let a), .double(let b)): return a == b
    case (.int(let a), .double(let b)): return Double(a) == b
    case (.double(let a), .int(let b)): return a == Double(b)
    case (.string(let a), .string(let b)): return a == b
    case (.array(let a), .array(let b)):
        guard a.count == b.count else { return false }
        return zip(a, b).allSatisfy { deepContains($0.0, $0.1) }
    case (.object(let a), .object(let b)):
        for (k, ev) in b {
            guard let av = a[k] else { return false }
            if !deepContains(av, ev) { return false }
        }
        return true
    default: return false
    }
}

/// Navigate a dotted path through an AnyJSON.
func navigate(_ json: AnyJSON, path: String?) -> (found: Bool, value: AnyJSON) {
    guard let path = path, !path.isEmpty else { return (true, json) }
    var cur = json
    for seg in path.split(separator: ".") {
        if case .object(let obj) = cur {
            guard let v = obj[String(seg)] else { return (false, .null) }
            cur = v
        } else if case .array(let arr) = cur, let idx = Int(seg), idx < arr.count {
            cur = arr[idx]
        } else {
            return (false, .null)
        }
    }
    return (true, cur)
}

// MARK: - Host subprocess ─────────────────────────────────────────────────────

/// Spawns the scenario host and returns (process, wsURL).
func spawnHost(hostScript: String, scenarioPath: String, timeout: TimeInterval = 10.0) async throws -> (Process, String) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = ["node", hostScript, scenarioPath]

    let stdoutPipe = Pipe()
    let stderrPipe = Pipe()
    process.standardOutput = stdoutPipe
    process.standardError = stderrPipe

    try process.run()

    // Read stdout until we see "SCENARIO HOST READY ws://127.0.0.1:<port>"
    let deadline = Date().addingTimeInterval(timeout)
    var buf = ""
    while Date() < deadline {
        let data = stdoutPipe.fileHandleForReading.availableData
        if !data.isEmpty, let chunk = String(data: data, encoding: .utf8) {
            buf += chunk
            if let range = buf.range(of: #"SCENARIO HOST READY (ws://127\.0\.0\.1:\d+)"#, options: .regularExpression) {
                let line = String(buf[range])
                let parts = line.split(separator: " ")
                if parts.count >= 4 {
                    let wsURL = String(parts[3])
                    return (process, wsURL)
                }
            }
        }
        try await Task.sleep(nanoseconds: 20_000_000) // 20ms poll
    }
    process.terminate()
    let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
    let stderr = String(data: stderrData, encoding: .utf8) ?? ""
    throw ConformanceError.hostTimeout("host did not print READY within \(timeout)s. stderr: \(stderr)")
}

enum ConformanceError: Error {
    case hostTimeout(String)
    case parseError(String)
    case wsError(String)
    case noAssertions
}

// MARK: - WebSocket drive ─────────────────────────────────────────────────────

struct RunState {
    var channels = ChannelStore()
    var synthetic: [String: AnyJSON] = [:]       // protocolVersion, lastResponseOk, etc.
    var observedEvents: [AnyJSON] = []            // raw envelopes + message forms
    var surfacedErrors: [AnyJSON] = []            // JSON-RPC error objects
    var warnings: [String] = []
}

/// Replay all steps against the host over a real WebSocket.
func driveProtocol(wsURLString: String, scenario: Scenario, timeout: TimeInterval = 10.0) async throws -> RunState {
    guard let url = URL(string: wsURLString) else {
        throw ConformanceError.wsError("bad URL: \(wsURLString)")
    }

    var state = RunState()

    let requests = scenario.steps.filter { $0.op == "client.request" }
    var requestCursor = 0

    // Build JSON-RPC request frame from a scenario step.
    func buildRequest(_ step: Step) -> Data? {
        var obj: [String: AnyJSON] = [
            "jsonrpc": .string("2.0"),
            "method": .string(step.method ?? ""),
            "id": .int(step.id ?? 0),
        ]
        if let params = step.params {
            obj["params"] = params
        }
        return try? encoder.encode(AnyJSON.object(obj))
    }

    // Process a received JSON-RPC frame.
    func processFrame(_ json: AnyJSON) {
        guard case .object(let msg) = json else { return }

        let hasId: Bool
        if let idVal = msg["id"] {
            if case .null = idVal { hasId = false } else { hasId = true }
        } else { hasId = false }

        let hasResult = msg["result"] != nil
        let hasError = msg["error"] != nil

        if hasId && (hasResult || hasError) {
            // Response to a request.
            if let errVal = msg["error"], errVal != .null {
                state.surfacedErrors.append(errVal)
                state.synthetic["lastResponseOk"] = .bool(false)
            } else {
                state.synthetic["lastResponseOk"] = .bool(true)
                // Seed snapshots if the result carries them.
                if let resultVal = msg["result"], case .object(let res) = resultVal {
                    if case .string(let pv) = res["protocolVersion"] {
                        state.synthetic["protocolVersion"] = .string(pv)
                    }
                    // Seed from snapshots array.
                    if case .array(let snaps) = res["snapshots"] {
                        for snap in snaps {
                            guard case .object(let s) = snap,
                                  case .string(let resource) = s["resource"],
                                  let rawState = s["state"]
                            else { continue }
                            state.channels.seed(resource: resource, rawState: rawState)
                        }
                    }
                    // Single snapshot shape (reconnect).
                    if case .object(let snap) = res["snapshot"],
                       case .string(let resource) = snap["resource"],
                       let rawState = snap["state"] {
                        state.channels.seed(resource: resource, rawState: rawState)
                    }
                }
            }
            return
        }

        // Server notification.
        if let methodVal = msg["method"], case .string(let method) = methodVal, !hasId {
            let paramsVal = msg["params"] ?? .null

            // Record message-level event for message.method assertions.
            state.observedEvents.append(.object(["method": .string(method), "params": paramsVal]))

            if method == "action", case .object(let params) = paramsVal {
                // Also record the envelope itself.
                state.observedEvents.append(paramsVal)

                // Decode and reduce.
                if case .string(let channel) = params["channel"],
                   let actionVal = params["action"],
                   let action = decodeStateAction(from: actionVal) {
                    state.channels.applyAction(channel: channel, action: action)
                } else {
                    // Could not decode action — event is observed; no state fold.
                    if case .string(let ch) = params["channel"] {
                        state.warnings.append("could not decode action on channel \(ch): \(paramsVal)")
                    }
                }
            }
        }
    }

    // URLSession WebSocket with retry on transient connect errors.
    let session = URLSession(configuration: .default)
    var connectAttempts = 0
    let maxAttempts = 5

    while connectAttempts < maxAttempts {
        connectAttempts += 1
        let task = session.webSocketTask(with: url)
        task.resume()

        var opened = false
        var settled = false
        var settledState: RunState?
        var settledError: Error?
        var taskRef: URLSessionWebSocketTask? = task

        // Send requests inline as we get responses back.
        func sendNext() {
            guard requestCursor < requests.count,
                  let t = taskRef else { return }
            let step = requests[requestCursor]
            requestCursor += 1
            guard let data = buildRequest(step),
                  let str = String(data: data, encoding: .utf8) else { return }
            t.send(.string(str)) { _ in }
        }

        // Receive loop.
        func receiveLoop() {
            taskRef?.receive { result in
                switch result {
                case .failure(let err):
                    if !settled {
                        settled = true
                        if !opened {
                            // Transient pre-open error — will retry.
                            settledError = err
                        } else {
                            settledState = state
                        }
                    }
                case .success(let msg):
                    opened = true
                    var raw: String?
                    switch msg {
                    case .string(let s): raw = s
                    case .data(let d): raw = String(data: d, encoding: .utf8)
                    @unknown default: break
                    }
                    if let raw = raw,
                       let data = raw.data(using: .utf8),
                       let json = try? decoder.decode(AnyJSON.self, from: data) {
                        processFrame(json)
                        // After a response (has id), send next request.
                        if case .object(let m) = json {
                            let hasId: Bool
                            if let idVal = m["id"] { if case .null = idVal { hasId = false } else { hasId = true } } else { hasId = false }
                            if hasId && (m["result"] != nil || m["error"] != nil) {
                                sendNext()
                            }
                        }
                    }
                    receiveLoop()
                }
            }
        }

        // Send first request once connected, then start receive loop.
        // URLSessionWebSocketTask connects lazily on first send/receive, so
        // we just kick the receive loop and send the first request now.
        receiveLoop()
        if !requests.isEmpty { sendNext() }

        // Wait for settled or timeout.
        let deadline2 = Date().addingTimeInterval(timeout)
        while !settled && Date() < deadline2 {
            try await Task.sleep(nanoseconds: 50_000_000) // 50ms
            // Also check if host closed the socket (task state).
            if task.state == .canceling || task.state == .completed {
                if !settled { settled = true; settledState = state }
                break
            }
        }

        // Soft timeout — collect what we have.
        if !settled { settled = true; settledState = state }
        taskRef = nil
        task.cancel(with: .normalClosure, reason: nil)

        // If there's a pre-open error, retry.
        if let err = settledError, !opened, connectAttempts < maxAttempts {
            let msg = err.localizedDescription
            let transient = msg.contains("ECONNREFUSED") || msg.contains("cancelled") || msg.contains("lost connection")
            if transient {
                try await Task.sleep(nanoseconds: 80_000_000) // 80ms backoff
                // Reset accumulator state for retry.
                state = RunState()
                requestCursor = 0
                continue
            }
        }

        if let s = settledState { return s }
        if let err = settledError { throw ConformanceError.wsError(err.localizedDescription) }
        return state
    }

    return state
}

// MARK: - Assertions ──────────────────────────────────────────────────────────

struct AssertResult {
    let op: String
    let label: String
    let ok: Bool
    let detail: String?
}

func checkAssertion(_ step: Step, state: RunState) -> AssertResult {
    let label = step.label ?? ""

    if step.op == "client.assert.state" {
        var target: AnyJSON?
        var bucketLabel: String

        if let ch = step.channel {
            target = state.channels.stateAsAnyJSON(ch)
            bucketLabel = "channel \(ch)"
            if target == nil {
                return AssertResult(op: step.op, label: label, ok: false,
                    detail: "no reduced state for channel \(ch); known: [\(state.channels.channels.keys.joined(separator: ", "))]")
            }
        } else if let _ = step.path {
            // Path with no channel → synthetic top-level.
            target = .object(state.synthetic)
            bucketLabel = "synthetic top-level state"
        } else {
            // No channel and no path → whole-state convergence, single channel.
            if state.channels.channels.count == 1 {
                let key = state.channels.channels.keys.first!
                target = state.channels.stateAsAnyJSON(key)
                bucketLabel = "the single channel (\(key))"
            } else {
                return AssertResult(op: step.op, label: label, ok: false,
                    detail: "whole-state assertion needs exactly 1 channel, found \(state.channels.channels.count): [\(state.channels.channels.keys.joined(separator: ", "))]")
            }
        }

        let (found, actual) = navigate(target!, path: step.path)

        // For synthetic top-level: undefined-at-path ≈ null when expected null.
        var effectiveActual = actual
        if !found && step.channel == nil, let p = step.path, !p.isEmpty, step.equals == .null {
            effectiveActual = .null
        }

        let actualCanon = canonicalize(found ? effectiveActual : .null)
        let expectedCanon = canonicalize(step.equals ?? .null)

        if deepEqual(actualCanon, expectedCanon) {
            return AssertResult(op: step.op, label: label, ok: true, detail: nil)
        }
        let pathStr = step.path.map { " path '\($0)'" } ?? " (whole state)"
        return AssertResult(op: step.op, label: label, ok: false,
            detail: "assert.state @ \(bucketLabel)\(pathStr): expected \(expectedCanon), got \(found ? actualCanon : AnyJSON.string("<path not found>"))")
    }

    if step.op == "client.assert.event" {
        let matches = step.matches ?? .object([:])
        // Try match against every observed event and its .action + .params views.
        for ev in state.observedEvents {
            var views = [ev]
            if case .object(let obj) = ev {
                if let a = obj["action"] { views.append(a) }
                if let p = obj["params"] { views.append(p) }
            }
            for view in views {
                if deepContains(view, matches) {
                    return AssertResult(op: step.op, label: label, ok: true, detail: nil)
                }
            }
        }
        return AssertResult(op: step.op, label: label, ok: false,
            detail: "assert.event: no observed event deep-contains \(matches). observed \(state.observedEvents.count) event(s)")
    }

    if step.op == "client.assert.error" {
        let expectedCode = step.code ?? 0
        for errJson in state.surfacedErrors {
            guard case .object(let err) = errJson else { continue }
            guard case .int(let code) = err["code"] ?? .null, code == expectedCode else { continue }
            if let msg = step.message {
                if case .string(let errMsg) = err["message"] ?? .null, errMsg.contains(msg) {
                    return AssertResult(op: step.op, label: label, ok: true, detail: nil)
                }
                continue
            }
            return AssertResult(op: step.op, label: label, ok: true, detail: nil)
        }
        return AssertResult(op: step.op, label: label, ok: false,
            detail: "assert.error: no surfaced error code \(expectedCode). surfaced: \(state.surfacedErrors)")
    }

    return AssertResult(op: step.op, label: label, ok: false, detail: "unknown op: \(step.op)")
}

// MARK: - Single scenario runner ──────────────────────────────────────────────

struct ScenarioResult {
    let id: String
    let status: String   // "PASS" | "FAIL" | "ERROR"
    let asserts: [AssertResult]
    let reason: String?
    let warnings: [String]
}

func runScenario(
    scenarioPath: String,
    hostScript: String,
    verbose: Bool
) async -> ScenarioResult {
    let id = URL(fileURLWithPath: scenarioPath)
        .lastPathComponent
        .replacingOccurrences(of: ".scenario.json", with: "")

    let scenarioData: Data
    let scenario: Scenario
    do {
        scenarioData = try Data(contentsOf: URL(fileURLWithPath: scenarioPath))
        scenario = try decoder.decode(Scenario.self, from: scenarioData)
    } catch {
        return ScenarioResult(id: id, status: "ERROR", asserts: [],
                              reason: "parse error: \(error)", warnings: [])
    }

    // Pin clock before any reduction.
    pinClock(scenario.pinClock)

    let (hostProcess, wsURL): (Process, String)
    do {
        (hostProcess, wsURL) = try await spawnHost(hostScript: hostScript, scenarioPath: scenarioPath)
    } catch {
        return ScenarioResult(id: id, status: "ERROR", asserts: [],
                              reason: "host error: \(error)", warnings: [])
    }
    defer { hostProcess.terminate() }

    let state: RunState
    do {
        state = try await driveProtocol(wsURLString: wsURL, scenario: scenario)
    } catch {
        return ScenarioResult(id: id, status: "ERROR", asserts: [],
                              reason: "ws error: \(error)", warnings: [])
    }

    let assertSteps = scenario.steps.filter { $0.op.hasPrefix("client.assert.") }
    if assertSteps.isEmpty {
        return ScenarioResult(id: id, status: "ERROR", asserts: [],
                              reason: "no client.assert.* steps", warnings: state.warnings)
    }

    var asserts: [AssertResult] = []
    var allOk = true
    for step in assertSteps {
        let r = checkAssertion(step, state: state)
        asserts.append(r)
        if !r.ok { allOk = false }
    }

    if verbose {
        for a in asserts {
            let tick = a.ok ? "PASS" : "FAIL"
            print("    \(tick)  \(a.op)  \(a.label)")
            if let detail = a.detail { print("          → \(detail)") }
        }
        for w in state.warnings { print("    WARN  \(w)") }
    }

    return ScenarioResult(id: id, status: allOk ? "PASS" : "FAIL",
                          asserts: asserts, reason: nil, warnings: state.warnings)
}

// MARK: - Entry point ─────────────────────────────────────────────────────────

let args = CommandLine.arguments.dropFirst()
let verbose = args.contains("--verbose")
let scenarioPaths: [String]

let repoRoot: String = {
    // conformance/swift/ → two levels up is repo root.
    let script = CommandLine.arguments[0]
    let swiftPkg = URL(fileURLWithPath: script)
        .deletingLastPathComponent()  // binary
        .deletingLastPathComponent()  // debug/release
        .deletingLastPathComponent()  // .build
        .deletingLastPathComponent()  // conformance/swift
        .deletingLastPathComponent()  // conformance
        .path
    return swiftPkg
}()

let explicitPaths = args.filter { !$0.hasPrefix("--") }
if !explicitPaths.isEmpty {
    scenarioPaths = explicitPaths
} else {
    // Default: all round-trips + reducers + negatives.
    let scenarioBase = "\(repoRoot)/types/test-cases/scenarios"
    let fm = FileManager.default
    var paths: [String] = []
    for dir in ["round-trips", "reducers", "negatives"] {
        let dirURL = URL(fileURLWithPath: "\(scenarioBase)/\(dir)")
        if let items = try? fm.contentsOfDirectory(at: dirURL, includingPropertiesForKeys: nil) {
            paths += items
                .filter { $0.pathExtension == "json" && $0.lastPathComponent.hasSuffix(".scenario.json") }
                .map { $0.path }
                .sorted()
        }
    }
    scenarioPaths = paths
}

let hostScript = "\(repoRoot)/conformance/host/scenario-host.mjs"

print("AHP Swift Conformance Runner (B5)")
print("Scenarios: \(scenarioPaths.count)  Host: \(hostScript)")
print(String(repeating: "─", count: 60))

var passed = 0
var failed = 0
var errored = 0

await withTaskGroup(of: ScenarioResult.self) { group in
    // Run concurrently but cap concurrency to avoid port exhaustion.
    let sem = AsyncSemaphore(limit: 8)
    for path in scenarioPaths {
        await sem.wait()
        group.addTask {
            let result = await runScenario(scenarioPath: path, hostScript: hostScript, verbose: verbose)
            await sem.signal()
            return result
        }
    }

    var results: [ScenarioResult] = []
    for await result in group {
        results.append(result)
    }

    // Print in sorted order.
    for result in results.sorted(by: { $0.id < $1.id }) {
        switch result.status {
        case "PASS":
            passed += 1
            if verbose { print("PASS  \(result.id)") }
        case "FAIL":
            failed += 1
            print("FAIL  \(result.id)")
            for a in result.asserts.filter({ !$0.ok }) {
                print("  ✗ \(a.op)  \(a.label)")
                if let d = a.detail { print("    → \(d)") }
            }
        case "ERROR":
            errored += 1
            print("ERROR \(result.id): \(result.reason ?? "unknown")")
        default:
            break
        }
    }
}

let total = passed + failed + errored
print(String(repeating: "─", count: 60))
print("RESULT  \(passed)/\(total) passed  (\(failed) failed, \(errored) error)")

exit(failed > 0 || errored > 0 ? 1 : 0)

// MARK: - AsyncSemaphore ──────────────────────────────────────────────────────
// Simple actor-based semaphore for concurrency limiting.

actor AsyncSemaphore {
    private var count: Int
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(limit: Int) { self.count = limit }

    func wait() async {
        if count > 0 {
            count -= 1
            return
        }
        await withCheckedContinuation { cont in
            waiters.append(cont)
        }
    }

    func signal() {
        if waiters.isEmpty {
            count += 1
        } else {
            let cont = waiters.removeFirst()
            cont.resume()
        }
    }
}
