using System.Runtime.CompilerServices;

// InternalsVisibleTo lets the test project exercise genuine internal helpers and
// internal state WITHOUT widening the public API surface — the idiomatic .NET pattern
// for unit-testing internals (as dotnet/runtime itself does pervasively). The test
// assembly reaches exactly these, each a real internal-helper or invariant test:
//   - ReconnectPolicy.BackoffFor  — unit-test the backoff curve / jitter / cap (pure fn)
//   - BoundedDropOldestChannel<T> — test drop-oldest eviction counting directly
//   - HostEntry                   — test SessionSummary copy-on-write (torn-read) isolation
//   - Subscription.OnClose        — test the once-only detach hook directly
//   - PendingRequestCount / SubscriptionCount / EventListenerCount /
//     StateListenerCount / NextRequestId — observe internal bookkeeping to prove
//     lifecycle invariants (a cancelled request goes 1->0; a direct Close() detaches
//     from the registry; a disposed stream leaves the fan-out list; a pre-cancelled
//     request mints no id). These have no place on the public surface.
// IVT keeps the public contract clean while the internals stay tested.
[assembly: InternalsVisibleTo("AgentHostProtocol.Tests")]
