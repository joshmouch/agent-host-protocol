// MultiHostClientTests — integration tests for the multi-host SDK.
//
// Each test spins up one or more `FakeHost`s over `InMemoryTransport.pair()`
// (mirroring `clients/rust/crates/ahp/tests/hosts.rs`) and exercises the
// `MultiHostClient` facade end to end.

import XCTest
import AgentHostProtocol
@testable import AgentHostProtocolClient

final class MultiHostClientTests: XCTestCase {

    // MARK: - single_constructor_yields_connected_handle

    func testSingleConstructorYieldsConnectedHandle() async throws {
        let agent = makeAgent()
        let state = FakeHostState(agents: [agent])
        let factory = makeFakeHostFactory(state: state)
        let config = HostConfig(id: "local", label: "Local", transportFactory: factory)

        let (multi, _) = try await MultiHostClient.single(config)
        defer { Task { await multi.shutdown() } }

        await waitForHostState(multi, id: "local") { $0.isConnected }

        let snap = await multi.host("local")
        XCTAssertNotNil(snap)
        XCTAssertEqual(snap?.label, "Local")
        XCTAssertEqual(snap?.protocolVersion, "0.1.0")
        XCTAssertEqual(snap?.agents.count, 1)
        XCTAssertEqual(snap?.agents.first?.provider, "copilot")
        XCTAssertNotNil(snap?.lastConnectedAt)
        XCTAssertTrue(snap?.state.isConnected ?? false)

        await multi.shutdown()
    }

    // MARK: - two_hosts_register_and_connect_independently

    func testTwoHostsRegisterAndConnectIndependently() async throws {
        let multi = MultiHostClient()

        _ = try await multi.add(HostConfig(
            id: "a",
            label: "A",
            transportFactory: makeFakeHostFactory(state: FakeHostState())
        ))
        _ = try await multi.add(HostConfig(
            id: "b",
            label: "B",
            transportFactory: makeFakeHostFactory(state: FakeHostState())
        ))

        await waitForHostState(multi, id: "a") { $0.isConnected }
        await waitForHostState(multi, id: "b") { $0.isConnected }

        let hosts = await multi.hosts()
        XCTAssertEqual(hosts.count, 2)
        XCTAssertTrue(hosts.allSatisfy { $0.state.isConnected })
        let labels = Set(hosts.map(\.label))
        XCTAssertEqual(labels, ["A", "B"])

        await multi.shutdown()
    }

    // MARK: - aggregated_sessions_track_listsessions_then_notification

    func testAggregatedSessionsTrackListSessionsThenNotification() async throws {
        let initial = makeSummary("copilot:/s1", "Initial title", modifiedAt: 1_000)
        let added = makeSummary("copilot:/s2", "Added later", modifiedAt: 2_000)

        let factory = makeFakeHostFactory(
            state: FakeHostState(sessions: [initial]),
            injectAfterInit: added
        )

        let multi = MultiHostClient()
        _ = try await multi.add(HostConfig(id: "local", label: "Local", transportFactory: factory))
        await waitForHostState(multi, id: "local") { $0.isConnected }

        await waitUntil { await multi.aggregatedSessions().count == 2 }
        let aggregated = await multi.aggregatedSessions()
        let titles = aggregated.map(\.summary.title)
        XCTAssertEqual(titles, ["Added later", "Initial title"])
        XCTAssertTrue(aggregated.allSatisfy { $0.hostId == "local" })
        XCTAssertTrue(aggregated.allSatisfy { $0.hostLabel == "Local" })

        await multi.shutdown()
    }

    // MARK: - host_client_handle_invalidates_after_reconnect

    func testHostClientHandleInvalidatesAfterReconnect() async throws {
        let factory = makeFakeHostFactory(state: FakeHostState())

        let multi = MultiHostClient()
        let config = HostConfig(id: "local", label: "Local", transportFactory: factory)
            .withReconnectPolicy(.immediateForever)
        _ = try await multi.add(config)

        await waitForHostState(multi, id: "local") { $0.isConnected }

        let handleOpt = await multi.client(for: "local")
        let handle = try XCTUnwrap(handleOpt)
        let initialGeneration = handle.generation
        try await handle.checkAlive()

        try await multi.reconnect("local")
        await waitUntil {
            guard let snap = await multi.host("local") else { return false }
            return snap.generation > initialGeneration && snap.state.isConnected
        }

        do {
            try await handle.checkAlive()
            XCTFail("expected HostError.hostReconnected")
        } catch let error as HostError {
            switch error {
            case .hostReconnected(_, let handleGen, let currentGen):
                XCTAssertEqual(handleGen, initialGeneration)
                XCTAssertGreaterThan(currentGen, initialGeneration)
            default:
                XCTFail("unexpected error: \(error)")
            }
        }

        let freshOpt = await multi.client(for: "local")
        let fresh = try XCTUnwrap(freshOpt)
        XCTAssertGreaterThan(fresh.generation, initialGeneration)
        try await fresh.checkAlive()

        await multi.shutdown()
    }

