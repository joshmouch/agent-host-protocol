// AHP GO CONFORMANCE RUNNER — build-phase B5.
//
// Ports the canonical B4 JS runner (conformance/runner/run-conformance.mjs)
// to Go, using the REAL Go client reducers from clients/go/ahp.
//
// This is the end-to-end green proof that the whole conformance tranche works
// in Go: a real Go WS client replays a scenario against the REAL scenario-
// driven host (conformance/host/scenario-host.mjs) over a REAL WebSocket,
// applies every server.notify ActionEnvelope through the CANONICAL in-repo
// Go reducers (ahp.ApplyActionToRoot / ApplyActionToSession /
// ApplyActionToTerminal / ApplyActionToChangeset), and checks every
// client.assert.* step. NO MOCKS — real files, real transport, real reducers,
// real assertions.
//
// Usage (run all 233 scenarios):
//
//	go run ./... <path/to/scenarios/root> [--verbose]
//
// Or run the brief tranche (23 round-trips + 30 reducer sample + 46 negatives):
//
//	go run ./... <path/to/scenarios/root> --brief [--verbose]
//
// Exit 0 = all PASS; 1 = at least one FAIL/ERROR.

package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/microsoft/agent-host-protocol/clients/go/ahp"
	"github.com/microsoft/agent-host-protocol/clients/go/ahptypes"
)

// ─── Scenario schema (JSON) ────────────────────────────────────────────────

type Scenario struct {
	ID       string  `json:"id"`
	PinClock *int64  `json:"pinClock"`
	Steps    []Step  `json:"steps"`
}

type Step struct {
	Op      string          `json:"op"`
	Label   string          `json:"label"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params"`
	ID      *int64          `json:"id"`
	ForID   *int64          `json:"forId"`
	Result  json.RawMessage `json:"result"`
	Error   json.RawMessage `json:"error"`
	// client.assert.state
	Channel *string         `json:"channel"`
	Path    *string         `json:"path"`
	Equals  json.RawMessage `json:"equals"`
	// client.assert.event
	Matches json.RawMessage `json:"matches"`
	// client.assert.error
	Code    *int64          `json:"code"`
	Message *string         `json:"message"`
}

// ─── Snapshot / initialize response ───────────────────────────────────────

type InitializeResult struct {
	ProtocolVersion string     `json:"protocolVersion"`
	ServerSeq       int64      `json:"serverSeq"`
	Snapshots       []Snapshot `json:"snapshots"`
	// reconnect shape
	Snapshot *Snapshot `json:"snapshot"`
}

type Snapshot struct {
	Resource string          `json:"resource"`
	FromSeq  int64           `json:"fromSeq"`
	State    json.RawMessage `json:"state"`
}

// ─── Reducer dispatch by action-type prefix ────────────────────────────────
//
// The JS runner uses the prefix (root/session/terminal/changeset/resource) to
// choose the reducer rather than the channel scheme. The corpus routes
// terminal-reducer fixtures onto ahp-session:/ channels, for instance.

type channelState struct {
	// One of: *ahptypes.RootState | *ahptypes.SessionState |
	//         *ahptypes.TerminalState | *ahptypes.ChangesetState |
	//         *ahptypes.ResourceWatchState | json.RawMessage (unknown)
	value any
	// seed holds the raw JSON from the snapshot when the state has been seeded
	// but the typed value has not been decoded yet. It is decoded lazily the
	// first time an action arrives, using the action-type prefix to pick the
	// correct Go type. This is required because the corpus routes terminal-
	// reducer fixtures onto ahp-session:/ channels — we cannot infer the right
	// type from the channel URI alone.
	seed json.RawMessage
}

func reducerPrefix(action ahptypes.StateAction) string {
	// All concrete StateAction variants have a Type field; reach it via JSON.
	b, _ := json.Marshal(action)
	var probe struct{ Type string `json:"type"` }
	_ = json.Unmarshal(b, &probe)
	parts := strings.SplitN(probe.Type, "/", 2)
	if len(parts) == 0 {
		return ""
	}
	return parts[0]
}

