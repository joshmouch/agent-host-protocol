// TypesRoundTripFixtureTests — data-driven wire round-trip parity for Swift.
//
// Loads the SHARED, language-agnostic round-trip corpus under
// types/test-cases/round-trips/*.json (the same fixtures the .NET client runs
// via clients/dotnet/tests/.../TypesRoundTripFixtures.cs) and asserts each via
// REAL Swift Codable decode/encode of the corresponding generated wire type.
//
// Why this lives in AgentHostProtocolClientTests rather than alongside
// FixtureDrivenReducerTests (AgentHostProtocolTests): the JsonRpcMessage union
// — the only Swift discriminated type for JSON-RPC requests/notifications/
// responses — ships in the AgentHostProtocolClient module (Transport/
// AHPTransport.swift), not the types module. The four jsonrpc fixtures
// (008–011) need it, so the loader runs in the client test target, which can
// import BOTH AgentHostProtocol (all the wire types) and AgentHostProtocolClient
// (JsonRpcMessage).
//
// The corpus carries language-neutral discriminators:
//   * expect            — dotted JSON paths checked against the RE-ENCODED wire.
//   * expectVariant     — { accessor: ConcreteTypeName }; "" means the whole
//                         decoded union's active case maps to that .NET concrete
//                         type. Here we map each .NET concrete type name to the
//                         Swift enum case that carries the same payload.
//   * expectJsonRpcVariant request|notification|success|error → JsonRpcMessage
//                         cases .request / .notification / .successResponse /
//                         .errorResponse.
//   * expectBitset      — SessionStatus flag membership + numeric value.
//   * expectNumberAbove — a re-encoded numeric field exceeds a bound (64-bit).
//   * expectReencodedAbsent — keys that must NOT appear in the re-encoded wire.
//   * reencodes         — re-encode is byte-exact with the input bytes.
//   * roundTripStable   — decode→encode→decode→encode is a fixed point (and any
//                         `expect` paths still hold on the 2nd pass).
//   * expectConstant    — ProtocolVersion constants (no wire decode).
//
// Run: swift test --filter TypesRoundTripFixtureTests
//
// Real-execution: no mocks. Every fixture decodes with JSONDecoder + the real
// generated types and re-encodes with JSONEncoder, then asserts the fixture's
// expectations against the decoded value and the re-encoded bytes.

import XCTest
import Foundation
import AgentHostProtocol
@testable import AgentHostProtocolClient

final class TypesRoundTripFixtureTests: XCTestCase {

    // MARK: - Known representational gaps (documented, not silent)
    //
    // A handful of corpus fixtures exercise .NET wire-type behavior that the
    // current Swift generated types cannot represent. These are REAL type gaps,
    // not test shortcuts — each is reported out of the suite (printed) and
    // listed here with the precise reason. The test asserts that the set of
    // fixtures that actually fail-to-represent equals THIS set, so a future
    // Swift type change that closes a gap (or opens a new one) fails loudly and
    // forces this list to be updated.
    //
    // 019 channel-scoped-notification-uri:
    //     The wire payload is { channel, session } with NO `summary`. .NET's
    //     SessionAddedParams.Summary is nullable, so the unknown `session` key is
    //     dropped and `channel` survives. Swift's SessionAddedParams.summary is a
    //     NON-optional SessionSummary, so decode throws keyNotFound("summary").
    //     This is NOT a Swift fidelity bug: schema/notifications.schema.json
    //     declares `summary` REQUIRED, so the fixture payload is schema-invalid
    //     and Swift's strict rejection is the spec-correct behavior (.NET's
    //     nullable `summary` is the deviation). Handled separately from the four
    //     genuine encode-fidelity bugs below; see
    //     types/test-cases/round-trips/KNOWN-FIDELITY-GAPS.md Gap 5.
    //
    // 002 / 003 / 012 / 013 were genuine Swift encode-fidelity bugs and are now
    // FIXED at the codegen (scripts/generate-swift.ts) + regenerated sources, so
    // they round-trip green and are NO LONGER in this set:
    //   * 002 state-action-unknown-variant-preserved — StateAction's unknown case
    //     now carries the raw payload as `AnyCodable` (was `unknown(type: String)`
    //     with `encode → break`), so foo:42 + the `type` discriminant survive and
    //     re-encode verbatim.
    //   * 003 customization-unknown-type-preserved — the Customization union now
    //     honors allowUnknown (mirrors .NET): an unrecognized `type` decodes to a
    //     raw `AnyCodable` passthrough instead of throwing, and re-encodes
    //     verbatim.
    //   * 012 / 013 changeset-target-{resource,range} — the variant structs now
    //     re-emit their constant `kind` discriminant on encode (custom encode with
    //     an EncodingKeys set; previously `kind` was a computed property excluded
    //     from CodingKeys and silently dropped).
    private static let knownRepresentationalGaps: Set<String> = [
        "019-channel-scoped-notification-uri",
    ]