    // MARK: - remove_host_terminates_supervisor_and_emits_event

    func testRemoveHostTerminatesSupervisorAndEmitsEvent() async throws {
        let factory = makeFakeHostFactory(state: FakeHostState())

        let multi = MultiHostClient()
        _ = try await multi.add(HostConfig(id: "temp", label: "Temporary", transportFactory: factory))
        await waitForHostState(multi, id: "temp") { $0.isConnected }

        let events = await multi.hostEvents()

        try await multi.remove("temp")

        var sawRemoved = false
        let deadline = ContinuousClock.now + .milliseconds(2_000)
        var iter = events.makeAsyncIterator()
        while ContinuousClock.now < deadline {
            guard let event = try await Self.nextWithTimeout(&iter, timeout: .milliseconds(200))
            else { break }
            if case .removed(let id) = event, id == "temp" {
                sawRemoved = true
                break
            }
        }
        XCTAssertTrue(sawRemoved, "expected HostEvent.removed for temp")

        let snap = await multi.host("temp")
        XCTAssertNil(snap)

        await multi.shutdown()
    }

    // MARK: - fan_in_events_carry_host_id_and_resource

    func testFanInEventsCarryHostIdAndResource() async throws {
        let initialA = makeSummary("copilot:/a-1", "first-a", modifiedAt: 100)
        let injectA = makeSummary("copilot:/added-a", "a-side", modifiedAt: 200)
        let initialB = makeSummary("copilot:/b-1", "first-b", modifiedAt: 100)
        let injectB = makeSummary("copilot:/added-b", "b-side", modifiedAt: 300)

        let multi = MultiHostClient()
        let events = await multi.events()

        _ = try await multi.add(HostConfig(
            id: "a",
            label: "Host A",
            transportFactory: makeFakeHostFactory(
                state: FakeHostState(sessions: [initialA]),
                injectAfterInit: injectA
            )
        ))
        _ = try await multi.add(HostConfig(
            id: "b",
            label: "Host B",
            transportFactory: makeFakeHostFactory(
                state: FakeHostState(sessions: [initialB]),
                injectAfterInit: injectB
            )
        ))

        var hostsSeen: Set<HostId> = []
        let deadline = ContinuousClock.now + .milliseconds(3_000)
        var iter = events.makeAsyncIterator()
        while hostsSeen.count < 2 && ContinuousClock.now < deadline {
            guard let event = try await Self.nextWithTimeout(&iter, timeout: .milliseconds(500))
            else { break }
            hostsSeen.insert(event.hostId)
            // Notifications carry no resource URI by design.
            XCTAssertNil(event.resource)
        }
        XCTAssertTrue(hostsSeen.contains("a"), "missing event from host A; saw \(hostsSeen)")
        XCTAssertTrue(hostsSeen.contains("b"), "missing event from host B; saw \(hostsSeen)")

        await multi.shutdown()
    }

    // MARK: - transport_factory_is_called_for_each_reconnect

    func testTransportFactoryIsCalledForEachReconnect() async throws {
        let counter = CallCounter()
        let factory: HostTransportFactory = { _ in
            await counter.bump()
            let (clientSide, serverSide) = InMemoryTransport.pair()
            _ = FakeHost.start(transport: serverSide, state: FakeHostState())
            return clientSide
        }

        let multi = MultiHostClient()
        let config = HostConfig(id: "local", label: "Local", transportFactory: factory)
            .withReconnectPolicy(.immediateForever)
        _ = try await multi.add(config)

        await waitForHostState(multi, id: "local") { $0.isConnected }
        let count1 = await counter.value()
        XCTAssertEqual(count1, 1)

        try await multi.reconnect("local")
        await waitUntil {
            let snap = await multi.host("local")
            return await counter.value() >= 2 && (snap?.state.isConnected ?? false)
        }
        let count2 = await counter.value()
        XCTAssertEqual(count2, 2)

        await multi.shutdown()
    }

    // MARK: - duplicate_host_id_is_rejected

    func testDuplicateHostIdIsRejected() async throws {
        let multi = MultiHostClient()
        _ = try await multi.add(HostConfig(
            id: "dup",
            label: "first",
            transportFactory: makeFakeHostFactory(state: FakeHostState())
        ))

        do {
            _ = try await multi.add(HostConfig(
                id: "dup",
                label: "second",
                transportFactory: makeFakeHostFactory(state: FakeHostState())
            ))
            XCTFail("expected duplicate-id rejection")
        } catch let error as HostError {
            if case .duplicateHost(let id) = error {
                XCTAssertEqual(id, "dup")
            } else {
                XCTFail("expected .duplicateHost, got \(error)")
            }
        }

        await multi.shutdown()
    }

    // MARK: - subscribe_while_failed_remembers_uri_for_replay

