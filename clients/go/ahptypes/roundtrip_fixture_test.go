// TestRoundTripCorpus — data-driven wire round-trip parity for the Go client.
//
// Loads the SHARED, language-agnostic round-trip corpus under
// types/test-cases/round-trips/*.json (the same fixtures the .NET reference
// client runs via clients/dotnet/tests/.../TypesRoundTripFixtures.cs and the
// Swift client runs via TypesRoundTripFixtureTests.swift) and asserts each via
// the REAL generated Go wire types — encoding/json (un)marshal, the real
// discriminated-union UnmarshalJSON/MarshalJSON, the real SessionStatus bitset.
// No mocks, no faked SUT: every fixture decodes real bytes into a real type and
// re-encodes with the same serializer.
//
// This file mirrors the loader/path-resolution/assertion shape of the reducer
// corpus runner next door (reducers_fixture_test.go in package ahp):
//   - findRoundTripFixtureDir walks upward from cwd to locate the corpus dir,
//   - the directory is iterated in sorted order so adding a fixture file runs
//     automatically,
//   - JSON values are compared structurally (key-order-independent, value- and
//     key-presence-sensitive) after canonicalizing through encoding/json.
//
// The corpus carries language-neutral discriminators; this file maps each to a
// Go accessor:
//   * expect              — dotted JSON paths checked against the RE-ENCODED wire.
//   * expectVariant       — { accessor: ConcreteTypeName }; "" is the whole
//                           decoded union's active variant. Maps the corpus's
//                           (.NET) concrete type names to the Go variant the
//                           same payload decodes into.
//   * expectJsonRpcVariant request|notification|success|error → which pointer of
//                           JsonRpcMessage is non-nil.
//   * expectBitset        — SessionStatus flag membership (has/lacks) + numeric.
//   * expectNumberAbove   — a re-encoded numeric field exceeds a 64-bit bound.
//   * expectReencodedAbsent keys that must NOT appear in the re-encoded wire.
//   * reencodes           — re-encode is byte/structure-exact with the input.
//   * roundTripStable     — decode→encode→decode→encode is a fixed point (and
//                           any `expect` paths still hold on the 2nd pass).
//   * expectConstant      — ProtocolVersion constants (no wire decode).
//
// Run: go test ./ahptypes/ -run TestRoundTripCorpus -v

package ahptypes

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"testing"
)

// roundTripKnownGaps lists corpus fixtures the Go client intentionally does not
// run a real assertion for, each with a precise reason. The whole-corpus runner
// asserts that the set of fixtures it actually skips equals THIS set, so a
// future type change that closes a gap (or a new fixture that can't be
// represented) fails loudly and forces this list to be updated — same tripwire
// discipline as Swift's knownRepresentationalGaps.
//
// Unlike Swift, the Go union model preserves unknown discriminators verbatim
// (the *XUnknown{Raw} variants re-emit their original bytes) and the changeset
// targets carry `kind` as a real serialized struct field — so Go has NONE of
// Swift's 002/003/012/013 encode-fidelity gaps. The only entry is the
// schema-invalid fixture 019.
var roundTripKnownGaps = map[string]string{
	// 019 channel-scoped-notification-uri:
	//   The wire payload is { channel, session } with NO `summary`, but
	//   SessionAddedParams.summary is a REQUIRED field per
	//   schema/notifications.schema.json (Go models it as a non-pointer
	//   SessionSummary, the spec-correct strict modeling — same as Swift).
	//   The fixture is itself schema-invalid; it rewards the lenient (.NET,
	//   nullable-summary) modeling and is being repaired separately by the
	//   .NET-side owner of the corpus (add a minimal `summary`). Per the task,
	//   it is NOT a Go bug to fix here. See
	//   types/test-cases/round-trips/KNOWN-FIDELITY-GAPS.md "Gap 5".
	//
	//   Note: Go's encoding/json does NOT error on the missing required field
	//   (it zero-fills `summary`), so this fixture would "pass by accident"
	//   today and then change behavior once a real `summary` is added upstream.
	//   We skip it explicitly rather than depend on that accidental pass.
	"019-channel-scoped-notification-uri.json": "schema-invalid fixture (missing required SessionAddedParams.summary); repaired by the corpus owner, not a Go bug",
}