    // MARK: - Fixture directory

    private static let fixtureDir: URL = {
        // This file: clients/swift/AgentHostProtocol/Tests/AgentHostProtocolClientTests/TypesRoundTripFixtureTests.swift
        // Walk up to the repo root, then into types/test-cases/round-trips.
        let thisFile = URL(fileURLWithPath: #filePath)
        let repoRoot = thisFile
            .deletingLastPathComponent() // TypesRoundTripFixtureTests.swift
            .deletingLastPathComponent() // AgentHostProtocolClientTests/
            .deletingLastPathComponent() // Tests/
            .deletingLastPathComponent() // AgentHostProtocol/
            .deletingLastPathComponent() // swift/
            .deletingLastPathComponent() // clients/
        return repoRoot.appendingPathComponent("types/test-cases/round-trips")
    }()

    private static func fixtureFiles() -> [String] {
        let fm = FileManager.default
        let files = (try? fm.contentsOfDirectory(atPath: fixtureDir.path)) ?? []
        return files.filter { $0.hasSuffix(".json") }.sorted()
    }

    // MARK: - Loaded-something guard

    func testCorpusIsPresent() {
        XCTAssertGreaterThan(
            Self.fixtureFiles().count, 0,
            "No round-trip fixtures found at \(Self.fixtureDir.path). Ensure the checkout includes types/test-cases/round-trips/."
        )
    }

    // MARK: - Whole-corpus runner

    func testRoundTripCorpus() throws {
        var failures: [String] = []
        var gapHits: Set<String> = []
        var ranRealAssertions = 0

        for file in Self.fixtureFiles() {
            let stem = (file as NSString).deletingPathExtension
            let url = Self.fixtureDir.appendingPathComponent(file)
            let data = try Data(contentsOf: url)
            let root = try JSONSerialization.jsonObject(with: data) as! [String: Any]

            do {
                try runFixture(file: file, root: root)
                ranRealAssertions += 1
            } catch {
                if Self.knownRepresentationalGaps.contains(stem) {
                    gapHits.insert(stem)
                    print("⊘ \(file): known Swift representational gap — \(error)")
                } else {
                    failures.append("✗ \(file): \(error)")
                }
            }
        }

        // Every fixture NOT in the gap set must have run a real assertion.
        let expectedReal = Self.fixtureFiles().count - Self.knownRepresentationalGaps.count
        XCTAssertEqual(
            ranRealAssertions, expectedReal,
            "Expected \(expectedReal) fixtures to decode+assert for real; only \(ranRealAssertions) did."
        )

        // The gap set must be exactly the fixtures that failed to represent.
        // If a gap closes, gapHits shrinks → mismatch → update the list.
        // If a new fixture can't be represented, it lands in `failures` → loud.
        XCTAssertEqual(
            gapHits, Self.knownRepresentationalGaps,
            "Known-gap set drifted. Hit gaps: \(gapHits.sorted()); declared: \(Self.knownRepresentationalGaps.sorted()). A gap that no longer reproduces must be removed from knownRepresentationalGaps (and ideally promoted to a real assertion)."
        )

        if !failures.isEmpty {
            XCTFail("\(failures.count) round-trip fixture(s) failed:\n" + failures.joined(separator: "\n"))
        }
    }

    // MARK: - Per-fixture dispatch

    private func runFixture(file: String, root: [String: Any]) throws {
        guard let type = root["type"] as? String else {
            throw FixtureError.message("\(file): missing `type`")
        }

        // ProtocolVersion fixtures assert constants, not wire decode.
        if type == "ProtocolVersion" {
            try verifyProtocolConstant(file: file, root: root)
            return
        }

        let inputJSON = try readInputJSON(file: file, root: root)
        let (decoded, reencoded) = try decodeAndReencode(type: type, inputJSON: inputJSON)

        var assertedSomething = false

        if let expect = root["expect"] as? [String: Any] {
            let reObj = try JSONSerialization.jsonObject(
                with: reencoded.data(using: .utf8)!, options: [.fragmentsAllowed])
            for (path, want) in expect {
                let got = try resolvePath(reObj, path: path, file: file)
                try assertJSONEquals(want: want, got: got, ctx: "\(file): expect[\"\(path)\"]")
                assertedSomething = true
            }
        }

        if let variants = root["expectVariant"] as? [String: Any] {
            try verifyVariant(file: file, decoded: decoded, variants: variants)
            assertedSomething = true
        }

        if let jrpc = root["expectJsonRpcVariant"] as? String {
            try verifyJsonRpcVariant(file: file, decoded: decoded, kind: jrpc)
            assertedSomething = true
        }

        if let bitset = root["expectBitset"] as? [String: Any] {
            try verifyBitset(file: file, decoded: decoded, reencoded: reencoded, bitset: bitset)
            assertedSomething = true
        }

        if let above = root["expectNumberAbove"] as? [String: Any] {
            let reObj = try JSONSerialization.jsonObject(
                with: reencoded.data(using: .utf8)!, options: [.fragmentsAllowed])
            for (path, boundAny) in above {
                let got = try resolvePath(reObj, path: path, file: file)
                guard let bound = asInt64(boundAny), let gotN = asInt64(got) else {
                    throw FixtureError.message("\(file): expectNumberAbove[\"\(path)\"] — non-numeric")
                }
                if !(gotN > bound) {
                    throw FixtureError.message("\(file): expectNumberAbove[\"\(path)\"] — \(gotN) is not > \(bound)")
                }
                assertedSomething = true
            }
        }

        if let absent = root["expectReencodedAbsent"] as? [Any] {
            let reObj = try JSONSerialization.jsonObject(
                with: reencoded.data(using: .utf8)!, options: [.fragmentsAllowed]) as? [String: Any] ?? [:]
            for keyAny in absent {
                guard let key = keyAny as? String else { continue }
                if reObj.keys.contains(key) {
                    throw FixtureError.message(
                        "\(file): re-encoded JSON must NOT contain key \"\(key)\" but it does. Re-encoded: \(reencoded)")
                }
                assertedSomething = true
            }
        }

        if let reencodes = root["reencodes"] as? Bool, reencodes {
            // Byte-exact comparison after canonicalizing both through the same
            // serializer (the corpus's `wireRaw` is already compact, and our
            // re-encode uses sortedKeys; compare via normalized JSON object
            // equality so key ORDER differences don't create false negatives but
            // VALUE / presence differences do).
            try assertCanonicalEqual(
                lhs: inputJSON, rhs: reencoded,
                ctx: "\(file): reencodes (byte/structure-exact)")
            assertedSomething = true
        }

        if let stable = root["roundTripStable"] as? Bool, stable {
            let (_, reencoded2) = try decodeAndReencode(type: type, inputJSON: reencoded)
            if let expect = root["expect"] as? [String: Any] {
                let re2Obj = try JSONSerialization.jsonObject(
                    with: reencoded2.data(using: .utf8)!, options: [.fragmentsAllowed])
                for (path, want) in expect {
                    let got = try resolvePath(re2Obj, path: path, file: file)
                    try assertJSONEquals(want: want, got: got,
                        ctx: "\(file): roundTripStable expect[\"\(path)\"] (2nd decode)")
                }
            } else {
                try assertCanonicalEqual(
                    lhs: reencoded, rhs: reencoded2,
                    ctx: "\(file): roundTripStable fixed-point")
            }
            assertedSomething = true
        }

        if !assertedSomething {
            throw FixtureError.message(
                "\(file): fixture made no assertions — coverage theater.")
        }
    }

    // MARK: - Real decode dispatch
    //
    // Mirrors the .NET DecodeAndReencode switch. Adding a wire type to the
    // corpus is a deliberate edit here; the corpus never decodes arbitrary types
    // reflectively. Returns a typed `DecodedValue` so variant assertions can
    // inspect the active case, plus the re-encoded (sortedKeys) bytes.

    private enum DecodedValue {
        case actionEnvelope(ActionEnvelope)
        case stateAction(StateAction)
        case customization(Customization)
        case sessionStatus(SessionStatus)
        case stringOrMarkdown(StringOrMarkdown)
        case jsonRpcMessage(JsonRpcMessage)
        case changesetTarget(ChangesetOperationTarget)
        case inputQuestion(SessionInputQuestion)
        case sessionSummary(SessionSummary)
        case sessionAddedParams(SessionAddedParams)
        case partialSummary(PartialSessionSummary)
    }

    private func decodeAndReencode(type: String, inputJSON: String) throws -> (DecodedValue, String) {
        let data = inputJSON.data(using: .utf8)!
        let dec = JSONDecoder()

        func reencode<T: Encodable>(_ value: T) throws -> String {
            let enc = JSONEncoder()
            enc.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            let out = try enc.encode(value)
            return String(data: out, encoding: .utf8)!
        }

        switch type {
        case "ActionEnvelope":
            let v = try dec.decode(ActionEnvelope.self, from: data)
            return (.actionEnvelope(v), try reencode(v))
        case "StateAction":
            let v = try dec.decode(StateAction.self, from: data)
            return (.stateAction(v), try reencode(v))
        case "Customization":
            let v = try dec.decode(Customization.self, from: data)
            return (.customization(v), try reencode(v))
        case "SessionStatus":
            let v = try dec.decode(SessionStatus.self, from: data)
            return (.sessionStatus(v), try reencode(v))
        case "StringOrMarkdown":
            let v = try dec.decode(StringOrMarkdown.self, from: data)
            return (.stringOrMarkdown(v), try reencode(v))
        case "JsonRpcMessage":
            let v = try dec.decode(JsonRpcMessage.self, from: data)
            return (.jsonRpcMessage(v), try reencode(v))
        case "ChangesetOperationTarget":
            let v = try dec.decode(ChangesetOperationTarget.self, from: data)
            return (.changesetTarget(v), try reencode(v))
        case "SessionInputQuestion":
            let v = try dec.decode(SessionInputQuestion.self, from: data)
            return (.inputQuestion(v), try reencode(v))
        case "SessionSummary":
            let v = try dec.decode(SessionSummary.self, from: data)
            return (.sessionSummary(v), try reencode(v))
        case "SessionAddedParams":
            let v = try dec.decode(SessionAddedParams.self, from: data)
            return (.sessionAddedParams(v), try reencode(v))
        case "PartialSessionSummary":
            let v = try dec.decode(PartialSessionSummary.self, from: data)
            return (.partialSummary(v), try reencode(v))
        default:
            throw FixtureError.message(
                "round-trip fixture: unknown wire type \"\(type)\". Add a decode entry to decodeAndReencode.")
        }
    }

    // MARK: - Variant identity (maps .NET concrete-type names → Swift cases)

    private func verifyVariant(file: String, decoded: DecodedValue, variants: [String: Any]) throws {
        for (accessor, wantAny) in variants {
            guard let want = wantAny as? String else { continue }

            if accessor.isEmpty {
                // Whole-decoded-value union identity.
                let actual = wholeVariantTypeName(decoded)
                if actual != want {
                    throw FixtureError.message(
                        "\(file): expectVariant[\"\"] — active variant is \(actual ?? "nil"), expected \(want)")
                }
                continue
            }

            // Named accessor whose value is itself a union (e.g. ActionEnvelope.action).
            let actual = try namedAccessorVariantTypeName(decoded, accessor: accessor, file: file)
            if actual != want {
                throw FixtureError.message(
                    "\(file): expectVariant[\"\(accessor)\"] — active variant is \(actual ?? "nil"), expected \(want)")
            }
        }
    }

    /// Maps the active case of a top-level decoded union to the .NET concrete
    /// type name the corpus uses. `nil` for non-union decoded values.
    private func wholeVariantTypeName(_ decoded: DecodedValue) -> String? {
        switch decoded {
        case .stateAction(let a):
            return stateActionVariantName(a)
        case .customization(let c):
            return customizationVariantName(c)
        case .changesetTarget(let t):
            return changesetTargetVariantName(t)
        case .inputQuestion(let q):
            return inputQuestionVariantName(q)
        case .stringOrMarkdown(let s):
            // The corpus uses expect/reencodes for StringOrMarkdown, not
            // expectVariant; map for completeness.
            switch s {
            case .string: return "String"
            case .markdown: return "MarkdownString"
            }
        default:
            return nil
        }
    }

    private func namedAccessorVariantTypeName(
        _ decoded: DecodedValue, accessor: String, file: String
    ) throws -> String? {
        switch (decoded, accessor.lowercased()) {
        case (.actionEnvelope(let env), "action"):
            return stateActionVariantName(env.action)
        default:
            throw FixtureError.message(
                "\(file): expectVariant accessor \"\(accessor)\" not wired for this decoded type")
        }
    }

    private func stateActionVariantName(_ a: StateAction) -> String? {
        switch a {
        case .sessionTitleChanged: return "SessionTitleChangedAction"
        case .unknown: return "JsonElement" // corpus name for the passthrough case
        default:
            // Derive a stable name from the enum case label for any other
            // variant the corpus might reference later.
            return "\(a)".split(separator: "(").first.map { titleCase(String($0)) + "Action" }
        }
    }

    private func customizationVariantName(_ c: Customization) -> String? {
        switch c {
        case .plugin: return "PluginCustomization"
        case .directory: return "DirectoryCustomization"
        case .unknown: return "JsonElement" // corpus name for the passthrough case
        }
    }

    private func changesetTargetVariantName(_ t: ChangesetOperationTarget) -> String? {
        switch t {
        case .resource: return "ChangesetOperationResourceTarget"
        case .range: return "ChangesetOperationRangeTarget"
        }
    }

    private func inputQuestionVariantName(_ q: SessionInputQuestion) -> String? {
        switch q {
        case .text: return "SessionInputTextQuestion"
        // The corpus maps BOTH `number` and `integer` kinds to the same .NET
        // concrete type (SessionInputNumberQuestion); Swift's enum has two cases
        // (.number / .integer) that both wrap SessionInputNumberQuestion.
        case .number, .integer: return "SessionInputNumberQuestion"
        case .boolean: return "SessionInputBooleanQuestion"
        case .singleSelect: return "SessionInputSingleSelectQuestion"
        case .multiSelect: return "SessionInputMultiSelectQuestion"
        }
    }

    private func titleCase(_ s: String) -> String {
        guard let first = s.first else { return s }
        return String(first).uppercased() + s.dropFirst()
    }

    // MARK: - JSON-RPC variant

    private func verifyJsonRpcVariant(file: String, decoded: DecodedValue, kind: String) throws {
        guard case .jsonRpcMessage(let msg) = decoded else {
            throw FixtureError.message("\(file): expectJsonRpcVariant requires a JsonRpcMessage")
        }
        let actual: String
        switch msg {
        case .request: actual = "request"
        case .notification: actual = "notification"
        case .successResponse: actual = "success"
        case .errorResponse: actual = "error"
        }
        let allowed = ["request", "notification", "success", "error"]
        guard allowed.contains(kind) else {
            throw FixtureError.message("\(file): expectJsonRpcVariant \"\(kind)\" is not one of \(allowed)")
        }
        if actual != kind {
            throw FixtureError.message(
                "\(file): expectJsonRpcVariant — decoded as \(actual), expected \(kind)")
        }
    }

    // MARK: - Bitset

    private func verifyBitset(file: String, decoded: DecodedValue, reencoded: String, bitset: [String: Any]) throws {
        guard case .sessionStatus(let status) = decoded else {
            throw FixtureError.message("\(file): expectBitset requires a SessionStatus")
        }

        if let has = bitset["has"] as? [Any] {
            for nameAny in has {
                guard let name = nameAny as? String else { continue }
                let flag = try statusFlag(name, file: file)
                if !status.contains(flag) {
                    throw FixtureError.message(
                        "\(file): SessionStatus must have flag \(name) but does not (value \(status.rawValue))")
                }
            }
        }

        if let lacks = bitset["lacks"] as? [Any] {
            for nameAny in lacks {
                guard let name = nameAny as? String else { continue }
                let flag = try statusFlag(name, file: file)
                if status.contains(flag) {
                    throw FixtureError.message(
                        "\(file): SessionStatus must NOT have flag \(name) but does (value \(status.rawValue))")
                }
            }
        }

        if let numericAny = bitset["numeric"], let want = asInt64(numericAny) {
            if Int64(status.rawValue) != want {
                throw FixtureError.message(
                    "\(file): SessionStatus numeric — got \(status.rawValue), expected \(want)")
            }
            // The re-encoded wire form must be the same bare number.
            let reObj = try JSONSerialization.jsonObject(
                with: reencoded.data(using: .utf8)!, options: [.fragmentsAllowed])
            guard let reNum = asInt64(reObj) else {
                throw FixtureError.message(
                    "\(file): SessionStatus must re-encode as a JSON number, got \(reencoded)")
            }
            if reNum != want {
                throw FixtureError.message(
                    "\(file): SessionStatus re-encoded numeric — got \(reNum), expected \(want)")
            }
        }
    }

    /// Maps a .NET SessionStatus flag name to the Swift OptionSet member.
    private func statusFlag(_ name: String, file: String) throws -> SessionStatus {
        switch name {
        case "Idle": return .idle
        case "Error": return .error
        case "InProgress": return .inProgress
        case "InputNeeded": return .inputNeeded
        case "IsRead": return .isRead
        case "IsArchived": return .isArchived
        default:
            throw FixtureError.message("\(file): unknown SessionStatus flag \"\(name)\"")
        }
    }

    // MARK: - ProtocolVersion constants

    private func verifyProtocolConstant(file: String, root: [String: Any]) throws {
        guard let c = root["expectConstant"] as? [String: Any] else {
            throw FixtureError.message("\(file): ProtocolVersion fixture missing expectConstant")
        }
        var asserted = false

        if let cur = c["current"] as? String {
            if cur != "non-empty" {
                throw FixtureError.message("\(file): expectConstant.current must be \"non-empty\"")
            }
            if PROTOCOL_VERSION.trimmingCharacters(in: .whitespaces).isEmpty {
                throw FixtureError.message("\(file): PROTOCOL_VERSION must be non-empty")
            }
            asserted = true
        }

        if let sup = c["supported"] as? String {
            if sup != "non-empty-list" {
                throw FixtureError.message("\(file): expectConstant.supported must be \"non-empty-list\"")
            }
            if SUPPORTED_PROTOCOL_VERSIONS.isEmpty {
                throw FixtureError.message("\(file): SUPPORTED_PROTOCOL_VERSIONS must be non-empty")
            }
            asserted = true
        }

        if let first = c["firstSupportedEqualsCurrent"] as? Bool, first {
            guard let head = SUPPORTED_PROTOCOL_VERSIONS.first else {
                throw FixtureError.message("\(file): SUPPORTED_PROTOCOL_VERSIONS is empty")
            }
            if head != PROTOCOL_VERSION {
                throw FixtureError.message(
                    "\(file): first supported \(head) != current \(PROTOCOL_VERSION)")
            }
            asserted = true
        }

        if !asserted {
            throw FixtureError.message("\(file): ProtocolVersion fixture asserted no constant")
        }
    }

    // MARK: - Input bytes

    private func readInputJSON(file: String, root: [String: Any]) throws -> String {
        let hasRaw = root["wireRaw"] != nil
        let hasWire = root["wire"] != nil
        if hasRaw == hasWire {
            throw FixtureError.message(
                "\(file): exactly one of `wire` / `wireRaw` is required (wire=\(hasWire), wireRaw=\(hasRaw)).")
        }
        if hasRaw {
            guard let raw = root["wireRaw"] as? String else {
                throw FixtureError.message("\(file): `wireRaw` is not a string")
            }
            return raw
        }
        // `wire` is a JSON value; compact-serialize it.
        let wire = root["wire"]!
        let data = try JSONSerialization.data(
            withJSONObject: wire, options: [.fragmentsAllowed])
        return String(data: data, encoding: .utf8)!
    }

    // MARK: - JSON path + equality

    /// Resolves a dotted path against a parsed JSON value. Empty path → the
    /// value itself (scalar unions whose whole value is the payload).
    private func resolvePath(_ rootObj: Any, path: String, file: String) throws -> Any {
        if path.isEmpty { return rootObj }
        var cur = rootObj
        for seg in path.split(separator: ".") {
            guard let dict = cur as? [String: Any], let next = dict[String(seg)] else {
                throw FixtureError.message(
                    "\(file): path \"\(path)\" — segment \"\(seg)\" not found")
            }
            cur = next
        }
        return cur
    }

    private func assertJSONEquals(want: Any, got: Any, ctx: String) throws {
        if let wantStr = want as? String {
            guard let gotStr = got as? String, gotStr == wantStr else {
                throw FixtureError.message("\(ctx) — expected string \"\(wantStr)\", got \(describe(got))")
            }
            return
        }
        // Numbers (incl. 64-bit) — compare numerically so 0 == 0.0 and large
        // ints stay exact.
        if let wantN = asInt64(want), let gotN = asInt64(got) {
            guard wantN == gotN else {
                throw FixtureError.message("\(ctx) — expected number \(wantN), got \(gotN)")
            }
            return
        }
        if let wantD = asDouble(want), let gotD = asDouble(got) {
            guard wantD == gotD else {
                throw FixtureError.message("\(ctx) — expected number \(wantD), got \(gotD)")
            }
            return
        }
        if let wantB = want as? Bool, let gotB = (got as? Bool) {
            guard wantB == gotB else {
                throw FixtureError.message("\(ctx) — expected \(wantB), got \(gotB)")
            }
            return
        }
        if want is NSNull {
            guard got is NSNull else {
                throw FixtureError.message("\(ctx) — expected null, got \(describe(got))")
            }
            return
        }
        // Objects / arrays — compare canonical JSON.
        let wd = try JSONSerialization.data(withJSONObject: want, options: [.sortedKeys, .fragmentsAllowed])
        let gd = try JSONSerialization.data(withJSONObject: got, options: [.sortedKeys, .fragmentsAllowed])
        guard wd == gd else {
            throw FixtureError.message("\(ctx) — expected \(describe(want)), got \(describe(got))")
        }
    }

    /// Compares two JSON documents structurally (key order independent, value
    /// and key-presence sensitive). Used for `reencodes` / fixed-point checks.
    private func assertCanonicalEqual(lhs: String, rhs: String, ctx: String) throws {
        let lo = try JSONSerialization.jsonObject(with: lhs.data(using: .utf8)!, options: [.fragmentsAllowed])
        let ro = try JSONSerialization.jsonObject(with: rhs.data(using: .utf8)!, options: [.fragmentsAllowed])
        let ld = try JSONSerialization.data(withJSONObject: lo, options: [.sortedKeys, .fragmentsAllowed])
        let rd = try JSONSerialization.data(withJSONObject: ro, options: [.sortedKeys, .fragmentsAllowed])
        guard ld == rd else {
            throw FixtureError.message(
                "\(ctx)\n  lhs: \(lhs)\n  rhs: \(rhs)")
        }
    }

    private func asInt64(_ v: Any) -> Int64? {
        if let n = v as? NSNumber {
            // Exclude booleans masquerading as NSNumber.
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return nil }
            // Only treat as integer if it has no fractional part.
            let d = n.doubleValue
            if d.rounded() == d { return n.int64Value }
            return nil
        }
        if let i = v as? Int { return Int64(i) }
        return nil
    }

    private func asDouble(_ v: Any) -> Double? {
        if let n = v as? NSNumber {
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return nil }
            return n.doubleValue
        }
        if let d = v as? Double { return d }
        return nil
    }

    private func describe(_ v: Any) -> String {
        if let s = v as? String { return "string \"\(s)\"" }
        if v is NSNull { return "null" }
        if let n = v as? NSNumber { return "number \(n)" }
        return "\(v)"
    }

    // MARK: - Errors

    private enum FixtureError: Error, CustomStringConvertible {
        case message(String)
        var description: String {
            switch self {
            case .message(let m): return m
            }
        }
    }
}