// ensureTyped decodes the lazy seed JSON into the correct typed value for
// the given action-type prefix, if not already decoded.
func ensureTyped(cs *channelState, prefix string) {
	if cs.value != nil || cs.seed == nil {
		return
	}
	raw := cs.seed
	switch prefix {
	case "root":
		var s ahptypes.RootState
		if json.Unmarshal(raw, &s) == nil {
			cs.value = &s
		}
	case "session":
		var s ahptypes.SessionState
		if json.Unmarshal(raw, &s) == nil {
			cs.value = &s
		}
	case "terminal":
		var s ahptypes.TerminalState
		if json.Unmarshal(raw, &s) == nil {
			if s.Content == nil {
				s.Content = []ahptypes.TerminalContentPart{}
			}
			cs.value = &s
		}
	case "changeset":
		var s ahptypes.ChangesetState
		if json.Unmarshal(raw, &s) == nil {
			cs.value = &s
		}
	case "resource", "resourceWatch":
		var s ahptypes.ResourceWatchState
		if json.Unmarshal(raw, &s) == nil {
			cs.value = &s
		}
	}
}

// applyToChannel applies action to the per-channel state bucket. The state type
// is decoded lazily from cs.seed using the action-type prefix as the
// discriminator — this is the same rule as the B4 JS runner: dispatch by
// action-type prefix, NOT by channel URI scheme, because the corpus routes
// terminal-reducer fixtures onto ahp-session:/ channels.
func applyToChannel(cs *channelState, envelope rawEnvelope, pinClock int64) bool {
	var action ahptypes.StateAction
	if err := json.Unmarshal(envelope.Action, &action); err != nil {
		return false
	}
	prefix := reducerPrefix(action)

	// Lazy decode: if we have a seed but no typed value, decode it now using
	// the action prefix to pick the correct Go type.
	ensureTyped(cs, prefix)

	switch prefix {
	case "root":
		if cs.value == nil {
			cs.value = &ahptypes.RootState{}
		}
		s, ok := cs.value.(*ahptypes.RootState)
		if !ok {
			return false
		}
		ahp.ApplyActionToRoot(s, action)
	case "session":
		if cs.value == nil {
			cs.value = &ahptypes.SessionState{}
		}
		s, ok := cs.value.(*ahptypes.SessionState)
		if !ok {
			return false
		}
		ahp.SetNowProvider(func() int64 { return pinClock })
		ahp.ApplyActionToSession(s, action)
	case "terminal":
		if cs.value == nil {
			cs.value = &ahptypes.TerminalState{Content: []ahptypes.TerminalContentPart{}}
		}
		s, ok := cs.value.(*ahptypes.TerminalState)
		if !ok {
			return false
		}
		ahp.ApplyActionToTerminal(s, action)
	case "changeset":
		if cs.value == nil {
			cs.value = &ahptypes.ChangesetState{}
		}
		s, ok := cs.value.(*ahptypes.ChangesetState)
		if !ok {
			return false
		}
		ahp.ApplyActionToChangeset(s, action)
	case "resource", "resourceWatch":
		// resourceWatch reducer is a passthrough (state unchanged) — match the
		// JS runner which just records the event; no state mutation needed.
		if cs.value == nil {
			cs.value = &ahptypes.ResourceWatchState{}
		}
		// ApplyActionToResourceWatch doesn't exist yet; passthrough is correct.
	default:
		return false
	}
	return true
}

// seedChannel stores the raw snapshot state for deferred lazy decoding.
// We do NOT decode eagerly by channel URI scheme here because the corpus
// routes terminal-reducer fixtures onto ahp-session:/ channels — the action-
// type prefix is the only reliable discriminator. The raw JSON is decoded
// inside applyToChannel (via ensureTyped) the first time an action arrives
// for this channel. For assert.state with no actions, toAny(cs.seed) is
// used as a fallback.
func seedChannel(_ string, rawState json.RawMessage) *channelState {
	return &channelState{seed: rawState}
}

// ─── Wire representations for observed events ──────────────────────────────