// findRoundTripFixtureDir walks upward from the cwd looking for
// types/test-cases/round-trips so the test works whether `go test` runs from
// clients/go/ahptypes or the module root. Mirrors findFixtureDir in
// reducers_fixture_test.go.
func findRoundTripFixtureDir(t *testing.T) string {
	t.Helper()
	wd, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		candidate := filepath.Join(wd, "types", "test-cases", "round-trips")
		if fi, err := os.Stat(candidate); err == nil && fi.IsDir() {
			return candidate
		}
		parent := filepath.Dir(wd)
		if parent == wd {
			t.Fatalf("could not locate types/test-cases/round-trips walking upward from cwd")
		}
		wd = parent
	}
}

// roundTripFixture is the decoded shape of one corpus JSON file. Discriminator
// blocks are kept as json.RawMessage / generic maps because their shape varies
// by fixture.
type roundTripFixture struct {
	Name        string          `json:"name"`
	Description string          `json:"description"`
	Type        string          `json:"type"`
	Wire        json.RawMessage `json:"wire"`
	WireRaw     *string         `json:"wireRaw"`

	Expect                map[string]json.RawMessage `json:"expect"`
	ExpectVariant         map[string]string          `json:"expectVariant"`
	ExpectJsonRpcVariant  *string                    `json:"expectJsonRpcVariant"`
	ExpectBitset          *bitsetExpectation         `json:"expectBitset"`
	ExpectNumberAbove     map[string]json.Number     `json:"expectNumberAbove"`
	ExpectReencodedAbsent []string                   `json:"expectReencodedAbsent"`
	ExpectConstant        map[string]json.RawMessage `json:"expectConstant"`
	Reencodes             bool                       `json:"reencodes"`
	RoundTripStable       bool                       `json:"roundTripStable"`
}

type bitsetExpectation struct {
	Has     []string     `json:"has"`
	Lacks   []string     `json:"lacks"`
	Numeric *json.Number `json:"numeric"`
}

// TestRoundTripCorpus is the primary cross-language wire round-trip parity gate
// for the Go client.
func TestRoundTripCorpus(t *testing.T) {
	dir := findRoundTripFixtureDir(t)

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })

	var fixtureFiles []string
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		fixtureFiles = append(fixtureFiles, entry.Name())
	}

	// Loaded-something guard: the checkout must actually include the corpus.
	if len(fixtureFiles) == 0 {
		t.Fatalf("no round-trip fixtures found in %s — ensure the checkout includes types/test-cases/round-trips/", dir)
	}

	gapHits := map[string]bool{}
	ranReal := 0

	for _, name := range fixtureFiles {
		name := name
		if reason, isGap := roundTripKnownGaps[name]; isGap {
			t.Run(name, func(tt *testing.T) {
				tt.Skipf("known gap: %s", reason)
			})
			gapHits[name] = true
			continue
		}

		ok := t.Run(name, func(tt *testing.T) {
			path := filepath.Join(dir, name)
			raw, err := os.ReadFile(path)
			if err != nil {
				tt.Fatalf("read: %v", err)
			}
			runRoundTripFixture(tt, name, raw)
		})
		if ok {
			ranReal++
		}
	}

	// Every fixture NOT in the gap set must have run a real assertion and passed.
	expectedReal := len(fixtureFiles) - len(roundTripKnownGaps)
	if ranReal != expectedReal {
		t.Fatalf("expected %d fixtures to decode+assert for real and pass; only %d did", expectedReal, ranReal)
	}

	// The gap set must be exactly the fixtures that were skipped. If a gap
	// closes (a fixture is fixed upstream and we removed it from the map), or a
	// new gap is added without a corresponding fixture file, this trips.
	if len(gapHits) != len(roundTripKnownGaps) {
		t.Fatalf("known-gap set drifted: hit %d gaps, declared %d. A declared gap whose fixture no longer exists must be removed from roundTripKnownGaps.", len(gapHits), len(roundTripKnownGaps))
	}
	for name := range roundTripKnownGaps {
		if !gapHits[name] {
			t.Fatalf("declared known gap %q has no matching fixture file on disk — remove it from roundTripKnownGaps", name)
		}
	}

	t.Logf("round-trip corpus: %d fixtures, %d asserted for real, %d known-gap skips", len(fixtureFiles), ranReal, len(roundTripKnownGaps))
}