    /// While a host is in `.failed` state, `subscribe(host:uri:)` should still
    /// remember the URI so the next successful reconnect picks it up.
    /// Unsubscribe in the same window should drop the URI from the replay set.
    func testSubscribeWhileFailedRemembersForReplay() async throws {
        // Build a transport factory that fails the first attempt and then
        // succeeds on subsequent attempts. Combine with `.disabled` so the
        // first failure parks the host in `.failed`.
        let didFirstFail = ActorBool()
        let factory: HostTransportFactory = { _ in
            if !(await didFirstFail.value) {
                await didFirstFail.set(true)
                throw TransportError.io("intentional first-attempt failure")
            }
            let (clientSide, serverSide) = InMemoryTransport.pair()
            _ = FakeHost.start(transport: serverSide, state: FakeHostState())
            return clientSide
        }
        let config = HostConfig(id: "tt", label: "T", transportFactory: factory)
            .withReconnectPolicy(.disabled)

        let multi = MultiHostClient()
        _ = try await multi.add(config)

        await waitForHostState(multi, id: "tt") { $0.isFailed }

        // Subscribe while disconnected. The runtime returns `hostShutDown`
        // but appends the URI to the replay set.
        do {
            _ = try await multi.subscribe(host: "tt", uri: "copilot:/queued")
            XCTFail("expected subscribe to reject while failed")
        } catch let error as HostError {
            if case .hostShutDown = error {} else {
                XCTFail("expected .hostShutDown while failed, got \(error)")
            }
        }

        // Unsubscribe an unrelated URI while disconnected — should succeed
        // and not throw, even though no live client exists.
        try await multi.unsubscribe(host: "tt", uri: "copilot:/never-subscribed")

        var snap = await multi.host("tt")
        XCTAssertEqual(snap?.subscriptions.contains("copilot:/queued"), true,
                       "queued subscribe URI should be recorded for replay")

        // Manually reconnect — second attempt succeeds.
        try await multi.reconnect("tt")
        await waitForHostState(multi, id: "tt") { $0.isConnected }

        snap = await multi.host("tt")
        XCTAssertEqual(snap?.subscriptions.contains("copilot:/queued"), true,
                       "subscription should survive into the new connection")

        await multi.shutdown()
    }

    // MARK: - shutdown_tears_down_all_hosts_and_streams

    func testShutdownTearsDownAllHostsAndStreams() async throws {
        let multi = MultiHostClient()

        _ = try await multi.add(HostConfig(
            id: "alpha",
            label: "Alpha",
            transportFactory: makeFakeHostFactory(state: FakeHostState())
        ))
        _ = try await multi.add(HostConfig(
            id: "beta",
            label: "Beta",
            transportFactory: makeFakeHostFactory(state: FakeHostState())
        ))
        await waitForHostState(multi, id: "alpha") { $0.isConnected }
        await waitForHostState(multi, id: "beta") { $0.isConnected }

        let events = await multi.events()
        let hostEvents = await multi.hostEvents()

        await multi.shutdown()

        // After shutdown, both streams should finish so `for await` exits.
        var subEvents = 0
        for await _ in events { subEvents += 1 }
        var hostEventCount = 0
        for await _ in hostEvents { hostEventCount += 1 }
        // We don't assert specific counts — just that the loops end.
        _ = subEvents
        _ = hostEventCount

        // No host snapshots should be retrievable.
        let alphaSnap = await multi.host("alpha")
        let betaSnap = await multi.host("beta")
        XCTAssertNil(alphaSnap)
        XCTAssertNil(betaSnap)

        // Subsequent `add` should reject with `hostShutDown`.
        do {
            _ = try await multi.add(HostConfig(
                id: "gamma",
                label: "Gamma",
                transportFactory: makeFakeHostFactory(state: FakeHostState())
            ))
            XCTFail("expected add to reject after shutdown")
        } catch let error as HostError {
            if case .hostShutDown(let id) = error {
                XCTAssertEqual(id, "gamma")
            } else {
                XCTFail("expected .hostShutDown, got \(error)")
            }
        }

        // Idempotent.
        await multi.shutdown()
    }

    // MARK: - Helpers

    /// Like `nextWithTimeout` from `AHPClientTestHelpers` but typed for any
    /// `AsyncStream` element.
    private static func nextWithTimeout<E>(
        _ iterator: inout AsyncStream<E>.AsyncIterator,
        timeout: Duration
    ) async throws -> E? where E: Sendable {
        try await withThrowingTaskGroup(of: E?.self) { group in
            group.addTask { [iterator = iterator] in
                var iter = iterator
                return await iter.next()
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw TestTimeoutError()
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }
}

private actor CallCounter {
    private var n: Int = 0
    func bump() { n += 1 }
    func value() -> Int { n }
}

private actor ActorBool {
    private var flag: Bool = false
    var value: Bool { flag }
    func set(_ v: Bool) { flag = v }
}

private struct TestTimeoutError: Error {}