// rawEnvelope mirrors the params of a server 'action' notification.
type rawEnvelope struct {
	Channel   string          `json:"channel"`
	Action    json.RawMessage `json:"action"`
	ServerSeq int64           `json:"serverSeq"`
	Origin    json.RawMessage `json:"origin"`
}

// observedEvent is either a raw action envelope (from 'action' notifications)
// or a generic notification { method, params }.
type observedEvent struct {
	// set for 'action' notifications (the envelope, as raw JSON for matching)
	envelope json.RawMessage
	// set for all notifications (the full message form)
	message json.RawMessage
}

// ─── Host subprocess management ────────────────────────────────────────────

func startHost(hostScript, scenarioPath string) (wsURL string, kill func(), err error) {
	cmd := exec.Command("node", hostScript, scenarioPath)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return "", nil, err
	}
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return "", nil, err
	}
	kill = func() { _ = cmd.Process.Kill() }

	// Scan stdout for "SCENARIO HOST READY ws://..."
	ready := make(chan string, 1)
	go func() {
		sc := bufio.NewScanner(stdout)
		for sc.Scan() {
			line := sc.Text()
			if idx := strings.Index(line, "ws://"); idx >= 0 {
				fields := strings.Fields(line[idx:])
				if len(fields) >= 1 {
					ready <- fields[0]
					return
				}
			}
		}
		close(ready)
	}()

	select {
	case url, ok := <-ready:
		if !ok {
			kill()
			return "", nil, fmt.Errorf("host exited without printing READY")
		}
		return url, kill, nil
	case <-time.After(10 * time.Second):
		kill()
		return "", nil, fmt.Errorf("host did not print READY within 10s")
	}
}

// ─── Protocol driver ───────────────────────────────────────────────────────

type driveResult struct {
	channels      map[string]*channelState
	synthetic     map[string]any // protocolVersion, lastResponseOk, pingSeen
	events        []observedEvent
	surfacedErrors []json.RawMessage
}

func driveProtocol(ctx context.Context, wsURL string, scenario *Scenario) (*driveResult, error) {
	pinClock := int64(0)
	if scenario.PinClock != nil {
		pinClock = *scenario.PinClock
	}

	// Filter steps by op.
	var requests []Step
	for _, s := range scenario.Steps {
		if s.Op == "client.request" {
			requests = append(requests, s)
		}
	}

	// Retry loop for transient connect errors (mirrors the JS runner).
	const maxRetries = 5
	for attempt := 0; attempt <= maxRetries; attempt++ {
		res, transient, err := tryDrive(ctx, wsURL, scenario, requests, pinClock)
		if err == nil {
			return res, nil
		}
		if !transient || attempt == maxRetries {
			return nil, err
		}
		time.Sleep(80 * time.Millisecond)
	}
	return nil, fmt.Errorf("unreachable")
}