func runRoundTripFixture(t *testing.T, name string, raw []byte) {
	t.Helper()

	var fx roundTripFixture
	if err := json.Unmarshal(raw, &fx); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}
	if fx.Type == "" {
		t.Fatalf("missing `type`")
	}

	// ProtocolVersion fixtures assert constants, not a wire decode.
	if fx.Type == "ProtocolVersion" {
		verifyProtocolConstant(t, name, fx)
		return
	}

	inputJSON := readInputJSON(t, name, fx)
	decoded, reencoded := decodeAndReencode(t, name, fx.Type, inputJSON)

	asserted := false

	// expect — dotted paths against the RE-ENCODED wire.
	if len(fx.Expect) > 0 {
		reObj := parseToAny(t, name, reencoded)
		for path, wantRaw := range fx.Expect {
			got := resolvePath(t, name, reObj, path)
			assertJSONEquals(t, name, fmt.Sprintf("expect[%q]", path), wantRaw, got)
			asserted = true
		}
	}

	if len(fx.ExpectVariant) > 0 {
		verifyVariant(t, name, decoded, fx.ExpectVariant)
		asserted = true
	}

	if fx.ExpectJsonRpcVariant != nil {
		verifyJsonRpcVariant(t, name, decoded, *fx.ExpectJsonRpcVariant)
		asserted = true
	}

	if fx.ExpectBitset != nil {
		verifyBitset(t, name, decoded, reencoded, *fx.ExpectBitset)
		asserted = true
	}

	if len(fx.ExpectNumberAbove) > 0 {
		reObj := parseToAny(t, name, reencoded)
		for path, boundNum := range fx.ExpectNumberAbove {
			got := resolvePath(t, name, reObj, path)
			bound, ok := asInt64(boundNum)
			gotN, ok2 := asInt64(got)
			if !ok || !ok2 {
				t.Fatalf("%s: expectNumberAbove[%q] — non-numeric (bound=%v got=%v)", name, path, boundNum, got)
			}
			if !(gotN > bound) {
				t.Fatalf("%s: expectNumberAbove[%q] — %d is not > %d", name, path, gotN, bound)
			}
			asserted = true
		}
	}

	if len(fx.ExpectReencodedAbsent) > 0 {
		reObj, ok := parseToAny(t, name, reencoded).(map[string]any)
		if !ok {
			t.Fatalf("%s: expectReencodedAbsent requires the re-encoded wire to be a JSON object, got %s", name, reencoded)
		}
		for _, key := range fx.ExpectReencodedAbsent {
			if _, present := reObj[key]; present {
				t.Fatalf("%s: re-encoded JSON must NOT contain key %q but it does. Re-encoded: %s", name, key, reencoded)
			}
			asserted = true
		}
	}

	if fx.Reencodes {
		assertCanonicalEqual(t, name, "reencodes (byte/structure-exact)", inputJSON, reencoded)
		asserted = true
	}

	if fx.RoundTripStable {
		_, reencoded2 := decodeAndReencode(t, name, fx.Type, reencoded)
		if len(fx.Expect) > 0 {
			re2 := parseToAny(t, name, reencoded2)
			for path, wantRaw := range fx.Expect {
				got := resolvePath(t, name, re2, path)
				assertJSONEquals(t, name, fmt.Sprintf("roundTripStable expect[%q] (2nd decode)", path), wantRaw, got)
			}
		} else {
			assertCanonicalEqual(t, name, "roundTripStable fixed-point", reencoded, reencoded2)
		}
		asserted = true
	}

	if !asserted {
		t.Fatalf("%s: fixture made no assertions — coverage theater", name)
	}
}

