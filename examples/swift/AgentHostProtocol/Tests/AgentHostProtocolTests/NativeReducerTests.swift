// NativeReducerTests.swift — Tests for the protocol-based native Swift reducers.
// Runs the same behavioral tests as ReducersTests.swift but using the
// AHPRootReducer and AHPSessionReducer (protocol-based `Reducer` conformers).
//
// This validates that the native Swift reducer pattern produces identical
// results to the free-function reducers.

import XCTest
@testable import AgentHostProtocol

final class NativeReducerTests: XCTestCase {

    // MARK: - Constants

    private let S = "copilot:/test-session"
    private let T = "turn-1"
    private let TC = "tc-1"

    // MARK: - Reducers under test

    private let rootR = AHPRootReducer()
    private let sessionR = AHPSessionReducer()

    // MARK: - Test Fixtures

    private func makeRootState(agents: [AgentInfo] = []) -> RootState {
        RootState(agents: agents)
    }

    private func makeSessionState(
        lifecycle: SessionLifecycle = .creating,
        status: SessionStatus = .idle,
        steeringMessage: PendingMessage? = nil,
        queuedMessages: [PendingMessage]? = nil,
        activeClient: SessionActiveClient? = nil,
        customizations: [SessionCustomization]? = nil
    ) -> SessionState {
        SessionState(
            summary: SessionSummary(
                resource: S,
                provider: "copilot",
                title: "Test Session",
                status: status,
                createdAt: 1000,
                modifiedAt: 1000
            ),
            lifecycle: lifecycle,
            activeClient: activeClient,
            turns: [],
            steeringMessage: steeringMessage,
            queuedMessages: queuedMessages,
            customizations: customizations
        )
    }

    private func makeSessionStateWithActiveTurn(
        steeringMessage: PendingMessage? = nil,
        queuedMessages: [PendingMessage]? = nil,
        activeClient: SessionActiveClient? = nil,
        customizations: [SessionCustomization]? = nil
    ) -> SessionState {
        SessionState(
            summary: SessionSummary(
                resource: S,
                provider: "copilot",
                title: "Test Session",
                status: .inProgress,
                createdAt: 1000,
                modifiedAt: 2000
            ),
            lifecycle: .ready,
            activeClient: activeClient,
            turns: [],
            activeTurn: ActiveTurn(
                id: T,
                userMessage: UserMessage(text: "Hello"),
                responseParts: [],
                usage: nil
            ),
            steeringMessage: steeringMessage,
            queuedMessages: queuedMessages,
            customizations: customizations
        )
    }

    /// Apply a session action using the native reducer
    private func apply(_ state: SessionState, _ action: StateAction) -> SessionState {
        sessionR.applying(action: action, to: state)
    }

    /// Starts a tool call in streaming state.
    private func startToolCall(_ state: SessionState, toolCallId: String? = nil) -> SessionState {
        let tcId = toolCallId ?? TC
        return apply(state, .sessionToolCallStart(SessionToolCallStartAction(
            session: S, turnId: T, toolCallId: tcId,
            type: .sessionToolCallStart,
            toolName: "bash", displayName: "Run Command"
        )))
    }