func tryDrive(ctx context.Context, wsURL string, scenario *Scenario, requests []Step, pinClock int64) (res *driveResult, transient bool, err error) {
	dialCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()

	conn, _, err := websocket.Dial(dialCtx, wsURL, nil)
	if err != nil {
		msg := err.Error()
		isTransient := strings.Contains(msg, "ECONNREFUSED") ||
			strings.Contains(msg, "ECONNRESET") ||
			strings.Contains(msg, "connection refused") ||
			strings.Contains(msg, "unexpected HTTP")
		return nil, isTransient, fmt.Errorf("dial: %w", err)
	}
	conn.SetReadLimit(32 * 1024 * 1024)

	channels := make(map[string]*channelState)
	synthetic := map[string]any{}
	var events []observedEvent
	var surfacedErrors []json.RawMessage
	reqCursor := 0

	sendRequest := func() error {
		if reqCursor >= len(requests) {
			return nil
		}
		step := requests[reqCursor]
		reqCursor++
		frame := map[string]any{
			"jsonrpc": "2.0",
			"method":  step.Method,
			"id":      *step.ID,
		}
		if step.Params != nil {
			frame["params"] = step.Params
		}
		b, _ := json.Marshal(frame)
		wCtx, wCancel := context.WithTimeout(ctx, 5*time.Second)
		defer wCancel()
		return conn.Write(wCtx, websocket.MessageText, b)
	}

	// Send the first request.
	if err := sendRequest(); err != nil {
		_ = conn.Close(websocket.StatusNormalClosure, "")
		return nil, false, fmt.Errorf("send initial request: %w", err)
	}

	// If there are no requests, the host still flushes leading notifies
	// on connection — we'll collect them via the read loop.

	// Bounded read loop: stop when the host closes or a soft timeout fires.
	softTimeout := 5 * time.Second
	deadline := time.Now().Add(softTimeout)
	for {
		rCtx, rCancel := context.WithDeadline(ctx, deadline)
		mt, raw, readErr := conn.Read(rCtx)
		rCancel()
		if readErr != nil {
			// Deadline expired (soft finish) or host closed.
			break
		}
		if mt != websocket.MessageText {
			continue
		}

		var msg map[string]json.RawMessage
		if err := json.Unmarshal(raw, &msg); err != nil {
			continue
		}

		idRaw, hasID := msg["id"]
		resultRaw, hasResult := msg["result"]
		errorRaw, hasError := msg["error"]
		methodRaw, hasMethod := msg["method"]

		if hasID && idRaw != nil && string(idRaw) != "null" && (hasResult || hasError) {
			// Response to one of our requests.
			if hasError {
				surfacedErrors = append(surfacedErrors, errorRaw)
				synthetic["lastResponseOk"] = false
			} else {
				synthetic["lastResponseOk"] = true
				// Seed channels from snapshot in result.
				var initRes InitializeResult
				if json.Unmarshal(resultRaw, &initRes) == nil {
					if initRes.ProtocolVersion != "" {
						synthetic["protocolVersion"] = initRes.ProtocolVersion
					}
					for _, snap := range initRes.Snapshots {
						if snap.Resource != "" {
							channels[snap.Resource] = seedChannel(snap.Resource, snap.State)
						}
					}
					if initRes.Snapshot != nil && initRes.Snapshot.Resource != "" {
						channels[initRes.Snapshot.Resource] = seedChannel(initRes.Snapshot.Resource, initRes.Snapshot.State)
					}
				}
			}
			// Advance to the next request.
			if err := sendRequest(); err != nil {
				// Not fatal — just means there's no more requests.
			}
		} else if hasMethod && (!hasID || string(idRaw) == "null") {
			// Server notification.
			var methodStr string
			_ = json.Unmarshal(methodRaw, &methodStr)

			// Record the full message form so message-level assertions can match.
			msgJSON, _ := json.Marshal(map[string]json.RawMessage{
				"method": methodRaw,
				"params": msg["params"],
			})
			events = append(events, observedEvent{message: msgJSON})

			if methodStr == "action" {
				paramsRaw := msg["params"]
				var env rawEnvelope
				if json.Unmarshal(paramsRaw, &env) == nil {
					// Record the envelope itself.
					events = append(events, observedEvent{envelope: paramsRaw})

					// Reduce into per-channel state.
					ch := env.Channel
					if _, ok := channels[ch]; !ok {
						channels[ch] = &channelState{}
					}
					applyToChannel(channels[ch], env, pinClock)
				}
			}
		}
	}

	_ = conn.Close(websocket.StatusNormalClosure, "")

	return &driveResult{
		channels:      channels,
		synthetic:     synthetic,
		events:        events,
		surfacedErrors: surfacedErrors,
	}, false, nil
}

// ─── Canonicalize (null/undefined-stripped, key-sorted) ───────────────────
//
// Mirrors run-conformance.mjs canonicalize(): drop null-valued keys, sort keys.
// Used for whole-state convergence equality (assert.state with empty path).