// decodedValue is the typed result of decoding a corpus wire type. Variant
// assertions inspect the active case off of it.
type decodedValue struct {
	kind  string
	value any
}

// decodeAndReencode decodes inputJSON into the real generated type named by
// `type` and re-encodes it with encoding/json. Returns both so assertions can
// inspect the decoded object (variant identity, flag bits) and the re-encoded
// wire (field paths, byte-exactness). Adding a wire type to the corpus is a
// deliberate edit here — the corpus never decodes arbitrary types reflectively.
// Mirrors the .NET / Swift decode-dispatch switches.
func decodeAndReencode(t *testing.T, name, typ, inputJSON string) (decodedValue, string) {
	t.Helper()

	dec := func(v any) {
		if err := json.Unmarshal([]byte(inputJSON), v); err != nil {
			t.Fatalf("%s: decode %s: %v", name, typ, err)
		}
	}
	enc := func(v any) string {
		out, err := json.Marshal(v)
		if err != nil {
			t.Fatalf("%s: re-encode %s: %v", name, typ, err)
		}
		return string(out)
	}

	switch typ {
	case "ActionEnvelope":
		var v ActionEnvelope
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "StateAction":
		var v StateAction
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "Customization":
		var v Customization
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "SessionStatus":
		var v SessionStatus
		dec(&v)
		return decodedValue{typ, v}, enc(v)
	case "StringOrMarkdown":
		var v StringOrMarkdown
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "JsonRpcMessage":
		var v JsonRpcMessage
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "ChangesetOperationTarget":
		var v ChangesetOperationTarget
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "SessionInputQuestion":
		var v SessionInputQuestion
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "SessionSummary":
		var v SessionSummary
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "SessionAddedParams":
		var v SessionAddedParams
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	case "PartialSessionSummary":
		var v PartialSessionSummary
		dec(&v)
		return decodedValue{typ, &v}, enc(&v)
	default:
		t.Fatalf("%s: round-trip fixture: unknown wire type %q. Add a decode entry to decodeAndReencode.", name, typ)
		return decodedValue{}, ""
	}
}

// ─── Variant identity (maps the corpus's concrete type names → Go variants) ───

func verifyVariant(t *testing.T, name string, decoded decodedValue, variants map[string]string) {
	t.Helper()
	for accessor, want := range variants {
		var actual string
		if accessor == "" {
			actual = wholeVariantTypeName(t, name, decoded)
		} else {
			actual = namedAccessorVariantTypeName(t, name, decoded, accessor)
		}
		if actual != want {
			t.Fatalf("%s: expectVariant[%q] — active variant is %q, expected %q", name, accessor, actual, want)
		}
	}
}

// wholeVariantTypeName maps the active case of a top-level decoded union to the
// corpus concrete-type name.
func wholeVariantTypeName(t *testing.T, name string, decoded decodedValue) string {
	t.Helper()
	switch decoded.kind {
	case "StateAction":
		return stateActionVariantName(t, name, decoded.value.(*StateAction))
	case "Customization":
		return customizationVariantName(t, name, decoded.value.(*Customization))
	case "ChangesetOperationTarget":
		return changesetTargetVariantName(t, name, decoded.value.(*ChangesetOperationTarget))
	case "SessionInputQuestion":
		return inputQuestionVariantName(t, name, decoded.value.(*SessionInputQuestion))
	default:
		t.Fatalf("%s: expectVariant[\"\"] not wired for decoded type %s", name, decoded.kind)
		return ""
	}
}

