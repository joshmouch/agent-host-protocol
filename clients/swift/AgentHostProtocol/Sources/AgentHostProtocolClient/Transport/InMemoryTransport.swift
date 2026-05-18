// InMemoryTransport — paired in-memory transport for tests.
//
// Two halves connected by AsyncStreams. Mirrors the `MemTransport` pattern
// in the Rust integration tests (`crates/ahp/tests/client_roundtrip.rs`).

import Foundation

/// One half of an in-memory transport pair.
///
/// Calls to `send` on one half are observed by `recv` on the other.
/// `close()` on either half finishes both inbound queues so both peers see
/// `recv() == nil` shortly afterward.
///
/// `recv()` is single-consumer — it is called from a single task at a time
/// (the `AHPClient` receive loop) and is not safe to invoke concurrently
/// from multiple tasks. `send()` and `close()` are safe to call concurrently.
public final class InMemoryTransport: AHPTransport, @unchecked Sendable {
    /// Continuation that delivers into our peer's inbound queue.
    private let peerInboundContinuation: AsyncStream<TransportMessage>.Continuation
    /// Continuation that delivers into our own inbound queue.
    private let ownInboundContinuation: AsyncStream<TransportMessage>.Continuation
    /// Storage cell for the iterator over our own inbound queue.
    private let iteratorBox: IteratorBox

    private let lock = NSLock()
    private var closed = false

    private init(
        peerInboundContinuation: AsyncStream<TransportMessage>.Continuation,
        ownInbound: AsyncStream<TransportMessage>,
        ownInboundContinuation: AsyncStream<TransportMessage>.Continuation
    ) {
        self.peerInboundContinuation = peerInboundContinuation
        self.ownInboundContinuation = ownInboundContinuation
        self.iteratorBox = IteratorBox(iterator: ownInbound.makeAsyncIterator())
    }

    /// Create a connected pair of in-memory transports.
    public static func pair() -> (InMemoryTransport, InMemoryTransport) {
        var aInCont: AsyncStream<TransportMessage>.Continuation!
        let aIn = AsyncStream<TransportMessage>(bufferingPolicy: .unbounded) { cont in
            aInCont = cont
        }
        var bInCont: AsyncStream<TransportMessage>.Continuation!
        let bIn = AsyncStream<TransportMessage>(bufferingPolicy: .unbounded) { cont in
            bInCont = cont
        }
        let a = InMemoryTransport(
            peerInboundContinuation: bInCont,
            ownInbound: aIn,
            ownInboundContinuation: aInCont
        )
        let b = InMemoryTransport(
            peerInboundContinuation: aInCont,
            ownInbound: bIn,
            ownInboundContinuation: bInCont
        )
        return (a, b)
    }

    public func send(_ message: TransportMessage) async throws {
        if isClosedSync() {
            throw TransportError.closed
        }
        peerInboundContinuation.yield(message)
    }

    public func recv() async throws -> TransportMessage? {
        return await iteratorBox.next()
    }

    public func close() async throws {
        if !markClosed() { return }
        peerInboundContinuation.finish()
        ownInboundContinuation.finish()
    }

    // MARK: - Lock helpers (sync to avoid holding the lock across `await`).

    private func isClosedSync() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return closed
    }

    /// Atomically transitions to the closed state. Returns `true` if this is
    /// the *first* close (the caller should run cleanup), `false` if the
    /// transport was already closed.
    private func markClosed() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if closed { return false }
        closed = true
        return true
    }
}

/// Single-consumer iterator wrapper. Locks are taken only synchronously,
/// never across an `await`. Concurrent calls to `next()` are unsupported and
/// trip a `preconditionFailure` — they would otherwise look like a clean
/// close to one of the racing callers.
private final class IteratorBox: @unchecked Sendable {
    private enum State {
        /// Iterator is sitting in the box, ready for the next caller.
        case available(AsyncStream<TransportMessage>.AsyncIterator)
        /// A caller has taken the iterator and is awaiting its next item.
        case inFlight
        /// The stream has finished; future `next()` calls return nil.
        case finished
    }

    private var state: State
    private let lock = NSLock()

    init(iterator: AsyncStream<TransportMessage>.AsyncIterator) {
        self.state = .available(iterator)
    }

    func next() async -> TransportMessage? {
        var iter = takeIterator()
        let item = await iter.next()
        if item == nil {
            markFinished()
            return nil
        }
        returnIterator(iter)
        return item
    }

    private func takeIterator() -> AsyncStream<TransportMessage>.AsyncIterator {
        lock.lock(); defer { lock.unlock() }
        switch state {
        case .available(let iter):
            state = .inFlight
            return iter
        case .inFlight:
            preconditionFailure(
                "InMemoryTransport.recv() is single-consumer; concurrent calls are not supported"
            )
        case .finished:
            // Hand out a no-op iterator. The caller will pump it once and
            // see `nil`, then we'll re-enter `markFinished` (idempotent).
            return AsyncStream<TransportMessage> { _ in }.makeAsyncIterator()
        }
    }

    private func returnIterator(_ iter: AsyncStream<TransportMessage>.AsyncIterator) {
        lock.lock(); defer { lock.unlock() }
        // The state may have transitioned to .finished concurrently if the
        // stream finished between our `await iter.next()` and this call;
        // honour that and don't resurrect the iterator.
        switch state {
        case .inFlight:
            state = .available(iter)
        case .finished:
            break
        case .available:
            preconditionFailure("IteratorBox.returnIterator called without takeIterator")
        }
    }

    private func markFinished() {
        lock.lock(); defer { lock.unlock() }
        state = .finished
    }
}