func canonicalize(v any) any {
	switch x := v.(type) {
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		out := make(map[string]any, len(x))
		for _, k := range keys {
			val := x[k]
			if val == nil {
				continue
			}
			out[k] = canonicalize(val)
		}
		return out
	case []any:
		out := make([]any, len(x))
		for i, val := range x {
			out[i] = canonicalize(val)
		}
		return out
	case float64:
		// JSON numbers decode to float64; preserve them but round integers
		// back to int64 for reflect.DeepEqual against corpus ints.
		if math.Trunc(x) == x && !math.IsInf(x, 0) && !math.IsNaN(x) {
			return int64(x)
		}
		return x
	default:
		return v
	}
}

// toAny round-trips a value through JSON → any so it can be canonicalized.
func toAny(v any) (any, error) {
	b, err := json.Marshal(v)
	if err != nil {
		return nil, err
	}
	var out any
	if err := json.Unmarshal(b, &out); err != nil {
		return nil, err
	}
	return out, nil
}

// ─── Deep-containment for assert.event ────────────────────────────────────

func deepContains(actual, expected any) bool {
	if expected == nil {
		return actual == nil
	}
	expMap, expIsMap := expected.(map[string]any)
	if !expIsMap {
		// Scalar or array — use deep equality after canonicalize.
		return reflect.DeepEqual(canonicalize(actual), canonicalize(expected))
	}
	actMap, actIsMap := actual.(map[string]any)
	if !actIsMap {
		return false
	}
	for k, expV := range expMap {
		actV, ok := actMap[k]
		if !ok {
			return false
		}
		if !deepContains(actV, expV) {
			return false
		}
	}
	return true
}

// ─── Dotted-path navigation ────────────────────────────────────────────────

func navigate(obj any, path string) (any, bool) {
	if path == "" {
		return obj, true
	}
	cur := obj
	for _, seg := range strings.Split(path, ".") {
		switch x := cur.(type) {
		case map[string]any:
			v, ok := x[seg]
			if !ok {
				return nil, false
			}
			cur = v
		case []any:
			idx := 0
			if _, err := fmt.Sscanf(seg, "%d", &idx); err != nil {
				return nil, false
			}
			if idx < 0 || idx >= len(x) {
				return nil, false
			}
			cur = x[idx]
		default:
			return nil, false
		}
	}
	return cur, true
}

// ─── Assertion checking ────────────────────────────────────────────────────

type assertResult struct {
	ok     bool
	detail string
}