func namedAccessorVariantTypeName(t *testing.T, name string, decoded decodedValue, accessor string) string {
	t.Helper()
	switch {
	case decoded.kind == "ActionEnvelope" && strings.EqualFold(accessor, "action"):
		env := decoded.value.(*ActionEnvelope)
		return stateActionVariantName(t, name, &env.Action)
	default:
		t.Fatalf("%s: expectVariant accessor %q not wired for decoded type %s", name, accessor, decoded.kind)
		return ""
	}
}

func stateActionVariantName(t *testing.T, name string, a *StateAction) string {
	t.Helper()
	switch a.Value.(type) {
	case *SessionTitleChangedAction:
		return "SessionTitleChangedAction"
	case *StateActionUnknown:
		// The corpus names the unknown-passthrough case "JsonElement" (the .NET
		// raw-element type). Go's equivalent is StateActionUnknown{Raw}.
		return "JsonElement"
	default:
		// Derive a stable name from the concrete Go type for any other variant
		// the corpus might reference later (Go names already end in "Action").
		return reflect.TypeOf(a.Value).Elem().Name()
	}
}

func customizationVariantName(t *testing.T, name string, c *Customization) string {
	t.Helper()
	switch c.Value.(type) {
	case *PluginCustomization:
		return "PluginCustomization"
	case *DirectoryCustomization:
		return "DirectoryCustomization"
	case *CustomizationUnknown:
		return "JsonElement"
	default:
		t.Fatalf("%s: unmapped Customization variant %T", name, c.Value)
		return ""
	}
}

func changesetTargetVariantName(t *testing.T, name string, target *ChangesetOperationTarget) string {
	t.Helper()
	switch target.Value.(type) {
	case *ChangesetOperationResourceTarget:
		return "ChangesetOperationResourceTarget"
	case *ChangesetOperationRangeTarget:
		return "ChangesetOperationRangeTarget"
	default:
		t.Fatalf("%s: unmapped ChangesetOperationTarget variant %T", name, target.Value)
		return ""
	}
}

func inputQuestionVariantName(t *testing.T, name string, q *SessionInputQuestion) string {
	t.Helper()
	switch q.Value.(type) {
	case *SessionInputTextQuestion:
		return "SessionInputTextQuestion"
	// The corpus maps BOTH `number` and `integer` wire kinds to the same
	// concrete type (SessionInputNumberQuestion); Go decodes both into
	// *SessionInputNumberQuestion (the typed Kind field preserves the
	// distinction on the value, but the variant identity is shared).
	case *SessionInputNumberQuestion:
		return "SessionInputNumberQuestion"
	case *SessionInputBooleanQuestion:
		return "SessionInputBooleanQuestion"
	case *SessionInputSingleSelectQuestion:
		return "SessionInputSingleSelectQuestion"
	case *SessionInputMultiSelectQuestion:
		return "SessionInputMultiSelectQuestion"
	default:
		t.Fatalf("%s: unmapped SessionInputQuestion variant %T", name, q.Value)
		return ""
	}
}

// ─── JSON-RPC variant ─────────────────────────────────────────────────────

func verifyJsonRpcVariant(t *testing.T, name string, decoded decodedValue, kind string) {
	t.Helper()
	msg, ok := decoded.value.(*JsonRpcMessage)
	if !ok {
		t.Fatalf("%s: expectJsonRpcVariant requires a JsonRpcMessage, got %s", name, decoded.kind)
	}
	allowed := map[string]bool{"request": true, "notification": true, "success": true, "error": true}
	if !allowed[kind] {
		t.Fatalf("%s: expectJsonRpcVariant %q is not one of request/notification/success/error", name, kind)
	}
	// Exactly one pointer must be non-nil, and it must be the expected one.
	present := map[string]bool{
		"request":      msg.Request != nil,
		"notification": msg.Notification != nil,
		"success":      msg.SuccessResponse != nil,
		"error":        msg.ErrorResponse != nil,
	}
	for variant, isPresent := range present {
		shouldBe := variant == kind
		if isPresent != shouldBe {
			t.Fatalf("%s: expectJsonRpcVariant %q — %s is %s, expected %s",
				name, kind, variant, presence(isPresent), presence(shouldBe))
		}
	}
}

