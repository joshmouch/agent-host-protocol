# ADR-SYNC — Synchronization & concurrency primitives

- **Status:** Accepted
- **Scope:** `clients/dotnet` (the `Microsoft.AgentHostProtocol*` packages)
- **Audience:** maintainers of the .NET client. This document is repo-only; it
  is not shipped in any NuGet package.

## Context

The client has several pieces of shared, concurrently-accessed state:

- the async JSON-RPC client's pending-request table and subscription registry
  (`AhpClient`);
- the multi-host registry, the per-host bookkeeping record (`HostEntry`), the
  client-id store, and the optional `MultiHostStateMirror`
  (`Hosts/MultiHostClient.cs`);
- the WebSocket transport's send path (`WebSocketTransport`).

An early version reflexively translated the Go client's `sync.RWMutex` to
`SemaphoreSlim` + `await WaitAsync()` for **all** of this state — which made
pure in-memory accessors `async` for no reason. `SemaphoreSlim` is the right
tool only when you must `await` **while holding** the lock; none of those
critical sections did. This ADR records the primitive chosen for each access
pattern and why, so the choice isn't re-litigated (or re-broken) later.

## Options considered

The full menu of .NET synchronization options (see the
[Microsoft Learn overview](https://learn.microsoft.com/en-us/dotnet/standard/threading/overview-of-synchronization-primitives)),
and where each lands for this client:

| Option | Category | Verdict here |
| --- | --- | --- |
| `lock` / `Monitor` | exclusive lock | **Used** — `HostEntry` field-bundle, channel-list append. Cannot be held across `await`. |
| `System.Threading.Lock` (.NET 9 / C# 13) | exclusive lock | **Used on net9** via a conditional alias — ~25% faster than `Monitor` (compiler emits `Lock.EnterScope()`). net9.0+ only; net8 falls back to `Monitor`. |
| `Mutex` | cross-**process** lock | No — everything is in-process; `Mutex` is a kernel object, far heavier. |
| `SpinLock` | busy-wait exclusive (struct) | No — only wins for nanosecond-scale sections on hot paths; ours aren't that hot, and it's easy to misuse. |
| `SemaphoreSlim` | count-limited / async-capable lock | **Used** — WebSocket send (the one place we `await` *inside* the critical section). |
| `Semaphore` | count-limited, cross-process (kernel) | No — kernel object; `SemaphoreSlim` suffices in-process. |
| `ReaderWriterLockSlim` | read-heavy maps, non-trivial sections | No — loses to `ConcurrentDictionary` under load; recursion/async footguns. |
| `ReaderWriterLock` (legacy) | read/write lock | No — deprecated. |
| `ManualResetEventSlim` / `AutoResetEvent` / `EventWaitHandle` | thread signaling | No — we coordinate via `TaskCompletionSource` and `Channels` (async), not thread events. |
| `Barrier` / `CountdownEvent` | phase / fan-in coordination | No — not our pattern. |
| `Interlocked` | single-value atomics (CAS/increment) | **Used** — request-id and client-seq counters. |
| `Volatile` / `volatile` | single-field visibility | **Used** — shutdown flag (`Volatile.Read`), client-id-store reference. |
| `Lazy<T>` / `LazyInitializer` | thread-safe one-time init | No — no expensive one-time init to guard. |
| `ConcurrentDictionary<K,V>` | concurrent keyed map | **Used** — host registry, client-id store, state mirror. Lock-free reads; atomic `TryAdd`/`GetOrAdd`/`AddOrUpdate`. |
| `ConcurrentQueue` / `ConcurrentStack` / `ConcurrentBag` | lock-free FIFO/LIFO/bag | No — our producer/consumer fan-out is `Channels`. |
| `BlockingCollection<T>` | blocking producer/consumer | No — superseded by `System.Threading.Channels` for async backpressure. |
| `ImmutableDictionary` + `ImmutableInterlocked` | read-mostly, free consistent snapshot | No — more write allocation than `ConcurrentDictionary`; overkill for a small, low-write registry. |
| `FrozenDictionary` / `FrozenSet` (.NET 8) | read-only after build, fastest reads | No — our maps mutate at runtime (hosts come and go); frozen sets are build-once. |
| `System.Threading.Channels` | async producer/consumer | **Used** — subscription/event fan-out (bounded, drop-oldest). |
| `TaskCompletionSource` | one-shot async completion | **Used** — request/response correlation and the client "done" signal. |

## Distinct concurrency use cases

There isn't a single locking pattern — the client has **several distinct
concurrency use cases**, and each gets the primitive that fits it. That is the
whole point of this ADR: not "what's our lock," but "what's the right tool for
each problem."

| # | Use case | Where | Primitive |
| --- | --- | --- | --- |
| 1 | Concurrent keyed map (independent entries) | host registry, client-id store, state mirror | `ConcurrentDictionary` |
| 2 | Update/read a small bundle of related fields **atomically** | `HostEntry` (`_client`/`_state`/`_protoVer`/`_generation`/`_updatedAt`) | `lock` |
| 3 | Append to + snapshot a list | subscription/event subscriber lists | `lock` |
| 4 | Serialize an **awaited** I/O call (no concurrent sends) | `WebSocketTransport.SendAsync` | `SemaphoreSlim` |
| 5 | Single-value atomic counter | JSON-RPC request id, client sequence | `Interlocked` |
| 6 | Publish a single field / flag visibly | shutdown flag, client-id-store reference | `Volatile` / `volatile` |
| 7 | Producer/consumer fan-out with backpressure | subscription + host event delivery | `System.Threading.Channels` |
| 8 | Request/response correlation by id | `AhpClient` pending-request table | `ConcurrentDictionary<ulong, TaskCompletionSource<…>>` |
| 9 | One-shot completion signal | client `Completion` / `Done` | `TaskCompletionSource` |

## Decision

Pick the primitive that matches the **access pattern**, not a single
one-size-fits-all lock.

| State | Access pattern | Primitive |
| --- | --- | --- |
| Host registry (`MultiHostClient._hosts`) | read-heavy; `TryAdd`/`TryRemove`/`TryGet`/snapshot-all | **`ConcurrentDictionary`** — `TryAdd` is the add-if-absent done atomically, removing both the lock and the check-then-act race. |
| `InMemoryClientIdStore` | single-key load/store | **`ConcurrentDictionary`** — lock-free; the `IClientIdStore` *interface* stays async because a real store does I/O. |
| `MultiHostStateMirror` (4 maps) | independent single-key put/get + per-host drop | **`ConcurrentDictionary`** per map. |
| `HostEntry` fields (`_client`/`_state`/`_protoVer`/`_generation`/`_updatedAt`) | a small bundle read and written **as a group** | **`lock`** — a `ConcurrentDictionary` cannot express "set these three fields atomically"; writes are rare connect/disconnect events. |
| Event/subscription channel lists | append + snapshot-iterate, near-zero contention | **`lock`** around a `List`. |
| WebSocket send | **awaits** `SendAsync` while holding | **`SemaphoreSlim`** — the one genuine async-lock. |
| Request-id / client-seq counters | single-value increment | **`Interlocked`**. |
| Client-id-store reference swap | single-field publish | **`volatile`**. |

### `System.Threading.Lock` on .NET 9, `Monitor` on .NET 8

The packages multi-target `net8.0;net9.0`. The `lock`-based fields use a
conditional type alias so each runtime gets its best lock with no change to the
`lock (…) { … }` statements:

```csharp
// GlobalUsings.cs
#if NET9_0_OR_GREATER
global using Gate = System.Threading.Lock;   // ~25% faster under contention
#else
global using Gate = System.Object;           // classic Monitor
#endif
```

```csharp
private readonly Gate _gate = new();
// ...
lock (_gate) { /* … */ }   // emits Lock.EnterScope() on net9, Monitor on net8
```

NuGet selects the `net9.0` assets for .NET 9+ consumers automatically; .NET 8
consumers get the `net8.0` assets. The target framework stays `net8.0` (the
current LTS) for maximum supported reach.

## Consequences

- Reads of host state (`Host`/`Hosts`) are synchronous and lock-free; the
  only `async` methods left are the ones that actually do I/O
  (connect/initialize/send/receive/shutdown).
- One small `lock` remains where it is genuinely correct (`HostEntry`), and one
  `SemaphoreSlim` remains where it is genuinely correct (WebSocket send).
- A `net9.0` build is validated in CI; the lock semantics are identical across
  TFMs, so behavior is unchanged — only the lock implementation differs.

## References

- [Best Practices for Using ConcurrentDictionary — Eli Arbel](https://arbel.net/2013/02/03/best-practices-for-using-concurrentdictionary/)
- [ConcurrentDictionary vs ReaderWriterLockSlim — aspnet/Caching#242](https://github.com/aspnet/Caching/issues/242)
- [The `lock` statement / `System.Threading.Lock` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock)