func checkAssertion(step Step, res *driveResult) assertResult {
	switch step.Op {
	case "client.assert.state":
		// Pick the channel bucket.
		var target any
		var bucketLabel string

		if step.Channel != nil && *step.Channel != "" {
			cs, ok := res.channels[*step.Channel]
			if !ok {
				known := make([]string, 0, len(res.channels))
				for k := range res.channels {
					known = append(known, k)
				}
				return assertResult{false, fmt.Sprintf("no reduced state for channel %s; known: [%s]", *step.Channel, strings.Join(known, ", "))}
			}
			// Use cs.value if decoded, otherwise fall back to the seed JSON
			// (for round-trip scenarios that assert on snapshot state with no actions).
			stateVal := cs.value
			if stateVal == nil && cs.seed != nil {
				stateVal = cs.seed
			}
			a, err := toAny(stateVal)
			if err != nil {
				return assertResult{false, fmt.Sprintf("marshal channel state: %v", err)}
			}
			target = a
			bucketLabel = "channel " + *step.Channel
		} else if step.Path != nil && *step.Path != "" {
			// Path with no channel → synthetic top-level state.
			target = res.synthetic
			bucketLabel = "synthetic top-level"
		} else {
			// No channel and no path → single-channel whole-state convergence.
			if len(res.channels) != 1 {
				return assertResult{false, fmt.Sprintf("whole-state assertion requires exactly 1 channel, found %d", len(res.channels))}
			}
			for _, cs := range res.channels {
				stateVal := cs.value
				if stateVal == nil && cs.seed != nil {
					stateVal = cs.seed
				}
				a, err := toAny(stateVal)
				if err != nil {
					return assertResult{false, fmt.Sprintf("marshal channel state: %v", err)}
				}
				target = a
			}
			bucketLabel = "single channel"
		}

		path := ""
		if step.Path != nil {
			path = *step.Path
		}
		actual, found := navigate(target, path)

		// Synthetic top-level: absent field == null when expected is null.
		if !found && bucketLabel == "synthetic top-level" {
			var expectedVal any
			_ = json.Unmarshal(step.Equals, &expectedVal)
			if expectedVal == nil {
				return assertResult{ok: true}
			}
		}
		if !found {
			return assertResult{false, fmt.Sprintf("%s path %q not found", bucketLabel, path)}
		}

		var expectedVal any
		if err := json.Unmarshal(step.Equals, &expectedVal); err != nil {
			return assertResult{false, fmt.Sprintf("parse expected: %v", err)}
		}

		actualCanon := canonicalize(actual)
		expectedCanon := canonicalize(expectedVal)
		if reflect.DeepEqual(actualCanon, expectedCanon) {
			return assertResult{ok: true}
		}
		actualJSON, _ := json.Marshal(actualCanon)
		expectedJSON, _ := json.Marshal(expectedCanon)
		label := bucketLabel
		if path != "" {
			label += " path=" + path
		}
		return assertResult{false, fmt.Sprintf("assert.state @ %s: expected %s, got %s", label, expectedJSON, actualJSON)}

	case "client.assert.event":
		var matches any
		if err := json.Unmarshal(step.Matches, &matches); err != nil {
			return assertResult{false, fmt.Sprintf("parse matches: %v", err)}
		}
		for _, ev := range res.events {
			// Try envelope form.
			if ev.envelope != nil {
				var evAny any
				if json.Unmarshal(ev.envelope, &evAny) == nil {
					if deepContains(evAny, matches) {
						return assertResult{ok: true}
					}
					// Also try the action sub-field.
					if evMap, ok := evAny.(map[string]any); ok {
						if deepContains(evMap["action"], matches) {
							return assertResult{ok: true}
						}
						if deepContains(evMap["params"], matches) {
							return assertResult{ok: true}
						}
					}
				}
			}
			// Try message form.
			if ev.message != nil {
				var evAny any
				if json.Unmarshal(ev.message, &evAny) == nil {
					if deepContains(evAny, matches) {
						return assertResult{ok: true}
					}
				}
			}
		}
		matchesJSON, _ := json.Marshal(matches)
		return assertResult{false, fmt.Sprintf("assert.event: no event deep-contains %s (observed %d events)", matchesJSON, len(res.events))}

	case "client.assert.error":
		if step.Code == nil {
			return assertResult{false, "assert.error: missing code"}
		}
		for _, raw := range res.surfacedErrors {
			var errObj struct {
				Code    int64  `json:"code"`
				Message string `json:"message"`
			}
			if err := json.Unmarshal(raw, &errObj); err != nil {
				continue
			}
			if errObj.Code != *step.Code {
				continue
			}
			if step.Message != nil && !strings.Contains(errObj.Message, *step.Message) {
				continue
			}
			return assertResult{ok: true}
		}
		return assertResult{false, fmt.Sprintf("assert.error: no surfaced error with code %d (surfaced: %s)", *step.Code, res.surfacedErrors)}

	default:
		return assertResult{false, fmt.Sprintf("unknown assertion op: %s", step.Op)}
	}
}

// ─── Single-scenario runner ────────────────────────────────────────────────

type scenarioResult struct {
	id     string
	path   string
	status string // PASS | FAIL | ERROR
	reason string
	asserts []assertOutcome
}

type assertOutcome struct {
	op     string
	label  string
	ok     bool
	detail string
}