func presence(b bool) string {
	if b {
		return "present"
	}
	return "absent"
}

// ─── Bitset ───────────────────────────────────────────────────────────────

func verifyBitset(t *testing.T, name string, decoded decodedValue, reencoded string, b bitsetExpectation) {
	t.Helper()
	status, ok := decoded.value.(SessionStatus)
	if !ok {
		t.Fatalf("%s: expectBitset requires a SessionStatus, got %s", name, decoded.kind)
	}

	for _, flagName := range b.Has {
		flag := statusFlag(t, name, flagName)
		if !status.Has(flag) {
			t.Fatalf("%s: SessionStatus must have flag %s but does not (value %d)", name, flagName, uint32(status))
		}
	}
	for _, flagName := range b.Lacks {
		flag := statusFlag(t, name, flagName)
		if status.Has(flag) {
			t.Fatalf("%s: SessionStatus must NOT have flag %s but does (value %d)", name, flagName, uint32(status))
		}
	}
	if b.Numeric != nil {
		want, err := b.Numeric.Int64()
		if err != nil {
			t.Fatalf("%s: expectBitset.numeric is not an integer: %v", name, err)
		}
		if int64(uint32(status)) != want {
			t.Fatalf("%s: SessionStatus numeric — got %d, expected %d", name, uint32(status), want)
		}
		// The re-encoded wire form must be the same bare number.
		reObj := parseToAny(t, name, reencoded)
		reNum, ok := asInt64(reObj)
		if !ok {
			t.Fatalf("%s: SessionStatus must re-encode as a JSON number, got %s", name, reencoded)
		}
		if reNum != want {
			t.Fatalf("%s: SessionStatus re-encoded numeric — got %d, expected %d", name, reNum, want)
		}
	}
}

// statusFlag maps a corpus SessionStatus flag name to the Go constant. The
// corpus uses the .NET PascalCase flag names.
func statusFlag(t *testing.T, name, flagName string) SessionStatus {
	t.Helper()
	switch flagName {
	case "Idle":
		return SessionStatusIdle
	case "Error":
		return SessionStatusError
	case "InProgress":
		return SessionStatusInProgress
	case "InputNeeded":
		return SessionStatusInputNeeded
	case "IsRead":
		return SessionStatusIsRead
	case "IsArchived":
		return SessionStatusIsArchived
	default:
		t.Fatalf("%s: unknown SessionStatus flag %q", name, flagName)
		return 0
	}
}

// ─── ProtocolVersion constants ─────────────────────────────────────────────

