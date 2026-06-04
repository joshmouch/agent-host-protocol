# ADR 0001 — Concurrency primitives in the .NET client

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

| Primitive | Good for | Notes / why not (here) |
| --- | --- | --- |
| `lock` (Monitor) | short, synchronous critical sections | Simple and correct. Cannot be held across `await`. On .NET 9 the dedicated `System.Threading.Lock` is faster (see below). |
| `System.Threading.Lock` (.NET 9 / C# 13) | same as `lock`, ~25% faster under contention | A purpose-built lock; the compiler emits `Lock.EnterScope()` instead of going through the object header's sync block. **net9.0+ only** — not available at our `net8.0` target. |
| `SemaphoreSlim` | mutual exclusion where you **await inside** the critical section | The only place that fits is the WebSocket send path (`ClientWebSocket.SendAsync` is awaited and forbids concurrent calls). Heavier than `lock` and allocates a `Task` per non-async use. |
| `ReaderWriterLockSlim` | read-heavy maps with non-trivial critical sections | Under load, contention on the read lock makes it lose to `ConcurrentDictionary`; recursion/async pitfalls. Not worth it here. |
| `ConcurrentDictionary<K,V>` | concurrent maps with independent, mostly single-key ops | Lock-free reads, fine-grained (striped) writes. Atomic `TryAdd`/`GetOrAdd`/`AddOrUpdate` express check-then-act without an external lock. Caveat: an operation spanning two calls is still not atomic. |
| `ImmutableDictionary` + `Interlocked` | read-mostly maps where you want a free consistent snapshot | Copy-on-write; elegant but more allocation on writes than `ConcurrentDictionary`. Overkill for our small, low-write registry. |
| `Interlocked` / `Volatile` | single-value atomics / single-field visibility | Used for the request-id and client-seq counters, and the `volatile` client-id-store reference. |

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