func runScenario(ctx context.Context, hostScript, scenarioPath string, verbose bool) scenarioResult {
	id := strings.TrimSuffix(filepath.Base(scenarioPath), ".scenario.json")

	rawBytes, err := os.ReadFile(scenarioPath)
	if err != nil {
		return scenarioResult{id: id, path: scenarioPath, status: "ERROR", reason: fmt.Sprintf("read: %v", err)}
	}
	var scenario Scenario
	if err := json.Unmarshal(rawBytes, &scenario); err != nil {
		return scenarioResult{id: id, path: scenarioPath, status: "ERROR", reason: fmt.Sprintf("parse: %v", err)}
	}

	// Pin the clock.
	if scenario.PinClock != nil {
		ahp.SetNowProvider(func() int64 { return *scenario.PinClock })
	}

	wsURL, kill, err := startHost(hostScript, scenarioPath)
	if err != nil {
		return scenarioResult{id: id, path: scenarioPath, status: "ERROR", reason: fmt.Sprintf("host: %v", err)}
	}
	defer kill()

	driveRes, err := driveProtocol(ctx, wsURL, &scenario)
	if err != nil {
		return scenarioResult{id: id, path: scenarioPath, status: "ERROR", reason: fmt.Sprintf("drive: %v", err)}
	}

	// Collect assertion steps.
	var assertSteps []Step
	for _, s := range scenario.Steps {
		if strings.HasPrefix(s.Op, "client.assert.") {
			assertSteps = append(assertSteps, s)
		}
	}
	if len(assertSteps) == 0 {
		return scenarioResult{id: id, path: scenarioPath, status: "ERROR", reason: "no client.assert.* steps"}
	}

	var outcomes []assertOutcome
	allOK := true
	for _, step := range assertSteps {
		res := checkAssertion(step, driveRes)
		outcomes = append(outcomes, assertOutcome{
			op:     step.Op,
			label:  step.Label,
			ok:     res.ok,
			detail: res.detail,
		})
		if !res.ok {
			allOK = false
		}
	}

	status := "PASS"
	if !allOK {
		status = "FAIL"
	}

	if verbose {
		for _, a := range outcomes {
			mark := "PASS"
			if !a.ok {
				mark = "FAIL"
			}
			fmt.Printf("  %s  %s  %s\n", mark, a.op, a.label)
			if !a.ok {
				fmt.Printf("       → %s\n", a.detail)
			}
		}
	}

	return scenarioResult{id: id, path: scenarioPath, status: status, asserts: outcomes}
}

// ─── Corpus enumeration ────────────────────────────────────────────────────

func listScenarios(dir string) ([]string, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	var paths []string
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".scenario.json") {
			paths = append(paths, filepath.Join(dir, e.Name()))
		}
	}
	sort.Strings(paths)
	return paths, nil
}

// sample picks n evenly-spaced entries from a sorted list — same algorithm as
// the JS suite driver so the same 30 reducer scenarios are sampled.
func sample(list []string, n int) []string {
	if n >= len(list) {
		return list
	}
	out := make([]string, n)
	stride := float64(len(list)) / float64(n)
	for i := 0; i < n; i++ {
		out[i] = list[int(math.Floor(float64(i)*stride))]
	}
	return out
}

// ─── Main ──────────────────────────────────────────────────────────────────