func verifyProtocolConstant(t *testing.T, name string, fx roundTripFixture) {
	t.Helper()
	if len(fx.ExpectConstant) == 0 {
		t.Fatalf("%s: ProtocolVersion fixture missing expectConstant", name)
	}
	asserted := false

	if raw, ok := fx.ExpectConstant["current"]; ok {
		var want string
		if err := json.Unmarshal(raw, &want); err != nil {
			t.Fatalf("%s: expectConstant.current not a string: %v", name, err)
		}
		if want != "non-empty" {
			t.Fatalf("%s: expectConstant.current must be \"non-empty\", got %q", name, want)
		}
		if strings.TrimSpace(ProtocolVersion) == "" {
			t.Fatalf("%s: ProtocolVersion must be non-empty", name)
		}
		asserted = true
	}

	if raw, ok := fx.ExpectConstant["supported"]; ok {
		var want string
		if err := json.Unmarshal(raw, &want); err != nil {
			t.Fatalf("%s: expectConstant.supported not a string: %v", name, err)
		}
		if want != "non-empty-list" {
			t.Fatalf("%s: expectConstant.supported must be \"non-empty-list\", got %q", name, want)
		}
		if len(SupportedProtocolVersions()) == 0 {
			t.Fatalf("%s: SupportedProtocolVersions() must be non-empty", name)
		}
		asserted = true
	}

	if raw, ok := fx.ExpectConstant["firstSupportedEqualsCurrent"]; ok {
		var want bool
		if err := json.Unmarshal(raw, &want); err != nil {
			t.Fatalf("%s: expectConstant.firstSupportedEqualsCurrent not a bool: %v", name, err)
		}
		if want {
			sup := SupportedProtocolVersions()
			if len(sup) == 0 {
				t.Fatalf("%s: SupportedProtocolVersions() is empty", name)
			}
			if sup[0] != ProtocolVersion {
				t.Fatalf("%s: first supported %q != current %q", name, sup[0], ProtocolVersion)
			}
			asserted = true
		}
	}

	if !asserted {
		t.Fatalf("%s: ProtocolVersion fixture asserted no constant", name)
	}
}

// ─── Input bytes ────────────────────────────────────────────────────────────

func readInputJSON(t *testing.T, name string, fx roundTripFixture) string {
	t.Helper()
	hasRaw := fx.WireRaw != nil
	hasWire := len(fx.Wire) > 0
	if hasRaw == hasWire {
		t.Fatalf("%s: exactly one of `wire` / `wireRaw` is required (wire=%v, wireRaw=%v)", name, hasWire, hasRaw)
	}
	if hasRaw {
		// `wireRaw` is a JSON string whose CONTENT is the exact bytes to decode.
		return *fx.WireRaw
	}
	// `wire` is a JSON value; compact-serialize it.
	return string(compactJSON(t, name, fx.Wire))
}

func compactJSON(t *testing.T, name string, raw json.RawMessage) []byte {
	t.Helper()
	var v any
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("%s: compact wire: %v", name, err)
	}
	out, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("%s: compact wire marshal: %v", name, err)
	}
	return out
}

// ─── JSON path + equality ───────────────────────────────────────────────────

// parseToAny decodes a JSON document into a generic value using json.Number so
// large 64-bit integers stay exact (the default float64 would lose precision
// above 2^53; fixture 016 lives above Int32.MaxValue and must stay exact).
func parseToAny(t *testing.T, name, s string) any {
	t.Helper()
	d := json.NewDecoder(strings.NewReader(s))
	d.UseNumber()
	var out any
	if err := d.Decode(&out); err != nil {
		t.Fatalf("%s: parse JSON %q: %v", name, s, err)
	}
	return out
}

// resolvePath resolves a dotted path against a parsed JSON value. The empty path
// returns the value itself (scalar unions whose whole value is the payload).
func resolvePath(t *testing.T, name string, root any, path string) any {
	t.Helper()
	if path == "" {
		return root
	}
	cur := root
	for _, seg := range strings.Split(path, ".") {
		obj, ok := cur.(map[string]any)
		if !ok {
			t.Fatalf("%s: path %q — segment %q: parent is not an object", name, path, seg)
		}
		next, ok := obj[seg]
		if !ok {
			t.Fatalf("%s: path %q — segment %q not found", name, path, seg)
		}
		cur = next
	}
	return cur
}