    /// Advances a streaming tool call to running (auto-confirmed).
    private func readyToolCallAutoConfirm(_ state: SessionState, toolCallId: String? = nil) -> SessionState {
        let tcId = toolCallId ?? TC
        return apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: tcId,
            type: .sessionToolCallReady,
            invocationMessage: .string("Run"),
            confirmed: .notNeeded
        )))
    }

    /// Gets tool call parts from response parts.
    private func getToolCallParts(_ state: SessionState) -> [ToolCallResponsePart] {
        guard let parts = state.activeTurn?.responseParts else { return [] }
        return parts.compactMap { part in
            if case .toolCall(let tcPart) = part { return tcPart }
            return nil
        }
    }

    /// Gets a tool call part by toolCallId.
    private func getToolCallPart(_ state: SessionState, toolCallId: String? = nil) -> ToolCallResponsePart? {
        let tcId = toolCallId ?? TC
        return getToolCallParts(state).first { part in
            toolCallIdOf(part.toolCall) == tcId
        }
    }

    private func toolCallIdOf(_ tc: ToolCallState) -> String {
        switch tc {
        case .streaming(let s): return s.toolCallId
        case .pendingConfirmation(let s): return s.toolCallId
        case .running(let s): return s.toolCallId
        case .pendingResultConfirmation(let s): return s.toolCallId
        case .completed(let s): return s.toolCallId
        case .cancelled(let s): return s.toolCallId
        }
    }

    private func statusOf(_ tc: ToolCallState) -> ToolCallStatus {
        switch tc {
        case .streaming(let s): return s.status
        case .pendingConfirmation(let s): return s.status
        case .running(let s): return s.status
        case .pendingResultConfirmation(let s): return s.status
        case .completed(let s): return s.status
        case .cancelled(let s): return s.status
        }
    }

    private func getMarkdownText(_ state: SessionState) -> String {
        guard let parts = state.activeTurn?.responseParts else { return "" }
        return parts.compactMap { part in
            if case .markdown(let md) = part { return md.content }
            return nil
        }.joined()
    }

    private func createMarkdownPart(_ state: SessionState, partId: String) -> SessionState {
        apply(state, .sessionResponsePart(SessionResponsePartAction(
            type: .sessionResponsePart, session: S, turnId: T,
            part: .markdown(MarkdownResponsePart(kind: .markdown, id: partId, content: ""))
        )))
    }

    private func createReasoningPart(_ state: SessionState, partId: String) -> SessionState {
        apply(state, .sessionResponsePart(SessionResponsePartAction(
            type: .sessionResponsePart, session: S, turnId: T,
            part: .reasoning(ReasoningResponsePart(kind: .reasoning, id: partId, content: ""))
        )))
    }

    private func getTurnToolCallParts(_ turn: Turn) -> [ToolCallResponsePart] {
        turn.responseParts.compactMap { part in
            if case .toolCall(let tcPart) = part { return tcPart }
            return nil
        }
    }

    // MARK: - Protocol Conformance Tests

    func testReducerProtocolConformance() {
        // Verify the types conform to the Reducer protocol
        let _: any Reducer = AHPRootReducer()
        let _: any Reducer = AHPSessionReducer()
    }

    func testTypeErasure() {
        let erased = AnyReducer(AHPSessionReducer())
        var state = makeSessionState()
        erased.reduce(into: &state, action: .sessionReady(SessionReadyAction(type: .sessionReady, session: S)))
        XCTAssertEqual(state.lifecycle, .ready)
    }

    func testCombinedReducer() {
        // Create two reducers that each handle different aspects
        let r1 = AnyReducer<SessionState, StateAction> { state, action in
            if case .sessionTitleChanged(let a) = action {
                state.summary.title = a.title
            }
        }
        let r2 = AnyReducer<SessionState, StateAction> { state, action in
            if case .sessionModelChanged(let a) = action {
                state.summary.model = a.model
            }
        }
        let combined = CombinedReducer([r1, r2])

        var state = makeSessionState()
        combined.reduce(into: &state, action: .sessionTitleChanged(SessionTitleChangedAction(
            type: .sessionTitleChanged, session: S, title: "Custom Title"
        )))
        XCTAssertEqual(state.summary.title, "Custom Title")

        combined.reduce(into: &state, action: .sessionModelChanged(SessionModelChangedAction(
            type: .sessionModelChanged, session: S, model: "gpt-4"
        )))
        XCTAssertEqual(state.summary.model, "gpt-4")
    }

    func testApplyingConvenience() {
        let state = makeSessionState()
        let next = sessionR.applying(
            action: .sessionReady(SessionReadyAction(type: .sessionReady, session: S)),
            to: state
        )
        // Original unchanged
        XCTAssertEqual(state.lifecycle, .creating)
        // New state updated
        XCTAssertEqual(next.lifecycle, .ready)
    }

    // MARK: - Root Reducer Tests

    func testRootAgentsChanged() {
        let agents = [AgentInfo(provider: "copilot", displayName: "Copilot", description: "AI", models: [])]
        var state = makeRootState()
        rootR.reduce(into: &state, action: .rootAgentsChanged(RootAgentsChangedAction(type: .rootAgentsChanged, agents: agents)))
        XCTAssertEqual(state.agents.count, 1)
        XCTAssertEqual(state.agents[0].provider, "copilot")
    }

    func testRootActiveSessionsChanged() {
        var state = makeRootState()
        rootR.reduce(into: &state, action: .rootActiveSessionsChanged(RootActiveSessionsChangedAction(type: .rootActiveSessionsChanged, activeSessions: 5)))
        XCTAssertEqual(state.activeSessions, 5)
    }

    func testRootReducerDoesNotMutateOriginalViaApplying() {
        let state = makeRootState(agents: [])
        let agents = [AgentInfo(provider: "x", displayName: "X", description: "x", models: [])]
        let _ = rootR.applying(
            action: .rootAgentsChanged(RootAgentsChangedAction(type: .rootAgentsChanged, agents: agents)),
            to: state
        )
        XCTAssertEqual(state.agents.count, 0)
    }

    // MARK: - Session Reducer: Lifecycle Tests

    func testSessionReady() {
        let next = apply(makeSessionState(), .sessionReady(SessionReadyAction(type: .sessionReady, session: S)))
        XCTAssertEqual(next.lifecycle, .ready)
        XCTAssertEqual(next.summary.status, .idle)
    }

    func testSessionCreationFailed() {
        let error = ErrorInfo(errorType: "init", message: "Failed to start")
        let next = apply(makeSessionState(), .sessionCreationFailed(SessionCreationFailedAction(type: .sessionCreationFailed, session: S, error: error)))
        XCTAssertEqual(next.lifecycle, .creationFailed)
        XCTAssertEqual(next.creationError?.errorType, "init")
    }

    // MARK: - Turn Lifecycle Tests

    func testTurnStarted() {
        let next = apply(makeSessionState(lifecycle: .ready), .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: S, turnId: T, userMessage: UserMessage(text: "Hello")
        )))
        XCTAssertEqual(next.summary.status, .inProgress)
        XCTAssertNotNil(next.activeTurn)
        XCTAssertEqual(next.activeTurn?.id, T)
        XCTAssertEqual(next.activeTurn?.userMessage.text, "Hello")
    }

    func testTurnStartedWithQueuedMessageIdRemovesFromQueue() {
        let state = makeSessionState(lifecycle: .ready, queuedMessages: [
            PendingMessage(id: "q-1", userMessage: UserMessage(text: "First")),
            PendingMessage(id: "q-2", userMessage: UserMessage(text: "Second")),
        ])
        let next = apply(state, .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: S, turnId: T,
            userMessage: UserMessage(text: "First"), queuedMessageId: "q-1"
        )))
        XCTAssertEqual(next.queuedMessages?.count, 1)
        XCTAssertEqual(next.queuedMessages?[0].id, "q-2")
    }

    func testTurnStartedRemovesLastQueuedMessageSetsNil() {
        let state = makeSessionState(lifecycle: .ready, queuedMessages: [
            PendingMessage(id: "q-1", userMessage: UserMessage(text: "Only"))
        ])
        let next = apply(state, .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: S, turnId: T,
            userMessage: UserMessage(text: "Only"), queuedMessageId: "q-1"
        )))
        XCTAssertNil(next.queuedMessages)
    }

    func testTurnStartedRemovesMatchingSteeringMessage() {
        let state = makeSessionState(lifecycle: .ready,
            steeringMessage: PendingMessage(id: "s-1", userMessage: UserMessage(text: "Steer")))
        let next = apply(state, .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: S, turnId: T,
            userMessage: UserMessage(text: "Steer"), queuedMessageId: "s-1"
        )))
        XCTAssertNil(next.steeringMessage)
    }

    func testSessionDelta() {
        var state = createMarkdownPart(makeSessionStateWithActiveTurn(), partId: "md-1")
        state = apply(state, .sessionDelta(SessionDeltaAction(type: .sessionDelta, session: S, turnId: T, partId: "md-1", content: "Hello ")))
        state = apply(state, .sessionDelta(SessionDeltaAction(type: .sessionDelta, session: S, turnId: T, partId: "md-1", content: "world")))
        XCTAssertEqual(getMarkdownText(state), "Hello world")
    }

    func testSessionDeltaIgnoresWrongTurnId() {
        let state = createMarkdownPart(makeSessionStateWithActiveTurn(), partId: "md-1")
        let next = apply(state, .sessionDelta(SessionDeltaAction(type: .sessionDelta, session: S, turnId: "wrong-turn", partId: "md-1", content: "orphan")))
        XCTAssertEqual(getMarkdownText(next), "")
    }

    func testTurnComplete() {
        var s = createMarkdownPart(makeSessionStateWithActiveTurn(), partId: "md-1")
        s = apply(s, .sessionDelta(SessionDeltaAction(type: .sessionDelta, session: S, turnId: T, partId: "md-1", content: "Response text")))
        s = apply(s, .sessionTurnComplete(SessionTurnCompleteAction(type: .sessionTurnComplete, session: S, turnId: T)))
        XCTAssertNil(s.activeTurn)
        XCTAssertEqual(s.turns.count, 1)
        XCTAssertEqual(s.turns[0].state, .complete)
        XCTAssertEqual(s.summary.status, .idle)
    }

    func testTurnCancelled() {
        let next = apply(makeSessionStateWithActiveTurn(), .sessionTurnCancelled(SessionTurnCancelledAction(type: .sessionTurnCancelled, session: S, turnId: T)))
        XCTAssertNil(next.activeTurn)
        XCTAssertEqual(next.turns[0].state, .cancelled)
    }

    func testSessionError() {
        let error = ErrorInfo(errorType: "runtime", message: "Something broke")
        let next = apply(makeSessionStateWithActiveTurn(), .sessionError(SessionErrorAction(type: .sessionError, session: S, turnId: T, error: error)))
        XCTAssertEqual(next.turns[0].state, .error)
        XCTAssertEqual(next.turns[0].error?.message, "Something broke")
        XCTAssertEqual(next.summary.status, .error)
    }

    func testForceCancelsInProgressToolCalls() {
        let state = startToolCall(makeSessionStateWithActiveTurn())
        let next = apply(state, .sessionTurnComplete(SessionTurnCompleteAction(type: .sessionTurnComplete, session: S, turnId: T)))
        let tcParts = getTurnToolCallParts(next.turns[0])
        XCTAssertEqual(tcParts.count, 1)
        if case .cancelled(let cancelled) = tcParts[0].toolCall {
            XCTAssertEqual(cancelled.reason, .skipped)
        } else {
            XCTFail("Expected cancelled tool call")
        }
    }

    // MARK: - Tool Call State Machine Tests

    func testFullToolCallLifecycle() {
        var state = startToolCall(makeSessionStateWithActiveTurn())
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .streaming)

        // Delta
        state = apply(state, .sessionToolCallDelta(SessionToolCallDeltaAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallDelta, content: "ls -la", invocationMessage: .string("Listing files")
        )))
        if case .streaming(let streaming) = getToolCallPart(state)!.toolCall {
            XCTAssertEqual(streaming.partialInput, "ls -la")
        }

        // Ready (pending confirmation)
        state = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallReady, invocationMessage: .string("Run: ls -la"), toolInput: "ls -la"
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .pendingConfirmation)

        // Confirmed
        state = apply(state, .sessionToolCallConfirmed(SessionToolCallConfirmedAction(
            session: S, turnId: T, toolCallId: TC, approved: true, confirmed: .userAction
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .running)

        // Complete
        state = apply(state, .sessionToolCallComplete(SessionToolCallCompleteAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallComplete,
            result: ToolCallResult(success: true, pastTenseMessage: .string("Ran command"))
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .completed)
    }

    func testToolCallAutoConfirm() {
        let state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .running)
        if case .running(let r) = getToolCallPart(state)!.toolCall {
            XCTAssertEqual(r.confirmed, .notNeeded)
        }
    }

    func testToolCallDenied() {
        var state = startToolCall(makeSessionStateWithActiveTurn())
        state = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallReady, invocationMessage: .string("Run: rm -rf /")
        )))
        state = apply(state, .sessionToolCallConfirmed(SessionToolCallConfirmedAction(
            session: S, turnId: T, toolCallId: TC, approved: false, reason: .denied
        )))
        if case .cancelled(let c) = getToolCallPart(state)!.toolCall {
            XCTAssertEqual(c.reason, .denied)
        } else {
            XCTFail("Expected cancelled")
        }
    }

    func testToolCallResultConfirmation() {
        var state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()))
        state = apply(state, .sessionToolCallComplete(SessionToolCallCompleteAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallComplete,
            result: ToolCallResult(success: true, pastTenseMessage: .string("Done")),
            requiresResultConfirmation: true
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .pendingResultConfirmation)

        state = apply(state, .sessionToolCallResultConfirmed(SessionToolCallResultConfirmedAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallResultConfirmed, approved: true
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .completed)
    }

    func testToolCallResultDenied() {
        var state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()))
        state = apply(state, .sessionToolCallComplete(SessionToolCallCompleteAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallComplete,
            result: ToolCallResult(success: true, pastTenseMessage: .string("Done")),
            requiresResultConfirmation: true
        )))
        state = apply(state, .sessionToolCallResultConfirmed(SessionToolCallResultConfirmedAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallResultConfirmed, approved: false
        )))
        if case .cancelled(let c) = getToolCallPart(state)!.toolCall {
            XCTAssertEqual(c.reason, .resultDenied)
        } else {
            XCTFail("Expected cancelled with resultDenied")
        }
    }

    func testToolCallReadyIgnoresNonStreamingNonRunning() {
        var state = startToolCall(makeSessionStateWithActiveTurn())
        state = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallReady, invocationMessage: .string("Run")
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .pendingConfirmation)

        let next = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallReady, invocationMessage: .string("Run again")
        )))
        if case .pendingConfirmation(let pc) = getToolCallPart(next)!.toolCall {
            XCTAssertEqual(pc.invocationMessage, .string("Run"))
        } else {
            XCTFail("Expected pending confirmation")
        }
    }

    // MARK: - Running Tool Re-confirmation Tests

    func testRunningToolReconfirmation() {
        var state = readyToolCallAutoConfirm(startToolCall(makeSessionStateWithActiveTurn()))
        state = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: S, turnId: T, toolCallId: TC,
            type: .sessionToolCallReady, invocationMessage: .string("Permission needed")
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .pendingConfirmation)

        state = apply(state, .sessionToolCallConfirmed(SessionToolCallConfirmedAction(
            session: S, turnId: T, toolCallId: TC, approved: true, confirmed: .userAction
        )))
        XCTAssertEqual(statusOf(getToolCallPart(state)!.toolCall), .running)
    }

    // MARK: - Metadata Tests

    func testTitleChanged() {
        let next = apply(makeSessionState(), .sessionTitleChanged(SessionTitleChangedAction(type: .sessionTitleChanged, session: S, title: "New Title")))
        XCTAssertEqual(next.summary.title, "New Title")
        XCTAssertGreaterThan(next.summary.modifiedAt, 1000)
    }

    func testUsage() {
        let usage = UsageInfo(inputTokens: 100, outputTokens: 50)
        let next = apply(makeSessionStateWithActiveTurn(), .sessionUsage(SessionUsageAction(type: .sessionUsage, session: S, turnId: T, usage: usage)))
        XCTAssertEqual(next.activeTurn?.usage?.inputTokens, 100)
    }

    func testReasoning() {
        var state = createReasoningPart(makeSessionStateWithActiveTurn(), partId: "r-1")
        state = apply(state, .sessionReasoning(SessionReasoningAction(type: .sessionReasoning, session: S, turnId: T, partId: "r-1", content: "Thinking about ")))
        state = apply(state, .sessionReasoning(SessionReasoningAction(type: .sessionReasoning, session: S, turnId: T, partId: "r-1", content: "the answer")))
        let content = state.activeTurn!.responseParts.compactMap { p -> String? in
            if case .reasoning(let r) = p { return r.content }
            return nil
        }
        XCTAssertEqual(content[0], "Thinking about the answer")
    }

    func testModelChanged() {
        let next = apply(makeSessionState(), .sessionModelChanged(SessionModelChangedAction(type: .sessionModelChanged, session: S, model: "gpt-4")))
        XCTAssertEqual(next.summary.model, "gpt-4")
    }

    func testServerToolsChanged() {
        let tools = [ToolDefinition(name: "bash", description: "Run shell commands")]
        let next = apply(makeSessionState(), .sessionServerToolsChanged(SessionServerToolsChangedAction(type: .sessionServerToolsChanged, session: S, tools: tools)))
        XCTAssertEqual(next.serverTools?[0].name, "bash")
    }

    func testActiveClientChanged() {
        let client = SessionActiveClient(clientId: "vscode-1", displayName: "VS Code", tools: [])
        let next = apply(makeSessionState(), .sessionActiveClientChanged(SessionActiveClientChangedAction(type: .sessionActiveClientChanged, session: S, activeClient: client)))
        XCTAssertEqual(next.activeClient?.clientId, "vscode-1")
    }

    func testActiveClientUnset() {
        let state = makeSessionState(activeClient: SessionActiveClient(clientId: "vscode-1", tools: []))
        let next = apply(state, .sessionActiveClientChanged(SessionActiveClientChangedAction(type: .sessionActiveClientChanged, session: S, activeClient: nil)))
        XCTAssertNil(next.activeClient)
    }

    func testActiveClientToolsChanged() {
        let state = makeSessionState(activeClient: SessionActiveClient(clientId: "vscode-1", tools: []))
        let tools = [ToolDefinition(name: "openFile", description: "Open a file")]
        let next = apply(state, .sessionActiveClientToolsChanged(SessionActiveClientToolsChangedAction(type: .sessionActiveClientToolsChanged, session: S, tools: tools)))
        XCTAssertEqual(next.activeClient?.tools[0].name, "openFile")
    }

    func testActiveClientToolsChangedIgnoresWithoutClient() {
        let next = apply(makeSessionState(), .sessionActiveClientToolsChanged(SessionActiveClientToolsChangedAction(type: .sessionActiveClientToolsChanged, session: S, tools: [ToolDefinition(name: "openFile")])))
        XCTAssertNil(next.activeClient)
    }

    // MARK: - Dispatch Validation Tests

    func testClientDispatchable() {
        let action: StateAction = .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: S, turnId: T, userMessage: UserMessage(text: "Hello")))
        XCTAssertTrue(isClientDispatchable(action))

        let serverAction: StateAction = .sessionReady(SessionReadyAction(type: .sessionReady, session: S))
        XCTAssertFalse(isClientDispatchable(serverAction))
    }

    // MARK: - Pending Message Tests

    func testSetSteeringMessage() {
        let result = apply(makeSessionState(), .sessionPendingMessageSet(SessionPendingMessageSetAction(
            type: .sessionPendingMessageSet, session: S, kind: .steering, id: "sm-1",
            userMessage: UserMessage(text: "Focus on tests")
        )))
        XCTAssertEqual(result.steeringMessage?.id, "sm-1")
    }

    func testRemoveSteeringMessage() {
        let state = makeSessionState(steeringMessage: PendingMessage(id: "sm-1", userMessage: UserMessage(text: "Steer")))
        let result = apply(state, .sessionPendingMessageRemoved(SessionPendingMessageRemovedAction(
            type: .sessionPendingMessageRemoved, session: S, kind: .steering, id: "sm-1"
        )))
        XCTAssertNil(result.steeringMessage)
    }

    func testSetQueuedMessage() {
        let result = apply(makeSessionState(), .sessionPendingMessageSet(SessionPendingMessageSetAction(
            type: .sessionPendingMessageSet, session: S, kind: .queued, id: "pm-1",
            userMessage: UserMessage(text: "Do something")
        )))
        XCTAssertEqual(result.queuedMessages?.count, 1)
    }

    func testUpdateQueuedMessageInPlace() {
        let state = makeSessionState(queuedMessages: [
            PendingMessage(id: "pm-1", userMessage: UserMessage(text: "First")),
            PendingMessage(id: "pm-2", userMessage: UserMessage(text: "Second")),
        ])
        let result = apply(state, .sessionPendingMessageSet(SessionPendingMessageSetAction(
            type: .sessionPendingMessageSet, session: S, kind: .queued, id: "pm-1",
            userMessage: UserMessage(text: "Updated first")
        )))
        XCTAssertEqual(result.queuedMessages?[0].userMessage.text, "Updated first")
        XCTAssertEqual(result.queuedMessages?[1].id, "pm-2")
    }

    func testRemoveLastQueuedMessageSetsNil() {
        let state = makeSessionState(queuedMessages: [PendingMessage(id: "pm-1", userMessage: UserMessage(text: "Only"))])
        let result = apply(state, .sessionPendingMessageRemoved(SessionPendingMessageRemovedAction(
            type: .sessionPendingMessageRemoved, session: S, kind: .queued, id: "pm-1"
        )))
        XCTAssertNil(result.queuedMessages)
    }

    // MARK: - Reorder Tests

    func testReorderQueuedMessages() {
        let state = makeSessionState(queuedMessages: [
            PendingMessage(id: "a", userMessage: UserMessage(text: "A")),
            PendingMessage(id: "b", userMessage: UserMessage(text: "B")),
            PendingMessage(id: "c", userMessage: UserMessage(text: "C")),
        ])
        let result = apply(state, .sessionQueuedMessagesReordered(SessionQueuedMessagesReorderedAction(
            type: .sessionQueuedMessagesReordered, session: S, order: ["c", "a", "b"]
        )))
        XCTAssertEqual(result.queuedMessages?[0].id, "c")
        XCTAssertEqual(result.queuedMessages?[1].id, "a")
        XCTAssertEqual(result.queuedMessages?[2].id, "b")
    }

    // MARK: - Customization Tests

    func testCustomizationsChanged() {
        let cRef = CustomizationRef(uri: "https://plugins.example/a", displayName: "Plugin A")
        let customizations = [SessionCustomization(customization: cRef, enabled: true)]
        let result = apply(makeSessionState(), .sessionCustomizationsChanged(SessionCustomizationsChangedAction(
            type: .sessionCustomizationsChanged, session: S, customizations: customizations
        )))
        XCTAssertEqual(result.customizations?.count, 1)
    }

    func testCustomizationToggled() {
        let cRef = CustomizationRef(uri: "https://plugins.example/a", displayName: "Plugin A")
        let state = makeSessionState(customizations: [SessionCustomization(customization: cRef, enabled: true)])
        let result = apply(state, .sessionCustomizationToggled(SessionCustomizationToggledAction(
            type: .sessionCustomizationToggled, session: S, uri: cRef.uri, enabled: false
        )))
        XCTAssertEqual(result.customizations?[0].enabled, false)
    }

    func testCustomizationToggledNoOpWhenNil() {
        let result = apply(makeSessionState(), .sessionCustomizationToggled(SessionCustomizationToggledAction(
            type: .sessionCustomizationToggled, session: S, uri: "https://plugins.example/a", enabled: false
        )))
        XCTAssertNil(result.customizations)
    }

    // MARK: - Full Turn Flow (Integration)

    func testFullTurnFlowIntegration() {
        var state = makeSessionState(lifecycle: .ready)

        state = apply(state, .sessionTurnStarted(SessionTurnStartedAction(
            type: .sessionTurnStarted, session: "s", turnId: "t1", userMessage: UserMessage(text: "Fix the bug")
        )))
        state = apply(state, .sessionResponsePart(SessionResponsePartAction(
            type: .sessionResponsePart, session: "s", turnId: "t1",
            part: .markdown(MarkdownResponsePart(kind: .markdown, id: "md-1", content: ""))
        )))
        state = apply(state, .sessionDelta(SessionDeltaAction(type: .sessionDelta, session: "s", turnId: "t1", partId: "md-1", content: "I will fix it.")))

        // Tool call
        state = apply(state, .sessionToolCallStart(SessionToolCallStartAction(
            session: "s", turnId: "t1", toolCallId: "tc1",
            type: .sessionToolCallStart, toolName: "edit", displayName: "Edit File"
        )))
        state = apply(state, .sessionToolCallReady(SessionToolCallReadyAction(
            session: "s", turnId: "t1", toolCallId: "tc1",
            type: .sessionToolCallReady, invocationMessage: .string("Edit main.ts"), confirmed: .notNeeded
        )))
        state = apply(state, .sessionToolCallComplete(SessionToolCallCompleteAction(
            session: "s", turnId: "t1", toolCallId: "tc1",
            type: .sessionToolCallComplete,
            result: ToolCallResult(success: true, pastTenseMessage: .string("Edited main.ts"))
        )))

        state = apply(state, .sessionTurnComplete(SessionTurnCompleteAction(type: .sessionTurnComplete, session: "s", turnId: "t1")))

        XCTAssertNil(state.activeTurn)
        XCTAssertEqual(state.turns.count, 1)
        XCTAssertEqual(state.turns[0].state, .complete)
        XCTAssertEqual(state.summary.status, .idle)

        let mdParts = state.turns[0].responseParts.compactMap { p -> String? in
            if case .markdown(let md) = p { return md.content }
            return nil
        }
        XCTAssertEqual(mdParts[0], "I will fix it.")

        let tcParts = getTurnToolCallParts(state.turns[0])
        XCTAssertEqual(tcParts.count, 1)
        XCTAssertEqual(statusOf(tcParts[0].toolCall), .completed)
    }

    // MARK: - Inout Mutation Test (Native Swift Advantage)

    func testInoutMutationEfficiency() {
        // Demonstrate that the native reducer can mutate state in-place
        // without creating intermediate copies (key benefit of the protocol-based approach)
        var state = makeSessionStateWithActiveTurn()

        // Multiple sequential mutations via inout - no intermediate copies needed
        sessionR.reduce(into: &state, action: .sessionResponsePart(SessionResponsePartAction(
            type: .sessionResponsePart, session: S, turnId: T,
            part: .markdown(MarkdownResponsePart(kind: .markdown, id: "md-1", content: ""))
        )))
        sessionR.reduce(into: &state, action: .sessionDelta(SessionDeltaAction(
            type: .sessionDelta, session: S, turnId: T, partId: "md-1", content: "Hello"
        )))
        sessionR.reduce(into: &state, action: .sessionDelta(SessionDeltaAction(
            type: .sessionDelta, session: S, turnId: T, partId: "md-1", content: " World"
        )))

        XCTAssertEqual(getMarkdownText(state), "Hello World")
    }
}