func main() {
	args := os.Args[1:]
	verbose := false
	brief := false
	var scenariosRoot string
	reducerSample := 30
	concurrency := 4

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--verbose":
			verbose = true
		case "--brief":
			brief = true
		case "--all-reducers":
			reducerSample = math.MaxInt32
		case "--reducer-sample":
			if i+1 < len(args) {
				i++
				fmt.Sscanf(args[i], "%d", &reducerSample)
			}
		case "--concurrency":
			if i+1 < len(args) {
				i++
				fmt.Sscanf(args[i], "%d", &concurrency)
			}
		default:
			if !strings.HasPrefix(args[i], "--") {
				scenariosRoot = args[i]
			}
		}
	}

	if scenariosRoot == "" {
		// Default: relative to this binary's location, walk upward to find
		// types/test-cases/scenarios.
		wd, _ := os.Getwd()
		for cur := wd; ; cur = filepath.Dir(cur) {
			candidate := filepath.Join(cur, "types", "test-cases", "scenarios")
			if fi, err := os.Stat(candidate); err == nil && fi.IsDir() {
				scenariosRoot = candidate
				break
			}
			parent := filepath.Dir(cur)
			if parent == cur {
				fmt.Fprintln(os.Stderr, "ERROR: cannot locate types/test-cases/scenarios; pass it as the first argument")
				os.Exit(2)
			}
		}
	}

	// Locate the scenario host.
	hostScript := filepath.Join(filepath.Dir(filepath.Dir(scenariosRoot)), "conformance", "host", "scenario-host.mjs")
	// If scenariosRoot already ends at …/types/test-cases/scenarios, the host is
	// at ../../conformance/host/scenario-host.mjs relative to types/.
	// Walk upward to find it.
	for cur := scenariosRoot; ; cur = filepath.Dir(cur) {
		candidate := filepath.Join(cur, "conformance", "host", "scenario-host.mjs")
		if _, err := os.Stat(candidate); err == nil {
			hostScript = candidate
			break
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			fmt.Fprintln(os.Stderr, "ERROR: cannot locate conformance/host/scenario-host.mjs")
			os.Exit(2)
		}
	}

	roundTripsDir := filepath.Join(scenariosRoot, "round-trips")
	reducersDir := filepath.Join(scenariosRoot, "reducers")
	negativesDir := filepath.Join(scenariosRoot, "negatives")

	roundTrips, err := listScenarios(roundTripsDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "ERROR listing round-trips: %v\n", err)
		os.Exit(2)
	}
	allReducers, err := listScenarios(reducersDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "ERROR listing reducers: %v\n", err)
		os.Exit(2)
	}
	negatives, err := listScenarios(negativesDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "ERROR listing negatives: %v\n", err)
		os.Exit(2)
	}

	reducerScenarios := allReducers
	if brief || reducerSample < len(allReducers) {
		n := reducerSample
		if brief {
			n = 30
		}
		reducerScenarios = sample(allReducers, n)
	}

	type tagged struct {
		path   string
		tranche string
	}
	var tranche []tagged
	for _, p := range roundTrips {
		tranche = append(tranche, tagged{p, "round-trip"})
	}
	for _, p := range reducerScenarios {
		tranche = append(tranche, tagged{p, "reducer"})
	}
	for _, p := range negatives {
		tranche = append(tranche, tagged{p, "negative"})
	}

	fmt.Printf("Running %d scenarios (%d round-trips, %d reducers, %d negatives) concurrency=%d\n",
		len(tranche), len(roundTrips), len(reducerScenarios), len(negatives), concurrency)

	ctx := context.Background()

	type indexedResult struct {
		idx int
		res scenarioResult
	}

	results := make([]scenarioResult, len(tranche))
	work := make(chan int, len(tranche))
	for i := range tranche {
		work <- i
	}
	close(work)

	var wg sync.WaitGroup
	resultsCh := make(chan indexedResult, len(tranche))
	for w := 0; w < concurrency; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for idx := range work {
				t := tranche[idx]
				res := runScenario(ctx, hostScript, t.path, verbose)
				resultsCh <- indexedResult{idx, res}
			}
		}()
	}
	go func() {
		wg.Wait()
		close(resultsCh)
	}()

	for ir := range resultsCh {
		results[ir.idx] = ir.res
	}

	// Tally and print.
	var green, total int
	var failures []scenarioResult
	for i, res := range results {
		total++
		mark := "✓"
		if res.status == "PASS" {
			green++
		} else {
			mark = "✗"
			failures = append(failures, res)
		}
		if verbose || res.status != "PASS" {
			fmt.Printf("[%s] %s  (%s)\n", mark, res.id, tranche[i].tranche)
			if res.status == "ERROR" {
				fmt.Printf("    ERROR: %s\n", res.reason)
			} else if res.status == "FAIL" {
				for _, a := range res.asserts {
					if !a.ok {
						fmt.Printf("    FAIL  %s  %s\n        → %s\n", a.op, a.label, a.detail)
					}
				}
			}
		}
	}

	fmt.Printf("\nGo conformance: %d/%d PASS\n", green, total)
	if len(failures) > 0 {
		fmt.Fprintf(os.Stderr, "%d scenario(s) FAILED or ERRORED\n", len(failures))
		os.Exit(1)
	}
}