// assertJSONEquals compares a fixture-declared expected value (raw JSON) against
// a resolved actual value, numerically-aware so 0 == 0.0 and large ints stay
// exact.
func assertJSONEquals(t *testing.T, name, ctx string, wantRaw json.RawMessage, got any) {
	t.Helper()
	want := parseToAny(t, name, string(wantRaw))

	// Numbers: compare via int64 first (exact), then float.
	if wn, ok := asInt64(want); ok {
		if gn, ok2 := asInt64(got); ok2 {
			if wn != gn {
				t.Fatalf("%s: %s — expected number %d, got %d", name, ctx, wn, gn)
			}
			return
		}
		t.Fatalf("%s: %s — expected number %d, got %s", name, ctx, wn, describe(got))
	}
	if wf, ok := asFloat64(want); ok {
		gf, ok2 := asFloat64(got)
		if !ok2 || wf != gf {
			t.Fatalf("%s: %s — expected number %v, got %s", name, ctx, wf, describe(got))
		}
		return
	}

	// Everything else (string, bool, null, object, array): canonical compare.
	if !canonicalJSONEqual(t, name, want, got) {
		t.Fatalf("%s: %s — expected %s, got %s", name, ctx, describe(want), describe(got))
	}
}

// assertCanonicalEqual compares two JSON documents structurally (key-order
// independent, value- and key-presence sensitive). Used for reencodes /
// fixed-point checks.
func assertCanonicalEqual(t *testing.T, name, ctx, lhs, rhs string) {
	t.Helper()
	lo := parseToAny(t, name, lhs)
	ro := parseToAny(t, name, rhs)
	if !canonicalJSONEqual(t, name, lo, ro) {
		t.Fatalf("%s: %s\n  lhs: %s\n  rhs: %s", name, ctx, lhs, rhs)
	}
}

// canonicalJSONEqual re-serializes both sides through encoding/json after
// normalizing json.Number values, so equality is structural. We round-trip each
// side through a canonicalizing marshal that sorts object keys (encoding/json
// already sorts map keys) and renders numbers from json.Number verbatim.
func canonicalJSONEqual(t *testing.T, name string, a, b any) bool {
	t.Helper()
	return canonicalString(t, name, a) == canonicalString(t, name, b)
}

func canonicalString(t *testing.T, name string, v any) string {
	t.Helper()
	out, err := json.Marshal(normalizeNumbers(v))
	if err != nil {
		t.Fatalf("%s: canonicalize: %v", name, err)
	}
	return string(out)
}

// normalizeNumbers walks a generic JSON value and converts json.Number leaves to
// a canonical numeric form so that, e.g., 0 and 0.0 compare equal. Integers (no
// fractional part) become int64; everything else becomes float64.
func normalizeNumbers(v any) any {
	switch x := v.(type) {
	case map[string]any:
		out := make(map[string]any, len(x))
		for k, val := range x {
			out[k] = normalizeNumbers(val)
		}
		return out
	case []any:
		out := make([]any, len(x))
		for i, val := range x {
			out[i] = normalizeNumbers(val)
		}
		return out
	case json.Number:
		if i, err := x.Int64(); err == nil {
			return i
		}
		if f, err := x.Float64(); err == nil {
			return f
		}
		return x.String()
	default:
		return v
	}
}

func asInt64(v any) (int64, bool) {
	switch x := v.(type) {
	case json.Number:
		i, err := x.Int64()
		if err != nil {
			return 0, false
		}
		return i, true
	case int64:
		return x, true
	case int:
		return int64(x), true
	case float64:
		if x == float64(int64(x)) {
			return int64(x), true
		}
		return 0, false
	default:
		return 0, false
	}
}

func asFloat64(v any) (float64, bool) {
	switch x := v.(type) {
	case json.Number:
		f, err := x.Float64()
		if err != nil {
			return 0, false
		}
		return f, true
	case float64:
		return x, true
	case int64:
		return float64(x), true
	case int:
		return float64(x), true
	default:
		return 0, false
	}
}

func describe(v any) string {
	switch x := v.(type) {
	case string:
		return fmt.Sprintf("string %q", x)
	case nil:
		return "null"
	case json.Number:
		return "number " + x.String()
	default:
		b, err := json.Marshal(normalizeNumbers(v))
		if err != nil {
			return fmt.Sprintf("%v", v)
		}
		return string(b)
	}
}
